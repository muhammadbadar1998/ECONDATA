# 📚 Global Economic Data Pipeline & Codebase Explainer

This document serves as the developer guide and architecture manual for the **Global Economic Data Extraction Pipeline** (`econdata`).

---

## 🚀 Quick Reference

| Task | Command |
| :--- | :--- |
| **Run Full Pipeline with Supabase PostgreSQL** | `python -m src.main --metrics all --db` |
| **Run Incremental Update for Specific Metrics** | `python -m src.main --metrics forecast_gdp_longterm_oecd forecast_gdp_growth_gep --db` |
| **Run with Custom JSON Configuration** | `python -m src.main --config ./config/metrics.json --metrics all --db` |
| **Initialize Database Schemas Only** | `python -m src.main --init-db` |
| **Run via Docker Container** | `docker-compose up --build` |

---

## 🔄 Incremental Updates & Merge (UPSERT) Strategy

The pipeline is designed to be **safe for recurring runs**. Executing the pipeline will **never wipe existing data**; instead, it incrementally updates changed records and appends new rows across both Database and CSV storage layers.

### 1. Database UPSERT Layer (`dbe_economic_observations`)
- **PostgreSQL (Supabase)**: Uses `psycopg2.extras.execute_values` with `ON CONFLICT (country_code, metric_code, period) DO UPDATE SET val = EXCLUDED.val, unit = EXCLUDED.unit, extracted_at = EXCLUDED.extracted_at`.
- **SQL Server (T-SQL)**: Uses `MERGE INTO dbe_economic_observations USING #stg_obs ON (country_code, metric_code, period) WHEN MATCHED THEN UPDATE WHEN NOT MATCHED THEN INSERT`.
- **Behavior**: Existing rows with unchanged values remain untouched. Updated values are overwritten, and new periods/countries are inserted seamlessly.

### 2. Local CSV Storage Layer ([`src/storage.py`](file:///c:/Users/muham/Documents/Code/econdata/src/storage.py))
- Reads pre-existing metric CSV files (`output/metrics/<metric_code>.csv`) if present.
- Merges newly extracted records with historical records.
- Deduplicates on `(country_code, period)` keeping the newest extracted record (`keep='last'`).
- Saves the sorted, merged dataset back to disk.

---

## ⚙️ How to Modify & Extend Pipeline via JSON (`config/metrics.json`)

All metrics, indicators, providers, and period ranges are defined in [`config/metrics.json`](file:///c:/Users/muham/Documents/Code/econdata/config/metrics.json). **You do NOT need to write Python code to add new metrics, adjust date ranges, or add forecast datasets.**

### 1. Adding a New Historical Indicator
To add a new World Bank or provider metric (e.g., Foreign Direct Investment `% of GDP`), add an entry to the `"metrics"` array in `config/metrics.json`:

```json
{
  "metric_code": "fdi_inflows_gdp",
  "metric_name": "Foreign Direct Investment, Net Inflows (% of GDP)",
  "category": "historical",
  "provider": "WB",
  "dataset": "WDI",
  "indicator_code": "BX.KLT.DINV.WD.GD.ZS",
  "unit": "% of GDP",
  "frequency": "Annual",
  "description": "Foreign direct investment are the net inflows of investment to acquire a lasting management interest.",
  "start_year": 1970,
  "end_year": 2026
}
```

### 2. Adding Long-Term Forecasted Data (e.g. OECD Projections 2024-2060)
To fetch long-term forecasted projections up to **2060**, set `"category": "forecast"` and specify provider details and date ranges (`start_year`: `2024`, `end_year`: `2060`):

```json
{
  "metric_code": "forecast_gdp_longterm_oecd",
  "metric_name": "OECD Real GDP Long-Term Forecast (2024-2060)",
  "category": "forecast",
  "provider": "OECD",
  "dataset": "DP_LIVE",
  "indicator_code": "GDPLTFORECAST",
  "unit": "USD Millions (2015 PPP)",
  "frequency": "Annual",
  "description": "Long-term projections of real GDP from OECD up to 2060.",
  "start_year": 2024,
  "end_year": 2060
}
```

---

## 🏗️ Architecture & Module Guide

```
c:\Users\muham\Documents\Code\econdata\
├── config/
│   └── metrics.json                # Master JSON configuration for all metrics & period filters
├── sql/
│   ├── dbe_postgres_schema.sql     # PostgreSQL DDL script (Supabase)
│   └── dbe_schema.sql              # SQL Server DDL script (T-SQL)
├── src/
│   ├── __init__.py                 # Module initializer
│   ├── config.py                   # Dynamic JSON loader & constants
│   ├── extractor.py                # DBnomics REST API reader with exponential retries (WB/IMF/OECD support)
│   ├── transformer.py              # Normalization, country aggregate detection, snapshot builders
│   ├── storage.py                  # Incremental CSV storage manager (merges & deduplicates)
│   ├── db.py                       # SQLAlchemy engine factory & DDL schema executor
│   ├── db_loader.py                # High-speed bulk loader (psycopg2 execute_values / T-SQL MERGE)
│   └── main.py                     # CLI orchestrator, FK country registration & file logger
├── output/
│   ├── countries.csv               # Master country catalog (273 countries/aggregates)
│   ├── metrics.csv                 # Master metrics catalog
│   ├── metrics/*.csv               # Individual time-series CSV per indicator (incrementally merged)
│   ├── snapshots/                  # Cross-sectional latest matrix snapshots
│   └── logs/                       # Daily persistent log files
├── Dockerfile                      # Container build manifest
├── docker-compose.yml              # Docker execution wrapper
├── README.md                       # Project overview & quick start
└── EXPLAINER.md                    # Developer architecture manual
```

---

## 🗄️ Database Schemas & Supabase Integration

All pipeline database tables are prefixed with `dbe_`:

1. **`dbe_countries`**: Master catalog of 273 countries and regional aggregates (`is_aggregate = TRUE`).
2. **`dbe_metrics`**: Catalog of indicators defined in `config/metrics.json`.
3. **`dbe_economic_observations`**: Time-series observations table with composite primary key `(country_code, metric_code, period)`. Contains **75,327+ rows** including long-term forecasts through 2060.
4. **`dbe_latest_country_snapshots`**: Pivoted cross-sectional matrix table storing the latest metric value and period for every country.
5. **`dbe_pipeline_runs`**: Execution audit log tracking extraction metrics count, observation count, and execution time.

### Database Credentials
- **Host**: `db.zcrgxnuqbylkhyzwsnjt.supabase.co`
- **Database**: `postgres`
- **Port**: `5432`
- **User**: `postgres`
- **SQLAlchemy Connection URI**:
  ```bash
  postgresql+psycopg2://postgres:ECONDATA.123@db.zcrgxnuqbylkhyzwsnjt.supabase.co:5432/postgres
  ```

---

## 💡 Troubleshooting & Common Tasks

### How Bulk Upserts Work in Supabase
- The pipeline uses `psycopg2.extras.execute_values` with `page_size=100` and `ON CONFLICT (...) DO UPDATE`.
- Chunking at `100` rows per multi-value query guarantees sub-second execution speeds without triggering Supabase statement timeouts.
- All `NaN` or invalid values are sanitized to Python `None` so they store cleanly as SQL `NULL`.

### Re-running the Pipeline
To re-run the extraction pipeline and update Supabase + CSV outputs incrementally:
```bash
python -m src.main --metrics all --db
```
