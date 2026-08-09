"""Airflow task group helpers for the EconData pipeline DAG."""

from datetime import datetime, timedelta

from airflow import DAG
from airflow.operators.python import PythonOperator
from airflow.utils.task_group import TaskGroup

from src.tasks import run_metric_pipeline

default_args = {
    "owner": "econdata",
    "retries": 3,
    "retry_delay": timedelta(minutes=5),
    "execution_timeout": timedelta(hours=1),
}

# Metrics to schedule. Higher-frequency sources could use a separate DAG/cadence.
METRICS = [
    "gdp_usd", "gdp_growth", "inflation_cpi", "unemployment_rate", "population",
    "current_account_balance", "govt_debt_gdp",
    "forecast_gdp_growth_gep", "forecast_gdp_longterm_oecd",
]


def _make_run_fn(metric_code: str):
    def _fn(**context):
        from src.config import DEFAULT_CONFIG_PATH, DEFAULT_DB_CONNECTION_STRING
        run_metric_pipeline(metric_code, DEFAULT_CONFIG_PATH, DEFAULT_DB_CONNECTION_STRING)
    return _fn


with DAG(
    dag_id="econdata_pipeline",
    default_args=default_args,
    description="Normalized EconData extraction pipeline (DBnomics -> Supabase)",
    schedule_interval="@daily",
    start_date=datetime(2026, 1, 1),
    catchup=False,
    tags=["econdata", "economics"],
) as dag:

    with TaskGroup("extract_load_metrics") as extract_load:
        for m in METRICS:
            PythonOperator(
                task_id=f"load_{m}",
                python_callable=_make_run_fn(m),
                task_group=extract_load,
            )
