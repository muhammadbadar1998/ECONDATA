import json
import logging
import time
import requests
from typing import Dict, List, Any, Optional

from src.config import DBNOMICS_BASE_URL, METRICS_CATALOG

logger = logging.getLogger(__name__)


class DBnomicsExtractor:
    """Handles fetching economic series and metadata from DBnomics REST API and client."""

    def __init__(self, base_url: str = DBNOMICS_BASE_URL, timeout: int = 30):
        self.base_url = base_url
        self.timeout = timeout
        self.session = requests.Session()
        self.session.headers.update({
            "User-Agent": "EconDataPipeline/1.0 (Economic Data Extraction Infra)"
        })

    def _get_with_retry(self, url: str, params: Optional[Dict] = None, max_retries: int = 3) -> Dict[str, Any]:
        """HTTP GET request with retries and exponential backoff."""
        for attempt in range(1, max_retries + 1):
            try:
                response = self.session.get(url, params=params, timeout=self.timeout)
                response.raise_for_status()
                return response.json()
            except Exception as e:
                logger.warning(f"Request attempt {attempt}/{max_retries} failed for {url}: {e}")
                if attempt == max_retries:
                    raise
                time.sleep(2 ** attempt)
        return {}

    def fetch_metric_series(self, metric_code: str, limit: int = 1000) -> List[Dict[str, Any]]:
        """
        Fetch all country series for a specific metric indicator.
        Returns a list of raw series documents containing dimensions, observations, values, and periods.
        """
        if metric_code not in METRICS_CATALOG:
            raise ValueError(f"Unknown metric code: '{metric_code}'. Available metrics: {list(METRICS_CATALOG.keys())}")

        metric_info = METRICS_CATALOG[metric_code]
        provider = metric_info["provider"]
        dataset = metric_info["dataset"]
        indicator_code = metric_info["indicator_code"]

        url = f"{self.base_url}/series/{provider}/{dataset}"
        dimensions_filter = json.dumps({"indicator": [indicator_code]})

        logger.info(f"Fetching series for metric '{metric_code}' (Indicator: {indicator_code}) from {provider}/{dataset}...")

        all_series = []
        offset = 0

        while True:
            params = {
                "dimensions": dimensions_filter,
                "observations": "1",  # Include period and value arrays
                "limit": limit,
                "offset": offset
            }

            data = self._get_with_retry(url, params=params)
            series_docs = data.get("series", {}).get("docs", [])
            num_found = data.get("series", {}).get("num_found", 0)

            all_series.extend(series_docs)
            logger.info(f"Retrieved {len(all_series)}/{num_found} series for {metric_code}...")

            if len(all_series) >= num_found or not series_docs:
                break

            offset += limit

        return all_series

    def fetch_countries_metadata(self, provider: str = "WB", dataset: str = "WDI") -> List[Dict[str, Any]]:
        """
        Fetch country dimension metadata from dataset provider.
        """
        url = f"{self.base_url}/series/{provider}/{dataset}"
        params = {"limit": 1}
        logger.info(f"Fetching country metadata from dataset {provider}/{dataset}...")

        data = self._get_with_retry(url, params=params)
        dataset_info = data.get("dataset", {})
        dimensions_values_labels = dataset_info.get("dimensions_values_labels", {})

        country_dict = dimensions_values_labels.get("country", {})
        if not country_dict:
            country_dict = dimensions_values_labels.get("ref_area", {})

        countries_list = []
        for code, label in country_dict.items():
            countries_list.append({
                "country_code": code,
                "country_name": label,
                "provider": provider,
                "dataset": dataset
            })

        logger.info(f"Extracted {len(countries_list)} country definitions from {provider}/{dataset}.")
        return countries_list
