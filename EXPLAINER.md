# 📚 Global Economic Data Pipeline & Codebase Explainer

This document serves as the developer guide and architecture manual for the **Global Economic Data Extraction Pipeline** (`econdata`).

> **Platform**: PostgreSQL/Supabase only. The SQL Server code path was removed during the normalized schema refactor.

---

## 🚀 Quick Reference

| Task | Command |
| :--- | :--- |
| **Run Full Pipeline (Supabase PostgreSQL)** | `python -m src.main --metrics all --db` |
| **Run Incremental Update for Specific Metrics** | `python -m src.main --metrics forecast_gdp_longterm_oecd forecast_gdp_growth_gep --db` |
| **Run with Custom JSON Configuration** | `python -m src.main --config ./config/metrics.json --metrics all --db` |
| **Initialize Database Schemas Only** | `python -m src.main --init-db` |
| **Run via Airflow (Docker)** | `docker compose -f airflow/docker-compose.airflow.yml up` |

---

## 🔒 Security & Privacy (owner-only database)

The Supabase database is **private** — it must not be publicly reachable.

- **Credentials live in `config/.env`** (gitignored). Copy `config/.env.example` → `config/.env` and fill in real values. Never commit a real password.
- **Rotate the password** if it was ever exposed.
- In the Supabase console: disable the `anon`/public PostgREST role for `dbe_*`, and restrict access by **IP allowlist** (or a private tunnel/VPN). No `0.0.0.0/0`.
- The connection string is built at runtime from `SUPABASE_*` env vars in `src/config.py`; there is **no hardcoded credential** in the codebase.

---

## 🔄 Idempotent UPSERT Strategy

The pipeline is safe for recurring runs. Executing it never wipes existing data; it incrementally upserts via `ON CONFLICT` clauses keyed on stable natural keys.

### 1. Normalized Observation UPSERT (`dbe_observations`)
- Uses `psycopg2.extras.execute_values` with `ON CONFLICT (metric_id, entity_id, start_date, end_date) DO UPDATE SET val = EXCLUDED.val, ...`.
- Typed `DATE` ranges (`start_date`, `end_date`) replace the old free-text `period` column, fixing string-sort ordering bugs.
- Existing rows with unchanged values stay untouched; updated values are overwritten; new periods/entities are inserted.

### 2. Registry / Dimension Tables
- `dbe_entities`, `dbe_entity_aliases`, `dbe_metrics`, `dbe_metric_synonyms`, `dbe_sources`, `dbe_entity_membership` are upserted from code + `config/metrics.json`.
- `dbe_observations` dedupe key is `(metric_id, entity_id, start_date, end_date)`.

---

## ⚙️ How to Modify & Extend via JSON (`config/metrics.json`)

All metrics, indicators, providers, and period ranges are defined in `config/metrics.json`. **You do NOT need to write Python code to add metrics, adjust date ranges, or add forecast datasets.**

Extended metric fields:

| Field | Purpose |
| :--- | :--- |
| `frequency` | `Annual` / `Quarterly` / `Monthly` |
| `base_unit` | Canonical unit observations are normalized to |
| `unit_normalization` | Safe arithmetic expression over `x` (raw value) to convert source unit → `base_unit` |
| `dimension_key` | Which dimension carries the entity (`country` / `ref_area` / `LOCATION` / `weo-country-code`) |
| `entity_scope` | `country` or `aggregate` |
| `synonyms` | Keywords for the future text-to-SQL layer |
| `aggregation` | `sum` / `mean` / `weighted` for aggregates |

### Adding a New Historical Indicator
```json
{
  "metric_code": "fdi_inflows_gdp",
  "metric_name": "Foreign Direct Investment, Net Inflows (% of GDP)",
  "category": "historical",
  "provider": "WB",
  "dataset": "WDI",
  "indicator_code": "BX.KLT.DINV.WD.GD.ZS",
  "unit": "% of GDP",
  "base_unit": "% of GDP",
  "frequency": "Annual",
  "dimension_key": "country",
  "entity_scope": "country",
  "synonyms": ["FDI", "foreign direct investment"],
  "description": "Foreign direct investment are the net inflows of investment to acquire a lasting management interest.",
  "start_year": 1970,
  "end_year": 2026
}
```

### Adding Long-Term Forecasted Data (OECD 2024-2060)
```json
{
  "metric_code": "forecast_gdp_longterm_oecd",
  "metric_name": "OECD Real GDP Long-Term Forecast (2024-2060)",
  "category": "forecast",
  "provider": "OECD",
  "dataset": "DP_LIVE",
  "indicator_code": "GDPLTFORECAST",
  "unit": "USD Millions (2015 PPP)",
  "base_unit": "USD Millions (2015 PPP)",
  "unit_normalization": "x*1000000",
  "frequency": "Annual",
  "dimension_key": "LOCATION",
  "entity_scope": "country",
  "description": "Long-term projections of real GDP from OECD up to 2060.",
  "start_year": 2024,
  "end_year": 2060
}
```

---

## 🏗️ Architecture & Module Guide

```
econdata/
├── config/
│   ├── metrics.json                # Master JSON configuration for all metrics & period filters
│   ├── .env.example                # Env var template (Supabase PostgreSQL only)
│   └── SUPABASE.env.example        # Supabase-specific env template
├── sql/
│   └── dbe_postgres_schema.sql     # Normalized PostgreSQL DDL (Supabase) + views
├── src/
│   ├── config.py                   # Env-based Supabase connection + JSON loader
│   ├── config_loader.py            # (legacy) JSON loader w/ extended field defaults
│   ├── extractor.py                # DBnomics REST reader w/ retries (WB/IMF/OECD)
│   ├── transformer.py              # Normalization: typed dates, entity resolution, unit normalization, dedupe
│   ├── helpers.py                  # Period-literal parsing + safe unit normalization
│   ├── entities.py                 # Canonical entity + alias + membership seed data
│   ├── entity_resolver.py          # Provider code → canonical entity resolver (no fabricated names)
│   ├── db.py                       # SQLAlchemy engine factory (PostgreSQL only) + DDL executor
│   ├── db_loader.py                # Idempotent bulk loader into normalized dbe_* tables
│   ├── tasks/                      # Discrete Airflow tasks (extract/transform/load per metric)
│   └── main.py                     # CLI orchestrator
├── airflow/
│   ├── docker-compose.airflow.yml  # Airflow scheduler + webserver + Postgres/Redis
│   └── dags/econdata_pipeline.py   # Daily DAG, one task per metric
└── tests/                          # pytest unit tests + ruff config (pyproject.toml)
```

---

## 🗄️ Normalized Database Schema (Supabase PostgreSQL)

All tables are prefixed with `dbe_` and live in the `public` schema:

1. **`dbe_entities`**: Canonical entities (countries = ISO-3166 alpha-3, aggregates, regions) with hierarchy (`parent_entity_id`).
2. **`dbe_entity_aliases`**: `(provider, provider_code) → entity_id` mapping resolving cross-provider collisions deterministically.
3. **`dbe_metrics`**: Indicator metadata registry (frequency, `base_unit`, `unit_normalization`, aggregation method).
4. **`dbe_metric_synonyms`**: Keyword synonyms for the future text-to-SQL layer.
5. **`dbe_sources`**: Provider/dataset/indicator registration (`metric_id`, `dimension_key`, `entity_scope`).
6. **`dbe_entity_membership`**: Aggregate → member entity relationships.
7. **`dbe_observations`**: Typed time-series core `(metric_id, entity_id, source_id, start_date, end_date, val, raw_val, unit, period_str)`.
8. **`dbe_pipeline_runs`**: Extended audit log (dag/task, counts, providers, source_ids).

### Views (replace the old wide snapshots)
- `v_latest_country_snapshot` — pivoted latest value per entity/metric, built dynamically from normalized data (new metrics appear automatically).
- `v_latest_metrics_snapshot` — long-format latest value per `(entity, metric)`.
- `v_dated_execution_snapshot` — full observation history.

---

## 💡 Troubleshooting & Common Tasks

### Entity resolution correctness
- The old heuristic that derived country names from `series_name.split(" – ")[-1]` (which produced bogus entities like `Annual`) is gone. Resolution now uses `dbe_entity_aliases` + curated mappings in `src/entities.py`, and unknown codes are registered deterministically (never fabricated).

### How Bulk Upserts Work in Supabase
- `psycopg2.extras.execute_values` with `page_size=500` and `ON CONFLICT ... DO UPDATE`.
- Chunking avoids Supabase statement timeouts. `NaN`/`None` are sanitized to SQL `NULL`.

### Re-running the Pipeline
```bash
python -m src.main --metrics all --db
```

### Running with Airflow
```bash
docker compose -f airflow/docker-compose.airflow.yml up
```
The DAG `econdata_pipeline` runs one idempotent load task per metric on a daily schedule.
