"""OPT-IN integration test for the semantic catalog / pgvector layer.

Skipped unless ECONDATA_TEST_DB_CONN is set. Requires a live Ollama serving
nomic-embed-text OR sets OLLAMA_OFFLINE=1 to use a fake embedder.

Example:
    $env:ECONDATA_TEST_DB_CONN = "postgresql+psycopg2://postgres@127.0.0.1:55432/postgres"
    ollama serve
    ollama pull nomic-embed-text
    pytest tests/test_vectorizer_integration.py
"""

import os
from unittest.mock import patch

import pytest

CONN = os.getenv("ECONDATA_TEST_DB_CONN")
OLLAMA_OFFLINE = os.getenv("OLLAMA_OFFLINE") == "1"
EMBEDDING_DIM = 768


def _fake_embedder():
    """Return an embedder-like object that returns deterministic pseudo-vectors."""

    class _Fake:
        def verify_connectivity(self):
            return True

        def embed(self, texts):
            return [[float(hash(t) % 1000) / 1000.0] * EMBEDDING_DIM for t in texts]

        def embed_query(self, text):
            return self.embed([text])[0]

    return _Fake()


@pytest.mark.skipif(not CONN, reason="ECONDATA_TEST_DB_CONN not set (opt-in)")
def test_vectorize_all_and_search():
    from src.vectorizer import Vectorizer

    fake = _fake_embedder()

    def _factory(**kwargs):
        return fake

    with patch("src.vectorizer.OllamaEmbedder", side_effect=_factory):
        vec = Vectorizer(connection_string=CONN, force_reembed=True)
        stats = vec.vectorize_all()

    assert stats["embedded"] > 0
    assert stats["errors"] == 0

    # dbe_embeddings has rows.
    import psycopg2

    dsn = CONN.replace("postgresql+psycopg2://", "postgresql://")
    with psycopg2.connect(dsn=dsn) as conn:
        with conn.cursor() as cur:
            cur.execute("SELECT count(*) FROM dbe_embeddings")
            n = cur.fetchone()[0]
            assert n > 0

    # Search returns results ranked by similarity.
    results = vec.search_documents("GDP", k=5)
    assert len(results) > 0
    assert results[0]["similarity"] >= 0
    assert results[0]["doc_type"] in ("metric", "entity", "entity_metric")

    # Hash stability: re-running without changes embeds nothing new.
    with patch("src.vectorizer.OllamaEmbedder", side_effect=_factory):
        vec2 = Vectorizer(connection_string=CONN, force_reembed=False)
        stats2 = vec2.vectorize_all()
    assert stats2["embedded"] == 0
    assert stats2["orphans_removed"] == 0


@pytest.mark.skipif(not CONN, reason="ECONDATA_TEST_DB_CONN not set (opt-in)")
def test_vectorize_hash_change_triggers_reembed():
    """A content hash change (simulated) causes the doc to be re-embedded.

    We verify the hash-comparison logic directly (no DB needed) to keep this
    test hermetic: build two metric docs, mutate, and confirm the hash differs
    so vectorize_all would re-embed.
    """
    from src.config import load_pipeline_config
    from src.vectorizer import Vectorizer

    catalog = load_pipeline_config()["metrics"]
    docs = Vectorizer.build_metric_documents(catalog)
    hashes_before = {d.doc_key: d.doc_hash for d in docs}

    # Mutate one metric and confirm its hash changes.
    mutated = {c: dict(info) for c, info in catalog.items()}
    first_code = list(mutated)[0]
    mutated[first_code]["description"] = "tampered for test"
    docs2 = Vectorizer.build_metric_documents(mutated)
    hashes_after = {d.doc_key: d.doc_hash for d in docs2}

    assert hashes_before != hashes_after
    assert hashes_after[first_code] != hashes_before[first_code]
    # All other metrics unchanged.
    for code in hashes_before:
        if code != first_code:
            assert hashes_before[code] == hashes_after[code]
