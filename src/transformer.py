from datetime import datetime, timezone
import pandas as pd
import logging
from typing import List, Dict, Any, Tuple

from src.config import METRICS_CATALOG

logger = logging.getLogger(__name__)

# List of known multi-country regional / income aggregates in World Bank WDI
KNOWN_AGGREGATE_CODES = {
    "WLD", "AFE", "AFW", "ARB", "CEB", "EAR", "EAS", "EAP", "TEA", "ECU", "ECA", "TEC",
    "EUU", "FCS", "HIC", "HPC", "IBD", "IBT", "IDA", "IDB", "IDX", "LAC", "LCN", "TLA",
    "LDC", "LIC", "LMY", "LTE", "MEA", "MNA", "TMN", "MIC", "NAC", "OED", "OSS", "PRE",
    "PSS", "PST", "SAS", "TSA", "SSA", "SSF", "TSS", "UMC", "EMU"
}


class DataTransformer:
    """Transforms raw DBnomics API outputs into clean, normalized economic DataFrames and snapshots."""

    @staticmethod
    def transform_series_list(metric_code: str, raw_series_list: List[Dict[str, Any]]) -> pd.DataFrame:
        """
        Transforms raw DBnomics series docs into a normalized time-series DataFrame.
        """
        metric_info = METRICS_CATALOG[metric_code]
        extracted_at = datetime.now(timezone.utc).isoformat()

        records = []

        for series in raw_series_list:
            dims = series.get("dimensions", {})
            country_code = dims.get("country") or dims.get("ref_area") or "UNKNOWN"
            country_name = series.get("series_name", "").split(" – ")[-1] if " – " in series.get("series_name", "") else country_code

            periods = series.get("period", [])
            values = series.get("value", [])

            # Zip periods and values
            for p, v in zip(periods, values):
                if v is None or pd.isna(v) or v == "NA":
                    continue
                try:
                    num_val = float(v)
                except (ValueError, TypeError):
                    continue

                records.append({
                    "country_code": str(country_code).upper(),
                    "country_name": str(country_name).strip(),
                    "metric_code": metric_code,
                    "metric_name": metric_info["metric_name"],
                    "period": str(p),
                    "value": num_val,
                    "unit": metric_info["unit"],
                    "frequency": metric_info["frequency"],
                    "provider": metric_info["provider"],
                    "dataset": metric_info["dataset"],
                    "indicator_code": metric_info["indicator_code"],
                    "extracted_at": extracted_at
                })

        df = pd.DataFrame(records)
        if df.empty:
            return pd.DataFrame(columns=[
                "country_code", "country_name", "metric_code", "metric_name",
                "period", "value", "unit", "frequency", "provider", "dataset",
                "indicator_code", "extracted_at"
            ])

        # Sort cleanly
        df.sort_values(by=["country_code", "period"], ascending=[True, True], inplace=True)
        df.reset_index(drop=True, inplace=True)
        return df

    @staticmethod
    def transform_countries_catalog(raw_countries: List[Dict[str, Any]]) -> pd.DataFrame:
        """
        Cleans and normalizes master country catalog DataFrame.
        """
        df = pd.DataFrame(raw_countries)
        if df.empty:
            return pd.DataFrame(columns=["country_code", "country_name", "is_aggregate", "provider", "dataset"])

        df["country_code"] = df["country_code"].astype(str).str.upper()
        df["country_name"] = df["country_name"].astype(str).str.strip()
        df["is_aggregate"] = df["country_code"].isin(KNOWN_AGGREGATE_CODES)
        
        # Deduplicate
        df.drop_duplicates(subset=["country_code"], keep="first", inplace=True)
        df.sort_values(by=["is_aggregate", "country_code"], ascending=[True, True], inplace=True)
        df.reset_index(drop=True, inplace=True)
        return df

    @staticmethod
    def create_metrics_catalog_df() -> pd.DataFrame:
        """
        Converts METRICS_CATALOG dict into a clean DataFrame for metrics.csv.
        """
        records = list(METRICS_CATALOG.values())
        df = pd.DataFrame(records)
        df["updated_at"] = datetime.now(timezone.utc).isoformat()
        return df

    @staticmethod
    def generate_snapshots(metric_dfs: Dict[str, pd.DataFrame], countries_df: pd.DataFrame) -> Tuple[pd.DataFrame, pd.DataFrame, pd.DataFrame]:
        """
        Generates cross-sectional and dated snapshots across all extracted metric DataFrames.
        Returns:
            - latest_country_snapshot (wide matrix per country)
            - latest_metrics_snapshot (long table)
            - dated_execution_snapshot (full long table tagged with extraction date)
        """
        extracted_at = datetime.now(timezone.utc).isoformat()
        date_str = datetime.now(timezone.utc).strftime("%Y-%m-%d")

        all_records = []
        latest_records = []

        for metric_code, df in metric_dfs.items():
            if df.empty:
                continue
            all_records.append(df)

            # Get latest period for each country
            df_sorted = df.sort_values(by=["country_code", "period"])
            latest_per_country = df_sorted.groupby("country_code").last().reset_index()
            latest_records.append(latest_per_country)

        if not all_records:
            empty_df = pd.DataFrame()
            return empty_df, empty_df, empty_df

        # Combine long-format latest metrics
        latest_metrics_df = pd.concat(latest_records, ignore_index=True)
        full_history_df = pd.concat(all_records, ignore_index=True)

        # Build wide matrix snapshot per country
        # Base country list
        if not countries_df.empty:
            base_countries = countries_df[["country_code", "country_name", "is_aggregate"]].copy()
        else:
            base_countries = latest_metrics_df[["country_code", "country_name"]].drop_duplicates()
            base_countries["is_aggregate"] = base_countries["country_code"].isin(KNOWN_AGGREGATE_CODES)

        snapshot_matrix = base_countries.copy()

        for metric_code in METRICS_CATALOG.keys():
            m_df = latest_metrics_df[latest_metrics_df["metric_code"] == metric_code]
            if m_df.empty:
                continue

            sub = m_df[["country_code", "value", "period"]].rename(columns={
                "value": f"{metric_code}_latest",
                "period": f"{metric_code}_period"
            })
            snapshot_matrix = snapshot_matrix.merge(sub, on="country_code", how="left")

        snapshot_matrix["snapshot_date"] = date_str
        latest_metrics_df["snapshot_date"] = date_str
        full_history_df["snapshot_date"] = date_str

        return snapshot_matrix, latest_metrics_df, full_history_df
