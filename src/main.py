import argparse
import logging
import os
import sys
import time
from datetime import datetime, timezone
from typing import List

from src.config import (
    DEFAULT_CONFIG_PATH,
    DEFAULT_DB_CONNECTION_STRING,
    DEFAULT_OUTPUT_DIR,
    LOGS_DIR_NAME,
    load_pipeline_config,
)
from src.db import init_db_schema
from src.db_loader import DatabaseLoader
from src.entity_resolver import EntityResolver
from src.extractor import DBnomicsExtractor
from src.transformer import DataTransformer


def setup_logger(output_dir: str) -> logging.Logger:
    """Configures multi-destination logging: stdout + file logging in output/logs/."""
    logger = logging.getLogger("EconDataPipeline")
    logger.setLevel(logging.INFO)
    logger.handlers.clear()

    formatter = logging.Formatter("%(asctime)s [%(levelname)s] %(name)s: %(message)s")

    stdout_handler = logging.StreamHandler(sys.stdout)
    stdout_handler.setFormatter(formatter)
    logger.addHandler(stdout_handler)

    logs_dir = os.path.join(output_dir, LOGS_DIR_NAME)
    os.makedirs(logs_dir, exist_ok=True)

    date_str = datetime.now(timezone.utc).strftime("%Y-%m-%d")
    log_filename = f"pipeline_{date_str}.log"
    log_filepath = os.path.join(logs_dir, log_filename)
    main_log_filepath = os.path.join(output_dir, "pipeline.log")

    file_handler = logging.FileHandler(log_filepath, encoding="utf-8")
    file_handler.setFormatter(formatter)
    logger.addHandler(file_handler)

    main_file_handler = logging.FileHandler(main_log_filepath, encoding="utf-8")
    main_file_handler.setFormatter(formatter)
    logger.addHandler(main_file_handler)

    logger.info(f"Logging initialized. Output log files: {log_filepath} and {main_log_filepath}")
    return logger


def run_pipeline(
    metrics_to_run: List[str],
    output_dir: str,
    config_path: str = DEFAULT_CONFIG_PATH,
    load_to_db: bool = False,
    connection_string: str = DEFAULT_DB_CONNECTION_STRING
):
    """Executes the normalized economic data extraction pipeline."""
    logger = setup_logger(output_dir)
    start_time = time.time()

    pipeline_cfg = load_pipeline_config(config_path)
    metrics_catalog = pipeline_cfg["metrics"]

    logger.info("=========================================================")
    logger.info(" Starting DBnomics Global Economic Data Extraction Infra ")
    logger.info("=========================================================")
    logger.info(f"Loaded Configuration File: {config_path}")
    logger.info(f"Total Metrics to Extract: {len(metrics_to_run)} -> {metrics_to_run}")
    logger.info(f"Target Database: Supabase PostgreSQL ({'Enabled' if load_to_db else 'Disabled'})")

    extractor = DBnomicsExtractor()
    db_loader = None
    resolver = EntityResolver()

    if load_to_db:
        db_loader = DatabaseLoader(connection_string=connection_string)
        logger.info("Verifying/Initializing PostgreSQL 'dbe_' database schema...")
        if not db_loader.verify_or_init_schema():
            logger.warning("Database schema verification returned warnings; proceeding with extraction.")

        # Seed canonical registry (entities, aliases, memberships) idempotently.
        entities_rows, aliases_rows = resolver.seed_rows()
        db_loader.seed_entities(entities_rows, aliases_rows)
        db_loader.seed_memberships()

    # Metrics + synonyms + sources registry.
    metrics_records = DataTransformer.create_metrics_records(catalog=metrics_catalog)
    if db_loader:
        db_loader.upsert_metrics(metrics_records)
        metric_ids, _ = db_loader.get_metric_and_source_ids()
        db_loader.upsert_sources(metrics_records, metric_ids)
        # Re-query source ids AFTER upserting sources so they are not stale on first run.
        metric_ids, source_ids = db_loader.get_metric_and_source_ids()

    total_observations = 0
    observations_upserted = 0
    providers_touched = set()

    for idx, metric_code in enumerate(metrics_to_run, 1):
        if metric_code not in metrics_catalog:
            logger.warning(f"Metric code '{metric_code}' not defined in {config_path}. Skipping.")
            continue

        metric_info = metrics_catalog[metric_code]
        provider = metric_info.get("provider", "WB")
        providers_touched.add(provider)

        try:
            logger.info(f"--> [{idx}/{len(metrics_to_run)}] Extracting '{metric_code}' ({metric_info.get('category', 'historical')}) from {provider}/{metric_info.get('dataset', 'WDI')}...")
            raw_series = extractor.fetch_metric_series(metric_code, catalog=metrics_catalog)
            obs_records = DataTransformer.transform_series_list(
                metric_code, raw_series, catalog=metrics_catalog, entity_resolver=resolver
            )
            total_observations += len(obs_records)
            logger.info(f"--> [{idx}/{len(metrics_to_run)}] Transformed {len(obs_records)} observations.")

            if db_loader:
                mid = metric_ids.get(metric_code)
                sid = source_ids.get(metric_code)
                if mid and sid:
                    n = db_loader.upsert_observations(metric_code, obs_records, mid, sid)
                    observations_upserted += n
                else:
                    logger.warning(f"No metric/source id for '{metric_code}'; skipping DB load.")
        except Exception as e:
            logger.error(f"Error extracting metric '{metric_code}': {e}", exc_info=True)

    elapsed = round(time.time() - start_time, 2)

    if db_loader:
        db_loader.log_pipeline_run(
            dag_id="econdata_manual",
            task_id="run_pipeline",
            metrics_extracted=len(metrics_to_run),
            observations_upserted=observations_upserted,
            observations_skipped=total_observations - observations_upserted,
            providers=list(providers_touched),
            source_ids=list(source_ids.values()),
            status="SUCCESS",
            execution_seconds=elapsed,
        )

    logger.info("=========================================================")
    logger.info(f" Pipeline Completed Successfully in {elapsed}s ")
    logger.info(f" Total Extracted Observations: {total_observations:,} ")
    logger.info("=========================================================")


def main():
    parser = argparse.ArgumentParser(description="DBnomics Global Economic Data Pipeline (PostgreSQL/Supabase)")
    parser.add_argument("--config", default=DEFAULT_CONFIG_PATH, help="Path to JSON metrics configuration file")
    parser.add_argument("--metrics", nargs="+", default=["all"], help="List of metric codes to extract or 'all'.")
    parser.add_argument("--output-dir", default=DEFAULT_OUTPUT_DIR, help="Path to output directory for logs")
    parser.add_argument("--db", action="store_true", help="Enable Database ingestion into dbe_* tables")
    parser.add_argument("--init-db", action="store_true", help="Only initialize dbe_* database tables schema and exit")
    parser.add_argument("--check-db", action="store_true", help="Test Supabase connectivity only (no schema changes) and exit")
    parser.add_argument("--connection-string", default=DEFAULT_DB_CONNECTION_STRING, help="Supabase PostgreSQL connection string")

    args = parser.parse_args()

    pipeline_cfg = load_pipeline_config(args.config)
    metrics_catalog = pipeline_cfg["metrics"]

    if args.check_db:
        from sqlalchemy import text

        from src.db import get_sqlalchemy_engine
        logger = setup_logger(args.output_dir)
        logger.info("Testing Supabase connectivity (SELECT version)...")
        try:
            with get_sqlalchemy_engine(args.connection_string).connect() as conn:
                ver = conn.execute(text("SELECT version();")).scalar()
            logger.info(f"Connection OK. Server: {ver}")
            sys.exit(0)
        except SystemExit:
            raise
        except Exception as e:
            logger.error(f"Connection FAILED: {e}")
            sys.exit(1)

    if args.init_db:
        logger = setup_logger(args.output_dir)
        logger.info("Initializing Supabase PostgreSQL dbe_* database schema...")
        success = init_db_schema(args.connection_string)
        if success:
            logger.info("Database schema initialized successfully.")
            sys.exit(0)
        else:
            logger.error("Failed to initialize database schema.")
            sys.exit(1)

    if "all" in args.metrics:
        metrics_to_run = list(metrics_catalog.keys())
    else:
        metrics_to_run = [m for m in args.metrics if m in metrics_catalog]
        if not metrics_to_run:
            print(f"No valid metric codes specified in {args.metrics}. Available: {list(metrics_catalog.keys())}")
            sys.exit(1)

    run_pipeline(
        metrics_to_run=metrics_to_run,
        output_dir=args.output_dir,
        config_path=args.config,
        load_to_db=args.db,
        connection_string=args.connection_string,
    )


if __name__ == "__main__":
    main()



