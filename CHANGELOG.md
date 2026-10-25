# CHANGELOG

All notable changes to this project are documented here. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/) and [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- Medallion ingestion: bronze (append-only raw Parquet), silver (typed Parquet, one
  partition per metric, overwritten per run) and gold (DuckDB `dbe_*` tables + views)
  under `output/lake/`. `extract` gains `--bronze`, `--silver`, `--gold`; `--db` still
  means "load to SQLite" and a run with no tier flags never touches the lake.
- `lake status`, `lake build-gold` and `lake rebuild` CLI commands; `lake build-gold`
  rebuilds the DuckDB gold tier from silver Parquet with no network access, and `lake
  rebuild` builds into a temporary file, validates, then swaps atomically.
- `lake init` (idempotent gold schema bootstrap) and `lake import-from-sqlite`
  (one-way backfill from an existing SQLite database using natural keys; `--dry-run`
  prints source counts and writes nothing; `--write-silver` also materialises silver
  partitions so `lake build-gold` can rebuild from them).
- `ECONDATA_SERVING_STORE` (`auto | gold | sqlite`) controls where structured analytics
  read from. `gold` fails fast when the gold database is missing; `sqlite` forces SQLite.
  Chat and the vector index always use SQLite.
- `GET /api/analytics/growth`, `/ranking`, `/correlation`, and `/freshness`.
- Data breadth: 17 new World Bank indicators (26 metrics total) and three aggregate
  groups (ASEAN, Mercosur, CPTPP) with memberships.
- `GET /api/lake/status` plus a **Lake** page, a Lake card on Overview, tier checkboxes
  on Runner, and a Tiers column on Runs.
- `doctor` reports a `lake` check (tier presence, gold readability). A missing lake is a
  warning, not a blocker.
- `AnalyticsService` and the vector catalog read the DuckDB gold tier once it exists,
  falling back to SQLite; public JSON shapes are unchanged.
- `ECONDATA_LAKE_DIR` (default `./output/lake`) configures the lake root.
- `sql/duckdb/schema.sql`: DuckDB DDL using sequences for surrogate ids and fully
  qualified view columns.

### Changed
- `dbe_pipeline_runs.tiers_written` records which tiers a run wrote (SQLite gains the
  column via an additive `ALTER TABLE` migration on existing databases).
- `doctor`'s `lake` check now also runs when the SQLite database is absent, so a
  lake-only deployment reports tier status instead of returning early.
- `backup --to <path>` CLI command: consistent `VACUUM INTO` snapshot that needs no
  external `sqlite3` binary.
- Derived aggregates layer: opt-in `aggregation` key (`sum`/`mean`, default `none`) in
  `config/metrics.json` drives computed aggregate rows in `dbe_derived_observations`,
  exposed through the unified `v_observations_all` view; `AnalyticsService` read paths
  now carry `source_kind` ("raw"/"derived") and separate derived counts in coverage.
  Aggregation failures are reported as `aggregate:<metric>` in `FailedMetrics`.
- `doctor` CLI command: read-only readiness check (runtime, config, DB presence,
  SQLite integrity, schema/view descriptor, sqlite-vec load, Ollama and LLM
  reachability). Exits non-zero only on blockers.
- `native/sqlite-vec/linux-x64/vec0.so` (sqlite-vec v0.1.9) so the vector layer
  runs on Linux without manual setup.
- `PipelineSummary.FailedMetrics` and run status `PARTIAL`/`FAILED` so a run with
  failed metrics is no longer recorded as `SUCCESS`; CLI exits 1 and the Web job
  fails.

### Fixed
- Analytics SQL now runs on DuckDB as well as SQLite: freshness computes the lag in C#
  (DuckDB rejects `strftime` on VARCHAR dates), and correlation no longer binds the same
  period placeholder twice.
- `lake import-from-sqlite` selects real columns and remaps SQLite surrogate ids through
  natural keys (metric/entity/source); it no longer copies ids or fabricates derived rows.

### Changed
- A run that extracts no valid metrics is now recorded as `FAILED` (not `SUCCESS`) with
  `metrics_extracted = 0`; the Web job filters unknown metric codes like the CLI and
  surfaces "No valid metrics requested" instead of a bogus success.
- `v_latest_metrics_snapshot` no longer coerces missing values to `0`; views are
  dropped and recreated on `init-db` so existing databases pick up the change.
- Database verification (`check-db`, the Web `Check` job, and `doctor`) is now
  read-only: it no longer creates a missing database or switches its journal
  mode, and a missing or corrupt database is reported as a failure instead of
  success.

## [2.1.0] — 2026-10 — Web UI (W1–W5)

### Added
- `src/EconData.Web`: localhost-only Blazor Server app with Overview, Runner, Data,
  Metrics, Entities, Runs, and Builder pages plus a Minimal API.
- `JobService`/`PipelineJobExecutor`: single-concurrency pipeline jobs with live
  progress, bounded in-memory log tail, cooperative cancel, and run history.
- Optional `IProgress<PipelineProgress>` / `IProgress<VectorizeProgress>` hooks on
  `PipelineRunner` and `Vectorizer`; per-batch embedding progress via
  `IEmbedder.BatchSize`.
- Analytics JSON endpoints (`/api/overview`, `coverage`, `series`, `snapshot`,
  `entities`, `runs`) and `/api/catalog/metrics`.
- Dashboard builder: GridStack 14 + ECharts 6 widgets (series/coverage/snapshot/runs),
  drag/resize/add/edit/remove, layout persistence at `output/dashboards.json`
  through `GET|PUT /api/dashboard/layout`.
- Vendored Bootstrap/ECharts/GridStack assets under `wwwroot/vendor/` for offline use.
- `tests/EconData.Web.Tests` (API, job service, layout store) and Core progress tests.
- `docs/DEV_GUIDE.md` expanded with Web architecture, builder contract, asset
  refresh, and troubleshooting.

### Fixed
- `SqliteConnectionFactory.Open` now resets `PRAGMA query_only = OFF`, so a pooled
  connection previously used by analytics read-only queries can still be used by
  pipeline writes (fixed "attempt to write a readonly database" on `/runner`).

### Verification
- `dotnet build` 0 warnings; **135 tests** green (112 Core + 23 Web);
  `dotnet format --verify-no-changes` clean.
- Live smoke: all pages return 200, catalog/jobs/layout endpoints respond, a Check
  job completes, and layout GET/PUT round-trips.

## [2.0.0] — 2026-10 — Migrated to C#/.NET 10 + SQLite

### Changed (platform)
- Rewrote the pipeline in **C# / .NET 10**; removed the Python implementation
  (`src/*.py`, `tests/*.py`, `requirements.txt`, `pyproject.toml`, `.venv`,
  pytest/ruff caches).
- Replaced **Supabase/PostgreSQL (+ pgvector)** with a local **SQLite** database and
  the **sqlite-vec** extension (`native/sqlite-vec`, pinned v0.1.9). Postgres
  `SERIAL` → `INTEGER PRIMARY KEY AUTOINCREMENT`, `TIMESTAMPTZ` → ISO-8601 `TEXT`,
  `JSONB`/`TEXT[]` → `TEXT`; the `DISTINCT ON`/`array_agg` coverage query was
  rewritten in C#.
- Configuration is now `config/appsettings.json` + environment variables (no
  `config/.env`); the SQLite file path replaces the connection string.
- Logging moved to **Serilog** (console + date-stamped + size-bounded rolling files).
- Integration tests are hermetic: a temporary SQLite file, including a real
  sqlite-vec kNN round-trip (no Docker/Supabase/Ollama).

### Added
- `.NET` solution `EconData.slnx` (`EconData.Core`, `EconData.Cli`, `EconData.Tests`),
  `Directory.Build.props` (nullable, analyzers, warnings-as-errors), `global.json`.
- New CLI: `init-db`, `check-db`, `extract --metrics all|a,b --db`,
  `vectorize [--reembed]`, `chat [--question Q --k N --show-sources --system FILE]`.
- `native/sqlite-vec/` (vendored loadable extension + README/VERSION).

### Kept
- `config/metrics.json` reused unchanged; canonical entities/aliases exported once
  to `config/entities.json`.
- Doc types (`metric`/`entity`/`entity_metric`), hash-diff vectorization with orphan
  cleanup, and the read-only grounded RAG chat with `doc_key` citations.

### Verification
- `dotnet build`, `dotnet test` (**104 tests**), `dotnet format --verify-no-changes` clean.
- Real run into SQLite: **75,327** observations across 9 metrics (idempotent re-run);
  `vectorize` produced **1,892** documents (idempotent re-run); `chat` answered with a
  `doc_key` citation.

## [Unreleased] — 2026-09

### Added (chatbot — vector RAG)
- `src/llm.py`: `LLMClient` — provider-agnostic chat completions over the
  OpenAI wire format (plain `requests`, no new dependency). Configured by
  `LLM_BASE_URL` / `LLM_API_KEY` / `LLM_MODEL` / `LLM_TIMEOUT` /
  `LLM_TEMPERATURE`; the `Authorization` header is sent only when a key is
  set. Retries transient failures with `2 ** attempt` backoff, returns
  `choices[0].message` (so `tool_calls` is reachable later), and raises
  `LLMError` with an actionable message for 401/404/connection/empty-choice
  failures. Import-safe: no network I/O until a method is called.
- `src/rag.py`: `RagAnswerer` — `retrieve` / `build_messages` / `answer`, kept
  separate so the prompt and retrieval are testable without an LLM. A
  system prompt restricts the model to the retrieved context, forbids invented
  numbers, requires `doc_key` citations, and tells it to decline when the
  context is insufficient. Empty retrieval short-circuits with a canned
  "no data" answer and no LLM call. Read-only; history is in-memory and capped.
- `src/chat.py`: CLI REPL (`python -m src.chat`) with `--k`, `--show-sources`,
  `--system FILE`, and a non-interactive `--question`. Commands: `help`,
  `sources`, `reset`, `exit`/`quit`. Startup preflight checks the embedding and
  chat backends and exits non-zero with an actionable message; per-turn failures
  print one line (no traceback) and the loop continues.
- `src/config.py`: lazy resolvers `get_llm_base_url`, `get_llm_api_key`,
  `get_llm_model`, `get_llm_timeout`, `get_llm_temperature`, `get_rag_top_k`,
  `get_rag_recent_points`. Functions, not module-level reads, so importing
  `src.config` stays side-effect free.
- `config/.env.example`: documented the `LLM_*` / `RAG_*` variables with
  local-Ollama defaults and the `ollama pull <chat-model>` prerequisite.
- `docs/DEV_GUIDE.md`: "Chatbot (vector RAG)" section (prerequisites, env
  table, commands, scope limits, troubleshooting). `README.md`: quickstart.

### Changed (vector catalog)
- `entity_metric` documents are now **value-enriched**: `_fetch_entity_metric_coverage`
  returns the latest value/year, the min and max value with their years, and the
  `RAG_RECENT_POINTS` (default 5) most recent `year=value` pairs (newest first),
  computed with CTEs (`DISTINCT ON` for latest, ordered `array_agg` for
  min/max, `ROW_NUMBER` for recent). The same fields are mirrored into
  `metadata` (JSONB — no DDL change) so a RAG answer can quote a number.
- Number rendering moved to `_format_value` (thousands separators for large
  magnitudes, fixed two decimals, `%` suffix for percentage units). Formatting
  is deterministic because `doc_hash` is the SHA-256 of the content: one
  re-embed of the 1,803 `entity_metric` docs was required, row counts are
  unchanged (9 metric / 80 entity / 1,803 entity_metric), and an immediate
  re-run reports `embedded=0`.

### Removed (debloat — ETL-only)
- **Streamlit dashboard**: deleted `dashboard.py`, `src/dashboard_data.py`,
  `src/dashboard_theme.py`, `.streamlit/`, `dashboard/` directory, and
  `tests/test_dashboard_counts.py`, `tests/test_dashboard_db_integration.py`,
  `tests/test_dashboard_theme.py`.
- **Airflow orchestration**: deleted the `airflow/` directory (DAG + compose).
  Scheduling is now manual/cron; no orchestration stack maintained.
- **Docker deployment**: deleted `Dockerfile`, `docker-compose.yml`, `.dockerignore`.
- **Dead modules**: deleted `src/config_loader.py`, `src/storage.py`,
  `src/tasks/__init__.py`.
- **Dead methods**: removed `DBnomicsExtractor.fetch_countries_metadata` and
  `DataTransformer.transform_countries_catalog` (no surviving callers).
- **Duplicate env template**: deleted `config/SUPABASE.env.example`
  (merged into `config/.env.example`).
- **Schema**: dropped `v_latest_country_snapshot` view (hardcoded to 7 metrics,
  `COALESCE(...,0)` conflated zero with missing data). Use
  `v_latest_metrics_snapshot` + `dbe_embeddings` instead.

### Fixed
- **Import-time crash** in `src/config.py`: `DEFAULT_DB_CONNECTION_STRING` was a
  module-level call to `get_supabase_connection_string()`, which raised
  `ValueError` when Supabase credentials were absent — breaking imports of the
  entire `src` package and all tests. Replaced with lazy
  `get_default_db_connection_string()`; `src/db.py`, `src/db_loader.py`, and
  `src/main.py` now resolve the string on first use.

### Added (vector catalog)
- `src/embeddings.py`: `OllamaEmbedder` — HTTP client for
  `nomic-embed-text` (768-dim) with batching, dimension validation, retries.
- `src/vectorizer.py`: `Vectorizer` — builds catalog/series documents,
  embeds changed/orphan docs, deletes stale rows, and exposes `search_documents`
  (cosine similarity against `dbe_embeddings`).
- SQL: `vector` extension, `dbe_embeddings(doc_key, doc_type, content, doc_hash,
  metadata, embedding vector(768), embedding_model, updated_at)`, and an HNSW
  cosine index.
- CLI: `python -m src.main --vectorize` and `--reembed`.

### Added (Ollama runtime)
- Installed Ollama binary service on the host and pulled `nomic-embed-text`.
- Added `ollama>=0.3.0` Python SDK to `requirements.txt` (documents the
  runtime; the embedder also works via raw `requests` as a fallback).
- Fixed `OllamaEmbedder.verify_connectivity` false-negative (model names
  returned by `/api/tags` carry a `:latest` suffix).

### Changed
- `requirements.txt`: removed `streamlit`, `plotly`; documented Ollama as an
  external service (uses `requests`).
- `README.md`, `EXPLAINER.md`, `docs/DEPLOY.md`: updated for ETL-only posture.

