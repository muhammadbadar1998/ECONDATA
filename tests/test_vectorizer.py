"""Unit tests for src.vectorizer document builders and helpers (pure functions)."""

from src.entities import ENTITIES
from src.vectorizer import (
    Document,
    Vectorizer,
    _format_scalar,
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
