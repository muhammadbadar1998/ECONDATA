# EconData — Deployment Runbook (SQLite)

Operational guide to run the .NET 10 pipeline against a **local SQLite** store.
There is no database server, no connection string, and no credential to rotate —
the store is a single file.

---

## Prerequisites

- **.NET SDK 10**.
- (For `vectorize`/`chat`) **Ollama** with `nomic-embed-text` and a chat model.

```bash
dotnet build EconData.slnx
```

---

## Step 1 — Initialize the database

```bash
dotnet run --project src/EconData.Cli -- init-db
```

Creates `output/econdata.db` with the `dbe_*` tables, views, and the sqlite-vec
`dbe_embeddings_vec` table. Idempotent — safe to re-run.

Verify:

```bash
dotnet run --project src/EconData.Cli -- check-db
# -> Database OK (integrity_check=ok; dbe_tables=14).
```

---

## Step 2 — Full extraction + load

```bash
dotnet run --project src/EconData.Cli -- extract --metrics all --db
```

This:

1. Seeds `dbe_entities`, `dbe_entity_aliases`, `dbe_entity_membership`.
2. Upserts `dbe_metrics`, `dbe_metric_synonyms`, `dbe_sources`.
3. For each metric: extracts from DBnomics, transforms (entity resolution + typed
   dates + unit normalization), and **idempotently upserts** into `dbe_observations`.
4. Writes a `dbe_pipeline_runs` audit row.

Expected scale: 9 metrics, ~75,000 observations in under two minutes on this machine.

---

## Step 3 — Verify idempotency

Run Step 2 a second time. `dbe_observations` must not grow:

```bash
dotnet run --project src/EconData.Cli -- extract --metrics all --db
```

Row counts stay identical because every write is an `ON CONFLICT ... DO UPDATE` upsert.

---

## Step 4 — Semantic catalog (optional)

```bash
ollama serve
ollama pull nomic-embed-text
dotnet run --project src/EconData.Cli -- vectorize
```

Builds ~1,892 documents (metric + entity + entity_metric) and their vectors.
Re-running reports `embedded=0` once nothing changed.

---

## Step 5 — Ask the catalog (optional)

```bash
ollama pull qwen2.5:7b-instruct        # or set LLM_MODEL to an installed chat model
dotnet run --project src/EconData.Cli -- chat --question "What is the latest US GDP?"
```

Read-only: retrieval + LLM answer with `doc_key` citations. Non-zero exit and an
actionable message if the embedding or chat backend is unreachable.

---

## Step 6 — Medallion lake (optional staging tier)

The lake stages data through `output/lake/` (set `ECONDATA_LAKE_DIR` to relocate):

| Tier | Location | Format | Semantics |
| :--- | :--- | :--- | :--- |
| Bronze | `bronze/<provider>/<dataset>/<metric>/<run>.parquet` | Parquet | Append-only; one file per run per metric; raw series JSON |
| Silver | `silver/<metric>.parquet` | Parquet | Whole-partition overwrite; typed, deduped, unit-normalized |
| Gold | `gold/econdata.duckdb` | DuckDB | `dbe_*` tables + views; idempotent upserts from silver |

Bronze → Silver → Gold is optional and additive — SQLite (`--db`) remains the
default serving store for analytics and vectors.

```bash
# Build all three tiers in one pass
dotnet run --project src/EconData.Cli -- extract --metrics all --bronze --silver --gold

# Inspect lake health (counts, files, size, newest run)
dotnet run --project src/EconData.Cli -- lake status

# Rebuild gold from silver without touching the network
dotnet run --project src/EconData.Cli -- lake build-gold

# Doctor reports lake health as a warning (not a blocker) when absent
dotnet run --project src/EconData.Cli -- doctor
```

The Web UI mirrors this data: `/lake` shows tier state, `/api/lake/status` returns
the same JSON the CLI prints, and `/api/overview` falls back to gold when SQLite is
absent.

---

## Scheduling

Schedule the CLI directly. The pipeline is idempotent, so re-runs are safe.
**Always alert on a non-zero exit** — `extract` returns 1 when any metric fails
(status `PARTIAL`/`FAILED`) and 1 when no valid metric resolves.

### cron

```
0 4 * * * cd /opt/econdata && dotnet run --project src/EconData.Cli -c Release -- extract --metrics all --db >> output/logs/pipeline.cron.log 2>&1 || /usr/local/bin/econdata-alert
```

### systemd timer

`/etc/systemd/system/econdata-extract.service`:

```ini
[Unit]
Description=EconData extraction
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
WorkingDirectory=/opt/econdata
ExecStart=/usr/bin/env dotnet run --project src/EconData.Cli -c Release -- extract --metrics all --db
StandardOutput=append:/var/log/econdata/pipeline.log
StandardError=append:/var/log/econdata/pipeline.log
# systemd marks the unit failed on non-zero exit, which is what alerting should watch
OnFailure=econdata-alert@%n.service
```

`/etc/systemd/system/econdata-extract.timer`:

```ini
[Unit]
Description=Daily EconData extraction

[Timer]
OnCalendar=*-*-* 04:00:00
Persistent=true
RandomizedDelaySec=300

[Install]
WantedBy=timers.target
```

Enable with `systemctl daemon-reload && systemctl enable --now econdata-extract.timer`.
Use `systemctl list-timers econdata-extract.timer` to confirm, and
`journalctl -u econdata-extract.service` for history.

### Log rotation and retention

Logs go to `output/logs/` (Serilog) plus whatever the scheduler redirects to.
Rotate daily and keep 30 days:

`/etc/logrotate.d/econdata`:

```
/var/log/econdata/*.log /opt/econdata/output/logs/*.log {
    daily
    rotate 30
    compress
    delaycompress
    missingok
    notifempty
    copytruncate
}
```

With journald instead, retention is governed by `SystemMaxUse=` /
`journalctl --vacuum-time=30d`; no logrotate entry is needed.

Run history (including `SUCCESS`/`PARTIAL`/`FAILED`) is visible in the Web UI
`/runs` page and via `GET /api/runs`.

---

## Backups

The database is one file (plus `-wal`/`-shm` while running). Take a consistent
snapshot with one of these:

```bash
# Option A: VACUUM INTO — no external tool needed, safe while running
dotnet run --project src/EconData.Cli -c Release -- backup --to "backups/econdata-$(date +%F).db"

# Option B: if sqlite3 is installed
sqlite3 output/econdata.db ".backup 'backups/econdata-$(date +%F).db'"
```

> `sqlite3` is **not** installed by default on every host. Prefer Option A unless you
> have verified `sqlite3 --version` works on the target machine.

Also archive `config/metrics.json` and `config/entities.json` alongside the snapshot.

### Restore (verified procedure)

```bash
# 1. Stop the app / scheduler so nothing writes during the restore.
systemctl stop econdata-web econdata-extract.timer   # or your equivalents

# 2. Move the current files aside (never restore over a live WAL).
mv output/econdata.db output/econdata.db.broken
rm -f output/econdata.db-wal output/econdata.db-shm

# 3. Copy the snapshot into place.
cp backups/econdata-2026-10-22.db output/econdata.db

# 4. Verify integrity + schema on the restored copy.
dotnet run --project src/EconData.Cli -c Release -- check-db
dotnet run --project src/EconData.Cli -c Release -- doctor

# 5. Sample query: confirm rows survived.
dotnet run --project src/EconData.Web &
curl 'http://127.0.0.1:5000/api/overview'
```

A restore is only complete when `check-db` reports `integrity_check=ok` **and**
`/api/overview` returns non-zero observation counts.

---

## Recovery / reset

- **Schema**: `init-db` is additive/idempotent.
- **Repopulate data**: `extract --metrics all --db` re-extracts and upserts from DBnomics.
- **Full reset (dev only)**: stop the app, delete `output/econdata.db*`
  (`-wal`/`-shm` too), then re-run Steps 1–2.

---

## Troubleshooting

| Symptom | Cause / fix |
| :--- | :--- |
| `Database check failed: ...` | The file is missing/corrupt; run `init-db`, or restore a backup. |
| `Could not create the sqlite-vec table` | The native extension is missing; check `native/sqlite-vec/<rid>/` or set `ECONDATA_SQLITE_VEC_PATH`. |
| `Ollama is not reachable` | Run `ollama serve`; confirm `OLLAMA_HOST`. |
| `no /chat/completions route (HTTP 404)` | `LLM_BASE_URL` must point at the OpenAI-compatible root; a missing Ollama model also returns 404 — `ollama pull <LLM_MODEL>`. |
| `period` parse warnings in logs | Unparseable period literals are skipped (logged), never crash. |
| Unknown entity codes | The resolver registers them deterministically (no fabricated names); review startup logs and add an alias to `config/entities.json` if needed. |
