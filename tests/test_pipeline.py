from datetime import date

import pytest

from src.entity_resolver import EntityResolver
from src.helpers import normalize_unit, parse_period
from src.transformer import DataTransformer


def test_parse_period_annual():
    s, e, f = parse_period("2023")
    assert (s, e, f) == (date(2023, 1, 1), date(2023, 12, 31), "Annual")


def test_parse_period_quarter():
    s, e, f = parse_period("2023-Q2")
    assert (s, e, f) == (date(2023, 4, 1), date(2023, 6, 30), "Quarterly")
    s, e, f = parse_period("Q3-2021")
    assert (s, e, f) == (date(2021, 7, 1), date(2021, 9, 30), "Quarterly")


def test_parse_period_month():
    s, e, f = parse_period("2023-07")
    assert (s, e, f) == (date(2023, 7, 1), date(2023, 7, 31), "Monthly")
    s, e, f = parse_period("2023-02")
    assert (s, e, f) == (date(2023, 2, 1), date(2023, 2, 28), "Monthly")


def test_parse_period_daily():
    s, e, f = parse_period("2023-07-15")
    assert (s, e, f) == (date(2023, 7, 15), date(2023, 7, 15), "Monthly")


def test_parse_period_invalid():
    assert parse_period("garbage") == (None, None, None)
    assert parse_period("") == (None, None, None)
    assert parse_period(None) == (None, None, None)


def test_normalize_unit_identity():
    assert normalize_unit(5.0, "x", "") == 5.0
    assert normalize_unit(1e6, "USD million", "x*1000000") == 1e12


def test_normalize_unit_unsafe_raises():
    with pytest.raises(ValueError):
        normalize_unit(1.0, "x", "__import__('os')")


def test_resolver_known_and_unknown():
    r = EntityResolver()
    assert r.resolve_code("WB", "WLD") == "WLD"
    assert r.resolve_code("OECD", "USA") == "USA"
    # Unknown ISO-3 is accepted as canonical (to be registered).
    assert r.resolve_code("WB", "XYZ") == "XYZ"
    # Non-entity codes are not fabricated.
    assert r.resolve_code("WB", "ANNUAL") is None
    code = r.register_unknown("WB", "ZZZ", "Newland")
    assert code == "ZZZ"
    assert r.entity_name("ZZZ") == "Newland"


def test_resolver_refuses_non_entity():
    r = EntityResolver()
    assert r.register_unknown("WB", "ANNUAL", "Annual") == "ANNUAL"
    assert not r.is_known_entity("ANNUAL")


def test_transformer_entity_resolution_and_dedup():
    catalog = {
        "gdp_usd": {
            "metric_code": "gdp_usd",
            "provider": "WB",
            "dataset": "WDI",
            "indicator_code": "NY.GDP.MKTP.CD",
            "unit": "Current US$",
            "base_unit": "Current US$",
            "frequency": "Annual",
            "unit_normalization": "",
            "start_year": 2020,
            "end_year": 2025,
        }
    }
    raw = [{
        "dimensions": {"country": "USA"},
        "period": ["2023", "2024", "2024"],
        "value": [21.0, 22.0, 22.0],
    }]
    r = EntityResolver()
    recs = DataTransformer.transform_series_list("gdp_usd", raw, catalog=catalog, entity_resolver=r)
    # Deduped on (entity, start_date) -> only one 2024 row.
    assert len(recs) == 2
    assert all(x["entity_code"] == "USA" for x in recs)
    assert all(x["frequency"] == "Annual" for x in recs)


def test_transformer_skips_nan_and_unparseable():
    catalog = {
        "m": {
            "metric_code": "m", "provider": "WB", "dataset": "WDI",
            "indicator_code": "X", "unit": "", "base_unit": "",
            "frequency": "Annual", "unit_normalization": "",
        }
    }
    raw = [{
        "dimensions": {"country": "USA"},
        "period": ["2023", "bad", "2024"],
        "value": [1.0, "NA", None],
    }]
    r = EntityResolver()
    recs = DataTransformer.transform_series_list("m", raw, catalog=catalog, entity_resolver=r)
    assert len(recs) == 1
    assert recs[0]["period_str"] == "2023"
