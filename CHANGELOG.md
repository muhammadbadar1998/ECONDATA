# CHANGELOG

All notable changes to this project are documented here. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/) and [Semantic Versioning](https://semver.org/).

## [Unreleased] — 2026-09

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

### Changed
- `requirements.txt`: removed `streamlit`, `plotly`; documented Ollama as an
  external service (uses `requests`).
- `README.md`, `EXPLAINER.md`, `docs/DEPLOY.md`: updated for ETL-only posture.
