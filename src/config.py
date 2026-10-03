import json
import logging
import os
from typing import Any, Dict, Optional, Tuple

# Load environment variables from .env file(s) if they exist.
# Searches the current directory (.env), plus the conventional config/.env.
try:
    from dotenv import load_dotenv
    load_dotenv()                     # CWD .env
    load_dotenv("config/.env")        # repo config/.env (gitignored)
    load_dotenv(".env")               # repo root .env
except ImportError:
    pass

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
# Discrete connection parts (fallback when no full URL is provided).
SUPABASE_HOST = os.getenv("SUPABASE_HOST")
SUPABASE_DB = os.getenv("SUPABASE_DB", "postgres")
SUPABASE_USER = os.getenv("SUPABASE_USER", "postgres")
SUPABASE_PASSWORD = os.getenv("SUPABASE_PASSWORD")
SUPABASE_PORT = os.getenv("SUPABASE_PORT", "5432")

# Environment variables that may hold a full connection URL, in priority order.
# The namespaced ECONDATA_DATABASE_URL is preferred so an ambient DATABASE_URL
# belonging to another tool cannot silently override this pipeline. A Supabase
# Session pooler URL (aws-<n>-<region>.pooler.supabase.com) is IPv4, unlike the
# IPv6-only direct db.<ref>.supabase.co host.
_DATABASE_URL_ENV_VARS = (
    "ECONDATA_DATABASE_URL",
    "DATABASE_URL",
    "SHAREDPOOLER",
    "sharedpooler",
)


def _resolve_database_url() -> Tuple[Optional[str], Optional[str]]:
    """Return ``(url, source_env_var)``, reading the environment at call time."""
    for name in _DATABASE_URL_ENV_VARS:
        value = os.getenv(name)
        if value:
            return value.strip(), name
    return None, None


def _normalize_database_url(url: str) -> str:
    """Ensure a SQLAlchemy psycopg2 driver scheme."""
    if url.startswith("postgres://"):
        return "postgresql+psycopg2://" + url[len("postgres://"):]
    if url.startswith("postgresql://"):
        return "postgresql+psycopg2://" + url[len("postgresql://"):]
    return url


def get_supabase_connection_string() -> str:
    """Constructs the PostgreSQL connection string.

    Prefers a full URL from ``ECONDATA_DATABASE_URL``/``DATABASE_URL``/
    ``SHAREDPOOLER`` (resolved at call time so credential edits can be picked up),
    falling back to the discrete ``SUPABASE_*`` variables.
    """
    url, source = _resolve_database_url()
    if url:
        if source == "DATABASE_URL":
            logger.warning(
                "Using the ambient DATABASE_URL environment variable; prefer "
                "ECONDATA_DATABASE_URL to avoid unintended overrides."
            )
        else:
            logger.info("Using database connection URL from %s.", source)
        return _normalize_database_url(url)

    host = os.getenv("SUPABASE_HOST")
    password = os.getenv("SUPABASE_PASSWORD")
    if not all([host, password]):
        raise ValueError(
            "Supabase connection credentials not fully set in environment variables. "
            "Set ECONDATA_DATABASE_URL (or SHAREDPOOLER), or SUPABASE_HOST and "
            "SUPABASE_PASSWORD (plus optionally SUPABASE_USER, SUPABASE_DB, SUPABASE_PORT)."
        )

    user = os.getenv("SUPABASE_USER", "postgres")
    port = os.getenv("SUPABASE_PORT", "5432")
    database = os.getenv("SUPABASE_DB", "postgres")
    return f"postgresql+psycopg2://{user}:{password}@{host}:{port}/{database}"


def _default_db_connection_string() -> str:
    """Resolver used by modules that need a connection string at import time.

    Kept lazy so importing :mod:`src.config` (and therefore every module in the
    package) does NOT raise when Supabase credentials are not yet present in the
    environment. The string is only materialized on first access.
    """
    return get_supabase_connection_string()


# Backwards-compatible alias. Resolved at call time (not import time) so that
# importing this module never raises when DB credentials are unavailable.
def get_default_db_connection_string() -> str:
    return _default_db_connection_string()


# --------------------------------------------------------------------------- #
# LLM / RAG runtime (chatbot over the semantic catalog)
# --------------------------------------------------------------------------- #
# Defaults target a local Ollama OpenAI-compatible endpoint. Any OpenAI-compatible
# server (vLLM, LM Studio, OpenAI, Groq, ...) works by overriding the env vars.
# These are *functions*, not module-level reads, so importing this module stays
# side-effect free and tests can monkeypatch the environment after import.
DEFAULT_LLM_BASE_URL = "http://127.0.0.1:11434/v1"
DEFAULT_LLM_MODEL = "qwen2.5:7b-instruct"
DEFAULT_LLM_TIMEOUT = 120
DEFAULT_LLM_TEMPERATURE = 0.2
DEFAULT_RAG_TOP_K = 8
DEFAULT_RAG_RECENT_POINTS = 5


def get_llm_base_url() -> str:
    """Base URL of an OpenAI-compatible ``/chat/completions`` endpoint."""
    return (os.getenv("LLM_BASE_URL") or DEFAULT_LLM_BASE_URL).rstrip("/")


def get_llm_api_key() -> str:
    """API key for the LLM endpoint; empty for local servers that ignore auth."""
    return (os.getenv("LLM_API_KEY") or "").strip()


def get_llm_model() -> str:
    """Chat model name (must be a chat-capable model, e.g. ``qwen2.5:7b-instruct``)."""
    return os.getenv("LLM_MODEL") or DEFAULT_LLM_MODEL


def get_llm_timeout() -> int:
    """Per-request timeout in seconds for chat completions."""
    return int(os.getenv("LLM_TIMEOUT") or DEFAULT_LLM_TIMEOUT)


def get_llm_temperature() -> float:
    """Sampling temperature for chat completions (low = factual)."""
    return float(os.getenv("LLM_TEMPERATURE") or DEFAULT_LLM_TEMPERATURE)


def get_rag_top_k() -> int:
    """Default number of documents retrieved per question."""
    return int(os.getenv("RAG_TOP_K") or DEFAULT_RAG_TOP_K)


def get_rag_recent_points() -> int:
    """Number of trailing observations embedded into each entity_metric document."""
    return int(os.getenv("RAG_RECENT_POINTS") or DEFAULT_RAG_RECENT_POINTS)


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
                "end_year": item.get("end_year"),
                "synonyms": item.get("synonyms", []),
                "base_unit": item.get("base_unit", item.get("unit", "")),
                "unit_normalization": item.get("unit_normalization", ""),
                "dimension_key": item.get("dimension_key", "country"),
                "entity_scope": item.get("entity_scope", "country"),
                "aggregation": item.get("aggregation", "mean"),
            }

        logger.debug("Loaded %d metric definitions from %s.", len(metrics_catalog), config_path)
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
            "end_year": 2026,
            "synonyms": ["GDP", "gross domestic product", "economic output"],
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


# Initial default metrics catalog object
PIPELINE_CONFIG = load_pipeline_config()
METRICS_CATALOG = PIPELINE_CONFIG["metrics"]

