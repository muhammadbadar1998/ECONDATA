# CHANGELOG

All notable changes to this project are documented here. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/) and [Semantic Versioning](https://semver.org/).

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
