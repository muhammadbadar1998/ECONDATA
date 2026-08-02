import logging
from typing import Dict
import pandas as pd
import numpy as np
from sqlalchemy import text
from sqlalchemy.engine import Engine
import psycopg2.extras

from src.config import (
    DEFAULT_DB_CONNECTION_STRING,
    DBE_TABLE_COUNTRIES,
    DBE_TABLE_METRICS,
    DBE_TABLE_OBSERVATIONS,
    DBE_TABLE_SNAPSHOTS,
    DBE_TABLE_RUNS
)
from src.db import get_sqlalchemy_engine, init_db_schema

logger = logging.getLogger(__name__)


def sanitize_dataframe(df: pd.DataFrame) -> pd.DataFrame:
    """Converts NaN / NaT values to Python None for clean SQL NULL insertion."""
    cleaned = df.astype(object).where(pd.notnull(df), None)
    return cleaned


class DatabaseLoader:
    """Manages loading and upserting extracted data into PostgreSQL (Supabase) or SQL Server dbe_* tables."""

    def __init__(self, connection_string: str = DEFAULT_DB_CONNECTION_STRING):
        self.connection_string = connection_string
        self.is_postgres = "postgresql" in connection_string or "postgres" in connection_string
        self.engine: Engine = get_sqlalchemy_engine(connection_string)

    def verify_or_init_schema(self) -> bool:
        """Verifies connection and initializes dbe tables if needed."""
        return init_db_schema(self.connection_string)

    def upsert_countries(self, countries_df: pd.DataFrame) -> int:
        """Bulk UPSERT master countries catalog into dbe_countries."""
        if countries_df.empty:
            logger.warning("Countries DataFrame is empty; skipping database loading.")
            return 0

        df = sanitize_dataframe(countries_df[["country_code", "country_name", "provider", "dataset", "is_aggregate"]])
        logger.info(f"Loading {len(df)} countries into '{DBE_TABLE_COUNTRIES}'...")

        if self.is_postgres:
            upsert_sql = f"""
                INSERT INTO {DBE_TABLE_COUNTRIES} (country_code, country_name, provider, dataset, is_aggregate)
                VALUES %s
                ON CONFLICT (country_code) DO UPDATE SET
                    country_name = EXCLUDED.country_name,
                    provider = EXCLUDED.provider,
                    dataset = EXCLUDED.dataset,
                    is_aggregate = EXCLUDED.is_aggregate,
                    updated_at = CURRENT_TIMESTAMP;
            """
            tuples = [tuple(x) for x in df.to_numpy()]
            raw_conn = self.engine.raw_connection()
            try:
                cursor = raw_conn.cursor()
                psycopg2.extras.execute_values(cursor, upsert_sql, tuples, page_size=100)
                raw_conn.commit()
            finally:
                raw_conn.close()

            logger.info(f"Successfully upserted countries into Supabase '{DBE_TABLE_COUNTRIES}'.")
            return len(df)
        else:
            staging_table = f"#stg_{DBE_TABLE_COUNTRIES}"
            with self.engine.begin() as conn:
                df.to_sql(name=staging_table, con=conn, if_exists="replace", index=False)
                merge_sql = f"""
                    MERGE INTO {DBE_TABLE_COUNTRIES} AS target
                    USING {staging_table} AS source
                    ON (target.country_code = source.country_code)
                    WHEN MATCHED THEN
                        UPDATE SET
                            target.country_name = source.country_name,
                            target.provider = source.provider,
                            target.dataset = source.dataset,
                            target.is_aggregate = source.is_aggregate,
                            target.updated_at = SYSDATETIME()
                    WHEN NOT MATCHED THEN
                        INSERT (country_code, country_name, provider, dataset, is_aggregate, created_at, updated_at)
                        VALUES (source.country_code, source.country_name, source.provider, source.dataset, source.is_aggregate, SYSDATETIME(), SYSDATETIME());
                """
                conn.execute(text(merge_sql))
            logger.info(f"Successfully upserted countries into SQL Server '{DBE_TABLE_COUNTRIES}'.")
            return len(df)

    def upsert_metrics(self, metrics_df: pd.DataFrame) -> int:
        """Bulk UPSERT master metrics catalog into dbe_metrics."""
        if metrics_df.empty:
            logger.warning("Metrics DataFrame is empty; skipping database loading.")
            return 0

        cols = ["metric_code", "metric_name", "provider", "dataset", "indicator_code", "unit", "frequency", "description"]
        df = sanitize_dataframe(metrics_df[cols])
        logger.info(f"Loading {len(df)} metrics into '{DBE_TABLE_METRICS}'...")

        if self.is_postgres:
            upsert_sql = f"""
                INSERT INTO {DBE_TABLE_METRICS} (metric_code, metric_name, provider, dataset, indicator_code, unit, frequency, description)
                VALUES %s
                ON CONFLICT (metric_code) DO UPDATE SET
                    metric_name = EXCLUDED.metric_name,
                    provider = EXCLUDED.provider,
                    dataset = EXCLUDED.dataset,
                    indicator_code = EXCLUDED.indicator_code,
                    unit = EXCLUDED.unit,
                    frequency = EXCLUDED.frequency,
                    description = EXCLUDED.description,
                    updated_at = CURRENT_TIMESTAMP;
            """
            tuples = [tuple(x) for x in df.to_numpy()]
            raw_conn = self.engine.raw_connection()
            try:
                cursor = raw_conn.cursor()
                psycopg2.extras.execute_values(cursor, upsert_sql, tuples, page_size=100)
                raw_conn.commit()
            finally:
                raw_conn.close()

            logger.info(f"Successfully upserted metrics into Supabase '{DBE_TABLE_METRICS}'.")
            return len(df)
        else:
            staging_table = f"#stg_{DBE_TABLE_METRICS}"
            with self.engine.begin() as conn:
                df.to_sql(name=staging_table, con=conn, if_exists="replace", index=False)
                merge_sql = f"""
                    MERGE INTO {DBE_TABLE_METRICS} AS target
                    USING {staging_table} AS source
                    ON (target.metric_code = source.metric_code)
                    WHEN MATCHED THEN
                        UPDATE SET
                            target.metric_name = source.metric_name,
                            target.provider = source.provider,
                            target.dataset = source.dataset,
                            target.indicator_code = source.indicator_code,
                            target.unit = source.unit,
                            target.frequency = source.frequency,
                            target.description = source.description,
                            target.updated_at = SYSDATETIME()
                    WHEN NOT MATCHED THEN
                        INSERT (metric_code, metric_name, provider, dataset, indicator_code, unit, frequency, description, updated_at)
                        VALUES (source.metric_code, source.metric_name, source.provider, source.dataset, source.indicator_code, source.unit, source.frequency, source.description, SYSDATETIME());
                """
                conn.execute(text(merge_sql))
            logger.info(f"Successfully upserted metrics into SQL Server '{DBE_TABLE_METRICS}'.")
            return len(df)

    def upsert_observations(self, metric_code: str, obs_df: pd.DataFrame) -> int:
        """Bulk UPSERT time-series observations into dbe_economic_observations."""
        if obs_df.empty:
            logger.warning(f"Observations DataFrame for '{metric_code}' is empty; skipping database loading.")
            return 0

        raw = obs_df[["country_code", "metric_code", "period", "value", "unit", "frequency", "extracted_at"]].copy()
        raw.rename(columns={"value": "val"}, inplace=True)
        df = sanitize_dataframe(raw)

        if self.is_postgres:
            logger.info(f"Bulk loading {len(df)} observations for '{metric_code}' into Supabase PostgreSQL '{DBE_TABLE_OBSERVATIONS}'...")
            upsert_sql = f"""
                INSERT INTO {DBE_TABLE_OBSERVATIONS} (country_code, metric_code, period, val, unit, frequency, extracted_at)
                VALUES %s
                ON CONFLICT (country_code, metric_code, period) DO UPDATE SET
                    val = EXCLUDED.val,
                    unit = EXCLUDED.unit,
                    frequency = EXCLUDED.frequency,
                    extracted_at = EXCLUDED.extracted_at;
            """
            tuples = [tuple(x) for x in df.to_numpy()]
            raw_conn = self.engine.raw_connection()
            try:
                cursor = raw_conn.cursor()
                psycopg2.extras.execute_values(cursor, upsert_sql, tuples, page_size=100)
                raw_conn.commit()
            finally:
                raw_conn.close()

            logger.info(f"Successfully upserted {len(df)} observations for '{metric_code}' into Supabase PostgreSQL.")
            return len(df)
        else:
            logger.info(f"Bulk loading {len(df)} observations for '{metric_code}' into SQL Server '{DBE_TABLE_OBSERVATIONS}'...")
            staging_table = f"#stg_obs_{metric_code}"
            with self.engine.begin() as conn:
                df.to_sql(name=staging_table, con=conn, if_exists="replace", index=False)
                merge_sql = f"""
                    MERGE INTO {DBE_TABLE_OBSERVATIONS} AS target
                    USING {staging_table} AS source
                    ON (target.country_code = source.country_code 
                        AND target.metric_code = source.metric_code 
                        AND target.period = source.period)
                    WHEN MATCHED THEN
                        UPDATE SET
                            target.val = source.val,
                            target.unit = source.unit,
                            target.frequency = source.frequency,
                            target.extracted_at = source.extracted_at
                    WHEN NOT MATCHED THEN
                        INSERT (country_code, metric_code, period, val, unit, frequency, extracted_at)
                        VALUES (source.country_code, source.metric_code, source.period, source.val, source.unit, source.frequency, source.extracted_at);
                """
                conn.execute(text(merge_sql))
            logger.info(f"Successfully upserted {len(df)} observations for '{metric_code}' into SQL Server.")
            return len(df)

    def upsert_snapshots(self, snapshot_matrix_df: pd.DataFrame) -> int:
        """Bulk UPSERT cross-sectional latest country snapshot matrix into dbe_latest_country_snapshots."""
        if snapshot_matrix_df.empty:
            logger.warning("Snapshot Matrix DataFrame is empty; skipping database loading.")
            return 0

        df = snapshot_matrix_df.copy()
        expected_cols = [
            "country_code", "country_name", "is_aggregate",
            "gdp_usd_latest", "gdp_usd_period",
            "gdp_growth_latest", "gdp_growth_period",
            "inflation_cpi_latest", "inflation_cpi_period",
            "unemployment_rate_latest", "unemployment_rate_period",
            "population_latest", "population_period",
            "current_account_balance_latest", "current_account_balance_period",
            "govt_debt_gdp_latest", "govt_debt_gdp_period",
            "snapshot_date"
        ]
        for col in expected_cols:
            if col not in df.columns:
                df[col] = None

        df = sanitize_dataframe(df[expected_cols])

        if self.is_postgres:
            logger.info(f"Loading {len(df)} country snapshot rows into Supabase PostgreSQL '{DBE_TABLE_SNAPSHOTS}'...")
            upsert_sql = f"""
                INSERT INTO {DBE_TABLE_SNAPSHOTS} (
                    country_code, country_name, is_aggregate,
                    gdp_usd_latest, gdp_usd_period,
                    gdp_growth_latest, gdp_growth_period,
                    inflation_cpi_latest, inflation_cpi_period,
                    unemployment_rate_latest, unemployment_rate_period,
                    population_latest, population_period,
                    current_account_balance_latest, current_account_balance_period,
                    govt_debt_gdp_latest, govt_debt_gdp_period,
                    snapshot_date
                )
                VALUES %s
                ON CONFLICT (country_code) DO UPDATE SET
                    country_name = EXCLUDED.country_name,
                    is_aggregate = EXCLUDED.is_aggregate,
                    gdp_usd_latest = EXCLUDED.gdp_usd_latest,
                    gdp_usd_period = EXCLUDED.gdp_usd_period,
                    gdp_growth_latest = EXCLUDED.gdp_growth_latest,
                    gdp_growth_period = EXCLUDED.gdp_growth_period,
                    inflation_cpi_latest = EXCLUDED.inflation_cpi_latest,
                    inflation_cpi_period = EXCLUDED.inflation_cpi_period,
                    unemployment_rate_latest = EXCLUDED.unemployment_rate_latest,
                    unemployment_rate_period = EXCLUDED.unemployment_rate_period,
                    population_latest = EXCLUDED.population_latest,
                    population_period = EXCLUDED.population_period,
                    current_account_balance_latest = EXCLUDED.current_account_balance_latest,
                    current_account_balance_period = EXCLUDED.current_account_balance_period,
                    govt_debt_gdp_latest = EXCLUDED.govt_debt_gdp_latest,
                    govt_debt_gdp_period = EXCLUDED.govt_debt_gdp_period,
                    snapshot_date = EXCLUDED.snapshot_date,
                    updated_at = CURRENT_TIMESTAMP;
            """
            tuples = [tuple(x) for x in df.to_numpy()]
            raw_conn = self.engine.raw_connection()
            try:
                cursor = raw_conn.cursor()
                psycopg2.extras.execute_values(cursor, upsert_sql, tuples, page_size=100)
                raw_conn.commit()
            finally:
                raw_conn.close()

            logger.info(f"Successfully upserted snapshot matrix into Supabase '{DBE_TABLE_SNAPSHOTS}'.")
            return len(df)
        else:
            logger.info(f"Loading {len(df)} country snapshot rows into SQL Server '{DBE_TABLE_SNAPSHOTS}'...")
            staging_table = f"#stg_{DBE_TABLE_SNAPSHOTS}"
            with self.engine.begin() as conn:
                df.to_sql(name=staging_table, con=conn, if_exists="replace", index=False)
                merge_sql = f"""
                    MERGE INTO {DBE_TABLE_SNAPSHOTS} AS target
                    USING {staging_table} AS source
                    ON (target.country_code = source.country_code)
                    WHEN MATCHED THEN
                        UPDATE SET
                            target.country_name = source.country_name,
                            target.is_aggregate = source.is_aggregate,
                            target.gdp_usd_latest = source.gdp_usd_latest,
                            target.gdp_usd_period = source.gdp_usd_period,
                            target.gdp_growth_latest = source.gdp_growth_latest,
                            target.gdp_growth_period = source.gdp_growth_period,
                            target.inflation_cpi_latest = source.inflation_cpi_latest,
                            target.inflation_cpi_period = source.inflation_cpi_period,
                            target.unemployment_rate_latest = source.unemployment_rate_latest,
                            target.unemployment_rate_period = source.unemployment_rate_period,
                            target.population_latest = source.population_latest,
                            target.population_period = source.population_period,
                            target.current_account_balance_latest = source.current_account_balance_latest,
                            target.current_account_balance_period = source.current_account_balance_period,
                            target.govt_debt_gdp_latest = source.govt_debt_gdp_latest,
                            target.govt_debt_gdp_period = source.govt_debt_gdp_period,
                            target.snapshot_date = source.snapshot_date,
                            target.updated_at = SYSDATETIME()
                    WHEN NOT MATCHED THEN
                        INSERT (
                            country_code, country_name, is_aggregate,
                            gdp_usd_latest, gdp_usd_period,
                            gdp_growth_latest, gdp_growth_period,
                            inflation_cpi_latest, inflation_cpi_period,
                            unemployment_rate_latest, unemployment_rate_period,
                            population_latest, population_period,
                            current_account_balance_latest, current_account_balance_period,
                            govt_debt_gdp_latest, govt_debt_gdp_period,
                            snapshot_date, updated_at
                        )
                        VALUES (
                            source.country_code, source.country_name, source.is_aggregate,
                            source.gdp_usd_latest, source.gdp_usd_period,
                            source.gdp_growth_latest, source.gdp_growth_period,
                            source.inflation_cpi_latest, source.inflation_cpi_period,
                            source.unemployment_rate_latest, source.unemployment_rate_period,
                            source.population_latest, source.population_period,
                            source.current_account_balance_latest, source.current_account_balance_period,
                            source.govt_debt_gdp_latest, source.govt_debt_gdp_period,
                            source.snapshot_date, SYSDATETIME()
                        );
                """
                conn.execute(text(merge_sql))
            logger.info(f"Successfully upserted snapshot matrix into SQL Server '{DBE_TABLE_SNAPSHOTS}'.")
            return len(df)

    def log_pipeline_run(self, metrics_extracted: int, total_observations: int, status: str, execution_seconds: float):
        """Records execution run audit log into dbe_pipeline_runs."""
        sql = f"""
            INSERT INTO {DBE_TABLE_RUNS} (metrics_extracted, total_observations, status, execution_seconds)
            VALUES (:metrics_extracted, :total_obs, :status, :seconds)
        """
        try:
            with self.engine.begin() as conn:
                conn.execute(text(sql), {
                    "metrics_extracted": metrics_extracted,
                    "total_obs": total_observations,
                    "status": status,
                    "seconds": execution_seconds
                })
            logger.info(f"Pipeline run audit logged into '{DBE_TABLE_RUNS}'.")
        except Exception as e:
            logger.error(f"Failed to log pipeline run: {e}")
