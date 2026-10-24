# EconData

A **C# / .NET 10** pipeline for macroeconomic data: it pulls indicators from
[DBnomics](https://db.nomics.world/), resolves entities and units, and loads them
idempotently into a **local SQLite** database or a **DuckDB gold tier** via an
optional bronze → silver → gold lake. On top of that it provides a vector catalog
for retrieval-augmented (RAG) questions, analytics endpoints, a CLI, and a
localhost Blazor dashboard with a pipeline runner.

No database server, Docker, or credentials are required — everything lives under
`output/` (SQLite for the vector store, DuckDB for gold) with a vendored
`sqlite-vec` extension.

## Features

- **ETL**: 26 macro indicators across ~275 countries/aggregates, typed dates,
  unit normalization, and bulk UPSERTs.
- **Medallion lake** (optional): append-only **bronze** Parquet, typed **silver**
  partitions, and a **DuckDB gold** serving database with idempotent upserts,
  derived aggregates, and run auditing.
- **Vector catalog**: embeds metric/entity/series summaries into `sqlite-vec`
  (768-dim cosine) and answers questions from those documents.
- **Chat**: tool-first answers for objective questions (fixed parameterized
  queries over gold/SQLite) with a retrieval path for explanatory questions.
- **Analytics API**: growth (YoY/CAGR), rankings, correlations, and data freshness.
- **CLI**: `init-db`, `check-db`, `backup`, `doctor`, `extract`, `lake`, `vectorize`, `chat`.
- **Web UI**: Overview, Runner with live progress/logs/cancel, Data, Metrics,
  Entities, Lake, Runs, and a drag/resize dashboard builder (ECharts + GridStack).
- **Hermetic tests**: temporary SQLite and DuckDB databases, including real
  `sqlite-vec` kNN and gold-backed analytics tests.

## How this was built (AI-first)

This project was developed AI-first: planning, implementation, verification, and
issue tracking were driven by an orchestrated set of AI agents rather than one long
chat session. The workflow was deliberately cost-aware, instruction-driven, and
**verification-heavy**.

- **Tiered model orchestration.** Model capability was matched to task complexity.
  Mechanical work (scaffolding, docs, renames, config entries) ran on fast, cheap
  models; standard implementation stayed on the default tier; the hardest reasoning
  — schema design, SQLite/`sqlite-vec`/DuckDB integration, Blazor concurrency, and
  failures without a reproduction — was escalated deliberately and briefly, with the
  resulting design handed back down for execution.
- **Persistent, committed working documents.** `.kilo/plans/` holds the phase plans,
  and `.kilo/work/{PLAN,PROGRESS,ISSUES}.md` track the active plan, execution state,
  and open issues. They are part of the repo, so any session — or any agent — can
  resume from the documents instead of re-reading the codebase.
- **Instruction-driven six-phase loop** for every change:
  1. **Plan** — requirements, architecture, test strategy, target files, Definition
     of Done, and UAT; pause for explicit approval.
  2. **Test first** — write tests and executable UAT assertions before code.
  3. **Implement** — the minimum production code needed to make the tests pass.
  4. **Shrink** — re-read the diff and remove complexity and footprint.
  5. **Verify** — build, test, format, and UAT for every touched project; fix root
     causes rather than weakening checks.
  6. **Commit & push** — conventional commit, push, and summarize what changed, UAT
     results, and the commit SHA.
- **Verification is adversarial.** Reports are not taken at face value. Claims are
  re-checked by independent builds and test runs, and new tests are **mutation
  tested** — for example, the lake-status parity test was proven by breaking the HTTP
  endpoint and confirming the test fails, and the config guard was proven by removing
  `/v1` from the LLM base URL. Bugs found this way are fixed with a regression test
  in the same change.
- **Engine-parity testing.** A feature is not "done" when it passes on one store.
  Analytics SQL is exercised against **both SQLite and DuckDB gold**, which is how two
  real defects were caught: `strftime` on VARCHAR dates and a duplicated bound
  parameter in the DuckDB adapter.
- **Prompt caching.** Stable project context (instructions, plan, conventions) was
  kept first and referenced by path, so long sessions stayed cheap and consistent.

The initial build — planning, implementation, tests, and documentation — cost
**under $4** for **207.5M tokens** across **2,401 requests**.

## Requirements

- **.NET SDK 10** (`10.0.4xx`).
- **Ollama** (only for `vectorize` and `chat`):
  ```bash
  ollama serve
  ollama pull nomic-embed-text
  ollama pull qwen2.5:7b-instruct   # any chat-capable model works
  ```

## Quick start

```bash
dotnet build EconData.slnx

# Create the schema (SQLite + vector table)
dotnet run --project src/EconData.Cli -- init-db

# Extract all metrics and load them into SQLite
dotnet run --project src/EconData.Cli -- extract --metrics all --db

# Embed the catalog (requires Ollama)
dotnet run --project src/EconData.Cli -- vectorize

# Ask a question grounded in the catalog
dotnet run --project src/EconData.Cli -- chat --question "What is the latest US GDP?"
```

Common options: `--config PATH`, `--db-path PATH`, `--entities PATH`,
`--vec PATH`, `--output-dir DIR`. `extract` also accepts
`--metrics all|gdp_usd,population`, `--db`, and the lake tier flags
`--bronze --silver --gold`; `vectorize` accepts `--reembed`;
`chat` accepts `--k N --show-sources --system FILE`.

### Medallion lake

Data can be staged through a medallion layout instead of a single SQLite load:

```bash
# Create the gold schema (idempotent)
dotnet run --project src/EconData.Cli -- lake init

# All three tiers, one pass
dotnet run --project src/EconData.Cli -- extract --metrics all --bronze --silver --gold

# Inspect and rebuild the serving tier without the network
dotnet run --project src/EconData.Cli -- lake status
dotnet run --project src/EconData.Cli -- lake build-gold --rebuild

# Backfill gold from an existing SQLite database (no re-extraction)
dotnet run --project src/EconData.Cli -- lake import-from-sqlite --dry-run
dotnet run --project src/EconData.Cli -- lake import-from-sqlite
```

| Tier | Location | Format | Refresh |
| :--- | :--- | :--- | :--- |
| Bronze | `output/lake/bronze/<provider>/<dataset>/<metric>/` | Parquet, raw series | Append-only, one file per run |
| Silver | `output/lake/silver/<metric>.parquet` | Parquet, typed observations | Partition overwritten per run |
| Gold | `output/lake/gold/econdata.duckdb` | DuckDB `dbe_*` tables + views | Idempotent upsert from silver |

A run with no tier flags keeps the original behaviour and writes only SQLite.

**What reads from where.** `ECONDATA_SERVING_STORE` controls where *structured
analytics* read from:

| Value | Behaviour |
| :--- | :--- |
| `auto` (default) | Gold when the file exists, otherwise SQLite |
| `gold` | Gold only; startup fails with an actionable message if it is missing |
| `sqlite` | SQLite only, even when gold exists (rollback escape hatch) |

SQLite is still required: the `sqlite-vec` index lives there, so `vectorize` and
`chat` always use it regardless of serving mode. `ECONDATA_LAKE_DIR` relocates the
lake.

## Web UI

```bash
dotnet run --project src/EconData.Web
# http://127.0.0.1:5000
```

| Page | What it does |
| :--- | :--- |
| `/` | Row counts and the latest pipeline run |
| `/runner` | Check / Init / Extract / Vectorize / Run All, live progress, log tail, cancel |
| `/data` | Series explorer and latest snapshot |
| `/metrics` | Per-metric coverage and year ranges |
| `/entities` | Searchable entity registry |
| `/runs` | Pipeline run history, including which tiers each run wrote |
| `/lake` | Bronze/silver/gold state: rows, files, size, newest, gold tables |
| `/builder` | Drag/resize ECharts widgets; layout saved to `output/dashboards.json` |

The app is localhost-only, single-user, and runs one pipeline job at a time.
Browser assets (Bootstrap, ECharts, GridStack) are vendored for offline use.

### Analytics API

| Endpoint | Returns |
| :--- | :--- |
| `GET /api/analytics/growth?metric=&entity=&kind=yoy\|cagr` | Period, value, and growth % |
| `GET /api/analytics/ranking?metric=&period=&direction=desc\|asc` | Ranked entities with values |
| `GET /api/analytics/correlation?metricA=&metricB=&period=` | Pearson `r` over aligned entities |
| `GET /api/analytics/freshness` | Latest period, row count, and lag per metric |

The same data also backs `/api/overview`, `/api/coverage`, `/api/series`,
`/api/snapshot`, `/api/entities`, `/api/runs`, and `/api/lake/status`.

## Configuration

Settings come from `config/appsettings.json`, then environment variables (which
win), with safe defaults. Main variables:

| Variable | Default |
| :--- | :--- |
| `ECONDATA_SQLITE_PATH` | `./output/econdata.db` |
| `OUTPUT_DIR` | `./output` |
| `ECONDATA_LAKE_DIR` | `./output/lake` |
| `ECONDATA_SERVING_STORE` | `auto` (`auto\|gold\|sqlite`) |
| `CONFIG_PATH` | `./config/metrics.json` |
| `ECONDATA_ENTITIES_PATH` | `./config/entities.json` |
| `DBNOMICS_BASE_URL` | `https://api.db.nomics.world/v22` |
| `ECONDATA_SQLITE_VEC_PATH` | `native/sqlite-vec/<rid>/vec0.{dll,so,dylib}` |
| `OLLAMA_HOST` | `http://127.0.0.1:11434` |
| `OLLAMA_EMBEDDING_MODEL` | `nomic-embed-text` |
| `LLM_BASE_URL` | `http://127.0.0.1:11434/v1` |
| `LLM_MODEL` | `qwen2.5:7b-instruct` |
| `LOG_LEVEL` | `INFO` |

## Project layout

```
src/EconData.Core   ETL, SQLite, DuckDB lake tiers, analytics, vector catalog, pipeline runner
src/EconData.Cli    Command-line entry point
src/EconData.Web    Blazor Server UI + Minimal API
tests/              Core integration tests + Web API/job/builder tests
config/             Metrics and entity seed data
sql/sqlite/         SQLite schema DDL
sql/duckdb/         DuckDB gold schema DDL
native/sqlite-vec/  Vendored vector extension
docs/               Developer guide and deploy notes
```

## Tests

```bash
dotnet test EconData.slnx                    # 260 tests
dotnet format EconData.slnx --verify-no-changes
```

Tests are hermetic (temporary SQLite and DuckDB files, fake services). The vectorizer
test uses the real `sqlite-vec` extension, and the analytics tests run against both
SQLite and DuckDB. No network or Ollama is needed.

## Documentation

- [`docs/DEV_GUIDE.md`](docs/DEV_GUIDE.md) — architecture, Web services, builder,
  configuration details, troubleshooting.
- [`docs/DEPLOY.md`](docs/DEPLOY.md) — operational runbook.
- [`EXPLAINER.md`](EXPLAINER.md) — architecture and codebase explainer.
- [`CHANGELOG.md`](CHANGELOG.md) — release history.

## Derived aggregates

Metrics opt in via an `"aggregation": "sum" | "mean"` key in `config/metrics.json` (default `"none"`). For each aggregate entity in `config/entities.json` memberships, the pipeline computes rows from member observations when coverage is at least 50% of members with data. Derived rows live in `dbe_derived_observations` and surface through `v_observations_all` with `source_kind = "derived"`; raw rows keep `source_kind = "raw"`. Analytics endpoints expose `source_kind` on series/snapshot points and separate derived counts in coverage.
