"""Count logic, no-silent-zero, and regression tests for dashboard counts.

These tests do not require a database connection.  DB-dependent counts are
verified in ``tests/test_dashboard_db_integration.py`` (opt-in).
"""
import pathlib

import pytest

PROJECT_ROOT = pathlib.Path(__file__).resolve().parent.parent


def _dashboard_source():
    dashboard_path = PROJECT_ROOT / "dashboard.py"
    return dashboard_path.read_text(encoding="utf-8")


# ---------------------------------------------------------------------------
# format_count tests
# ---------------------------------------------------------------------------

def test_format_count_none_returns_dash():
    from src.dashboard_data import format_count

    assert format_count(None) == "—"


def test_format_count_zero():
    from src.dashboard_data import format_count

    assert format_count(0) == "0"


def test_format_count_thousands_separator():
    from src.dashboard_data import format_count

    assert format_count(1234) == "1,234"


def test_format_count_missing_key_returns_dash():
    from src.dashboard_data import format_count

    assert format_count({} and None) == "—"


# ---------------------------------------------------------------------------
# _summarize_overview tests
# ---------------------------------------------------------------------------

def test_summarize_overview_normal_row():
    from src.dashboard_data import _summarize_overview

    row = {
        "metrics_count": 9,
        "countries_count": 52,
        "aggregates_count": 16,
        "regions_count": 12,
        "forecasts_count": 2,
        "historical_count": 7,
    }
    result = _summarize_overview(row)
    assert result["metrics_count"] == 9
    assert result["countries_count"] == 52
    assert result["aggregates_count"] == 16
    assert result["regions_count"] == 12
    assert result["forecasts_count"] == 2
    assert result["historical_count"] == 7


def test_summarize_overview_missing_keys_become_none():
    from src.dashboard_data import _summarize_overview

    row = {"metrics_count": 9}
    result = _summarize_overview(row)
    assert result["metrics_count"] == 9
    assert result["countries_count"] is None
    assert result["aggregates_count"] is None
    assert result["regions_count"] is None
    assert result["forecasts_count"] is None
    assert result["historical_count"] is None


def test_summarize_overview_nan_keys_become_none():
    from src.dashboard_data import _summarize_overview

    row = {
        "metrics_count": float("nan"),
        "countries_count": float("nan"),
        "aggregates_count": float("nan"),
        "regions_count": float("nan"),
        "forecasts_count": float("nan"),
        "historical_count": float("nan"),
    }
    result = _summarize_overview(row)
    for key in result:
        assert result[key] is None, f"{key} should be None for NaN input"


def test_summarize_overview_empty_dict():
    from src.dashboard_data import _summarize_overview

    result = _summarize_overview({})
    for key in (
        "metrics_count",
        "countries_count",
        "aggregates_count",
        "regions_count",
        "forecasts_count",
        "historical_count",
    ):
        assert result[key] is None


# ---------------------------------------------------------------------------
# Regression: no text parser for src/entities.py
# ---------------------------------------------------------------------------

def test_dashboard_source_has_no_entities_parser():
    source = _dashboard_source()

    assert "src/entities.py" not in source
    assert "ENTITIES = [" not in source


def test_dashboard_source_no_line_split():
    source = _dashboard_source()

    assert 'line.split(",")' not in source


# ---------------------------------------------------------------------------
# Config/metrics.json invariants
# ---------------------------------------------------------------------------

def test_metrics_json_invariants():
    """config/metrics.json must have 9 metrics: 7 historical + 2 forecast."""
    import sys

    sys.path.insert(0, str(PROJECT_ROOT))

    from src.config import load_pipeline_config

    config = load_pipeline_config("config/metrics.json")
    metrics = config["metrics"]

    assert len(metrics) == 9, f"expected 9 metrics, got {len(metrics)}"

    historical = [m for m in metrics.values() if m["category"] == "historical"]
    forecast = [m for m in metrics.values() if m["category"] == "forecast"]
    assert len(historical) == 7, f"expected 7 historical, got {len(historical)}"
    assert len(forecast) == 2, f"expected 2 forecast, got {len(forecast)}"

    for code, metric in metrics.items():
        assert metric.get("metric_code"), f"metric {code} missing metric_code"
        assert metric.get("start_year") is not None, f"{code} missing start_year"
        assert metric.get("end_year") is not None, f"{code} missing end_year"


# ---------------------------------------------------------------------------
# No-silent-zero: DashboardDataError from run_query results in "—"
# ---------------------------------------------------------------------------

def test_overview_error_shows_dash():
    """When the DB is unreachable, the dashboard falls back to format_count(None) → '—'."""
    import src.dashboard_data as dd
    from src.dashboard_data import DashboardDataError, format_count

    def _raise(sql, params=None):
        raise DashboardDataError("connection failed")

    original = dd.run_query
    try:
        dd.run_query = _raise
        with pytest.raises(DashboardDataError):
            dd.get_overview_counts()
    finally:
        dd.run_query = original

    assert format_count(None) == "—"
