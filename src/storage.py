import os
import logging
from datetime import datetime, timezone
import pandas as pd

from src.config import DEFAULT_OUTPUT_DIR, METRICS_DIR_NAME, SNAPSHOTS_DIR_NAME, LOGS_DIR_NAME

logger = logging.getLogger(__name__)


class DataStorageManager:
    """Manages saving and incrementally merging DataFrames to CSV files according to output structure."""

    def __init__(self, output_dir: str = DEFAULT_OUTPUT_DIR):
        self.output_dir = os.path.abspath(output_dir)
        self.metrics_dir = os.path.join(self.output_dir, METRICS_DIR_NAME)
        self.snapshots_dir = os.path.join(self.output_dir, SNAPSHOTS_DIR_NAME)
        self.dated_snapshots_dir = os.path.join(self.snapshots_dir, "dated")
        self.logs_dir = os.path.join(self.output_dir, LOGS_DIR_NAME)

        self._ensure_directories()

    def _ensure_directories(self):
        """Creates output directory structure if it does not exist."""
        for d in [self.output_dir, self.metrics_dir, self.snapshots_dir, self.dated_snapshots_dir, self.logs_dir]:
            os.makedirs(d, exist_ok=True)
            logger.info(f"Directory ensured: {d}")

    def save_countries_catalog(self, df: pd.DataFrame) -> str:
        """Saves and incrementally merges master countries catalog to countries.csv."""
        filepath = os.path.join(self.output_dir, "countries.csv")
        if os.path.exists(filepath) and not df.empty:
            try:
                existing_df = pd.read_csv(filepath)
                combined = pd.concat([existing_df, df], ignore_index=True)
                combined.drop_duplicates(subset=["country_code"], keep="last", inplace=True)
                combined.sort_values(by=["is_aggregate", "country_code"], ascending=[True, True], inplace=True)
                df = combined
            except Exception as e:
                logger.warning(f"Could not merge with existing countries.csv: {e}")

        df.to_csv(filepath, index=False, encoding="utf-8")
        logger.info(f"Saved master countries catalog ({len(df)} total rows) -> {filepath}")
        return filepath

    def save_metrics_catalog(self, df: pd.DataFrame) -> str:
        """Saves and incrementally merges master metrics catalog to metrics.csv."""
        filepath = os.path.join(self.output_dir, "metrics.csv")
        if os.path.exists(filepath) and not df.empty:
            try:
                existing_df = pd.read_csv(filepath)
                combined = pd.concat([existing_df, df], ignore_index=True)
                combined.drop_duplicates(subset=["metric_code"], keep="last", inplace=True)
                df = combined
            except Exception as e:
                logger.warning(f"Could not merge with existing metrics.csv: {e}")

        df.to_csv(filepath, index=False, encoding="utf-8")
        logger.info(f"Saved master metrics catalog ({len(df)} total rows) -> {filepath}")
        return filepath

    def save_metric_timeseries(self, metric_code: str, df: pd.DataFrame) -> str:
        """
        Saves time-series DataFrame for an individual metric into output/metrics/<metric_code>.csv.
        Incrementally merges existing file rows with new/updated rows.
        """
        filepath = os.path.join(self.metrics_dir, f"{metric_code}.csv")
        if os.path.exists(filepath) and not df.empty:
            try:
                existing_df = pd.read_csv(filepath)
                combined = pd.concat([existing_df, df], ignore_index=True)
                combined["period"] = combined["period"].astype(str)
                combined.drop_duplicates(subset=["country_code", "period"], keep="last", inplace=True)
                combined.sort_values(by=["country_code", "period"], ascending=[True, True], inplace=True)
                df = combined
            except Exception as e:
                logger.warning(f"Could not merge with existing {metric_code}.csv: {e}")

        df.to_csv(filepath, index=False, encoding="utf-8")
        logger.info(f"Incrementally saved metric timeseries '{metric_code}' ({len(df)} total rows) -> {filepath}")
        return filepath

    def save_snapshots(self, country_snapshot_df: pd.DataFrame, metrics_snapshot_df: pd.DataFrame, execution_snapshot_df: pd.DataFrame) -> dict:
        """
        Saves snapshot CSVs:
        - latest_country_snapshot.csv
        - latest_metrics_snapshot.csv
        - dated/snapshot_<date>.csv
        """
        date_str = datetime.now(timezone.utc).strftime("%Y-%m-%d")
        saved_paths = {}

        # 1. Latest Country Matrix Snapshot
        p1 = os.path.join(self.snapshots_dir, "latest_country_snapshot.csv")
        if os.path.exists(p1) and not country_snapshot_df.empty:
            try:
                existing_p1 = pd.read_csv(p1)
                combined_p1 = pd.concat([existing_p1, country_snapshot_df], ignore_index=True)
                combined_p1.drop_duplicates(subset=["country_code"], keep="last", inplace=True)
                country_snapshot_df = combined_p1
            except Exception as e:
                logger.warning(f"Could not merge existing latest_country_snapshot.csv: {e}")

        country_snapshot_df.to_csv(p1, index=False, encoding="utf-8")
        saved_paths["latest_country_snapshot"] = p1
        logger.info(f"Saved latest country snapshot matrix ({len(country_snapshot_df)} rows) -> {p1}")

        # 2. Latest Metrics Long Table
        p2 = os.path.join(self.snapshots_dir, "latest_metrics_snapshot.csv")
        if os.path.exists(p2) and not metrics_snapshot_df.empty:
            try:
                existing_p2 = pd.read_csv(p2)
                combined_p2 = pd.concat([existing_p2, metrics_snapshot_df], ignore_index=True)
                combined_p2["period"] = combined_p2["period"].astype(str)
                combined_p2.drop_duplicates(subset=["country_code", "metric_code"], keep="last", inplace=True)
                metrics_snapshot_df = combined_p2
            except Exception as e:
                logger.warning(f"Could not merge existing latest_metrics_snapshot.csv: {e}")

        metrics_snapshot_df.to_csv(p2, index=False, encoding="utf-8")
        saved_paths["latest_metrics_snapshot"] = p2
        logger.info(f"Saved latest metrics long snapshot ({len(metrics_snapshot_df)} rows) -> {p2}")

        # 3. Dated Execution Snapshot
        p3 = os.path.join(self.dated_snapshots_dir, f"snapshot_{date_str}.csv")
        execution_snapshot_df.to_csv(p3, index=False, encoding="utf-8")
        saved_paths["dated_snapshot"] = p3
        logger.info(f"Saved dated execution snapshot ({len(execution_snapshot_df)} rows) -> {p3}")

        return saved_paths
