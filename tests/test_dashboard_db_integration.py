"""
OPT-IN integration test: verifies dashboard DB query helpers against a real
Supabase PostgreSQL database.  Skipped unless a connection string is
provided via ECONDATA_TEST_DB_CONN (or ECONDATA_DATABASE_URL / SHAREDPOOLER).

Example (local throwaway cluster):
    initdb -D /tmp/pg -A trust
    pg_ctl -D /tmp/pg -o "-p 55432" start
    $env:ECONDATA_TEST_DB_CONN = "postgresql+psycopg2://postgres@127.0.0.1:55432/postgres"
    pytest tests/test_dashboard_db_integration.py
"""

import os

import pytest

CONN = (
    os.getenv("ECONDATA_TEST_DB_CONN")
    or os.getenv("ECONDATA_DATABASE_URL")
    or os.getenv("SHAREDPOOLER")
)


@pytest.mark.skipif(not CONN, reason="ECONDATA_TEST_DB_CONN not set (opt-in integration test)")
class TestDashboardDBIntegration:
    def test_check_connection(self):
        from src.dashboard_data import check_connection

        connected, _ = check_connection()
        assert connected is True

    def test_overview_counts(self):
        import pandas as pd

        from src.dashboard_data import (
            DashboardDataError,
            get_overview_counts,
            get_totals,
        )

        try:
            counts = get_overview_counts()
        except DashboardDataError:
            pytest.fail("get_overview_counts raised DashboardDataError")

        for key in (
            "metrics_count",
            "countries_count",
            "aggregates_count",
            "regions_count",
            "forecasts_count",
            "historical_count",
        ):
            assert key in counts, f"missing {key}"
            val = counts[key]
            assert isinstance(val, (int, float)) or pd.isna(val) is False, (
                f"{key} is not a number"
            )
            assert val >= 0, f"{key} is negative"

        assert (
            counts["countries_count"]
            + counts["aggregates_count"]
            + counts["regions_count"]
            <= get_totals()["entities_count"]
        ), "entity type counts exceed total entities"
        assert counts["metrics_count"] == (
            counts["forecasts_count"] + counts["historical_count"]
        ), "metrics_count != forecasts + historical"
        assert counts["countries_count"] > 0, "DB has no countries"

    def test_metrics_reconcile_with_catalog(self):
        from src.config import load_pipeline_config
        from src.dashboard_data import get_metric_coverage

        catalog = load_pipeline_config("config/metrics.json")["metrics"]
        coverage = get_metric_coverage()

        db_codes = set(coverage["metric_code"].unique())
        catalog_codes = set(catalog.keys())

        missing_in_db = catalog_codes - db_codes
        if missing_in_db:
            pytest.fail(
                f"Declared metrics missing from DB: {missing_in_db}"
            )

        extras = db_codes - catalog_codes
        if extras:
            import logging

            logging.warning(f"DB has metrics not in catalog: {extras}")

    def test_entities_by_type(self):
        from src.dashboard_data import (
            DashboardDataError,
            get_entities_by_type,
        )

        for entity_type in ("country", "aggregate", "region"):
            try:
                df = get_entities_by_type(entity_type)
            except DashboardDataError:
                pytest.fail(f"get_entities_by_type('{entity_type}') raised")

            assert not df.empty, f"no {entity_type} entities returned"
            assert all(df["entity_type"] == entity_type), (
                f"wrong entity_type in results for {entity_type}"
            )
            assert all(df["entity_code"].str.len() > 0), (
                f"empty entity_code in {entity_type}"
            )

    def test_entities_by_type_counts_match_overview(self
    ):
        from src.dashboard_data import get_entities_by_type, get_overview_counts

        counts = get_overview_counts()
        for entity_type, count_key in (
            ("country", "countries_count"),
            ("aggregate", "aggregates_count"),
            ("region", "regions_count"),
        ):
            df = get_entities_by_type(entity_type)
            assert len(df) == counts[count_key], (
                f"{entity_type}: DB list length {len(df)} != "
                f"overview tile {counts[count_key]}"
            )

    def test_entity_aliases_resolve(self):
        from src.dashboard_data import (
            DashboardDataError,
            get_entity_aliases,
        )

        try:
            aliases = get_entity_aliases()
        except DashboardDataError:
            pytest.fail("get_entity_aliases raised")

        if not aliases.empty:
            assert "entity_code" in aliases.columns
            valid_codes = set(aliases["entity_code"].unique())
            for code in valid_codes:
                assert len(code) > 0, "empty entity_code in aliases"

    def test_dated_view_has_data(self):
        from src.config import load_pipeline_config
        from src.dashboard_data import run_query

        catalog = load_pipeline_config("config/metrics.json")["metrics"]
        first_metric_code = list(catalog.keys())[0]

        df = run_query(
            f"""
            SELECT * FROM v_dated_execution_snapshot
            WHERE metric_code = '{first_metric_code}'
            LIMIT 1;
            """
        )
        assert not df.empty, (
            f"v_dated_execution_snapshot has no rows for {first_metric_code}"
        )
