# EconData

A **C# / .NET 10** pipeline for macroeconomic data: it pulls indicators from
[DBnomics](https://db.nomics.world/), resolves entities and units, and loads them
idempotently into a **local SQLite** database. On top of that it provides an
optional vector catalog for retrieval-augmented (RAG) questions, a CLI, and a
localhost Blazor dashboard with a pipeline runner.

No database server, Docker, or credentials are required — everything lives in one
SQLite file (`output/econdata.db`) plus a vendored `sqlite-vec` extension.

## Features

- **ETL**: 9 macro indicators across ~275 countries/aggregates (75k+ observations),
  typed dates, unit normalization, and bulk UPSERTs.
- **Vector catalog**: embeds metric/entity/series summaries into `sqlite-vec`
  (768-dim cosine) and answers questions only from those documents.
- **CLI**: `init-db`, `check-db`, `extract`, `vectorize`, `chat`.
- **Web UI**: Overview, Runner with live progress/logs/cancel, Data, Metrics,
  Entities, Runs, and a drag/resize dashboard builder (ECharts + GridStack).
- **Hermetic tests**: temporary SQLite databases, including a real `sqlite-vec`
  kNN round-trip.

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
`--metrics all|gdp_usd,population` and `--db`; `vectorize` accepts `--reembed`;
`chat` accepts `--k N --show-sources --system FILE`.

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
| `/runs` | Pipeline run history |
| `/builder` | Drag/resize ECharts widgets; layout saved to `output/dashboards.json` |

The app is localhost-only, single-user, and runs one pipeline job at a time.
Browser assets (Bootstrap, ECharts, GridStack) are vendored for offline use.

## Configuration

Settings come from `config/appsettings.json`, then environment variables (which
win), with safe defaults. Main variables:

| Variable | Default |
| :--- | :--- |
| `ECONDATA_SQLITE_PATH` | `./output/econdata.db` |
| `OUTPUT_DIR` | `./output` |
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
src/EconData.Core   ETL, SQLite, analytics, vector catalog, pipeline runner
src/EconData.Cli    Command-line entry point
src/EconData.Web    Blazor Server UI + Minimal API
tests/              Core integration tests + Web API/job/builder tests
config/             Metrics and entity seed data
sql/sqlite/         Schema DDL
native/sqlite-vec/  Vendored vector extension
docs/               Developer guide and deploy notes
```

## Tests

```bash
dotnet test EconData.slnx                    # 135 tests
dotnet format EconData.slnx --verify-no-changes
```

Tests are hermetic (temporary SQLite files, fake services). The vectorizer test
uses the real `sqlite-vec` extension; no network or Ollama is needed.

## Documentation

- [`docs/DEV_GUIDE.md`](docs/DEV_GUIDE.md) — architecture, Web services, builder,
  configuration details, troubleshooting.
- [`docs/DEPLOY.md`](docs/DEPLOY.md) — operational runbook.
- [`EXPLAINER.md`](EXPLAINER.md) — metric/entity field reference and examples.
- [`CHANGELOG.md`](CHANGELOG.md) — release history.
