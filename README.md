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

## Web UI

The same stack also ships a localhost-only Blazor Server UI (single user, no auth):

```bash
dotnet run --project src/EconData.Web
# http://127.0.0.1:5000
```

| Page | Purpose |
| :--- | :--- |
| `/` | Overview tiles (row counts) and the latest pipeline run |
| `/runner` | Run Check / Init / Extract / Vectorize / Run All with live progress, log tail, cancel, and run history |
| `/data` | Entity/metric series explorer plus the latest snapshot |
| `/metrics` | Metric coverage: entities, observations, year range |
| `/entities` | Searchable entity registry with alias/observation counts |
| `/runs` | Pipeline run audit history |
| `/builder` | Drag/resize dashboard of ECharts widgets with a saved layout |

Job execution is single-concurrency: one pipeline job at a time, with progress and
the in-memory log tail served by polling (`GET /api/jobs/current`,
`GET /api/jobs/{id}/log?since=N`). Cancelling a job is cooperative.

The builder has four predefined widget types (series line, coverage/snapshot/runs
bars). Layouts persist to `output/dashboards.json` and are restored on reload or
fall back to defaults if the file is missing or corrupt. Browser assets
(Bootstrap, ECharts, GridStack) are vendored under
`src/EconData.Web/wwwroot/vendor/` so the UI works offline.

### HTTP API

| Method & path | Returns |
| :--- | :--- |
| `GET /api/overview` | Row counts and last-run status |
| `GET /api/coverage` | Per-metric coverage summary |
| `GET /api/series?entity=USA&metric=gdp_usd` | Time-series points |
| `GET /api/snapshot?entity=&metric=&category=&limit=` | Latest values |
| `GET /api/entities?q=&type=&limit=` | Entity summaries |
| `GET /api/runs?limit=` | Pipeline run history |
| `GET /api/catalog/metrics` | Metric catalog (works before DB init) |
| `POST /api/jobs` | Start a job (`202`), or `409` when one is running |
| `GET /api/jobs/current` | Current/last job status |
| `POST /api/jobs/{id}/cancel` | Request cancellation |
| `GET /api/jobs/{id}/log?since=N` | In-memory log tail |
| `GET /api/dashboard/layout` | Saved dashboard layout (or defaults) |
| `PUT /api/dashboard/layout` | Validate and persist a layout |

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
dotnet test EconData.slnx                    # 135 xUnit unit, integration, and Web API tests
dotnet format EconData.slnx --verify-no-changes
```

The integration tests run hermetically against a temporary SQLite file (including
a real `sqlite-vec` kNN round-trip) — no Docker, no server, no network. Web tests
use `WebApplicationFactory<Program>` with temporary databases and, for jobs, a fake
`IJobExecutor`.

---

## Operations

See [`docs/DEV_GUIDE.md`](docs/DEV_GUIDE.md) for architecture, development workflow,
and troubleshooting. Pipeline operations use the CLI commands in
[Quick start](#quick-start) or the `/runner` page.
