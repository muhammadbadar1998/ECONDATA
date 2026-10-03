"""
Interactive CLI for the vector-RAG catalog assistant: ``python -m src.chat``.

    python -m src.chat                          # REPL against the configured LLM
    python -m src.chat --k 5 --show-sources     # narrower retrieval, print sources
    python -m src.chat --question "latest US GDP"   # one-shot, non-interactive
    python -m src.chat --system prompts/rag.txt     # custom system prompt

The REPL is read-only: it retrieves documents from ``dbe_embeddings`` and asks the
LLM to answer from them. Commands: ``exit``/``quit`` (or Ctrl-D) leave, ``reset``
clears the in-memory transcript, ``help`` lists commands, ``sources`` re-prints
the sources of the last answer.
"""

import argparse
import logging
import os
import sys
from datetime import datetime, timezone
from typing import Any, Dict, List, Optional, Sequence, TextIO, Tuple

from src.config import DEFAULT_OUTPUT_DIR, LOGS_DIR_NAME, get_rag_top_k
from src.embeddings import OllamaEmbedder
from src.llm import LLMClient, LLMError
from src.rag import DEFAULT_SYSTEM_PROMPT, RagAnswerer

logger = logging.getLogger(__name__)

BANNER = """\
EconData catalog assistant (vector RAG over dbe_embeddings)
Type a question and press Enter. Commands: help, sources, reset, exit
"""
EXIT_COMMANDS = frozenset({"exit", "quit", ":q", "/exit", "/quit"})


def setup_logger(output_dir: str) -> logging.Logger:
    """Configure logging: stdout plus ``output/logs/chat_<date>.log`` and ``output/chat.log``."""
    log = logging.getLogger("EconDataChat")
    log.setLevel(logging.INFO)
    log.handlers.clear()
    log.propagate = False

    formatter = logging.Formatter("%(asctime)s [%(levelname)s] %(name)s: %(message)s")

    stdout_handler = logging.StreamHandler(sys.stderr)
    stdout_handler.setFormatter(formatter)
    log.addHandler(stdout_handler)

    logs_dir = os.path.join(output_dir, LOGS_DIR_NAME)
    os.makedirs(logs_dir, exist_ok=True)
    date_str = datetime.now(timezone.utc).strftime("%Y-%m-%d")
    for path in (
        os.path.join(logs_dir, f"chat_{date_str}.log"),
        os.path.join(output_dir, "chat.log"),
    ):
        handler = logging.FileHandler(path, encoding="utf-8")
        handler.setFormatter(formatter)
        log.addHandler(handler)
    return log


def format_sources(docs: Sequence[Dict[str, Any]]) -> str:
    """Render the retrieved documents as a compact source listing."""
    if not docs:
        return "  (no sources retrieved)"
    lines = []
    for i, doc in enumerate(docs, 1):
        similarity = doc.get("similarity")
        score = f"{similarity:.4f}" if isinstance(similarity, (int, float)) else "n/a"
        lines.append(f"  [{i}] {doc.get('doc_key')} ({doc.get('doc_type')}) similarity={score}")
    return "\n".join(lines)


def read_system_prompt(path: str) -> str:
    """Read a system-prompt override from ``path`` (raises OSError if unreadable)."""
    with open(path, "r", encoding="utf-8") as fh:
        text = fh.read().strip()
    if not text:
        raise ValueError(f"System prompt file {path!r} is empty.")
    return text


def preflight(answerer: RagAnswerer) -> Optional[str]:
    """Check the embedding and chat backends. Returns an actionable message on failure."""
    embedder = answerer.embedder or answerer._get_vectorizer().embedder
    if not embedder.verify_connectivity():
        return (
            f"The embedding service is not reachable (OLLAMA_HOST="
            f"{getattr(embedder, 'base_url', '?')}). Run `ollama serve` and "
            "`ollama pull nomic-embed-text`."
        )
    llm = answerer.llm
    if isinstance(llm, LLMClient) and not llm.verify_connectivity():
        return (
            f"The LLM endpoint {llm.base_url} is not reachable. Start the server, or point "
            f"LLM_BASE_URL at a running OpenAI-compatible endpoint "
            f"(currently model {llm.model!r})."
        )
    return None


def _print(out: TextIO, text: str, end: str = "\n") -> None:
    print(text, file=out, end=end)


def handle_turn(
    answerer: RagAnswerer,
    question: str,
    history: List[Dict[str, Any]],
    out: TextIO,
    show_sources: bool = False,
) -> Tuple[Optional[str], List[Dict[str, Any]]]:
    """Answer one question, print it, and record it in ``history``.

    Returns ``(answer_text, docs)``; ``(None, [])`` when the call failed. The
    error is printed as a single line, never a traceback, so the REPL survives a
    dropped backend.
    """
    try:
        text, docs = answerer.answer(question, history=history)
    except LLMError as exc:
        _print(out, f"[error] {exc}")
        return None, []
    except Exception as exc:  # network, DB, or embedder failure
        logger.error("Turn failed for %r: %s", question, exc, exc_info=True)
        _print(out, f"[error] {type(exc).__name__}: {exc}")
        return None, []
    _print(out, text)
    if show_sources:
        _print(out, "\nSources:")
        _print(out, format_sources(docs))
    return text, docs


def run_repl(
    answerer: RagAnswerer,
    show_sources: bool = False,
    stdin: Optional[TextIO] = None,
    stdout: Optional[TextIO] = None,
) -> int:
    """Run the interactive loop. Returns a process exit code."""
    stdin = stdin or sys.stdin
    stdout = stdout or sys.stdout
    history: List[Dict[str, Any]] = []
    last_docs: List[Dict[str, Any]] = []

    _print(stdout, BANNER)
    _print(stdout, f"model={answerer.llm.model}  top_k={answerer.k}")
    _print(stdout, "")

    while True:
        try:
            _print(stdout, "you> ", end="")
            stdout.flush()
        except (ValueError, OSError):  # closed stream
            break
        line = stdin.readline()
        if not line:  # EOF
            _print(stdout, "")
            break
        question = line.strip()
        if not question:
            continue
        lowered = question.lower()
        if lowered in EXIT_COMMANDS:
            _print(stdout, "bye")
            break
        if lowered == "help":
            _print(stdout, "Commands: help, sources (last answer), reset (clear history), exit/quit")
            continue
        if lowered == "reset":
            history.clear()
            _print(stdout, "conversation history cleared")
            continue
        if lowered == "sources":
            _print(stdout, "Sources:")
            _print(stdout, format_sources(last_docs))
            continue

        before = len(history)
        text, docs = handle_turn(answerer, question, history, stdout, show_sources=show_sources)
        if text is not None:
            last_docs = docs
        elif before != len(history):
            history[:] = history[:before]  # a failed turn must not poison the transcript
    return 0


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        prog="python -m src.chat",
        description="Ask questions about the embedded economic-data catalog (vector RAG).",
    )
    parser.add_argument("--k", type=int, default=get_rag_top_k(), help="Documents retrieved per question")
    parser.add_argument("--show-sources", action="store_true", help="Print the retrieved doc_keys and similarities")
    parser.add_argument("--system", default="", help="Path to a system-prompt override file")
    parser.add_argument("--question", default="", help="Answer a single question and exit (non-interactive)")
    parser.add_argument("--connection-string", default="", help="Database connection string (overrides env config)")
    parser.add_argument("--output-dir", default=DEFAULT_OUTPUT_DIR, help="Directory for log files")
    args = parser.parse_args(argv)

    log = setup_logger(args.output_dir)
    system_prompt = DEFAULT_SYSTEM_PROMPT
    if args.system:
        try:
            system_prompt = read_system_prompt(args.system)
        except (OSError, ValueError) as exc:
            log.error("Could not read --system prompt file: %s", exc)
            return 1

    answerer = RagAnswerer(
        connection_string=args.connection_string,
        embedder=OllamaEmbedder(),
        llm=LLMClient(),
        k=args.k,
        system_prompt=system_prompt,
    )

    try:
        problem = preflight(answerer)
    except Exception as exc:
        log.error("Startup check failed: %s", exc)
        print(f"[error] Startup check failed: {exc}", file=sys.stderr)
        return 1
    if problem:
        log.error("%s", problem)
        print(f"[error] {problem}", file=sys.stderr)
        return 1

    if args.question:
        handle_turn(answerer, args.question, [], sys.stdout, show_sources=args.show_sources)
        return 0

    return run_repl(answerer, show_sources=args.show_sources)


if __name__ == "__main__":
    sys.exit(main())
