import os
import json
import logging
from typing import Dict, Any

logger = logging.getLogger(__name__)

# Default File System Paths
DEFAULT_OUTPUT_DIR = os.getenv("OUTPUT_DIR", "./output")
DEFAULT_CONFIG_PATH = os.getenv("CONFIG_PATH", "./config/metrics.json")
METRICS_DIR_NAME = "metrics"
SNAPSHOTS_DIR_NAME = "snapshots"
LOGS_DIR_NAME = "logs"

# DBnomics REST API Base URL
DBNOMICS_BASE_URL = "https://api.db.nomics.world/v22"

# Supabase PostgreSQL Database Connection String
DEFAULT_DB_CONNECTION_STRING = os.getenv(
    "DB_CONNECTION_STRING",
    "postgresql+psycopg2://postgres:ECONDATA.123@db.zcrgxnuqbylkhyzwsnjt.supabase.co:5432/postgres"
)

# Database Table Names (All Prefixed with dbe_)
DBE_TABLE_COUNTRIES = "dbe_countries"
DBE_TABLE_METRICS = "dbe_metrics"
DBE_TABLE_OBSERVATIONS = "dbe_economic_observations"
DBE_TABLE_SNAPSHOTS = "dbe_latest_country_snapshots"
DBE_TABLE_RUNS = "dbe_pipeline_runs"


def load_pipeline_config(config_path: str = DEFAULT_CONFIG_PATH) -> Dict[str, Any]:
    """Loads metrics configuration and pipeline settings from JSON file."""
    if not os.path.exists(config_path):
        logger.warning(f"Config file '{config_path}' not found. Falling back to default built-in indicators catalog.")
        return get_fallback_config()

    try:
        with open(config_path, "r", encoding="utf-8") as f:
            data = json.load(f)

        settings = data.get("pipeline_settings", {})
        metrics_list = data.get("metrics", [])
        
        metrics_catalog = {}
        for item in metrics_list:
            code = item["metric_code"]
            metrics_catalog[code] = {
                "metric_code": code,
                "metric_name": item.get("metric_name", code),
                "category": item.get("category", "historical"),
                "provider": item.get("provider", "WB"),
                "dataset": item.get("dataset", "WDI"),
                "indicator_code": item.get("indicator_code", ""),
                "unit": item.get("unit", ""),
                "frequency": item.get("frequency", "Annual"),
                "description": item.get("description", ""),
                "start_year": item.get("start_year"),
                "end_year": item.get("end_year")
            }

        return {
            "settings": settings,
            "metrics": metrics_catalog
        }
    except Exception as e:
        logger.error(f"Error loading configuration file '{config_path}': {e}. Using fallback defaults.", exc_info=True)
        return get_fallback_config()


def get_fallback_config() -> Dict[str, Any]:
    """Returns fallback dictionary if config/metrics.json is missing."""
    fallback_metrics = {
        "gdp_usd": {
            "metric_code": "gdp_usd",
            "metric_name": "Gross Domestic Product (Current USD)",
            "category": "historical",
            "provider": "WB",
            "dataset": "WDI",
            "indicator_code": "NY.GDP.MKTP.CD",
            "unit": "Current US$",
            "frequency": "Annual",
            "description": "GDP at purchaser's prices is the sum of gross value added by all resident producers.",
            "start_year": 1960,
            "end_year": 2026
        },
        "gdp_growth": {
            "metric_code": "gdp_growth",
            "metric_name": "GDP Growth Rate (Annual %)",
            "category": "historical",
            "provider": "WB",
            "dataset": "WDI",
            "indicator_code": "NY.GDP.MKTP.KD.ZG",
            "unit": "%",
            "frequency": "Annual",
            "description": "Annual percentage growth rate of GDP at market prices based on constant local currency.",
            "start_year": 1960,
            "end_year": 2026
        }
    }
    return {
        "settings": {"default_start_year": 1960, "default_end_year": 2030, "db_batch_size": 100},
        "metrics": fallback_metrics
    }


# Initial default metrics catalog object
PIPELINE_CONFIG = load_pipeline_config()
METRICS_CATALOG = PIPELINE_CONFIG["metrics"]
