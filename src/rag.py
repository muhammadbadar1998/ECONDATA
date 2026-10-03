"""
Retrieval-augmented question answering over the embedded economic-data catalog.

The layer is deliberately split into pure-ish, individually testable steps:

  1. :meth:`RagAnswerer.retrieve`      — vector search in ``dbe_embeddings``
  2. :meth:`RagAnswerer.build_messages` — system prompt + context + history + question
  3. :meth:`RagAnswerer.answer`        — the two above plus the LLM call

Nothing here writes to the database and no SQL is generated from the question:
the assistant can only say what the retrieved documents already contain. The
`LLMClient.chat` seam already passes ``tools`` through and returns
``tool_calls``, so read-only tool-calling can be added without touching this
module or :mod:`src.chat`.
"""

import logging
from typing import Any, Dict, List, Optional, Sequence, Tuple

from src.config import get_rag_top_k
from src.embeddings import OllamaEmbedder
from src.llm import LLMClient

logger = logging.getLogger(__name__)

# Returned verbatim when retrieval finds nothing, so an out-of-scope question
# never reaches the model (cheaper, and the fastest way to avoid hallucination).
NO_CONTEXT_ANSWER = (
    "I don't have any catalog data that covers that question. "
    "I can only answer from the embedded economic catalog: metric definitions, "
    "country/region entities, and per-country series summaries (latest, min, max, "
    "and the most recent observations). Try naming a metric (e.g. GDP, inflation, "
    "population) and a country or region."
)

DEFAULT_SYSTEM_PROMPT = """\
You are a precise assistant for an economic-data catalog of country-level \
macroeconomic indicators (source: DBnomics / World Bank, stored in Supabase \
PostgreSQL).

Answer ONLY from the CONTEXT DOCUMENTS supplied in the user message.

Rules:
1. Use only numbers, units, years, entities, and definitions that appear in the \
context. Never invent, estimate, extrapolate, or recall a figure from memory.
2. If the context does not contain the answer, say so plainly and state what is \
missing. A clear "the catalog does not cover that" is always better than a guess.
3. The context contains, per (entity, metric) pair: observation count and year \
range, the latest value, the minimum and maximum with their years, and the most \
recent observations. Arbitrary historical years outside those recent points, and \
cross-entity aggregates or rankings, are NOT available. Say so instead of \
computing or guessing them.
4. Preserve the given units exactly (e.g. "Current US$", "%"). Quote values with \
thousands separators as shown in the context.
5. Cite every document you use by its doc_key in square brackets, e.g. \
[USA|gdp_usd]. Add no citations for facts not drawn from the context.
6. Be concise: a short paragraph or a compact list, no preamble.\
"""

# Number of prior user/assistant messages retained across turns.
DEFAULT_HISTORY_TURNS = 10


class RagAnswerer:
    """Answers catalog questions from retrieved documents (read-only)."""

    def __init__(
        self,
        connection_string: Optional[str] = None,
        embedder: Optional[OllamaEmbedder] = None,
        llm: Optional[LLMClient] = None,
        k: Optional[int] = None,
        system_prompt: Optional[str] = None,
        vectorizer: Optional[Any] = None,
    ):
        self.connection_string = connection_string or ""
        self.embedder = embedder
        self.llm = llm if llm is not None else LLMClient()
        self.k = int(k) if k else get_rag_top_k()
        self.system_prompt = system_prompt or DEFAULT_SYSTEM_PROMPT
        # Injectable for tests; built lazily so no DB handle is opened at import
        # or construction time.
        self._vectorizer = vectorizer

    # ------------------------------------------------------------------ #
    # Step 1 — retrieval
    # ------------------------------------------------------------------ #
    def _get_vectorizer(self):
        if self._vectorizer is None:
            from src.vectorizer import Vectorizer

            self._vectorizer = Vectorizer(
                connection_string=self.connection_string,
                embedder=self.embedder or OllamaEmbedder(),
            )
        return self._vectorizer

    def retrieve(self, question: str, k: Optional[int] = None) -> List[Dict[str, Any]]:
        """Return the top-k catalog documents most similar to ``question``.

        Each entry is ``{doc_key, doc_type, content, metadata, similarity}`` as
        produced by :meth:`src.vectorizer.Vectorizer.search_documents`.
        """
        if not question or not question.strip():
            return []
        docs = self._get_vectorizer().search_documents(question.strip(), k=k or self.k)
        logger.info("Retrieved %d document(s) for query.", len(docs))
        logger.debug("Query text: %r", question.strip()[:80])
        return docs

    # ------------------------------------------------------------------ #
    # Step 2 — prompt assembly
    # ------------------------------------------------------------------ #
    @staticmethod
    def format_context(docs: Sequence[Dict[str, Any]]) -> str:
        """Render retrieved documents as a labelled, numbered context block."""
        blocks = []
        for i, doc in enumerate(docs, 1):
            blocks.append(
                f"[{i}] doc_key={doc.get('doc_key')} doc_type={doc.get('doc_type')}\n"
                f"{doc.get('content', '').strip()}"
            )
        return "CONTEXT DOCUMENTS:\n\n" + "\n\n".join(blocks)

    def build_messages(
        self,
        question: str,
        docs: Sequence[Dict[str, Any]],
        history: Optional[Sequence[Dict[str, Any]]] = None,
    ) -> List[Dict[str, Any]]:
        """Build the chat message list: system, context, history, question.

        Kept separate from :meth:`answer` so the prompt can be asserted in tests
        without an LLM.
        """
        messages: List[Dict[str, Any]] = [
            {"role": "system", "content": self.system_prompt},
            {"role": "user", "content": self.format_context(docs)},
        ]
        messages.extend(dict(m) for m in _tail(history or []))
        messages.append({"role": "user", "content": question})
        return messages

    # ------------------------------------------------------------------ #
    # Step 3 — answer
    # ------------------------------------------------------------------ #
    def answer(
        self,
        question: str,
        history: Optional[List[Dict[str, Any]]] = None,
    ) -> Tuple[str, List[Dict[str, Any]]]:
        """Answer ``question`` from retrieved context.

        Returns ``(answer_text, docs)``. When ``history`` is a list, the
        question and the answer are appended to it so the caller can keep a
        bounded multi-turn transcript. Empty retrieval short-circuits: the
        canned answer is returned and the LLM is **not** called.
        """
        question = (question or "").strip()
        if not question:
            return "", []

        docs = self.retrieve(question)
        if not docs:
            logger.info("No documents retrieved; answering without the LLM.")
            logger.debug("Uncovered query: %r", question)
            return NO_CONTEXT_ANSWER, []

        messages = self.build_messages(question, docs, history)
        logger.info("Querying LLM with %d context document(s).", len(docs))
        message = self.llm.chat(messages)
        text = (message.get("content") or "").strip() if isinstance(message, dict) else ""
        if not text:
            text = "The model returned an empty answer for this question."
        logger.debug("LLM answer length: %d character(s).", len(text))

        if isinstance(history, list):
            history.append({"role": "user", "content": question})
            history.append({"role": "assistant", "content": text})
        return text, docs


def _tail(history: Sequence[Dict[str, Any]], max_messages: int = DEFAULT_HISTORY_TURNS * 2) -> List[Dict[str, Any]]:
    """Keep only the most recent ``max_messages`` prior turns."""
    history = [dict(m) for m in history if isinstance(m, dict) and m.get("role")]
    return history[-max_messages:] if max_messages and len(history) > max_messages else history
