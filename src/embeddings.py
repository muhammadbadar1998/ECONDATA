"""
Embedding client for the semantic catalog layer.

Communicates with a locally-running Ollama service (no Python embedding
dependency — HTTP via ``requests``). The default model is
``nomic-embed-text`` (768-dimensional output).

Environment (see ``config/.env.example``):
  - ``OLLAMA_HOST``               Base URL, default ``http://127.0.0.1:11434``
  - ``OLLAMA_EMBEDDING_MODEL``    Model name, default ``nomic-embed-text``
  - ``OLLAMA_TIMEOUT``            Per-request timeout seconds, default 60
  - ``OLLAMA_EMBEDDING_BATCH_SIZE`` Max texts per batch, default 32

The module is import-safe: it does **not** validate connectivity at import time.
Callers should invoke :meth:`OllamaEmbedder.verify_connectivity` to surface
setup problems before vectorization runs.
"""

import logging
import os
import time
from typing import List, Optional

import requests

# nomic-embed-text produces 768-dimensional vectors.
DEFAULT_EMBEDDING_DIM = 768

logger = logging.getLogger(__name__)


class OllamaEmbedder:
    """HTTP client for Ollama's ``/api/embed`` endpoint (nomic-embed-text)."""

    def __init__(
        self,
        base_url: Optional[str] = None,
        model: Optional[str] = None,
        timeout: Optional[int] = None,
        batch_size: Optional[int] = None,
        embedding_dim: int = DEFAULT_EMBEDDING_DIM,
    ):
        self.base_url = (base_url or os.getenv("OLLAMA_HOST", "http://127.0.0.1:11434")).rstrip("/")
        self.model = model or os.getenv("OLLAMA_EMBEDDING_MODEL", "nomic-embed-text")
        self.timeout = timeout if timeout is not None else int(os.getenv("OLLAMA_TIMEOUT", "60"))
        self.batch_size = (
            batch_size
            if batch_size is not None
            else int(os.getenv("OLLAMA_EMBEDDING_BATCH_SIZE", "32"))
        )
        self.embedding_dim = embedding_dim
        self._session = requests.Session()

    def verify_connectivity(self) -> bool:
        """Return True if Ollama is reachable and the configured model is loaded."""
        try:
            resp = self._session.get(
                f"{self.base_url}/api/tags", timeout=self.timeout
            )
            resp.raise_for_status()
            tags = resp.json().get("models", [])
            # Ollama /api/tags returns model names with a ":latest" (or other) tag.
            loaded = {m.get("model", "").split(":")[0] for m in tags}
            if self.model not in loaded:
                logger.warning(
                    "Ollama model %r not in /api/tags (%s); "
                    "it may still be pulled on first use.",
                    self.model,
                    sorted(loaded) or "no models listed",
                )
            return True
        except requests.RequestException as exc:
            logger.error("Ollama connectivity check failed: %s", exc)
            return False

    def embed(self, texts: List[str]) -> List[List[float]]:
        """Embed a list of texts, returning a list of float vectors.

        Texts are sent in batches of ``batch_size``. Each request is retried with
        exponential backoff on transient HTTP/network errors. Vectors are
        dimension-validated against ``embedding_dim``.
        """
        results: List[List[float]] = []
        logger.info(
            "Embedding %d text(s) with model %s in batches of %d.",
            len(texts),
            self.model,
            self.batch_size,
        )
        for i in range(0, len(texts), self.batch_size):
            batch = texts[i : i + self.batch_size]
            embeddings = self._embed_batch(batch)
            results.extend(embeddings)
        return results

    def embed_query(self, text: str) -> List[float]:
        """Embed a single query text and return its vector."""
        return self.embed([text])[0]

    def _embed_batch(self, texts: List[str]) -> List[List[float]]:
        payload = {"model": self.model, "input": texts}
        last_exc: Optional[Exception] = None
        for attempt in range(1, 4):
            try:
                resp = self._session.post(
                    f"{self.base_url}/api/embed",
                    json=payload,
                    timeout=self.timeout,
                )
                resp.raise_for_status()
                data = resp.json()
                # Newer Ollama returns {"embeddings": [[...], ...]} (list per input).
                embeddings = data.get("embeddings")
                if embeddings is None and "embedding" in data:
                    embeddings = [data["embedding"]]
                if not embeddings:
                    raise ValueError(
                        f"Ollama /api/embed returned no embeddings for batch "
                        f"(model={self.model!r}). Response keys: {list(data.keys())}"
                    )
                if len(embeddings) != len(texts):
                    raise ValueError(
                        f"Embedding count mismatch: requested {len(texts)} but got "
                        f"{len(embeddings)}"
                    )
                for vec in embeddings:
                    self._validate_vector(vec)
                logger.debug("Embedded batch of %d text(s).", len(texts))
                return embeddings
            except (requests.RequestException, ValueError) as exc:
                last_exc = exc
                logger.warning(
                    "Embedding attempt %d/3 failed for %d texts: %s",
                    attempt,
                    len(texts),
                    exc,
                )
                if attempt < 3:
                    time.sleep(2 ** attempt)
        raise RuntimeError(
            f"Ollama embedding failed after 3 attempts (model={self.model!r}): {last_exc}"
        )

    def _validate_vector(self, vec: List[float]) -> None:
        if not isinstance(vec, list) or len(vec) != self.embedding_dim:
            raise ValueError(
                f"Expected {self.embedding_dim}-dimensional embedding, got "
                f"{len(vec) if isinstance(vec, list) else 'non-list'} vector"
            )
