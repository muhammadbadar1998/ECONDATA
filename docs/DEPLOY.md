# EconData — Deployment Runbook

This is the operational guide to get the normalized pipeline live against a
private **Supabase PostgreSQL** database, then run it via the CLI (cron/manual).

Prerequisites (already done in the repo):
- Normalized schema `sql/dbe_postgres_schema.sql` (now includes `dbe_embeddings`)
- Config/registry/entity code fully built and integration-tested
- Unit + integration tests in `tests/`

> **Security rule that overrides everything:** this database is private. Only the
> owner's roles/IPs reach it. Never expose `dbe_*` via the public PostgREST/anon role.

---

## Step 0 — Rotate the compromised password (REQUIRED, do this first)

The previous Supabase password (the one committed to repo history in the pre-refactor
`config/SUPABASE.env.example`) is **compromised** and must be rotated before anything
is connected.

1. Open the **Supabase dashboard** -> your project -> **Settings -> Database** ->
   **Database password** -> **Reset database password**.
2. Generate a long random password. Save it only into `config/.env` (gitignored).
3. If this is a fresh project, note the connection values:
   - **Host**: `db.<project-ref>.supabase.co`
   - **Port**: `5432`
   - **Database**: `postgres`
   - **User**: `postgres`

---

## Step 1 — Configure `config/.env`

`config/.env` is gitignored (verified). Create it and fill in the rotated values:

    cp config/.env.example config/.env

Edit `config/.env`:

    SUPABASE_HOST=db.xxxxxxxxxxxxxxxx.supabase.co
    SUPABASE_DB=postgres
    SUPABASE_USER=postgres
    SUPABASE_PASSWORD=<THE ROTATED PASSWORD>
    SUPABASE_PORT=5432

> Do **not** put the port in `SUPABASE_HOST` — host and port are kept separate;
> the connection string is built in `src/config.py`.

---

## Step 2 — Harden Supabase (console, no code)

In the Supabase dashboard:

1. **Disable the `anon` role for `dbe_*`**:
   The pipeline uses the **postgres/owner role** only. The `anon`/public key
   must not read or write `dbe_*`.
2. **IP allowlist** (or a private tunnel/VPN):
   **Settings -> Database -> Connection pooling / Network restrictions** -> restrict
   to the owner's IPs. Do **not** use `0.0.0.0/0`.
3. Confirm Table Editor / PostgREST is not letting `anon` read your tables.

You can run this (per `dbe_*` table/view) in the **Supabase SQL Editor**:

    revoke all on table public.dbe_observations from anon, authenticated;
    revoke all on table public.dbe_entities   from anon, authenticated;
    -- repeat for each dbe_* table and view ...

> `postgres` (owner) and `service_role` keep full access — that's what the pipeline
> and the future text-to-SQL backend use.

---

## Step 3 — Install dependencies

    pip install -r requirements.txt

---

## Step 4 — Verify connectivity first (optional smoke test)

Once `config/.env` has the rotated password, confirm the connection before the
full run (no schema changes, just `SELECT version()`):

    python -m src.main --check-db

- Exit code `0` + `Connection OK. Server: ...` means credentials are valid.
- Exit code `1` + `Connection FAILED` means wrong password / IP not allowlisted /
  host unreachable — fix those before proceeding.

---
## Step 5 — Create the schema (idempotent)

    python -m src.main --init-db

Executes `sql/dbe_postgres_schema.sql` (tables, enums, views). Uses
`IF NOT EXISTS` / `CREATE OR REPLACE`, safe to re-run.

---

## Step 6 — Seed registry + full extraction (postgres role only)

    # Everything (all 9 configured metrics): DBnomics extraction + upsert to Supabase
    python -m src.main --metrics all --db

This:
1. Seeds `dbe_entities`, `dbe_entity_aliases`, `dbe_entity_membership`.
2. Upserts `dbe_metrics`, `dbe_metric_synonyms`, `dbe_sources`.
3. For each metric: extracts from DBnomics, transforms (entity resolution + typed
   dates + unit normalization), and **idempotently upserts** into `dbe_observations`.
4. Writes an `dbe_pipeline_runs` audit row.

Sanity-check afterward (Supabase SQL editor):

    select m.metric_code, count(*) from dbe_observations o
    join dbe_metrics m on m.metric_id = o.metric_id
    group by m.metric_code order by 1;

    select country_code, metric_code, value from v_latest_metrics_snapshot
    where country_code in ('USA','DEU') and metric_code='gdp_usd'

Re-running is safe — it upserts, never duplicates.

---

## Step 7 — Verify idempotency + privacy

1. Run `--metrics all --db` twice; the second run must not increase row counts.
2. From a different network/IP (or a browser without the Supabase session), confirm
   the `anon` key **cannot** list `dbe_observations`.

---

## Step 8 — Schedule the Pipeline (cron / manual)

Airflow has been removed. Schedule the CLI directly, e.g. via cron:

    0 4 * * * /opt/econdata/.venv/bin/python -m src.main --metrics all --db >> /var/log/econdata/pipeline.log 2>&1

- Runs the idempotent full extraction + upsert once daily at 04:00.
- Re-runs are safe (idempotent upserts, no duplicates).
- For one-off runs, just execute the same command manually:

---

## Step 9 — (Optional) Semantic Catalog — Embeddings

Enable the pgvector extension in Supabase (`CREATE EXTENSION vector;` is already
in the DDL), then run:

    python -m src.main --vectorize      # build & persist catalog/series embeddings
    python -m src.main --reembed        # force recompute of all vectors

Requires Ollama: `ollama serve` + `ollama pull nomic-embed-text`.

## Step 10 — CSV Backups

Local CSV export/import helpers were removed with the storage module. The
normalized flow is Postgres-first; use `COPY`/`pg_dump` for backups.

---

## Rollback / recovery

- **Schema**: `python -m src.main --init-db` (additive / idempotent).
- **Data repopulation**: `--metrics all --db` re-extracts and upserts from DBnomics.
- **Full reset (dev only)**: drop the `public` schema and re-run Steps 5-6. Never do
  this on production without a backup.

---

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `ValueError: Supabase connection credentials not fully set` | `config/.env` missing or `SUPABASE_HOST`/`PASSWORD` unset. Do Step 1. |
| `Only PostgreSQL/Supabase connections are supported` | Stale SQL Server string passed. Use env-based config or a `postgresql+psycopg2://` string. |
| Supabase statement timeout on big loads | Loader chunks at `page_size=500`; lower it in `src/db_loader.py` if needed. |
| `period` parse warnings in logs | Unparseable period literals are skipped (logged), never crash. |
| Unknown entity codes | Resolver registers them deterministically (no fabricated names); review startup logs. |




