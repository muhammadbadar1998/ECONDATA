# DBnomics Global Economic Data Extraction & SQL Server Pipeline

A containerized Python pipeline to extract high-level macroeconomic indicators across all available countries from [DBnomics](https://db.nomics.world/), normalize time-series observations, export master catalogs, and perform bulk UPSERT loading into **SQL Server Database** (`dbe_*` tables) while maintaining local CSV backups.

---

## 🗄️ SQL Server Database Schema (`dbe_*`)

The pipeline connects to SQL Server using ODBC Driver 17 / 18 and automatically manages the `dbe_*` tables:

| Table Name | Description | Key Columns |
| :--- | :--- | :--- |
| `dbe_countries` | Master catalog of countries & regional aggregates | `country_code` (PK), `country_name`, `provider`, `dataset`, `is_aggregate` |
| `dbe_metrics` | Data dictionary of tracked economic indicators | `metric_code` (PK), `metric_name`, `provider`, `dataset`, `indicator_code`, `unit` |
| `dbe_economic_observations` | Full historical time-series observations | `id` (PK), `country_code`, `metric_code`, `period`, `val`, `unit`, `frequency`, `extracted_at` |
| `dbe_latest_country_snapshots` | Cross-sectional latest value matrix per country | `country_code` (PK), `gdp_usd_latest`, `gdp_growth_latest`, `inflation_cpi_latest`, etc. |
| `dbe_pipeline_runs` | Execution audit log & pipeline run history | `run_id` (PK), `run_timestamp`, `metrics_extracted`, `total_observations`, `status`, `execution_seconds` |

---

## 📁 Backup CSV Directory Structure (`./output/`)

```
output/
├── countries.csv                 # Master catalog of countries
├── metrics.csv                   # Data dictionary & metadata of tracked indicators
├── metrics/                      # Full historical time-series CSV per metric
│   ├── gdp_usd.csv               # Gross Domestic Product in Current USD
│   ├── gdp_growth.csv            # Annual GDP Growth Rate (%)
│   ├── inflation_cpi.csv         # Inflation Rate, CPI Annual (%)
│   ├── unemployment_rate.csv     # Unemployment Rate (% of Labor Force)
│   ├── population.csv            # Total Population
│   ├── current_account_balance.csv# Current Account Balance (% of GDP)
│   └── govt_debt_gdp.csv         # Central Government Debt (% of GDP)
└── snapshots/                    # Cross-sectional and versioned snapshots
    ├── latest_country_snapshot.csv # Pivoted matrix of latest available value per country across all metrics
    ├── latest_metrics_snapshot.csv # Normalized long-format table of single latest observation per country/metric
    └── dated/
        └── snapshot_YYYY-MM-DD.csv # Execution-dated snapshot for tracking extraction history over time
```

---

## 🚀 Execution Guide

### 1. Environment Configuration

Define connection string in environment variables or command-line:
```bash
DB_CONNECTION_STRING="Driver={ODBC Driver 17 for SQL Server};Server=(localdb)\MSSQLLocalDB;UID=IMFUser;PWD=IMFPassword;Encrypt=no;TrustServerCertificate=yes;"
```

---

### 2. Initialize Database Schema Only
```bash
python -m src.main --init-db --connection-string "Driver={ODBC Driver 17 for SQL Server};Server=(localdb)\MSSQLLocalDB;UID=IMFUser;PWD=IMFPassword;Encrypt=no;TrustServerCertificate=yes;"
```

---

### 3. Run Pipeline with SQL Server Ingestion & CSV Backups
```bash
# Full extraction & database load
python -m src.main --metrics all --db

# Extract specific metrics into database
python -m src.main --metrics gdp_usd population --db
```

---

### 4. Docker Execution
```bash
docker compose up --build
```
