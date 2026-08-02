import os

# Default Output Directories
DEFAULT_OUTPUT_DIR = os.getenv("OUTPUT_DIR", "./output")
METRICS_DIR_NAME = "metrics"
SNAPSHOTS_DIR_NAME = "snapshots"
LOGS_DIR_NAME = "logs"

# DBnomics API settings
DBNOMICS_BASE_URL = "https://api.db.nomics.world/v22"

# Supabase PostgreSQL Database Configuration & Defaults
DEFAULT_DB_CONNECTION_STRING = os.getenv(
    "DB_CONNECTION_STRING",
    "postgresql+psycopg2://postgres:ECONDATA.123@db.zcrgxnuqbylkhyzwsnjt.supabase.co:5432/postgres"
)

# Table Names with dbe_ Prefix for DBnomics Data
DBE_TABLE_COUNTRIES = "dbe_countries"
DBE_TABLE_METRICS = "dbe_metrics"
DBE_TABLE_OBSERVATIONS = "dbe_economic_observations"
DBE_TABLE_SNAPSHOTS = "dbe_latest_country_snapshots"
DBE_TABLE_RUNS = "dbe_pipeline_runs"

# Master Macroeconomic Indicators Catalog
METRICS_CATALOG = {
    "gdp_usd": {
        "metric_code": "gdp_usd",
        "metric_name": "Gross Domestic Product (Current USD)",
        "provider": "WB",
        "dataset": "WDI",
        "indicator_code": "NY.GDP.MKTP.CD",
        "unit": "Current US$",
        "frequency": "Annual",
        "description": "GDP at purchaser's prices is the sum of gross value added by all resident producers in the economy plus any product taxes and minus any subsidies not included in the value of the products."
    },
    "gdp_growth": {
        "metric_code": "gdp_growth",
        "metric_name": "GDP Growth Rate (Annual %)",
        "provider": "WB",
        "dataset": "WDI",
        "indicator_code": "NY.GDP.MKTP.KD.ZG",
        "unit": "%",
        "frequency": "Annual",
        "description": "Annual percentage growth rate of GDP at market prices based on constant local currency."
    },
    "inflation_cpi": {
        "metric_code": "inflation_cpi",
        "metric_name": "Inflation Rate, Consumer Prices (Annual %)",
        "provider": "WB",
        "dataset": "WDI",
        "indicator_code": "FP.CPI.TOTL.ZG",
        "unit": "%",
        "frequency": "Annual",
        "description": "Inflation as measured by the consumer price index reflects the annual percentage change in the cost to the average consumer of acquiring a basket of goods and services."
    },
    "unemployment_rate": {
        "metric_code": "unemployment_rate",
        "metric_name": "Unemployment Rate (% of Total Labor Force)",
        "provider": "WB",
        "dataset": "WDI",
        "indicator_code": "SL.UEM.TOTL.ZS",
        "unit": "%",
        "frequency": "Annual",
        "description": "Unemployment refers to the share of the labor force that is without work but available for and seeking employment."
    },
    "population": {
        "metric_code": "population",
        "metric_name": "Total Population",
        "provider": "WB",
        "dataset": "WDI",
        "indicator_code": "SP.POP.TOTL",
        "unit": "Persons",
        "frequency": "Annual",
        "description": "Total population counts all residents regardless of legal status or citizenship."
    },
    "current_account_balance": {
        "metric_code": "current_account_balance",
        "metric_name": "Current Account Balance (% of GDP)",
        "provider": "WB",
        "dataset": "WDI",
        "indicator_code": "BN.CAB.XOKA.GD.ZS",
        "unit": "% of GDP",
        "frequency": "Annual",
        "description": "Current account balance is the sum of net exports of goods and services, net primary income, and net secondary income relative to GDP."
    },
    "govt_debt_gdp": {
        "metric_code": "govt_debt_gdp",
        "metric_name": "Central Government Debt (% of GDP)",
        "provider": "WB",
        "dataset": "WDI",
        "indicator_code": "GC.DOD.TOTL.GD.ZS",
        "unit": "% of GDP",
        "frequency": "Annual",
        "description": "Debt is the entire stock of direct government fixed-term contractual obligations to others outstanding on a particular date."
    }
}
