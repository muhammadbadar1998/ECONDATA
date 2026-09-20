"""Data-access helpers for the Streamlit dashboard.

All functions read from the live Supabase PostgreSQL database through the
existing SQLAlchemy engine in ``src.db``. Connection problems are surfaced with
actionable messages instead of being swallowed, and active connection checks are
cached briefly so the UI stays responsive when the database is unreachable.
"""

import math
from typing import Dict, List, Optional, Tuple

import pandas as pd
import streamlit as st
from sqlalchemy import bindparam, text
from sqlalchemy.engine import Engine


class DashboardDataError(Exception):
    """Raised when the dashboard cannot read data from the database."""


class DashboardConfigError(DashboardDataError):
    """Raised when database credentials/configuration are missing or invalid."""


# Safety cap for explorer result sets (keeps reruns and CSV serialization bounded).
OBSERVATIONS_ROW_LIMIT = 50000


@st.cache_resource(show_spinner=False)
def get_engine_cached() -> Tuple[Optional[Engine], Optional[str]]:
    """Build (and cache) the SQLAlchemy engine.

    Returns ``(engine, error)``. ``engine`` is ``None`` when configuration is
    missing/invalid or the engine cannot be constructed. Engine creation is lazy
    and does not open a socket, so reachability/auth problems are reported later
    by :func:`check_connection`.
    """
    try:
        from src.config import get_supabase_connection_string
        from src.db import get_sqlalchemy_engine
    except ValueError as exc:
        return None, f"Database configuration error: {exc}"
    except Exception as exc:  # defensive: surface import-time failures too
        return None, f"Failed to import database module: {exc}"

    try:
        # Resolve the connection string at call time so a cleared cache (Refresh)
        # can pick up edited credentials without a process restart.
        return get_sqlalchemy_engine(get_supabase_connection_string()), None
    except Exception as exc:
        return None, f"Failed to create database engine: {exc}"


def reload_env() -> None:
    """Re-read .env files so credential edits apply without a process restart."""
    try:
        from dotenv import load_dotenv
    except ImportError:
        return
    load_dotenv("config/.env", override=True)
    load_dotenv(".env", override=True)


def _explain_connection_error(error: Exception) -> str:
    """Translate low-level driver errors into actionable guidance."""
    message = str(error)
    lowered = message.lower()

    if any(token in lowered for token in (
        "could not translate host name",
        "name or service not known",
        "nodename nor servname",
        "temporary failure in name resolution",
        "unknown server error",
        "getaddrinfo failed",
    )):
        return (
            "Cannot resolve the Supabase host in config/.env. The project may be "
            "paused or deleted, or the project ref is wrong. If the project is "
            "active, the direct db.<ref>.supabase.co host is IPv6-only; use the "
            "Session pooler connection string (IPv4) from the Supabase dashboard. "
            f"Details: {message}"
        )
    if "password authentication failed" in lowered:
        return (
            "Authentication failed. Check SUPABASE_USER and SUPABASE_PASSWORD in "
            f"config/.env. Details: {message}"
        )
    if "timeout" in lowered or "timed out" in lowered:
        return (
            "Connection timed out. Check the Supabase IP allowlist / network access. "
            f"Details: {message}"
        )
    if "network is unreachable" in lowered or "no route to host" in lowered:
        return (
            "No network route to the database. The host may be IPv6-only while this "
            "machine has no IPv6 connectivity; use the Session pooler (IPv4) host. "
            f"Details: {message}"
        )
    return message


def _check_connection_uncached() -> Tuple[bool, str]:
    engine, error = get_engine_cached()
    if engine is None:
        return False, error or "Database engine is unavailable."
    try:
        with engine.connect() as conn:
            version = conn.execute(text("SELECT version();")).scalar()
        return True, str(version)
    except Exception as exc:
        return False, _explain_connection_error(exc)


@st.cache_data(ttl=30, show_spinner=False)
def check_connection() -> Tuple[bool, str]:
    """Actively test the database connection (result cached for 30 seconds)."""
    return _check_connection_uncached()


def _engine_or_raise() -> Engine:
    engine, error = get_engine_cached()
    if engine is None:
        raise DashboardConfigError(error or "Database engine is unavailable.")
    return engine


def run_query(sql: str, params: Optional[Dict] = None) -> pd.DataFrame:
    engine = _engine_or_raise()
    try:
        return pd.read_sql(text(sql), engine, params=params)
    except Exception as exc:
        raise DashboardDataError(_explain_connection_error(exc)) from exc


@st.cache_data(ttl=60, show_spinner=False)
def get_pipeline_runs(limit: int = 50) -> pd.DataFrame:
    sql = """
        SELECT run_timestamp, dag_id, task_id, metrics_extracted,
               observations_upserted, observations_skipped, providers, source_ids,
               status, execution_seconds
        FROM dbe_pipeline_runs
        ORDER BY run_timestamp DESC
        LIMIT :limit;
    """
    return run_query(sql, params={"limit": limit})


@st.cache_data(ttl=60, show_spinner=False)
def get_totals() -> Dict:
    sql = """
        SELECT
            (SELECT COUNT(*) FROM dbe_metrics)                  AS metrics_count,
            (SELECT COUNT(*) FROM dbe_entities WHERE is_active) AS entities_count,
            (SELECT COUNT(*) FROM dbe_observations)             AS observations_count,
            (SELECT COUNT(*) FROM dbe_sources)                  AS sources_count,
            (SELECT COUNT(DISTINCT provider) FROM dbe_sources)  AS providers_count,
            (SELECT MIN(start_date) FROM dbe_observations)      AS min_date,
            (SELECT MAX(end_date) FROM dbe_observations)        AS max_date,
            (SELECT MAX(extracted_at) FROM dbe_observations)    AS last_extracted_at;
    """
    df = run_query(sql)
    return {} if df.empty else df.iloc[0].to_dict()


@st.cache_data(ttl=60, show_spinner=False)
def get_metric_coverage() -> pd.DataFrame:
    sql = """
        SELECT
            m.metric_code,
            m.metric_name,
            m.category,
            m.frequency,
            m.base_unit,
            COUNT(DISTINCT o.id)                  AS observation_count,
            COUNT(DISTINCT o.entity_id)           AS entity_count,
            COUNT(DISTINCT o.source_id)           AS source_count,
            MIN(o.start_date)                     AS min_date,
            MAX(o.end_date)                       AS max_date,
            MAX(o.extracted_at)                   AS last_extracted_at,
            string_agg(DISTINCT s.provider, ', ') AS providers
        FROM dbe_metrics m
        LEFT JOIN dbe_observations o ON o.metric_id = m.metric_id
        LEFT JOIN dbe_sources s      ON s.metric_id = m.metric_id
        GROUP BY m.metric_code, m.metric_name, m.category, m.frequency, m.base_unit
        ORDER BY m.metric_code;
    """
    return run_query(sql)


def merge_declared_coverage(coverage_df: pd.DataFrame) -> pd.DataFrame:
    """Attach declared bounds from config/metrics.json and derive coverage gaps."""
    if coverage_df.empty:
        return coverage_df

    # Imported lazily: src.config evaluates the connection string at import time.
    from src.config import load_pipeline_config

    catalog = load_pipeline_config().get("metrics", {})
    rows = []
    for _, row in coverage_df.iterrows():
        declared = catalog.get(row["metric_code"], {})
        declared_start = declared.get("start_year")
        declared_end = declared.get("end_year")

        min_date = pd.to_datetime(row["min_date"]) if pd.notna(row["min_date"]) else pd.NaT
        max_date = pd.to_datetime(row["max_date"]) if pd.notna(row["max_date"]) else pd.NaT
        actual_start = int(min_date.year) if pd.notna(min_date) else None
        actual_end = int(max_date.year) if pd.notna(max_date) else None
        end_year_gap = (
            declared_end - actual_end
            if declared_end is not None and actual_end is not None
            else None
        )

        rows.append({
            **row.to_dict(),
            "declared_start_year": declared_start,
            "declared_end_year": declared_end,
            "actual_start_year": actual_start,
            "actual_end_year": actual_end,
            "end_year_gap": end_year_gap,
            "has_data": bool(row.get("observation_count")),
            "min_date": min_date.strftime("%Y-%m-%d") if pd.notna(min_date) else None,
            "max_date": max_date.strftime("%Y-%m-%d") if pd.notna(max_date) else None,
            "last_extracted_at": (
                pd.to_datetime(row["last_extracted_at"]).strftime("%Y-%m-%d %H:%M")
                if pd.notna(row["last_extracted_at"]) else None
            ),
        })
    return pd.DataFrame(rows)


@st.cache_data(ttl=60, show_spinner=False)
def get_entity_options() -> pd.DataFrame:
    sql = """
        SELECT DISTINCT e.entity_code, e.entity_name, e.entity_type
        FROM dbe_entities e
        JOIN dbe_observations o ON o.entity_id = e.entity_id
        WHERE e.is_active = TRUE
        ORDER BY e.entity_type, e.entity_code;
    """
    return run_query(sql)


def get_observations(
    metric_codes: List[str],
    entity_codes: List[str],
    start_date: str,
    end_date: str,
    limit: int = OBSERVATIONS_ROW_LIMIT,
) -> pd.DataFrame:
    """Fetch observations for the explorer from the dated execution view."""
    if not metric_codes or not entity_codes:
        return pd.DataFrame()

    sql = """
        SELECT country_code, country_name, metric_code, metric_name, category,
               frequency, base_unit, start_period, end_period, val, unit,
               period_str, extracted_at
        FROM v_dated_execution_snapshot
        WHERE metric_code IN :metric_codes
          AND country_code IN :entity_codes
          AND start_period BETWEEN :start_date AND :end_date
        ORDER BY metric_code, country_code, start_period
        LIMIT :limit;
    """
    statement = text(sql).bindparams(
        bindparam("metric_codes", expanding=True),
        bindparam("entity_codes", expanding=True),
    )
    engine = _engine_or_raise()
    try:
        return pd.read_sql(statement, engine, params={
            "metric_codes": metric_codes,
            "entity_codes": entity_codes,
            "start_date": start_date,
            "end_date": end_date,
            "limit": limit,
        })
    except Exception as exc:
        raise DashboardDataError(_explain_connection_error(exc)) from exc


def format_count(value) -> str:
    """Format a count for display; never returns a bare zero for missing data."""
    if value is None:
        return "—"
    try:
        return f"{int(value):,}"
    except (TypeError, ValueError):
        return "—"


def _summarize_overview(row: Dict[str, object]) -> Dict[str, Optional[int]]:
    """Normalize a raw overview row into tile values.

    Missing or NaN keys become ``None``.  Only the six named tile keys
    are returned so that custom entity types never leak into the UI.
    """

    def _as_int(key):
        val = row.get(key)
        if val is None or (isinstance(val, float) and math.isnan(val)):
            return None
        try:
            return int(val)
        except (TypeError, ValueError):
            return None

    return {
        "metrics_count": _as_int("metrics_count"),
        "countries_count": _as_int("countries_count"),
        "aggregates_count": _as_int("aggregates_count"),
        "regions_count": _as_int("regions_count"),
        "forecasts_count": _as_int("forecasts_count"),
        "historical_count": _as_int("historical_count"),
    }


@st.cache_data(ttl=60, show_spinner=False)
def get_overview_counts() -> Dict[str, object]:
    """Return one value per Overview tile from Supabase."""
    sql = """
        SELECT
            (SELECT COUNT(*) FROM dbe_metrics)                                                AS metrics_count,
            (SELECT COUNT(*) FROM dbe_entities WHERE is_active AND entity_type='country')      AS countries_count,
            (SELECT COUNT(*) FROM dbe_entities WHERE is_active AND entity_type='aggregate')    AS aggregates_count,
            (SELECT COUNT(*) FROM dbe_entities WHERE is_active AND entity_type='region')       AS regions_count,
            (SELECT COUNT(*) FROM dbe_metrics WHERE category='forecast')                       AS forecasts_count,
            (SELECT COUNT(*) FROM dbe_metrics WHERE category='historical')                     AS historical_count;
    """
    df = run_query(sql)
    if df.empty:
        return _summarize_overview({})
    return _summarize_overview(df.iloc[0].to_dict())


@st.cache_data(ttl=60, show_spinner=False)
def get_entities_by_type(entity_type: str) -> pd.DataFrame:
    """Return entities of a given type that have observations.

    Delegates to :func:`get_entity_options` (which inner-joins
    ``dbe_observations``) and filters by ``entity_type``.
    """
    options = get_entity_options()
    if options.empty:
        return options
    return options[options["entity_type"] == entity_type].reset_index(drop=True)


@st.cache_data(ttl=60, show_spinner=False)
def get_entity_aliases(limit: int = 50) -> pd.DataFrame:
    """Return provider/provider-code → entity_code mappings."""
    sql = """
        SELECT a.provider, a.provider_code, e.entity_code
        FROM dbe_entity_aliases a
        JOIN dbe_entities e ON e.entity_id = a.entity_id
        ORDER BY a.provider, a.provider_code
        LIMIT :limit;
    """
    return run_query(sql, params={"limit": limit})
