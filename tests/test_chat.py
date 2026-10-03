"""Unit tests for the src.chat CLI loop (fakes for stdin/stdout and the answerer)."""

import io

import pytest

from src.chat import format_sources, main, read_system_prompt, run_repl
from src.llm import LLMError

DOC = {
    "doc_key": "USA|gdp_usd",
    "doc_type": "entity_metric",
    "content": "United States ... Latest: 27,369,400,000,000 (2024)",
    "metadata": {},
    "similarity": 0.8123,
}


class FakeLLM:
    model = "fake-model"


class FakeAnswerer:
    """Minimal RagAnswerer stand-in: fixed replies, recorded history."""

    def __init__(self, docs=(DOC,), error=None, answers=None):
        self.k = 8
        self.llm = FakeLLM()
        self.docs = list(docs)
        self.error = error
        self.answers = list(answers or [])
        self.questions = []
        self.histories = []

    def answer(self, question, history=None):
        self.questions.append(question)
        # Snapshot: the REPL mutates the same list object across turns.
        self.histories.append(list(history) if history is not None else None)
        if self.error is not None:
            raise self.error
        text = self.answers.pop(0) if self.answers else f"answer to {question}"
        if isinstance(history, list):
            history.append({"role": "user", "content": question})
            history.append({"role": "assistant", "content": text})
        return text, self.docs


def _run(answerer, stdin_text, show_sources=False):
    out = io.StringIO()
    code = run_repl(answerer, show_sources=show_sources, stdin=io.StringIO(stdin_text), stdout=out)
    return code, out.getvalue()


# --------------------------------------------------------------------------- #
# Sources rendering
# --------------------------------------------------------------------------- #
def test_format_sources_includes_key_type_and_similarity():
    text = format_sources([DOC])
    assert "USA|gdp_usd" in text
    assert "entity_metric" in text
    assert "0.8123" in text


def test_format_sources_empty():
    assert format_sources([]) == "  (no sources retrieved)"


# --------------------------------------------------------------------------- #
# REPL loop
# --------------------------------------------------------------------------- #
def test_repl_answers_a_question():
    answerer = FakeAnswerer()
    code, output = _run(answerer, "What is the latest US GDP?\n")
    assert code == 0
    assert "answer to What is the latest US GDP?" in output
    assert answerer.questions == ["What is the latest US GDP?"]


def test_repl_exits_on_exit_and_quit():
    for command in ("exit", "quit"):
        answerer = FakeAnswerer()
        code, output = _run(answerer, f"{command}\n")
        assert code == 0
        assert "bye" in output
        assert answerer.questions == []


def test_repl_exits_on_eof():
    answerer = FakeAnswerer()
    code, _ = _run(answerer, "")
    assert code == 0
    assert answerer.questions == []


def test_repl_resets_history():
    answerer = FakeAnswerer()
    code, output = _run(answerer, "first\nreset\nsecond\nexit\n")
    assert code == 0
    assert "history cleared" in output
    assert answerer.questions == ["first", "second"]
    # The first turn saw an empty transcript...
    assert answerer.histories[0] == []
    # ...and so did the turn after `reset`.
    assert answerer.histories[1] == []


def test_repl_carries_history_between_turns():
    answerer = FakeAnswerer()
    _run(answerer, "first\nsecond\nexit\n")
    assert answerer.histories[0] == []
    assert len(answerer.histories[1]) == 2
    assert answerer.histories[1][0]["content"] == "first"
    assert answerer.histories[1][1]["content"] == "answer to first"


def test_repl_ignores_blank_lines():
    answerer = FakeAnswerer()
    _run(answerer, "\n   \nreal question\nexit\n")
    assert answerer.questions == ["real question"]


def test_repl_show_sources_flag():
    answerer = FakeAnswerer()
    _, output = _run(answerer, "gdp\nexit\n", show_sources=True)
    assert "Sources:" in output
    assert "USA|gdp_usd" in output
    assert "0.8123" in output


def test_sources_command_prints_last_sources():
    answerer = FakeAnswerer()
    _, output = _run(answerer, "gdp\nsources\nexit\n")
    assert "Sources:\n  [1] USA|gdp_usd (entity_metric) similarity=0.8123" in output


def test_help_command_lists_commands():
    _, output = _run(FakeAnswerer(), "help\nexit\n")
    assert "reset" in output and "exit" in output


def test_llm_error_prints_single_line_without_traceback():
    answerer = FakeAnswerer(error=LLMError("endpoint at http://x is down"))
    code, output = _run(answerer, "gdp\nstill alive?\nexit\n")
    assert code == 0
    assert "[error] endpoint at http://x is down" in output
    assert "Traceback" not in output
    # The loop survives and keeps answering.
    assert answerer.questions == ["gdp", "still alive?"]


def test_unexpected_error_prints_type_name():
    answerer = FakeAnswerer(error=ValueError("bad payload"))
    _, output = _run(answerer, "gdp\nexit\n")
    assert "[error] ValueError: bad payload" in output
    assert "Traceback" not in output


# --------------------------------------------------------------------------- #
# System prompt file
# --------------------------------------------------------------------------- #
def test_read_system_prompt(tmp_path):
    path = tmp_path / "p.txt"
    path.write_text("Be terse.\n", encoding="utf-8")
    assert read_system_prompt(str(path)) == "Be terse."


def test_read_system_prompt_rejects_empty(tmp_path):
    path = tmp_path / "p.txt"
    path.write_text("   \n", encoding="utf-8")
    with pytest.raises(ValueError):
        read_system_prompt(str(path))


def test_main_fails_on_unreadable_system_file(tmp_path, capsys):
    code = main(["--system", str(tmp_path / "missing.txt")])
    assert code == 1


# --------------------------------------------------------------------------- #
# Non-interactive mode
# --------------------------------------------------------------------------- #
def test_main_question_mode(monkeypatch, capsys):
    """--question answers once without the preflight checks or the REPL."""
    import src.chat as chat_mod

    answerer = FakeAnswerer()
    monkeypatch.setattr(chat_mod, "RagAnswerer", lambda **kwargs: answerer)
    monkeypatch.setattr(chat_mod, "preflight", lambda a: None)
    monkeypatch.setattr(chat_mod, "setup_logger", lambda output_dir: __import__("logging").getLogger("t"))

    code = main(["--question", "latest US GDP", "--show-sources"])
    assert code == 0
    output = capsys.readouterr().out
    assert "answer to latest US GDP" in output
    assert "USA|gdp_usd" in output


def test_main_exits_non_zero_when_preflight_fails(monkeypatch, capsys):
    import src.chat as chat_mod

    monkeypatch.setattr(chat_mod, "RagAnswerer", lambda **kwargs: FakeAnswerer())
    monkeypatch.setattr(chat_mod, "preflight", lambda a: "The LLM endpoint is not reachable.")
    monkeypatch.setattr(chat_mod, "setup_logger", lambda output_dir: __import__("logging").getLogger("t"))

    code = main([])
    assert code == 1
    assert "not reachable" in capsys.readouterr().err
