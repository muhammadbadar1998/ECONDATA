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

## Scheduling

Schedule the CLI directly (cron, Task Scheduler, systemd timer). Example daily at 04:00:

```
0 4 * * * cd /opt/econdata && dotnet run --project src/EconData.Cli -c Release -- extract --metrics all --db >> output/pipeline.cron.log 2>&1
```

Re-runs are safe (idempotent upserts). Logs are also written to `output/logs/`.

---

## Backups

The database is one file. Back it up with a consistent snapshot:

```bash
# SQLite-aware online backup (safe while the app is running)
sqlite3 output/econdata.db ".backup 'backups/econdata-$(date +%F).db'"
```

Also archive `config/metrics.json` and `config/entities.json` alongside it.

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
