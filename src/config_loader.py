import json
import logging
import os
from typing import Any, Dict

from src.config import (
    DEFAULT_CONFIG_PATH,
)

logger = logging.getLogger(__name__)


def load_metrics_config(config_path: str = DEFAULT_CONFIG_PATH) -> Dict[str, Any]:
    """Load metrics configuration from JSON file and validate required fields."""
    if not os.path.exists(config_path):
        logger.warning(f"Config file '{config_path}' not found. Using fallback built-in indicators catalog.")
        return load_fallback_config()

    try:
        with open(config_path, 'r', encoding='utf-8') as f:
            data = json.load(f)

        pipeline_settings = data.get("pipeline_settings", {})
        metrics_list = data.get("metrics", [])

        # Validate required fields for each metric
        validated_metrics = {}
        for item in metrics_list:
            metric_code = item.get("metric_code")
            if not metric_code:
                logger.warning("Metric missing 'metric_code', skipping")
                continue

            # Set defaults for optional fields
            metric_info = {
                "metric_code": metric_code,
                "metric_name": item.get("metric_name", metric_code),
                "category": item.get("category", "historical"),
                "provider": item.get("provider", "WB"),
                "dataset": item.get("dataset", "WDI"),
                "indicator_code": item.get("indicator_code", ""),
                "unit": item.get("unit", ""),
                "frequency": item.get("frequency", "Annual"),
                "description": item.get("description", ""),
                "start_year": item.get("start_year"),
                "end_year": item.get("end_year"),
                "synonyms": item.get("synonyms", []),
                "base_unit": item.get("base_unit", item.get("unit", "")),
                "unit_normalization": item.get("unit_normalization", ""),
                "dimension_key": item.get("dimension_key", "country"),
                "entity_scope": item.get("entity_scope", "country"),
            }
            validated_metrics[metric_code] = metric_info

        return {
            "settings": pipeline_settings,
            "metrics": validated_metrics
        }

    except Exception as e:
        logger.error(f"Error loading configuration file '{config_path}': {e}. Using fallback defaults.", exc_info=True)
        return load_fallback_config()


def load_fallback_config() -> Dict[str, Any]:
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
            "end_year": 2026,
            "synonyms": ["GDP", "gross domestic product"],
            "base_unit": "Current US$",
            "unit_normalization": "",
            "dimension_key": "country",
            "entity_scope": "country",
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
            "end_year": 2026,
            "synonyms": ["GDP growth", "economic growth"],
            "base_unit": "%",
            "unit_normalization": "",
            "dimension_key": "country",
            "entity_scope": "country",
        }
    }

    return {
        "settings": {"default_start_year": 1960, "default_end_year": 2030, "db_batch_size": 100},
        "metrics": fallback_metrics
    }
