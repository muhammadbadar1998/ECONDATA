"""Unit tests for src.llm.LLMClient (transport is mocked; no network)."""

import pytest
import requests

from src.llm import LLMClient, LLMError


class _FakeResponse:
    def __init__(self, status_code=200, payload=None, text=""):
        self.status_code = status_code
        self._payload = payload
        self.text = text

    def raise_for_status(self):
        if self.status_code >= 400:
            raise requests.HTTPError(f"{self.status_code} error", response=self)

    def json(self):
        if self._payload is None:
            raise ValueError("not json")
        return self._payload


class FakeSession:
    """Records requests and replays canned responses/errors."""

    def __init__(self, responses=None, errors=None, get_payload=None, get_errors=None):
        self.responses = list(responses or [])
        self.errors = list(errors or [])
        self.get_errors = list(get_errors or [])
        self.get_payload = get_payload
        self.calls = []
        self.get_calls = []

    def post(self, url, json=None, headers=None, timeout=None):
        self.calls.append({"url": url, "json": json, "headers": headers, "timeout": timeout})
        if self.errors:
            raise self.errors.pop(0)
        if self.responses:
            return self.responses.pop(0)
        return _FakeResponse(200, {"choices": [{"message": {"role": "assistant", "content": "ok"}}]})

    def get(self, url, headers=None, timeout=None):
        self.get_calls.append(url)
        if self.get_errors:
            raise self.get_errors.pop(0)
        return _FakeResponse(200, self.get_payload or {"data": [{"id": "qwen2.5:7b-instruct"}]})


def _client(**kwargs):
    kwargs.setdefault("base_url", "http://llm.test/v1")
    kwargs.setdefault("model", "qwen2.5:7b-instruct")
    return LLMClient(**kwargs)


def _ok(content="The latest value is X."):
    return _FakeResponse(200, {"choices": [{"message": {"role": "assistant", "content": content}}]})


# --------------------------------------------------------------------------- #
# Request shape
# --------------------------------------------------------------------------- #
def test_chat_posts_to_chat_completions():
    sess = FakeSession(responses=[_ok()])
    client = _client(session=sess)
    result = client.chat([{"role": "user", "content": "hi"}])
    assert result["content"] == "The latest value is X."
    call = sess.calls[0]
    assert call["url"] == "http://llm.test/v1/chat/completions"
    assert call["json"]["model"] == "qwen2.5:7b-instruct"
    assert call["json"]["messages"] == [{"role": "user", "content": "hi"}]
    assert call["json"]["stream"] is False
    assert call["json"]["temperature"] == client.temperature
    assert "tools" not in call["json"]


def test_chat_omits_authorization_header_without_key():
    sess = FakeSession(responses=[_ok()])
    _client(api_key="", session=sess).chat([{"role": "user", "content": "hi"}])
    assert "Authorization" not in sess.calls[0]["headers"]


def test_chat_sends_authorization_header_with_key():
    sess = FakeSession(responses=[_ok()])
    _client(api_key="sk-secret", session=sess).chat([{"role": "user", "content": "hi"}])
    assert sess.calls[0]["headers"]["Authorization"] == "Bearer sk-secret"


def test_chat_passes_tools_through():
    sess = FakeSession(responses=[_ok()])
    tools = [{"type": "function", "function": {"name": "get_series"}}]
    _client(session=sess).chat([{"role": "user", "content": "hi"}], tools=tools)
    assert sess.calls[0]["json"]["tools"] == tools


def test_chat_returns_tool_calls_when_present():
    payload = {
        "choices": [
            {
                "message": {
                    "role": "assistant",
                    "content": None,
                    "tool_calls": [{"id": "call_1", "function": {"name": "get_series"}}],
                }
            }
        ]
    }
    sess = FakeSession(responses=[_FakeResponse(200, payload)])
    message = _client(session=sess).chat([{"role": "user", "content": "hi"}])
    assert message["tool_calls"][0]["function"]["name"] == "get_series"


def test_chat_uses_timeout():
    sess = FakeSession(responses=[_ok()])
    client = _client(timeout=7, session=sess)
    client.chat([{"role": "user", "content": "hi"}])
    assert sess.calls[0]["timeout"] == 7


# --------------------------------------------------------------------------- #
# Environment defaults (lazy resolution)
# --------------------------------------------------------------------------- #
def test_defaults_come_from_environment(monkeypatch):
    monkeypatch.setenv("LLM_BASE_URL", "http://example.test/v9/")
    monkeypatch.setenv("LLM_API_KEY", "key-123")
    monkeypatch.setenv("LLM_MODEL", "some-chat-model")
    monkeypatch.setenv("LLM_TIMEOUT", "42")
    monkeypatch.setenv("LLM_TEMPERATURE", "0.7")
    client = LLMClient()
    assert client.base_url == "http://example.test/v9"
    assert client.api_key == "key-123"
    assert client.model == "some-chat-model"
    assert client.timeout == 42
    assert client.temperature == 0.7


def test_defaults_without_environment(monkeypatch):
    for name in ("LLM_BASE_URL", "LLM_API_KEY", "LLM_MODEL", "LLM_TIMEOUT", "LLM_TEMPERATURE"):
        monkeypatch.delenv(name, raising=False)
    client = LLMClient()
    assert client.base_url == "http://127.0.0.1:11434/v1"
    assert client.api_key == ""
    assert client.model == "qwen2.5:7b-instruct"
    assert client.timeout == 120
    assert client.temperature == 0.2


def test_explicit_arguments_win_over_environment(monkeypatch):
    monkeypatch.setenv("LLM_MODEL", "from-env")
    client = LLMClient(model="explicit", base_url="http://x.test/v1")
    assert client.model == "explicit"


# --------------------------------------------------------------------------- #
# Errors
# --------------------------------------------------------------------------- #
def test_unauthorized_error_is_actionable():
    sess = FakeSession(responses=[_FakeResponse(401, None, text="nope")])
    with pytest.raises(LLMError, match="LLM_API_KEY"):
        _client(session=sess).chat([{"role": "user", "content": "hi"}])
    assert len(sess.calls) == 1  # not retried


def test_not_found_error_mentions_base_url():
    sess = FakeSession(responses=[_FakeResponse(404, None, text="")])
    with pytest.raises(LLMError, match="chat/completions"):
        _client(session=sess).chat([{"role": "user", "content": "hi"}])


def test_connection_error_is_actionable(monkeypatch):
    monkeypatch.setattr("src.llm.time.sleep", lambda _s: None)
    sess = FakeSession(errors=[requests.ConnectionError("refused")] * 3)
    with pytest.raises(LLMError, match="failed after 3 attempts"):
        _client(session=sess).chat([{"role": "user", "content": "hi"}])


def test_retry_then_succeeds(monkeypatch):
    monkeypatch.setattr("src.llm.time.sleep", lambda _s: None)
    sess = FakeSession(errors=[requests.ConnectionError("transient")], responses=[_ok("second")])
    assert _client(session=sess).chat([{"role": "user", "content": "hi"}])["content"] == "second"
    assert len(sess.calls) == 2


def test_retries_on_server_error_then_fails(monkeypatch):
    monkeypatch.setattr("src.llm.time.sleep", lambda _s: None)
    sess = FakeSession(responses=[_FakeResponse(503, None, text="unavailable")] * 3)
    with pytest.raises(LLMError, match="HTTP 503"):
        _client(session=sess).chat([{"role": "user", "content": "hi"}])
    assert len(sess.calls) == 3


def test_bad_request_is_not_retried():
    sess = FakeSession(responses=[_FakeResponse(400, None, text="bad schema")])
    with pytest.raises(LLMError, match="HTTP 400"):
        _client(session=sess).chat([{"role": "user", "content": "hi"}])
    assert len(sess.calls) == 1


def test_empty_choices_error_mentions_model():
    sess = FakeSession(responses=[_FakeResponse(200, {"choices": [], "id": "x"})])
    with pytest.raises(LLMError, match="chat-capable model"):
        _client(session=sess).chat([{"role": "user", "content": "hi"}])


def test_error_object_in_body_is_surfaced():
    sess = FakeSession(responses=[_FakeResponse(200, {"error": {"message": "model not found"}})])
    with pytest.raises(LLMError, match="model not found"):
        _client(session=sess).chat([{"role": "user", "content": "hi"}])


def test_non_json_body_is_surfaced():
    sess = FakeSession(responses=[_FakeResponse(200, None, text="<html>oops</html>")])
    with pytest.raises(LLMError, match="non-JSON"):
        _client(session=sess).chat([{"role": "user", "content": "hi"}])


# --------------------------------------------------------------------------- #
# Connectivity
# --------------------------------------------------------------------------- #
def test_verify_connectivity_ok():
    sess = FakeSession()
    assert _client(session=sess).verify_connectivity() is True
    assert sess.get_calls == ["http://llm.test/v1/models"]


def test_verify_connectivity_fail():
    sess = FakeSession(get_errors=[requests.ConnectionError("refused")])
    assert _client(session=sess).verify_connectivity() is False


def test_verify_connectivity_tolerates_missing_models_route():
    """A server without /v1/models is still reachable; only warn."""
    sess = FakeSession()
    sess.get = lambda url, headers=None, timeout=None: _FakeResponse(404, None, text="not found")
    assert _client(session=sess).verify_connectivity() is True
