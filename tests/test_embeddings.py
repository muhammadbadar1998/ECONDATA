"""Unit tests for src.embeddings.OllamaEmbedder (network calls are mocked)."""

import pytest

from src.embeddings import DEFAULT_EMBEDDING_DIM, OllamaEmbedder


class _FakeResponse:
    def __init__(self, status_code=200, payload=None, raise_for_status=None):
        self.status_code = status_code
        self._payload = payload or {}
        self._raise_for_status = raise_for_status

    def raise_for_status(self):
        if self._raise_for_status:
            raise self._raise_for_status

    def json(self):
        if self._payload is None:
            raise ValueError("no payload")
        return self._payload


class FakeSession:
    """Records calls and returns canned responses for GET/POST."""

    def __init__(self, get_payload=None, post_payload=None, post_errors=None):
        self.get_payload = get_payload
        # post_payload: if callable, called with the batch list to produce the
        # response; otherwise a fixed dict returned for every POST.
        self.post_payload = post_payload
        self.post_errors = post_errors or []
        self.post_calls = 0
        self.get_calls = 0

    def get(self, url, params=None, timeout=None):
        self.get_calls += 1
        if self.get_payload is None:
            import requests

            raise requests.ConnectionError("refused")
        return _FakeResponse(200, self.get_payload)

    def post(self, url, json=None, timeout=None):
        self.post_calls += 1
        if self.post_errors and self.post_calls in self.post_errors:
            import requests

            raise requests.ConnectionError("transient failure")
        if self.post_payload is None:
            import requests

            raise requests.HTTPError("500 Server Error")
        payload = self.post_payload
        if callable(payload):
            # Build a per-batch response with the right number of vectors.
            input_texts = json.get("input", []) if json else []
            vec = [0.1] * DEFAULT_EMBEDDING_DIM
            payload = {"embeddings": [list(vec) for _ in input_texts]}
        return _FakeResponse(200, payload)


def _ok_session(n_results):
    """A session whose POST returns `n_results` embeddings of correct dim."""
    vec = [0.1] * DEFAULT_EMBEDDING_DIM
    return FakeSession(
        get_payload={"models": [{"model": "nomic-embed-text"}]},
        post_payload={"embeddings": [list(vec) for _ in range(n_results)]},
    )


def test_embed_single():
    sess = _ok_session(1)
    emb = OllamaEmbedder()
    emb._session = sess
    result = emb.embed(["hello"])
    assert len(result) == 1
    assert len(result[0]) == DEFAULT_EMBEDDING_DIM


def test_embed_batches():
    """Texts are chunked by batch_size; FakeSession returns per-batch vectors."""
    sess = FakeSession(
        get_payload={"models": [{"model": "nomic-embed-text"}]},
        post_payload=lambda: None,  # callable triggers per-batch sizing
    )
    emb = OllamaEmbedder(batch_size=2)
    emb._session = sess
    emb.embed(["a", "b", "c", "d", "e"])
    # ceil(5/2) = 3 POST calls
    assert sess.post_calls == 3


def test_embed_query_returns_one_vector():
    sess = _ok_session(1)
    emb = OllamaEmbedder()
    emb._session = sess
    vec = emb.embed_query("what is GDP?")
    assert len(vec) == DEFAULT_EMBEDDING_DIM


def test_verify_connectivity_ok():
    sess = _ok_session(1)
    emb = OllamaEmbedder()
    emb._session = sess
    assert emb.verify_connectivity() is True


def test_verify_connectivity_fail():
    sess = FakeSession()  # get raises ConnectionError
    emb = OllamaEmbedder()
    emb._session = sess
    assert emb.verify_connectivity() is False


def test_dimension_validation_rejects_wrong_dim():
    sess = FakeSession(
        get_payload={"models": [{"model": "nomic-embed-text"}]},
        post_payload={"embeddings": [[0.1, 0.2, 0.3]]},  # wrong dim
    )
    emb = OllamaEmbedder()
    emb._session = sess
    with pytest.raises(RuntimeError, match="failed after 3 attempts"):
        emb.embed(["text"])


def test_retry_then_succeeds():
    sess = _ok_session(1)
    # First POST call fails (error 1), second succeeds.
    sess.post_errors = [1]
    emb = OllamaEmbedder()
    emb._session = sess
    result = emb.embed(["retry me"])
    assert len(result) == 1
    assert sess.post_calls == 2
