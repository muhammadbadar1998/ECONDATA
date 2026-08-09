"""
OPT-IN integration test: exercises the full normalized pipeline against a real
PostgreSQL database (idempotency included). Skipped unless a connection string is
provided via the ECONDATA_TEST_DB_CONN env var.

Example (local throwaway cluster):
    initdb -D /tmp/pg -A trust
    pg_ctl -D /tmp/pg -o "-p 55432" start
    $env:ECONDATA_TEST_DB_CONN = "postgresql+psycopg2://postgres@127.0.0.1:55432/postgres"
    pytest tests/test_integration.py
"""
import os

import pytest

from src.config import load_pipeline_config
from src.db import init_db_schema
from src.db_loader import DatabaseLoader
from src.entity_resolver import EntityResolver
from src.transformer import DataTransformer

CONN = os.getenv("ECONDATA_TEST_DB_CONN")


@pytest.mark.skipif(not CONN, reason="ECONDATA_TEST_DB_CONN not set (opt-in integration test)")
def test_full_normalized_pipeline_idempotent():
    from sqlalchemy import create_engine

    loader = DatabaseLoader(CONN)

    # Clean, repeatable run.
    with create_engine(CONN).begin() as conn:
        conn.exec_driver_sql("DROP SCHEMA IF EXISTS public CASCADE;")
        conn.exec_driver_sql("CREATE SCHEMA public;")

    # 1. Schema
    assert init_db_schema(CONN, "sql/dbe_postgres_schema.sql"), "schema init failed"

    # 2. Seed entities/aliases/memberships
    er_ = EntityResolver()
    ents, aliases = er_.seed_rows()
    loader.seed_entities(ents, aliases)
    loader.seed_memberships()
    with loader.engine.raw_connection() as conn:
        cur = conn.cursor()
        cur.execute("SELECT count(*) FROM dbe_entities")
        assert cur.fetchone()[0] >= 80
        cur.execute("SELECT count(*) FROM dbe_entity_aliases")
        assert cur.fetchone()[0] >= 90
        cur.execute("SELECT count(*) FROM dbe_entity_membership")
        assert cur.fetchone()[0] > 0

    # 3. Metrics + sources
    catalog = load_pipeline_config("config/metrics.json")["metrics"]
    metrics_records = DataTransformer.create_metrics_records(catalog=catalog)
    loader.upsert_metrics(metrics_records)
    metric_ids, _ = loader.get_metric_and_source_ids()
    loader.upsert_sources(metrics_records, metric_ids)
    metric_ids, source_ids = loader.get_metric_and_source_ids()
    with loader.engine.raw_connection() as conn:
        cur = conn.cursor()
        cur.execute("SELECT count(*) FROM dbe_metrics")
        assert cur.fetchone()[0] == len(catalog)
        cur.execute("SELECT count(*) FROM dbe_sources")
        assert cur.fetchone()[0] == len(catalog)

    # 4. Synthetic observations (no network) spanning annual + quarterly.
    synth = {
        "gdp_usd": [
            {"dimensions": {"country": "USA"}, "period": ["2022", "2023", "2024"],
             "value": [25000000.0, 26000000.0, 27000000.0]},
            {"dimensions": {"country": "DEU"}, "period": ["2023", "2024"],
             "value": [4100000.0, 4200000.0]},
        ],
        "inflation_cpi": [
            {"dimensions": {"country": "USA"}, "period": ["2023", "2024"], "value": [4.1, 2.9]},
        ],
        "unemployment_rate": [
            {"dimensions": {"country": "USA"},
             "period": ["2023-Q1", "2023-Q2", "2023-Q3"], "value": [3.5, 3.6, 3.8]},
        ],
    }
    total = 10  # 5 + 2 + 3
    for metric_code, raw_series in synth.items():
        obs = DataTransformer.transform_series_list(metric_code, raw_series,
                                                    catalog=catalog, entity_resolver=er_)
        loader.upsert_observations(metric_code, obs, metric_ids[metric_code], source_ids[metric_code])

    # 5. Idempotency: re-run the same data -> no duplicates.
    for metric_code, raw_series in synth.items():
        obs = DataTransformer.transform_series_list(metric_code, raw_series,
                                                    catalog=catalog, entity_resolver=er_)
        loader.upsert_observations(metric_code, obs, metric_ids[metric_code], source_ids[metric_code])
    with loader.engine.raw_connection() as conn:
        cur = conn.cursor()
        cur.execute("SELECT count(*) FROM dbe_observations")
        assert cur.fetchone()[0] == total, "idempotency broken: duplicate rows inserted"

    # 6. Views reflect correct latest values.
    with loader.engine.raw_connection() as conn:
        cur = conn.cursor()
        cur.execute(
            "SELECT gdp_usd_latest, gdp_usd_period FROM v_latest_country_snapshot "
            "WHERE entity_code='USA'"
        )
        latest, period = cur.fetchone()
        assert latest == 27000000.0, f"unexpected latest {latest}"
        assert str(period) == "2024-01-01"

    # 7. Quarterly period correctly typed (not conflated with annual sort-by-string).
    with loader.engine.raw_connection() as conn:
        cur = conn.cursor()
        cur.execute(
            "SELECT start_date, end_date FROM dbe_observations o "
            "JOIN dbe_metrics m ON m.metric_id=o.metric_id "
            "WHERE m.metric_code='unemployment_rate' AND o.start_date='2023-07-01'"
        )
        sd, ed = cur.fetchone()
        assert str(sd) == "2023-07-01" and str(ed) == "2023-09-30"

    # 8. Audit log record.
    loader.log_pipeline_run("econdata_test_dag", "test_task", 3, total, 0,
                            ["WB", "OECD"], list(source_ids.values()), "SUCCESS", 1.5)
    with loader.engine.raw_connection() as conn:
        cur = conn.cursor()
        cur.execute("SELECT observations_upserted FROM dbe_pipeline_runs LIMIT 1")
        assert cur.fetchone()[0] == total
