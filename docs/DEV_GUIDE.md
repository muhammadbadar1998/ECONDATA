# Developer Guide

## Project posture (after debloat)
`ECONDATA` is a **Python ETL only**. The pipeline extracts normalized macro-economic
series from the DBnomics REST API, transforms them (entity resolution, typed dates,
unit normalization, dedupe), and performs idempotent bulk UPSERTs into
**Supabase PostgreSQL** (`dbe_*` schema). The Streamlit dashboard, the Airflow DAG,
and all Docker files have been removed; scheduling is manual (e.g. cron).

## Prerequisites
- **Python 3.10+**
- **PostgreSQL** with the `vector` extension (Supabase has it; self-hosted Postgres
  ≥11 needs `CREATE EXTENSION vector;`).
- **Ollama** (for the semantic catalog layer):
  ```bash
  ollama serve            # in a terminal / systemd service
  ollama pull nomic-embed-text   # 768-dim embeddings
  ```

## Setup
```bash
python -m venv .venv
source .venv/bin/activate     # or .venv\Scripts\activate on Windows
pip install -r requirements.txt
cp config/.env.example config/.env   # then edit config/.env with DB creds
```

## Common tasks
```bash
# Check Supabase connectivity (no schema changes)
python -m src.main --check-db

# Initialize the dbe_* schema (idempotent: CREATE IF NOT EXISTS / CREATE OR REPLACE)
python -m src.main --init-db

# Full ETL run for all metrics into Supabase
python -m src.main --metrics all --db

# Embed catalog + series summaries into dbe_embeddings (cosine HNSW index)
python -m src.main --vectorize

# Force re-embedding every document (recompute changed + orphans)
python -m src.main --reembed

# Run tests
pytest                 # unit tests; opt-in integration tests via ECONDATA_TEST_DB_CONN

# Lint
ruff check src/
```

## Connection-string resolution
`src/config.py::get_supabase_connection_string()` resolves the connection lazily:
1. `ECONDATA_DATABASE_URL` (preferred) → `postgresql+psycopg2://` scheme coerced.
2. Fallback: `DATABASE_URL` / `SHAREDPOOLER` (with a warning for the ambient name).
3. Fallback: discrete `SUPABASE_HOST`/`SUPABASE_USER`/`SUPABASE_PASSWORD`/
   `SUPABASE_DB`/`SUPABASE_PORT`.

Importing `src/config` **never** raises — even without credentials — because the
string is materialized on first call, not at import time.

## Vector catalog design
- Documents live in `dbe_embeddings`; each row is identified by
  `(doc_key, doc_type)` and carries a `doc_hash` (SHA-256 of the canonical content).
- `--vectorize` embeds only documents whose hash changed plus any orphans
  (rows in the table not present in the freshly-built set).
- `--reembed` recomputes every vector.
- `Vectorizer.search_documents(query, k=...)` runs a cosine nearest-neighbour
  query against the HNSW index.

## Security
- `config/.env` is gitignored; never commit real credentials.
- Restrict the Supabase `anon` role away from `dbe_*` and use an IP allowlist.
