import logging
from datetime import datetime, timezone
from typing import Any, Dict, List, Optional

from src.config import METRICS_CATALOG
from src.helpers import normalize_unit, parse_period

logger = logging.getLogger(__name__)


class DataTransformer:
    """Transforms raw DBnomics API outputs into normalized long-format records."""

    @staticmethod
    def transform_series_list(
        metric_code: str,
        raw_series_list: List[Dict[str, Any]],
        catalog: Optional[Dict[str, Any]] = None,
        entity_resolver=None,
    ) -> List[Dict[str, Any]]:
        """
        Transforms raw DBnomics series docs into normalized observation records:
        {
          provider, provider_code, entity_code, metric_code,
          start_date, end_date, frequency, val, raw_val, unit, period_str, extracted_at
        }
        Entity resolution uses the supplied EntityResolver when available.
        """
        active_catalog = catalog if catalog is not None else METRICS_CATALOG
        metric_info = active_catalog.get(metric_code, {
            "metric_name": metric_code,
            "provider": "",
            "dataset": "",
            "indicator_code": "",
            "frequency": "Annual",
            "unit": "",
            "unit_normalization": "",
        })

        provider = metric_info.get("provider", "WB")
        base_unit = metric_info.get("base_unit", metric_info.get("unit", ""))
        unit_normalization = metric_info.get("unit_normalization", "")
        catalog_frequency = metric_info.get("frequency", "Annual")
        start_year = metric_info.get("start_year")
        end_year = metric_info.get("end_year")
        extracted_at = datetime.now(timezone.utc).isoformat()

        records = []
        skipped = 0

        for series in raw_series_list:
            dims = series.get("dimensions", {})
            provider_code = (
                dims.get("country") or
                dims.get("ref_area") or
                dims.get("weo-country-code") or
                dims.get("LOCATION") or
                dims.get("location") or
                "UNKNOWN"
            )
            provider_code = str(provider_code).upper()

            # Resolve canonical entity code (no string-split name heuristics).
            entity_code = None
            entity_name = provider_code
            if entity_resolver is not None:
                entity_code = entity_resolver.resolve_code(provider, provider_code)
                if entity_code is None:
                    label = series.get("series_name", "")
                    entity_code = entity_resolver.register_unknown(provider, provider_code, label or None)
                entity_name = entity_resolver.entity_name(entity_code)
            else:
                entity_code = provider_code

            periods = series.get("period", [])
            values = series.get("value", [])

            for p, v in zip(periods, values, strict=False):
                if v is None:
                    continue
                try:
                    raw_val = float(v)
                except (ValueError, TypeError):
                    skipped += 1
                    continue
                if raw_val != raw_val:  # NaN
                    skipped += 1
                    continue

                period_str = str(p).strip()
                start_date, end_date, parsed_freq = parse_period(period_str)
                if start_date is None:
                    skipped += 1
                    continue

                # Year filtering from catalog bounds.
                if start_year is not None and start_date.year < start_year:
                    continue
                if end_year is not None and start_date.year > end_year:
                    continue

                # Unit normalization -> base_unit value.
                try:
                    val = normalize_unit(raw_val, metric_info.get("unit", ""), unit_normalization)
                except ValueError as e:
                    logger.warning(
                        f"Skipping row for {metric_code} {entity_code} {period_str}: {e}"
                    )
                    skipped += 1
                    continue

                records.append({
                    "provider": provider,
                    "provider_code": provider_code,
                    "entity_code": entity_code,
                    "entity_name": entity_name,
                    "metric_code": metric_code,
                    "start_date": start_date,
                    "end_date": end_date,
                    "frequency": parsed_freq or catalog_frequency,
                    "val": val,
                    "raw_val": raw_val,
                    "unit": base_unit or metric_info.get("unit", ""),
                    "period_str": period_str,
                    "extracted_at": extracted_at,
                })

        if skipped:
            logger.info(f"Skipped {skipped} non-numeric/unparseable rows for '{metric_code}'.")

        # Dedupe on (entity_code, start_date) keeping the last occurrence.
        deduped = {}
        for rec in records:
            deduped[(rec["entity_code"], rec["start_date"])] = rec
        final = list(deduped.values())

        if len(final) != len(records):
            logger.info(f"Deduped {len(records) - len(final)} duplicate rows for '{metric_code}'.")
        return final

    @staticmethod
    def transform_countries_catalog(raw_countries: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
        """Cleans country metadata docs into a canonical list."""
        rows = []
        for c in raw_countries:
            code = str(c.get("country_code", "")).upper()
            if not code:
                continue
            rows.append({
                "entity_code": code,
                "entity_name": str(c.get("country_name", code)).strip(),
                "provider": c.get("provider", "WB"),
                "dataset": c.get("dataset", "WDI"),
            })
        # Dedupe by code
        seen = {}
        for r in rows:
            seen.setdefault(r["entity_code"], r)
        return list(seen.values())

    @staticmethod
    def create_metrics_records(catalog: Optional[Dict[str, Any]] = None) -> List[Dict[str, Any]]:
        """
        Converts the metrics catalog dict into registry records including
        synonyms for text-to-SQL retrieval.
        """
        active_catalog = catalog if catalog is not None else METRICS_CATALOG
        records = []
        for code, info in active_catalog.items():
            records.append({
                "metric_code": code,
                "metric_name": info.get("metric_name", code),
                "category": info.get("category", "historical"),
                "frequency": info.get("frequency", "Annual"),
                "base_unit": info.get("base_unit", info.get("unit", "")),
                "unit_normalization": info.get("unit_normalization", ""),
                "description": info.get("description", ""),
                "is_aggregatable": info.get("entity_scope", "country") == "aggregate",
                "aggregation_method": info.get("aggregation", "mean"),
                "synonyms": info.get("synonyms", []),
                # Source registration fields
                "provider": info.get("provider", "WB"),
                "dataset": info.get("dataset", "WDI"),
                "indicator_code": info.get("indicator_code", ""),
                "dimension_key": info.get("dimension_key", "country"),
                "entity_scope": info.get("entity_scope", "country"),
            })
        return records
