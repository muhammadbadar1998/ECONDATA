import argparse
import os
import logging
import sys
import time
from datetime import datetime, timezone
from typing import List

from src.config import DEFAULT_OUTPUT_DIR, DEFAULT_DB_CONNECTION_STRING, METRICS_CATALOG, LOGS_DIR_NAME
from src.extractor import DBnomicsExtractor
from src.transformer import DataTransformer
from src.storage import DataStorageManager
from src.db import init_db_schema
from src.db_loader import DatabaseLoader


def setup_logger(output_dir: str) -> logging.Logger:
    """Configures multi-destination logging: stdout + file logging in output/logs/."""
    logger = logging.getLogger("EconDataPipeline")
    logger.setLevel(logging.INFO)
    logger.handlers.clear()

    formatter = logging.Formatter("%(asctime)s [%(levelname)s] %(name)s: %(message)s")

    # 1. Console Handler (stdout)
    stdout_handler = logging.StreamHandler(sys.stdout)
    stdout_handler.setFormatter(formatter)
    logger.addHandler(stdout_handler)

    # 2. Persistent File Handlers
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
    load_to_db: bool = False,
    connection_string: str = DEFAULT_DB_CONNECTION_STRING
):
    """Executes full economic data extraction pipeline with CSV and Supabase/SQL Server DB support."""
    logger = setup_logger(output_dir)

    start_time = time.time()
    db_type = "Supabase PostgreSQL" if ("postgresql" in connection_string or "postgres" in connection_string) else "SQL Server"

    logger.info("=========================================================")
    logger.info(" Starting DBnomics Global Economic Data Extraction Infra ")
    logger.info("=========================================================")
    logger.info(f"Target Output Directory: {output_dir}")
    logger.info(f"Total Metrics to Extract: {len(metrics_to_run)} -> {metrics_to_run}")
    logger.info(f"Target Database Engine: {db_type} ({'Enabled' if load_to_db else 'Disabled'})")

    extractor = DBnomicsExtractor()
    storage = DataStorageManager(output_dir=output_dir)
    db_loader = DatabaseLoader(connection_string=connection_string) if load_to_db else None

    if load_to_db:
        logger.info(f"Verifying/Initializing {db_type} 'dbe_' database schema...")
        if not db_loader.verify_or_init_schema():
            logger.warning("Database schema verification returned warnings; proceeding with extraction.")

    # 1. Fetch & Save Countries Catalog
    logger.info("Step 1/4: Extracting Master Country Catalog...")
    try:
        raw_countries = extractor.fetch_countries_metadata(provider="WB", dataset="WDI")
        countries_df = DataTransformer.transform_countries_catalog(raw_countries)
        storage.save_countries_catalog(countries_df)
        if db_loader and not countries_df.empty:
            db_loader.upsert_countries(countries_df)
    except Exception as e:
        logger.error(f"Failed to process countries catalog: {e}")
        countries_df = DataTransformer.transform_countries_catalog([])

    # 2. Save Metrics Catalog
    logger.info("Step 2/4: Exporting Master Metrics Catalog...")
    metrics_df = DataTransformer.create_metrics_catalog_df()
    storage.save_metrics_catalog(metrics_df)
    if db_loader and not metrics_df.empty:
        db_loader.upsert_metrics(metrics_df)

    # 3. Extract & Save Metric Time-Series
    logger.info("Step 3/4: Extracting Metric Series from DBnomics...")
    extracted_metric_dfs = {}
    total_observations_count = 0

    for idx, metric_code in enumerate(metrics_to_run, 1):
        try:
            logger.info(f"--> [{idx}/{len(metrics_to_run)}] Extracting metric '{metric_code}'...")
            raw_series = extractor.fetch_metric_series(metric_code)
            df = DataTransformer.transform_series_list(metric_code, raw_series)
            
            # Save CSV Backup
            storage.save_metric_timeseries(metric_code, df)
            extracted_metric_dfs[metric_code] = df
            total_observations_count += len(df)

            # Ingest into Database
            if db_loader and not df.empty:
                logger.info(f"--> [{idx}/{len(metrics_to_run)}] Upserting {len(df)} rows for '{metric_code}' into {db_type}...")
                db_loader.upsert_observations(metric_code, df)

            logger.info(f"--> [{idx}/{len(metrics_to_run)}] Done '{metric_code}': {len(df)} observations across {df['country_code'].nunique() if not df.empty else 0} countries.")
        except Exception as e:
            logger.error(f"Error extracting metric '{metric_code}': {e}", exc_info=True)

    # 4. Generate & Save Snapshots
    logger.info("Step 4/4: Generating Economic Snapshots...")
    if extracted_metric_dfs:
        country_snapshot, metrics_snapshot, execution_snapshot = DataTransformer.generate_snapshots(
            extracted_metric_dfs, countries_df
        )
        saved_snapshots = storage.save_snapshots(country_snapshot, metrics_snapshot, execution_snapshot)
        logger.info(f"CSV Snapshots successfully saved: {list(saved_snapshots.keys())}")

        if db_loader and not country_snapshot.empty:
            logger.info(f"Upserting cross-sectional snapshot matrix into {db_type} 'dbe_latest_country_snapshots'...")
            db_loader.upsert_snapshots(country_snapshot)
    else:
        logger.warning("No metric dataframes were extracted; skipping snapshot generation.")

    elapsed = round(time.time() - start_time, 2)

    # 5. Audit Log
    if db_loader:
        db_loader.log_pipeline_run(
            metrics_extracted=len(extracted_metric_dfs),
            total_observations=total_observations_count,
            status="SUCCESS",
            execution_seconds=elapsed
        )

    logger.info("=========================================================")
    logger.info(f" Pipeline Completed Successfully in {elapsed}s ")
    logger.info(f" Total Extracted Observations: {total_observations_count:,} ")
    logger.info("=========================================================")


def main():
    parser = argparse.ArgumentParser(description="DBnomics Economic Data Extraction Pipeline")
    parser.add_argument(
        "--metrics",
        nargs="+",
        default=["all"],
        help=f"List of metric codes to extract or 'all'. Available: {list(METRICS_CATALOG.keys())}"
    )
    parser.add_argument(
        "--output-dir",
        default=DEFAULT_OUTPUT_DIR,
        help="Path to output directory for CSV exports and logs (default: ./output)"
    )
    parser.add_argument(
        "--db",
        action="store_true",
        help="Enable Database ingestion into dbe_* tables"
    )
    parser.add_argument(
        "--init-db",
        action="store_true",
        help="Only initialize dbe_* database tables schema and exit"
    )
    parser.add_argument(
        "--connection-string",
        default=DEFAULT_DB_CONNECTION_STRING,
        help="Database connection string (Supabase PostgreSQL or SQL Server)"
    )

    args = parser.parse_args()

    if args.init_db:
        logger = setup_logger(args.output_dir)
        db_type = "Supabase PostgreSQL" if ("postgresql" in args.connection_string or "postgres" in args.connection_string) else "SQL Server"
        logger.info(f"Initializing {db_type} dbe_* database schema...")
        success = init_db_schema(args.connection_string)
        if success:
            logger.info("Database schema initialized successfully.")
            sys.exit(0)
        else:
            logger.error("Failed to initialize database schema.")
            sys.exit(1)

    if "all" in args.metrics:
        metrics_to_run = list(METRICS_CATALOG.keys())
    else:
        metrics_to_run = [m for m in args.metrics if m in METRICS_CATALOG]
        if not metrics_to_run:
            print(f"No valid metric codes specified in {args.metrics}. Available: {list(METRICS_CATALOG.keys())}")
            sys.exit(1)

    run_pipeline(
        metrics_to_run=metrics_to_run,
        output_dir=args.output_dir,
        load_to_db=args.db,
        connection_string=args.connection_string
    )


if __name__ == "__main__":
    main()
