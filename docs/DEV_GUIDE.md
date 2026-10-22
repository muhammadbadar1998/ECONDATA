# Developer Guide

## Project posture

`ECONDATA` is a **C# / .NET 10** ETL + vector-RAG service. The pipeline extracts
normalized macro-economic series from the DBnomics REST API, transforms them
(entity resolution, typed dates, unit normalization, dedupe), and performs
idempotent bulk UPSERTs into a **local SQLite** database (`dbe_*` schema). An
optional semantic layer embeds metric/entity/series summaries and answers
questions **from those documents only** (read-only).

The former Python implementation and the Supabase/PostgreSQL backend have been
removed. The SQLite database is a single file; there are no server credentials.

## Prerequisites

- **.NET SDK 10** (`global.json` pins `10.0.4xx`).
- **Ollama** (only for `vectorize` + `chat`):
  ```bash
  ollama serve
  ollama pull nomic-embed-text        # 768-dim embeddings
  ollama pull qwen2.5:7b-instruct     # chat model (any chat-capable model works)
  ```

## Setup

```bash
dotnet build EconData.slnx
dotnet run --project src/EconData.Cli -- init-db
```

Paths default to the repository root (resolved by walking up to `EconData.slnx`).
Override with `--config`, `--db-path`, `--entities`, `--vec`, `--output-dir`.

## Common tasks

```bash
# Verify the SQLite schema (integrity + dbe_* table count)
dotnet run --project src/EconData.Cli -- check-db

# Full ETL for all metrics into SQLite
dotnet run --project src/EconData.Cli -- extract --metrics all --db

# A subset of metrics
dotnet run --project src/EconData.Cli -- extract --metrics gdp_usd,population --db

# Embed catalog + series summaries into dbe_embeddings (sqlite-vec cosine)
dotnet run --project src/EconData.Cli -- vectorize

# Force re-embedding every document
dotnet run --project src/EconData.Cli -- vectorize --reembed

# Ask the catalog a question
dotnet run --project src/EconData.Cli -- chat --question "What is the latest US GDP?"
dotnet run --project src/EconData.Cli -- chat --k 5 --show-sources

# Tests + formatting
dotnet test EconData.slnx
dotnet format EconData.slnx --verify-no-changes
```

## Web app (`src/EconData.Web`)

Blazor Server + Minimal API, localhost only:

```bash
dotnet run --project src/EconData.Web    # http://127.0.0.1:5000
```

| Project | Responsibility |
| :--- | :--- |
| `src/EconData.Core` | Config, extraction/transform/load, analytics, vector catalog, `PipelineRunner`, Serilog |
| `src/EconData.Cli` | `init-db`, `check-db`, `extract`, `vectorize`, `chat` |
| `src/EconData.Web` | Pages (`/`, `/runner`, `/data`, `/metrics`, `/entities`, `/runs`, `/builder`) + `/api/*` |
| `tests/EconData.Tests` | Core unit + hermetic SQLite integration tests |
| `tests/EconData.Web.Tests` | `WebApplicationFactory<Program>` API tests, `JobService` tests, layout store tests |

`Directory.Build.props` enables nullable, analyzers, and
`TreatWarningsAsErrors=true`; new `.cs` files need a trailing newline and a clean
`dotnet format` run.

### Web DI and services

Singletons: `AppConfig`, `SqliteConnectionFactory`, `MetricsCatalog`, `EntitySeed`,
`AnalyticsService`, `InMemoryLogSink`, `ISeriesSource` (`DBnomicsExtractor`),
`IEmbedder` (`OllamaEmbedder`), `VectorStore`, `SchemaInitializer`,
`IJobExecutor` (`PipelineJobExecutor`), `JobService`, and `DashboardLayoutStore`.

`ISeriesSource` and `IEmbedder` each get their own `HttpClient` because both mutate
timeouts/headers; the scoped UI `HttpClient` (based on `NavigationManager.BaseUri`)
must not be reused by singletons.

### Jobs

- `IJobExecutor.ExecuteAsync(JobRequest, IProgress<JobProgressUpdate>, CancellationToken)`
  is the test seam (`FakeJobExecutor` in Web tests).
- `JobService` enforces single concurrency: `TryStart` returns `false` while a job
  is running. State is guarded by one lock; `Snapshot()` returns deep copies.
- `PipelineJobExecutor` runs `Check`, `Init`, `Extract`, `Vectorize`, and `RunAll`
  (init → extract all with DB load → vectorize), mapping
  `PipelineProgress`/`VectorizeProgress` into `JobProgressUpdate` and per-metric
  step lines. Cancellation is cooperative (`Cancel(jobId)` → linked CTS).
- `InMemoryLogSink` keeps the last 500 Serilog entries with a monotonic sequence;
  `/runner` polls `/api/jobs/current` and `/api/jobs/{id}/log?since=N`.
- `PipelineProgress`/`VectorizeProgress` hooks are optional parameters placed
  before `CancellationToken`; keep existing positional callers working.

### Dashboard builder

Layouts persist to `output/dashboards.json`:

```json
{
  "version": 1,
  "widgets": [
    {
      "id": "w_series", "type": "series",
      "title": "USA - GDP (current US$)",
      "x": 0, "y": 0, "w": 6, "h": 3,
      "config": { "entity": "USA", "metric": "gdp_usd", "category": null }
    }
  ]
}
```

- Types: `series`, `coverage`, `snapshot`, `runs`; grid is 12 columns.
- Validation: `x` 0–11, `y` 0–1000, `w`/`h` 1–12, ≤ 24 widgets, unique ids
  (≤ 64 chars), title ≤ 120, config strings ≤ 64.
- `DashboardLayoutStore.Load()` falls back to the four-widget default on missing,
  corrupt, or invalid files. `SaveAsync` validates and writes atomically.
- Endpoints: `GET|PUT /api/dashboard/layout`. See README for the full API table.
- `wwwroot/js/dashboard.js` (ES module) owns GridStack/ECharts instances:
  `initGrid`, `renderCharts`, `resizeCharts`, `disposeChart`, `dispose`.
  Drag/resize is debounced into `[JSInvokable] OnLayoutChanged`; Blazor owns
  widget state and debounce-saves the layout. Re-init is keyed to a structural
  version counter, not every render.
- Browser assets are vendored and tracked under `wwwroot/vendor/`:
  Bootstrap 5.x, ECharts 6.1.0 (`echarts.min.js`), GridStack 14.0.0
  (`gridstack-all.js` + `gridstack.min.css`) with licenses and
  `vendor/README.md`. Refresh by re-downloading the pinned versions; never use a
  CDN. (The old `wwwroot/lib/` tree is ignored by `.gitignore`, which is why the
  assets live under `vendor/`.)

### Web troubleshooting

| Symptom | Fix |
| :--- | :--- |
| UI unstyled / charts missing | Verify `wwwroot/vendor/**` exists and `App.razor` uses the `vendor/` paths |
| Dashboard resets to defaults | Check write access to `output/`; invalid/corrupt JSON intentionally falls back |
| Job start returns `409` | Another job is running; wait or cancel it on `/runner` |
| No live logs | Confirm the Serilog sink is registered before the logger in `Program.cs` |
| Web tests hang on a job | Use `FakeJobExecutor` and always release/cancel blocked runs |

The integration tests are hermetic: they run against a temporary SQLite file,
including a real `sqlite-vec` kNN round-trip. `LiveChatIntegrationTests` (if run)
additionally needs Ollama; the offline unit tests use a deterministic fake embedder.

## Configuration

Settings come from `config/appsettings.json`, then environment variables (which
win), with compiled defaults. None are secrets — the store is a local file.

| Variable | Default | Meaning |
| :--- | :--- | :--- |
| `ECONDATA_SQLITE_PATH` | `./output/econdata.db` | SQLite database file |
| `OUTPUT_DIR` | `./output` | Log directory |
| `ECONDATA_SQLITE_VEC_PATH` | `./native/sqlite-vec/<rid>/vec0.{dll,so,dylib}` | sqlite-vec extension |
| `CONFIG_PATH` | `./config/metrics.json` | Metric catalog |
| `ECONDATA_ENTITIES_PATH` | `./config/entities.json` | Entity/alias/membership seed |
| `DBNOMICS_BASE_URL` | `https://api.db.nomics.world/v22` | Source API |
| `OLLAMA_HOST` | `http://127.0.0.1:11434` | Embedding service base URL |
| `OLLAMA_EMBEDDING_MODEL` | `nomic-embed-text` | Embedding model (768-dim) |
| `OLLAMA_TIMEOUT` | `60` | Per-request timeout for *embedding* calls |
| `OLLAMA_EMBEDDING_BATCH_SIZE` | `32` | Texts per embedding request |
| `LLM_BASE_URL` | `http://127.0.0.1:11434/v1` | OpenAI-compatible base URL |
| `LLM_API_KEY` | *(empty)* | Bearer token; header omitted when empty |
| `LLM_MODEL` | `qwen2.5:7b-instruct` | Chat model (must be chat-capable) |
| `LLM_TIMEOUT` | `120` | Timeout for one *chat completion* |
| `LLM_TEMPERATURE` | `0.2` | Lower = more factual |
| `RAG_TOP_K` | `8` | Documents retrieved per question |
| `RAG_RECENT_POINTS` | `5` | Trailing observations per `entity_metric` document |
| `LOG_LEVEL` | `INFO` | Serilog minimum level |

`LLM_TIMEOUT` is deliberately separate from `OLLAMA_TIMEOUT`: chat completions
generate tokens and are far slower than embedding calls.

## Logging

Serilog writes to `output/logs/pipeline_<date>.log` and a size-bounded rolling
`output/pipeline.log` (5 MB × 5); chat mirrors this to `chat_<date>.log` /
`chat.log` with the console on **stderr** (so piped answers stay on stdout).

## Chatbot (vector RAG)

A read-only CLI assistant: it embeds the question, retrieves the top-k documents
from `dbe_embeddings`, and asks an OpenAI-compatible chat model to answer **from
those documents only**, citing the `doc_key`s used. No text-to-SQL, no writes.

### Prerequisites
1. The database is populated and vectorized:
   ```bash
   dotnet run --project src/EconData.Cli -- extract --metrics all --db
   dotnet run --project src/EconData.Cli -- vectorize
   ```
2. A **chat-capable** model is available on the LLM endpoint. For local Ollama,
   embeddings and chat are separate models (`nomic-embed-text` + a chat model).

### Commands
```bash
dotnet run --project src/EconData.Cli -- chat                                   # REPL
dotnet run --project src/EconData.Cli -- chat --question "latest US GDP"        # one-shot
dotnet run --project src/EconData.Cli -- chat --k 5 --show-sources
dotnet run --project src/EconData.Cli -- chat --system prompts/rag.txt
```
REPL commands: `help`, `sources` (re-print the last answer's sources), `reset`
(clear the in-memory transcript), `exit`/`quit` (or Ctrl-D). History is capped at
the last 10 turns and is never persisted.

### What the assistant can and cannot answer
Retrieval is the only source of truth, so each `entity_metric` document carries
coverage (count, year range), the **latest** value, the **min**/**max** with their
years, and the **N most recent** `year=value` points. That answers "latest /
highest / lowest / recent trend" questions. It does **not** answer arbitrary
historical years outside those recent points, nor cross-entity aggregates,
rankings, or comparisons — the system prompt instructs the model to say so rather
than guess.

### Troubleshooting
| Symptom | Fix |
| :--- | :--- |
| Non-zero exit at startup | The embedding service or `LLM_BASE_URL` is unreachable; the message names which. |
| `[error] ... rejected the credentials (HTTP 401)` | Set `LLM_API_KEY`, or clear it for a local server. |
| `[error] ... no /chat/completions route (HTTP 404)` | `LLM_BASE_URL` must point at the OpenAI-compatible root (usually `.../v1`); a missing Ollama model also returns 404. |
| `no choices ... Is 'x' a chat-capable model?` | `ollama pull <model>` or set `LLM_MODEL` to a chat model. |
| "I don't have any catalog data that covers that" | Nothing relevant was retrieved; the LLM was not called. Run `vectorize` if the table is empty. |

## Vector catalog design

- Documents live in `dbe_embeddings`; each row is identified by
  `(doc_key, doc_type)` and carries a `doc_hash` (SHA-256 of the canonical content).
- `vectorize` embeds only documents whose hash changed plus any orphans (rows in
  the table not present in the freshly-built set). `--reembed` recomputes every vector.
- Vectors are stored in a sqlite-vec `vec0` table (`float[768]`, cosine); search is
  `similarity = 1 - distance`.
- `entity_metric` documents embed a **value summary** so a RAG answer can quote a
  number without a second query. Because `doc_hash` hashes the content, changing
  the summary changes the hash; re-running `vectorize` immediately after reports
  `embedded=0` (the idempotency check).

## sqlite-vec

The loadable extension is vendored under `native/sqlite-vec/<rid>/` (pinned to
v0.1.9). `VectorStore` loads it per connection and manages the `dbe_embeddings_vec`
`vec0` table alongside `dbe_embeddings`. To support another platform, add its RID
directory from the matching sqlite-vec release asset (see `native/sqlite-vec/README.md`).

## Derived aggregates

Opt-in via the `aggregation` key in `config/metrics.json` (`sum`/`mean`, default `none`). Computed rows are stored in `dbe_derived_observations` and exposed through `v_observations_all` with `source_kind = "derived"`. Read paths (`/api/series`, `/api/snapshot`, `/api/coverage`) expose `source_kind` and separate derived counts.
