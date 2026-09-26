import json
import logging
import time
from typing import Any, Dict, List, Optional

import requests

from src.config import DBNOMICS_BASE_URL, METRICS_CATALOG

logger = logging.getLogger(__name__)


class DBnomicsExtractor:
    """Handles fetching economic series and metadata from DBnomics REST API."""

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

    def fetch_metric_series(
        self,
        metric_code: str,
        catalog: Optional[Dict[str, Any]] = None,
        limit: int = 1000
    ) -> List[Dict[str, Any]]:
        """
        Fetch all country series for a specific metric indicator.
        Returns a list of raw series documents containing dimensions, observations, values, and periods.
        """
        active_catalog = catalog if catalog is not None else METRICS_CATALOG
        if metric_code not in active_catalog:
            raise ValueError(f"Unknown metric code: '{metric_code}'. Available metrics: {list(active_catalog.keys())}")

        metric_info = active_catalog[metric_code]
        provider = metric_info.get("provider", "WB")
        dataset = metric_info.get("dataset", "WDI")
        indicator_code = metric_info.get("indicator_code", "")

        url = f"{self.base_url}/series/{provider}/{dataset}"

        # Build dimensions filter based on provider conventions
        if provider == "IMF" and dataset == "WEO":
            dimensions_filter = json.dumps({"weo-subject-code": [indicator_code]})
        elif provider == "OECD":
            dimensions_filter = json.dumps({"INDICATOR": [indicator_code]})
        else:
            dimensions_filter = json.dumps({"indicator": [indicator_code]})

        logger.info(f"Fetching series for '{metric_code}' (Indicator: {indicator_code}) from {provider}/{dataset}...")

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
