"""OPT-IN end-to-end test for the catalog chatbot (vector RAG over a live stack).

Skipped unless ECONDATA_TEST_DB_CONN is set. It then additionally needs:
  - a reachable embedding service (``nomic-embed-text``), and
  - a reachable OpenAI-compatible chat endpoint with a chat model available
    (``LLM_MODEL``; for local Ollama run ``ollama pull <model>``).

Example (PowerShell):
    $env:ECONDATA_TEST_DB_CONN = "postgresql+psycopg2://postgres:pw@host:5432/postgres"
    $env:LLM_MODEL = "qwen2.5:7b-instruct"
    ollama serve
    pytest tests/test_chat_integration.py

The DB must already be vectorized (``python -m src.main --vectorize``).
"""

import os

import pytest

CONN = os.getenv("ECONDATA_TEST_DB_CONN")
SKIP_NO_DB = "ECONDATA_TEST_DB_CONN not set (opt-in)"

GDP_DOC = "USA|gdp_usd"


def _answerer():
    from src.rag import RagAnswerer

    return RagAnswerer(connection_string=CONN)


def _skip_unless_llm():
    """Skip with a clear reason when the chat endpoint is unusable."""
    from src.config import get_llm_base_url
    from src.llm import LLMClient

    client = LLMClient()
    if not client.verify_connectivity():
        pytest.skip(f"LLM endpoint {get_llm_base_url()} is unreachable")
    return client


@pytest.mark.skipif(not CONN, reason=SKIP_NO_DB)
def test_retrieval_ranks_the_us_gdp_document():
    """Semantic search alone must surface USA|gdp_usd for a GDP question."""
    from src.embeddings import OllamaEmbedder

    if not OllamaEmbedder().verify_connectivity():
        pytest.skip("Ollama embedding service is unreachable")

    docs = _answerer().retrieve("What is the latest US GDP?", k=8)
    assert docs, "retrieval returned no documents"
    assert GDP_DOC in [d["doc_key"] for d in docs]
    assert docs[0]["doc_key"] == GDP_DOC or docs[0]["doc_type"] == "entity_metric"


@pytest.mark.skipif(not CONN, reason=SKIP_NO_DB)
def test_retrieved_document_carries_values_for_grounding():
    """The enriched entity_metric content must expose a quotable number."""
    from src.embeddings import OllamaEmbedder

    if not OllamaEmbedder().verify_connectivity():
        pytest.skip("Ollama embedding service is unreachable")

    docs = _answerer().retrieve("latest US GDP", k=8)
    target = next(d for d in docs if d["doc_key"] == GDP_DOC)
    assert "Latest:" in target["content"]
    assert "Min:" in target["content"]
    assert "Max:" in target["content"]
    assert target["metadata"]["latest_year"] is not None


@pytest.mark.skipif(not CONN, reason=SKIP_NO_DB)
def test_answer_grounded_in_the_gdp_document():
    """A grounded answer quotes a number from a retrieved document and cites it."""
    _skip_unless_llm()
    from src.embeddings import OllamaEmbedder

    if not OllamaEmbedder().verify_connectivity():
        pytest.skip("Ollama embedding service is unreachable")

    text, docs = _answerer().answer("What is the latest US GDP?")
    assert GDP_DOC in [d["doc_key"] for d in docs]
    assert GDP_DOC in text, f"answer did not cite {GDP_DOC}: {text!r}"
    # No invented numbers: at least one significant figure quoted in the answer
    # must come verbatim from one of the documents the model was given.
    haystack = text.replace(",", "").replace(" ", "")
    needles = {
        token
        for doc in docs
        for token in doc["content"].replace(",", "").split()
        if token.isdigit() and len(token) >= 4
    }
    assert any(needle in haystack for needle in needles), (
        f"answer quoted no figure present in the retrieved context: {text!r}"
    )


@pytest.mark.skipif(not CONN, reason=SKIP_NO_DB)
def test_out_of_scope_question_is_declined():
    """An unrelated question must not produce an invented economic answer."""
    _skip_unless_llm()
    from src.embeddings import OllamaEmbedder

    if not OllamaEmbedder().verify_connectivity():
        pytest.skip("Ollama embedding service is unreachable")

    text, _ = _answerer().answer("What is the weather in Paris tomorrow?")
    lowered = text.lower()
    assert any(
        marker in lowered
        for marker in (
            "don't have",
            "do not have",
            "not available",
            "not in the catalog",
            "no data",
            "cannot",
            "can't",
            "insufficient",
            "not cover",
        )
    ), f"expected a decline, got: {text!r}"


@pytest.mark.skipif(not CONN, reason=SKIP_NO_DB)
def test_multi_turn_history_is_accepted():
    """Two turns in one session: the second prompt carries the first exchange."""
    _skip_unless_llm()
    from src.embeddings import OllamaEmbedder

    if not OllamaEmbedder().verify_connectivity():
        pytest.skip("Ollama embedding service is unreachable")

    answerer = _answerer()
    history = []
    answerer.answer("What metrics are in the catalog?", history=history)
    assert [m["role"] for m in history] == ["user", "assistant"]
    answerer.answer("And which entity is the World aggregate?", history=history)
    assert len(history) == 4
