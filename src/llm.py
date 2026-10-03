"""
Provider-agnostic chat-completion client (OpenAI wire format over ``requests``).

The chatbot talks to any server that implements ``POST {base_url}/chat/completions``:
a local Ollama (``http://127.0.0.1:11434/v1``), vLLM, LM Studio, OpenAI, Groq, ...
Nothing here is Ollama-specific and no new Python dependency is required.

Environment (see ``config/.env.example``):
  - ``LLM_BASE_URL``     OpenAI-compatible base URL, default ``http://127.0.0.1:11434/v1``
  - ``LLM_API_KEY``      Bearer token; omit the header entirely when empty
  - ``LLM_MODEL``        Chat model name, default ``qwen2.5:7b-instruct``
  - ``LLM_TIMEOUT``      Per-request timeout seconds, default 120
  - ``LLM_TEMPERATURE``  Sampling temperature, default 0.2

``LLM_TIMEOUT`` is deliberately distinct from ``OLLAMA_TIMEOUT``: one bounds chat
completions (slow, token-generating), the other bounds embedding calls (fast).

The module is import-safe: it performs **no** network I/O at import time and
makes no request until a method is called. Call :meth:`LLMClient.verify_connectivity`
first to turn setup problems into an actionable message instead of a traceback.
"""

import logging
import time
from typing import Any, Dict, List, Optional

import requests

from src.config import (
    get_llm_api_key,
    get_llm_base_url,
    get_llm_model,
    get_llm_temperature,
    get_llm_timeout,
)

logger = logging.getLogger(__name__)

# HTTP statuses worth retrying: request timeout, rate limit, and server errors.
_RETRYABLE_STATUS = frozenset({408, 409, 425, 429, 500, 502, 503, 504})


class LLMError(RuntimeError):
    """Raised for any chat-completion failure, with an actionable message."""


class LLMClient:
    """Thin client for the OpenAI ``/chat/completions`` endpoint."""

    def __init__(
        self,
        base_url: Optional[str] = None,
        api_key: Optional[str] = None,
        model: Optional[str] = None,
        timeout: Optional[int] = None,
        temperature: Optional[float] = None,
        max_retries: int = 3,
        session: Optional[Any] = None,
    ):
        # Defaults are resolved lazily from the environment (via src.config) so
        # construction never reads or validates anything the caller did not pass.
        self.base_url = (base_url if base_url is not None else get_llm_base_url()).rstrip("/")
        self.api_key = api_key if api_key is not None else get_llm_api_key()
        self.model = model if model is not None else get_llm_model()
        self.timeout = timeout if timeout is not None else get_llm_timeout()
        self.temperature = temperature if temperature is not None else get_llm_temperature()
        self.max_retries = max(1, int(max_retries))
        self._session = session or requests.Session()

    # ------------------------------------------------------------------ #
    # Health
    # ------------------------------------------------------------------ #
    @property
    def chat_url(self) -> str:
        return f"{self.base_url}/chat/completions"

    def verify_connectivity(self) -> bool:
        """Return True if ``GET {base_url}/models`` responds.

        A missing/unreachable ``/models`` route is only logged, not fatal: the
        point of the check is to catch a dead endpoint early.
        """
        try:
            resp = self._session.get(f"{self.base_url}/models", headers=self._headers(), timeout=self.timeout)
        except requests.RequestException as exc:
            logger.error("LLM endpoint %s unreachable: %s", self.base_url, exc)
            return False
        status = getattr(resp, "status_code", 200)
        if status >= 400:
            logger.warning(
                "GET %s/models returned HTTP %s; the endpoint may not implement it.",
                self.base_url,
                status,
            )
            return True
        try:
            listed = {m.get("id", "") for m in (resp.json().get("data") or [])}
        except (ValueError, AttributeError):
            listed = set()
        if listed and self.model not in listed:
            logger.warning(
                "LLM model %r is not listed by %s (%s). "
                "For a local Ollama server run `ollama pull %s`.",
                self.model,
                self.base_url,
                sorted(listed)[:10],
                self.model,
            )
        return True

    # ------------------------------------------------------------------ #
    # Chat
    # ------------------------------------------------------------------ #
    def _headers(self) -> Dict[str, str]:
        headers = {"Content-Type": "application/json"}
        # Local servers ignore auth; cloud endpoints require it. Only send the
        # header when a key exists, and never log it.
        if self.api_key:
            headers["Authorization"] = f"Bearer {self.api_key}"
        return headers

    def build_payload(self, messages: List[Dict[str, Any]], tools: Optional[List[Dict[str, Any]]] = None) -> Dict[str, Any]:
        """Build the request body for a chat completion."""
        payload: Dict[str, Any] = {
            "model": self.model,
            "messages": messages,
            "temperature": self.temperature,
            "stream": False,
        }
        if tools:
            payload["tools"] = tools
        return payload

    def chat(
        self,
        messages: List[Dict[str, Any]],
        tools: Optional[List[Dict[str, Any]]] = None,
    ) -> Dict[str, Any]:
        """Run one chat completion and return ``choices[0].message``.

        Returning the whole message dict (rather than just its ``content``)
        keeps ``tool_calls`` reachable, so tool-calling can be added later
        without changing this signature. Transient failures are retried with
        ``2 ** attempt`` second backoff; everything else raises
        :class:`LLMError` with an actionable message.
        """
        payload = self.build_payload(messages, tools)
        headers = self._headers()
        last_error: Optional[str] = None
        started = time.monotonic()

        for attempt in range(1, self.max_retries + 1):
            try:
                resp = self._session.post(
                    self.chat_url,
                    json=payload,
                    headers=headers,
                    timeout=self.timeout,
                )
            except requests.RequestException as exc:
                last_error = f"cannot reach the LLM endpoint at {self.base_url} ({exc})"
                logger.warning("LLM attempt %d/%d failed: %s", attempt, self.max_retries, exc)
            else:
                status = getattr(resp, "status_code", 200)
                if status in (401, 403):
                    raise LLMError(
                        f"LLM endpoint {self.base_url} rejected the credentials (HTTP {status}). "
                        "Check LLM_API_KEY (it may be unset or wrong for this server)."
                    )
                if status == 404:
                    raise LLMError(
                        f"LLM endpoint {self.base_url} has no /chat/completions route (HTTP 404). "
                        "Check LLM_BASE_URL points at the OpenAI-compatible root (usually .../v1)."
                    )
                if status not in _RETRYABLE_STATUS and status >= 400:
                    raise LLMError(
                        f"LLM request failed with HTTP {status}: {_safe_detail(resp)}"
                    )
                if status in _RETRYABLE_STATUS:
                    last_error = f"HTTP {status}: {_safe_detail(resp)}"
                    logger.warning("LLM attempt %d/%d failed: %s", attempt, self.max_retries, last_error)
                else:
                    message = self._parse_message(resp)
                    logger.info(
                        "Chat completion succeeded in %.2fs (model=%s).",
                        time.monotonic() - started,
                        self.model,
                    )
                    return message

            if attempt < self.max_retries:
                time.sleep(2 ** attempt)

        raise LLMError(
            f"LLM request to {self.base_url} failed after {self.max_retries} attempts "
            f"({last_error}). Is the endpoint running and is LLM_MODEL={self.model!r} available "
            "(`ollama pull <model>` for a local Ollama server)?"
        )

    def _parse_message(self, resp: Any) -> Dict[str, Any]:
        try:
            data = resp.json()
        except ValueError as exc:
            raise LLMError(f"LLM endpoint {self.base_url} returned a non-JSON body: {exc}") from exc
        if not isinstance(data, dict):
            raise LLMError(f"LLM endpoint {self.base_url} returned {type(data).__name__}, expected an object.")
        if "error" in data and not data.get("choices"):
            error = data["error"]
            detail = error.get("message") if isinstance(error, dict) else str(error)
            raise LLMError(f"LLM endpoint returned an error: {detail}")
        choices = data.get("choices") or []
        if not choices:
            raise LLMError(
                f"LLM endpoint {self.base_url} returned no choices (keys: {sorted(data)}). "
                f"Is {self.model!r} a chat-capable model?"
            )
        message = choices[0].get("message")
        if not isinstance(message, dict):
            raise LLMError(f"LLM response choice[0] has no 'message' object: {choices[0]!r}")
        return message


def _safe_detail(resp: Any, limit: int = 300) -> str:
    """Best-effort, bounded error detail from a response (never credentials)."""
    text = getattr(resp, "text", "") or ""
    text = " ".join(str(text).split())
    return text[:limit] if text else "no response body"
