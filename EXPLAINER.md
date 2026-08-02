# 📚 Global Economic Data Pipeline & Codebase Explainer

This document serves as the developer guide and architecture manual for the **Global Economic Data Extraction Pipeline** (`econdata`).

---

## 🚀 Quick Reference

| Task | Command |
| :--- | :--- |
| **Run Full Pipeline with Supabase PostgreSQL** | `python -m src.main --metrics all --db` |
| **Run Specific Metrics Only** | `python -m src.main --metrics gdp_growth inflation_cpi --db` |
| **Run with Custom JSON Configuration** | `python -m src.main --config ./config/metrics.json --metrics all --db` |
| **Initialize Database Schemas Only** | `python -m src.main --init-db` |
| **Run via Docker Container** | `docker-compose up --build` |

---

## ⚙️ How to Modify & Extend Pipeline via JSON (`config/metrics.json`)

All metrics, indicators, providers, and period ranges are defined in [`config/metrics.json`](file:///c:/Users/muham/Documents/Code/econdata/config/metrics.json). **You do NOT need to write Python code to add new metrics, adjust date ranges, or add forecast datasets.**

### 1. Adding a New Historical Indicator
To add a new World Bank or provider metric (e.g., Foreign Direct Investment `% of GDP` or Central Bank Interest Rate), simply add an entry to the `"metrics"` array in `config/metrics.json`:

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
  "description": "Foreign direct investment are the net inflows of investment to acquire a lasting management interest in an enterprise operating in an economy other than that of the investor.",
  "start_year": 1970,
  "end_year": 2026
}
```

### 2. Adding Forecasted Data (e.g. IMF World Economic Outlook)
To fetch forecasted projections (e.g., IMF WEO GDP or Inflation forecasts up to 2030), set `"category": "forecast"` and specify the provider dataset details and period range:

```json
{
  "metric_code": "forecast_gdp_growth_weo",
  "metric_name": "IMF WEO Real GDP Growth Projections (Annual %)",
  "category": "forecast",
  "provider": "IMF",
  "dataset": "WEO",
  "indicator_code": "NGDP_RPCH",
  "unit": "Annual % change",
  "frequency": "Annual",
  "description": "Gross domestic product, constant prices percentage change from IMF World Economic Outlook medium-term forecasts.",
  "start_year": 2024,
  "end_year": 2030
}
```

### 3. Filtering Periods & Date Ranges
Each metric entry in `config/metrics.json` accepts optional date boundaries:
- `"start_year"`: Minimum year to retain (e.g. `2000`).
- `"end_year"`: Maximum year to retain (e.g. `2030`).

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
│   ├── extractor.py                # DBnomics REST API reader with exponential retries
│   ├── transformer.py              # Normalization, country aggregate detection, snapshot builders
│   ├── storage.py                  # Directory manager & CSV exporter
│   ├── db.py                       # SQLAlchemy engine factory & DDL schema executor
│   ├── db_loader.py                # High-speed bulk loader (psycopg2 execute_values)
│   └── main.py                     # CLI orchestrator & file logger
├── output/
│   ├── countries.csv               # Master country catalog (266 countries)
│   ├── metrics.csv                 # Master metrics catalog
│   ├── metrics/*.csv               # Individual time-series CSV per indicator
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

1. **`dbe_countries`**: Master catalog of 266 countries and regional aggregates (`is_aggregate = TRUE`).
2. **`dbe_metrics`**: Catalog of indicators defined in `config/metrics.json`.
3. **`dbe_economic_observations`**: Time-series observations table with composite primary key `(country_code, metric_code, period)`.
4. **`dbe_latest_country_snapshots`**: Pivoted cross-sectional matrix table storing the latest metric value and period for every country.
5. **`dbe_pipeline_runs`**: Execution audit log tracking extraction metrics count, observation count, and execution time.

### Database Credentials (Environment Variables)
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
To re-run the extraction pipeline and update Supabase + CSV outputs:
```bash
python -m src.main --metrics all --db
```
