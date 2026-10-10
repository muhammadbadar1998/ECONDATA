# EconData — .NET Economic Data Pipeline (SQLite)

A **C# / .NET 10** pipeline that extracts high-level macroeconomic indicators for
all available countries from [DBnomics](https://db.nomics.world/), normalizes them
into a **typed, extensible schema**, and performs idempotent bulk UPSERTs into a
**local SQLite** database (`dbe_*` tables). An optional semantic layer embeds a
catalog of metric/entity/series summaries and serves **vector RAG** answers.

> Migrated from the former Python + Supabase/PostgreSQL implementation. There is no
> server, no credential, and no `pip`/`pytest`/`ruff` dependency anymore.

---

## Requirements

- **.NET SDK 10** (`dotnet --list-sdks` shows `10.0.4xx`).
- **Ollama** (only for the semantic catalog + chat): `ollama serve`, then
  `ollama pull nomic-embed-text` and a chat model (e.g. `ollama pull qwen2.5:7b-instruct`).

No database server is required — the store is a single SQLite file (`output/econdata.db`)
plus a vendored `sqlite-vec` native extension (`native/sqlite-vec/`).

---

## Quick start

```bash
dotnet build EconData.slnx

# Create the SQLite dbe_* schema (and the sqlite-vec vector table)
dotnet run --project src/EconData.Cli -- init-db

# Full extraction + transform + idempotent load for all metrics
dotnet run --project src/EconData.Cli -- extract --metrics all --db

# Build/refresh the semantic catalog (needs Ollama)
dotnet run --project src/EconData.Cli -- vectorize

# Ask the catalog a question (read-only vector RAG)
dotnet run --project src/EconData.Cli -- chat --question "What is the latest US GDP?"
```

Common options: `--config PATH --db-path PATH --entities PATH --vec PATH --output-dir DIR`.
`extract` also accepts `--metrics all|gdp_usd,population` and `--db`;
`vectorize` accepts `--reembed`; `chat` accepts `--k N --show-sources --system FILE`.

---

## Database schema (`dbe_*`, SQLite)

| Table | Description |
| :--- | :--- |
| `dbe_entities` | Canonical entity registry (ISO-3166 alpha-3 countries, aggregates, regions) with hierarchy |
| `dbe_entity_aliases` | `(provider, provider_code) → entity` mapping resolving cross-provider collisions |
| `dbe_metrics` | Indicator metadata registry (frequency, `base_unit`, `unit_normalization`, aggregation) |
| `dbe_metric_synonyms` | Keyword synonyms for retrieval |
| `dbe_sources` | Provider/dataset/indicator registration |
| `dbe_entity_membership` | Aggregate → member entity relationships |
| `dbe_observations` | Typed time-series core `(metric_id, entity_id, source_id, start_date, end_date, val, raw_val, unit, period_str)` |
| `dbe_pipeline_runs` | Execution audit log |
| `dbe_embeddings` | Document metadata for the semantic catalog |
| `dbe_embeddings_vec` | sqlite-vec `vec0` table (768-dim cosine) |

Views: `v_latest_metrics_snapshot`, `v_dated_execution_snapshot`. DDL lives in
[`sql/sqlite/schema.sql`](sql/sqlite/schema.sql).

---

## Extending the pipeline

All metrics are defined in `config/metrics.json`; canonical entities and aliases
live in `config/entities.json` (exported once from the former Python seed). See
[`EXPLAINER.md`](EXPLAINER.md) for the full field reference and examples
(historical indicators, long-term forecasts, unit normalization).

---

## Tests & formatting

```bash
dotnet test EconData.slnx                    # xUnit unit + integration tests
dotnet format EconData.slnx --verify-no-changes
```

The integration tests run hermetically against a temporary SQLite file (including
a real `sqlite-vec` kNN round-trip) — no Docker, no server, no network.

---

## Operations

See [`docs/DEV_GUIDE.md`](docs/DEV_GUIDE.md) for development and [`docs/DEPLOY.md`](docs/DEPLOY.md)
for the runbook (scheduling, backups, recovery).