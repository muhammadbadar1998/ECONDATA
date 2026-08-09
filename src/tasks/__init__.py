"""
Discrete, idempotent pipeline tasks for Airflow orchestration.

Each task operates on a single metric and is independently retryable; a failure
in one metric does not halt the others. Idempotency is guaranteed by the
ON CONFLICT upserts in DatabaseLoader.
"""

import logging
from typing import List

from src.config import DEFAULT_CONFIG_PATH, DEFAULT_DB_CONNECTION_STRING, load_pipeline_config
from src.db_loader import DatabaseLoader
from src.entity_resolver import EntityResolver
from src.extractor import DBnomicsExtractor
from src.transformer import DataTransformer

logger = logging.getLogger(__name__)


def ensure_registry(db_loader: DatabaseLoader, resolver: EntityResolver):
    """Seed entities/aliases/memberships and metrics/sources once per DAG run."""
    entities_rows, aliases_rows = resolver.seed_rows()
    db_loader.seed_entities(entities_rows, aliases_rows)
    db_loader.seed_memberships()


def task_extract_metric(metric_code: str, catalog: dict, extractor: DBnomicsExtractor, resolver: EntityResolver) -> List[dict]:
    """Extract + transform a single metric into normalized observation records."""
    logger.info(f"[extract] {metric_code}")
    raw = extractor.fetch_metric_series(metric_code, catalog=catalog)
    return DataTransformer.transform_series_list(metric_code, raw, catalog=catalog, entity_resolver=resolver)


def task_load_metric(metric_code: str, obs_records: List[dict], db_loader: DatabaseLoader,
                     metric_ids: dict, source_ids: dict) -> int:
    """Idempotently upsert a single metric's observations."""
    logger.info(f"[load] {metric_code} ({len(obs_records)} rows)")
    mid = metric_ids.get(metric_code)
    sid = source_ids.get(metric_code)
    if not mid or not sid:
        logger.warning(f"[load] missing ids for {metric_code}; skipping")
        return 0
    return db_loader.upsert_observations(metric_code, obs_records, mid, sid)


def task_refresh_registry(db_loader: DatabaseLoader, catalog: dict):
    """Re-sync metrics/sources/synonyms from config; returns fresh metric & source id maps."""
    metrics_records = DataTransformer.create_metrics_records(catalog=catalog)
    db_loader.upsert_metrics(metrics_records)
    metric_ids, _ = db_loader.get_metric_and_source_ids()
    db_loader.upsert_sources(metrics_records, metric_ids)
    # Re-query source ids AFTER upserting sources so they are not stale on first run.
    return db_loader.get_metric_and_source_ids()


def run_metric_pipeline(metric_code: str, config_path: str = DEFAULT_CONFIG_PATH,
                        connection_string: str = DEFAULT_DB_CONNECTION_STRING) -> int:
    """End-to-end extract+transform+load for one metric (used by Airflow task)."""
    catalog = load_pipeline_config(config_path)["metrics"]
    if metric_code not in catalog:
        raise ValueError(f"Unknown metric: {metric_code}")
    extractor = DBnomicsExtractor()
    resolver = EntityResolver()
    db_loader = DatabaseLoader(connection_string=connection_string)
    obs = task_extract_metric(metric_code, catalog, extractor, resolver)
    metric_ids, source_ids = task_refresh_registry(db_loader, catalog)
    return task_load_metric(metric_code, obs, db_loader, metric_ids, source_ids)
