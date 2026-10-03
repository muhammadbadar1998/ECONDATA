"""Unit tests for src.vectorizer document builders and helpers (pure functions)."""

import json
from decimal import Decimal

from src.entities import ENTITIES
from src.vectorizer import (
    Document,
    Vectorizer,
    _coerce_db_value,
    _format_scalar,
    _format_value,
    _parse_recent_points,
    _vector_to_literal,
)

CATALOG = {
    "gdp_usd": {
        "metric_code": "gdp_usd",
        "metric_name": "Gross Domestic Product (Current USD)",
        "category": "historical",
        "frequency": "Annual",
        "base_unit": "Current US$",
        "unit": "Current US$",
        "provider": "WB",
        "dataset": "WDI",
        "indicator_code": "NY.GDP.MKTP.CD",
        "description": "GDP at purchaser's prices.",
        "synonyms": ["GDP", "gross domestic product"],
    },
}


def test_build_metric_documents():
    docs = Vectorizer.build_metric_documents(CATALOG)
    assert len(docs) == 1
    doc = docs[0]
    assert doc.doc_type == "metric"
    assert doc.doc_key == "gdp_usd"
    assert "Gross Domestic Product" in doc.content
    assert "GDP" in doc.content  # synonym folded in
    assert doc.metadata["frequency"] == "Annual"
    assert doc.doc_hash  # non-empty sha256


def test_build_metric_documents_hash_is_stable():
    docs1 = Vectorizer.build_metric_documents(CATALOG)
    docs2 = Vectorizer.build_metric_documents(CATALOG)
    assert docs1[0].doc_hash == docs2[0].doc_hash


def test_build_metric_documents_hash_changes_on_content():
    docs1 = Vectorizer.build_metric_documents(CATALOG)
    mutated = {**CATALOG, "gdp_usd": {**CATALOG["gdp_usd"], "description": "changed"}}
    docs2 = Vectorizer.build_metric_documents(mutated)
    assert docs1[0].doc_hash != docs2[0].doc_hash


def test_build_entity_documents():
    docs = Vectorizer.build_entity_documents(ENTITIES)
    assert len(docs) == len(ENTITIES)
    # All are entity type
    assert all(d.doc_type == "entity" for d in docs)
    # WLD is the World aggregate
    wld = next(d for d in docs if d.doc_key == "WLD")
    assert "World" in wld.content
    # Parent shown when present
    assert "Parent: WLD" in wld.content or "Type: aggregate" in wld.content


def test_build_entity_metric_documents():
    coverage = [
        {
            "entity_code": "USA",
            "entity_name": "United States",
            "metric_code": "gdp_usd",
            "metric_name": "Gross Domestic Product",
            "frequency": "Annual",
            "base_unit": "USD",
            "obs_count": 60,
            "min_year": 1960,
            "max_year": 2024,
        }
    ]
    docs = Vectorizer.build_entity_metric_documents(coverage)
    assert len(docs) == 1
    doc = docs[0]
    assert doc.doc_key == "USA|gdp_usd"
    assert doc.doc_type == "entity_metric"
    assert "1960" in doc.content and "2024" in doc.content
    assert doc.metadata["obs_count"] == 60


# --------------------------------------------------------------------------- #
# Enriched (value summary) entity_metric documents
# --------------------------------------------------------------------------- #
ENRICHED_ROW = {
    "entity_code": "USA",
    "entity_name": "United States",
    "metric_code": "gdp_usd",
    "metric_name": "Gross Domestic Product (Current USD)",
    "frequency": "Annual",
    "base_unit": "Current US$",
    "obs_count": 65,
    "min_year": 1960,
    "max_year": 2024,
    "latest_val": 27369400000000.0,
    "latest_year": 2024,
    "min_val": 543300000000.0,
    "min_val_year": 1960,
    "max_val": 27369400000000.0,
    "max_val_year": 2024,
    "recent_points": [
        {"year": 2024, "value": 27369400000000.0},
        {"year": 2023, "value": 27720700000000.0},
    ],
}


def test_entity_metric_document_contains_value_summary():
    doc = Vectorizer.build_entity_metric_documents([dict(ENRICHED_ROW)])[0]
    assert "Latest: 27,369,400,000,000 (2024)" in doc.content
    assert "Min: 543,300,000,000 (1960)" in doc.content
    assert "Max: 27,369,400,000,000 (2024)" in doc.content
    assert "Recent: 2024=27,369,400,000,000; 2023=27,720,700,000,000" in doc.content
    # The original coverage line is unchanged.
    assert "Observations: 65, range 1960–2024" in doc.content
    # Fixed field order: Latest -> Min -> Max -> Recent, all after coverage.
    lines = doc.content.splitlines()
    assert lines[4].startswith("Observations:")
    assert [ln.split(":")[0] for ln in lines[5:]] == ["Latest", "Min", "Max", "Recent"]


def test_entity_metric_metadata_mirrors_summary():
    doc = Vectorizer.build_entity_metric_documents([dict(ENRICHED_ROW)])[0]
    meta = doc.metadata
    assert meta["latest_val"] == 27369400000000.0
    assert meta["latest_year"] == 2024
    assert meta["min_val"] == 543300000000.0
    assert meta["min_val_year"] == 1960
    assert meta["max_val"] == 27369400000000.0
    assert meta["max_val_year"] == 2024
    assert meta["recent_points"] == [
        {"year": 2024, "value": 27369400000000.0},
        {"year": 2023, "value": 27720700000000.0},
    ]
    # Metadata must be JSON-serializable (it is stored as JSONB).
    json.dumps(meta, sort_keys=True, ensure_ascii=False)


def test_entity_metric_metadata_is_json_serializable_with_decimals():
    row = {
        **ENRICHED_ROW,
        "obs_count": Decimal("65"),
        "min_year": Decimal("1960"),
        "max_year": Decimal("2024"),
        "latest_val": Decimal("27369400000000.0"),
        "latest_year": Decimal("2024"),
        "recent_points": '[{"year": 2024, "value": 27369400000000.0}]',
    }
    doc = Vectorizer.build_entity_metric_documents([row])[0]
    # Decimals are coerced to int/float before serializing.
    assert doc.metadata["latest_val"] == 27369400000000.0
    assert doc.metadata["latest_year"] == 2024
    json.dumps(doc.metadata, sort_keys=True, ensure_ascii=False)


def test_entity_metric_document_hash_is_stable():
    """Re-building the same rows must not change doc_hash (idempotent --vectorize)."""
    a = Vectorizer.build_entity_metric_documents([dict(ENRICHED_ROW)])[0]
    b = Vectorizer.build_entity_metric_documents([dict(ENRICHED_ROW)])[0]
    assert a.doc_hash == b.doc_hash


def test_entity_metric_document_hash_changes_on_value_change():
    a = Vectorizer.build_entity_metric_documents([dict(ENRICHED_ROW)])[0]
    mutated = {**ENRICHED_ROW, "latest_val": 999.0}
    b = Vectorizer.build_entity_metric_documents([mutated])[0]
    assert a.doc_hash != b.doc_hash


def test_entity_metric_missing_summary_is_tolerated():
    """Rows without the value summary still build (no Latest/Min/Max lines)."""
    doc = Vectorizer.build_entity_metric_documents([dict(ENRICHED_ROW)])[0]
    legacy = {k: ENRICHED_ROW[k] for k in (
        "entity_code", "entity_name", "metric_code", "metric_name",
        "frequency", "base_unit", "obs_count", "min_year", "max_year",
    )}
    doc2 = Vectorizer.build_entity_metric_documents([legacy])[0]
    assert "Latest:" not in doc2.content
    assert "Recent:" not in doc2.content
    assert doc2.metadata["latest_val"] is None
    assert doc2.metadata["recent_points"] == []
    assert doc.doc_key == doc2.doc_key


def test_format_value_rules():
    assert _format_value(None) == "n/a"
    assert _format_value(27369400000000) == "27,369,400,000,000"
    assert _format_value(999) == "999"
    assert _format_value(-1234.0) == "-1,234"
    assert _format_value(2.5) == "2.50"
    assert _format_value(3.5, "%") == "3.50%"
    assert _format_value(1234.5678, "%") == "1,234.57%"
    assert _format_value(float("nan")) == "n/a"
    assert _format_value("not a number") == "not a number"


def test_parse_recent_points():
    assert _parse_recent_points(None) == []
    assert _parse_recent_points("") == []
    assert _parse_recent_points("not json") == []
    assert _parse_recent_points([{"year": 2024, "value": 1.5}]) == [{"year": 2024, "value": 1.5}]
    assert _parse_recent_points('[{"year": 2024, "value": 1.5}]') == [{"year": 2024, "value": 1.5}]
    assert _parse_recent_points("[]") == []


def test_vector_to_literal_basic():
    assert _vector_to_literal([0.1, 0.2, 0.3]) == "[0.1,0.2,0.3]"


def test_vector_to_literal_empty():
    assert _vector_to_literal([]) == "[]"


def test_format_scalar_non_finite():
    assert _format_scalar(float("nan")) == "0"
    assert _format_scalar(float("inf")) == "0"
    assert _format_scalar(float("-inf")) == "0"


def test_format_scalar_bool():
    assert _format_scalar(True) == "1"
    assert _format_scalar(False) == "0"


def test_document_dataclass_fields():
    d = Document(doc_key="x", doc_type="metric", content="hello", metadata={"a": 1})
    assert d.doc_hash  # derived
    assert isinstance(d.doc_hash, str)


def test_coerce_db_value_decimal():
    assert _coerce_db_value(Decimal("60")) == 60
    assert isinstance(_coerce_db_value(Decimal("3.5")), float)
    assert _coerce_db_value(Decimal("3.5")) == 3.5
    assert _coerce_db_value("plain") == "plain"
    assert _coerce_db_value(42) == 42
