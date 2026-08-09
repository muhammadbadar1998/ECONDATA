# DBnomics Global Economic Data Extraction & Supabase PostgreSQL Pipeline

A Python pipeline to extract high-level macroeconomic indicators across all available countries from [DBnomics](https://db.nomics.world/), normalize them into a **typed, extensible schema**, and perform idempotent bulk UPSERT loading into **Supabase PostgreSQL** (`dbe_*` tables).

---

## 🗄️ Normalized Database Schema (`dbe_*`)

The pipeline connects to Supabase PostgreSQL and manages the normalized `dbe_*` tables:

| Table Name | Description |
| :--- | :--- |
| `dbe_entities` | Canonical entity registry (countries = ISO-3166 alpha-3, aggregates, regions) with hierarchy |
| `dbe_entity_aliases` | `(provider, provider_code) → entity` mapping resolving cross-provider collisions |
| `dbe_metrics` | Indicator metadata registry (frequency, `base_unit`, `unit_normalization`, aggregation) |
| `dbe_metric_synonyms` | Keyword synonyms for the future text-to-SQL layer |
| `dbe_sources` | Provider/dataset/indicator registration |
| `dbe_entity_membership` | Aggregate → member entity relationships |
| `dbe_observations` | Typed time-series core `(metric_id, entity_id, source_id, start_date, end_date, val, raw_val, unit, period_str)` |
| `dbe_pipeline_runs` | Extended execution audit log |

Views: `v_latest_country_snapshot`, `v_latest_metrics_snapshot`, `v_dated_execution_snapshot`.

---

## 🔒 Security (owner-only database)

- Credentials are read from `config/.env` (gitignored). Copy `config/.env.example` → `config/.env`.
- **Never commit a real password.** Rotate it if exposed.
- In Supabase: disable the public `anon` role for `dbe_*` and restrict access by IP allowlist (no `0.0.0.0/0`).

> **Full step-by-step deployment** (password rotation, Supabase hardening, schema init, live extraction, and Airflow scheduling): see [docs/DEPLOY.md](docs/DEPLOY.md).

---

## 🚀 Execution Guide

### 1. Environment Configuration
```bash
cp config/.env.example config/.env
# edit config/.env and set SUPABASE_HOST / SUPABASE_PASSWORD
```

### 2. Install dependencies
```bash
pip install -r requirements.txt
```

### 3. Initialize Database Schema Only
```bash
python -m src.main --init-db
```

### 4. Run Pipeline with PostgreSQL Ingestion
```bash
# Full extraction & database load
python -m src.main --metrics all --db

# Extract specific metrics into database
python -m src.main --metrics gdp_usd population --db
```

### 5. Airflow Orchestration (Docker)
```bash
docker compose -f airflow/docker-compose.airflow.yml up
```
The `econdata_pipeline` DAG runs one idempotent load task per metric on a daily schedule.

---

## 🧩 Extending the Pipeline

All metrics are defined in `config/metrics.json`. See `EXPLAINER.md` for the full field reference and examples (historical indicators, long-term forecasts, higher-frequency series).

---

## 🧪 Tests & Linting
```bash
pytest            # unit tests
ruff check src/   # lint
```

