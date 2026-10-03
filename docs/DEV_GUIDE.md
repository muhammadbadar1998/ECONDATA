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
  - **Ollama** (for the semantic catalog layer): install the service **and** the
    Python SDK (the SDK is in `requirements.txt` as `ollama>=0.3.0`):
    ```bash
    ollama serve            # in a terminal / systemd service / Windows service
    ollama pull nomic-embed-text   # 768-dim embeddings
    ```
    The pipeline also uses raw `requests` calls as a fallback, but the SDK is
    available for ad-hoc use and health checks.
  - A **chat model** on the LLM endpoint for the chatbot
    (`python -m src.chat`): `ollama pull qwen2.5:7b-instruct`, or point
    `LLM_BASE_URL`/`LLM_MODEL` at any OpenAI-compatible server.

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

# Ask the catalog a question (vector RAG over dbe_embeddings)
python -m src.chat
python -m src.chat --question "What is the latest US GDP?"
python -m src.chat --k 5 --show-sources

# Run tests
pytest                 # unit tests; opt-in integration tests via ECONDATA_TEST_DB_CONN

# Lint
ruff check src/
```

## Chatbot (vector RAG)

A read-only CLI assistant answers questions about the catalog: it embeds the
question, retrieves the top-k documents from `dbe_embeddings`, and asks an
OpenAI-compatible chat model to answer **from those documents only**, citing the
`doc_key`s it used. There is no text-to-SQL and no write path.

### Prerequisites
1. The database is populated and vectorized:
   ```bash
   python -m src.main --metrics all --db
   python -m src.main --vectorize
   ```
2. A **chat-capable** model is available on the LLM endpoint. For a local
   Ollama server (the default), embeddings and chat are separate models:
   ```bash
   ollama serve
   ollama pull nomic-embed-text        # embeddings (required)
   ollama pull qwen2.5:7b-instruct    # chat model (required for the chatbot)
   ```
   Any OpenAI-compatible server works by pointing `LLM_BASE_URL` at it (vLLM,
   LM Studio, OpenAI, Groq, ...). No new Python dependency is used — the client
   is plain `requests`.

### Environment (`config/.env`)

| Variable | Default | Meaning |
| :--- | :--- | :--- |
| `OLLAMA_HOST` | `http://127.0.0.1:11434` | Embedding service base URL |
| `OLLAMA_EMBEDDING_MODEL` | `nomic-embed-text` | Embedding model (768-dim) |
| `OLLAMA_TIMEOUT` | `60` | Per-request timeout for *embedding* calls |
| `LLM_BASE_URL` | `http://127.0.0.1:11434/v1` | OpenAI-compatible base URL |
| `LLM_API_KEY` | *(empty)* | Bearer token; the header is omitted when empty |
| `LLM_MODEL` | `qwen2.5:7b-instruct` | Chat model (must be chat-capable) |
| `LLM_TIMEOUT` | `120` | Timeout for one *chat completion* |
| `LLM_TEMPERATURE` | `0.2` | Lower = more factual |
| `RAG_TOP_K` | `8` | Documents retrieved per question |
| `RAG_RECENT_POINTS` | `5` | Trailing observations per `entity_metric` document |

`LLM_TIMEOUT` is deliberately separate from `OLLAMA_TIMEOUT`: chat completions
generate tokens and are far slower than embedding calls.

`LLM_API_KEY` lives only in `config/.env` (gitignored) and is never logged.

### Commands
```bash
python -m src.chat                                   # interactive REPL
python -m src.chat --question "latest US GDP"        # one-shot
python -m src.chat --k 5 --show-sources              # fewer docs, print sources
python -m src.chat --system prompts/rag.txt          # system-prompt override
```
REPL commands: `help`, `sources` (re-print the last answer's sources), `reset`
(clear the in-memory transcript), `exit`/`quit` (or Ctrl-D). History is capped
at the last 10 turns and is never persisted.

### What the assistant can and cannot answer
Retrieval is the only source of truth, so each `entity_metric` document carries
coverage (observation count, year range), the **latest** value, the **min** and
**max** with their years, and the **N most recent** `year=value` points. That
answers "latest / highest / lowest / recent trend" questions. It does **not**
answer arbitrary historical years outside those recent points, nor cross-entity
aggregates, rankings, or comparisons — the system prompt instructs the model to
say so rather than guess. Adding read-only tool-calling (`get_series`,
`list_entities`) is the planned way to close that gap; `LLMClient.chat` already
accepts `tools` and returns `tool_calls`.

### Troubleshooting
| Symptom | Fix |
| :--- | :--- |
| Non-zero exit at startup | The embedding service or `LLM_BASE_URL` is unreachable; the message names which. |
| `[error] ... rejected the credentials (HTTP 401)` | Set `LLM_API_KEY`, or clear it for a local server. |
| `[error] ... no /chat/completions route (HTTP 404)` | `LLM_BASE_URL` must point at the OpenAI-compatible root (usually `.../v1`). |
| `no choices ... Is 'x' a chat-capable model?` | `ollama pull <model>` or set `LLM_MODEL` to a chat model. |
| "I don't have any catalog data that covers that" | Nothing relevant was retrieved; the LLM was not called. Run `--vectorize` if the table is empty. |

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
- `entity_metric` documents embed a **value summary** (latest / min / max with
  their years, plus the `RAG_RECENT_POINTS` most recent `year=value` pairs) so a
  RAG answer can quote a number without a second query. Because `doc_hash` is
  the hash of the content, changing the summary changes the hash: the first run
  after changing `RAG_RECENT_POINTS` re-embeds the 1,803 `entity_metric` docs
  only, and per-`doc_type` row counts stay the same. Re-running `--vectorize`
  immediately afterwards reports `embedded=0`, which is the idempotency check.

## Security
- `config/.env` is gitignored; never commit real credentials.
- Restrict the Supabase `anon` role away from `dbe_*` and use an IP allowlist.
