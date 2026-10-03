"""Unit tests for src.rag.RagAnswerer (no DB, no LLM, no network)."""

from typing import Any, Dict, List

import pytest

from src.llm import LLMError
from src.rag import NO_CONTEXT_ANSWER, RagAnswerer

DOC_USA_GDP = {
    "doc_key": "USA|gdp_usd",
    "doc_type": "entity_metric",
    "content": (
        "United States\nGross Domestic Product (Current USD)\n"
        "Metric code: gdp_usd\nFrequency: Annual Unit: Current US$\n"
        "Observations: 65, range 1960–2024\nLatest: 27,369,400,000,000 (2024)"
    ),
    "metadata": {"entity_code": "USA", "metric_code": "gdp_usd"},
    "similarity": 0.81,
}
DOC_METRIC = {
    "doc_key": "gdp_growth",
    "doc_type": "metric",
    "content": "GDP Growth Rate (Annual %)",
    "metadata": {"metric_code": "gdp_growth"},
    "similarity": 0.62,
}


class FakeVectorizer:
    """Stands in for src.vectorizer.Vectorizer (no DB handle)."""

    def __init__(self, docs=None, raise_exc=None):
        self.docs = [DOC_USA_GDP, DOC_METRIC] if docs is None else docs
        self.raise_exc = raise_exc
        self.calls: List[Any] = []

    def search_documents(self, query, k=10):
        self.calls.append((query, k))
        if self.raise_exc is not None:
            raise self.raise_exc
        return self.docs[:k]


class FakeLLM:
    """Records the messages it receives and returns a canned reply."""

    def __init__(self, content="US GDP was 27,369,400,000,000 in 2024 [USA|gdp_usd]."):
        self.model = "fake-model"
        self.content = content
        self.messages: List[List[Dict[str, Any]]] = []

    def chat(self, messages, tools=None):
        self.messages.append(messages)
        return {"role": "assistant", "content": self.content}


def _answerer(docs=None, llm=None, **kwargs):
    return RagAnswerer(
        vectorizer=FakeVectorizer(docs=docs),
        llm=llm or FakeLLM(),
        **kwargs,
    )


# --------------------------------------------------------------------------- #
# Retrieval
# --------------------------------------------------------------------------- #
def test_retrieve_returns_top_k_documents():
    vec = FakeVectorizer()
    answerer = RagAnswerer(vectorizer=vec, llm=FakeLLM(), k=1)
    docs = answerer.retrieve("latest US GDP")
    assert [d["doc_key"] for d in docs] == ["USA|gdp_usd"]
    assert vec.calls == [("latest US GDP", 1)]


def test_retrieve_uses_default_k_from_env(monkeypatch):
    monkeypatch.setenv("RAG_TOP_K", "3")
    vec = FakeVectorizer()
    answerer = RagAnswerer(vectorizer=vec, llm=FakeLLM())
    assert answerer.k == 3
    answerer.retrieve("gdp")
    assert vec.calls[0][1] == 3


def test_retrieve_explicit_k_overrides_default():
    answerer = _answerer(k=8)
    answerer.retrieve("gdp", k=1)
    assert answerer._vectorizer.calls[0][1] == 1


def test_retrieve_blank_question_short_circuits():
    vec = FakeVectorizer()
    answerer = RagAnswerer(vectorizer=vec, llm=FakeLLM())
    assert answerer.retrieve("   ") == []
    assert vec.calls == []


# --------------------------------------------------------------------------- #
# Prompt assembly
# --------------------------------------------------------------------------- #
def test_build_messages_structure():
    answerer = _answerer()
    messages = answerer.build_messages("What is the latest US GDP?", [DOC_USA_GDP, DOC_METRIC])
    assert [m["role"] for m in messages] == ["system", "user", "user"]
    assert messages[0]["content"] == answerer.system_prompt
    context = messages[1]["content"]
    assert "CONTEXT DOCUMENTS" in context
    assert "[1] doc_key=USA|gdp_usd doc_type=entity_metric" in context
    assert "Latest: 27,369,400,000,000 (2024)" in context
    assert "[2] doc_key=gdp_growth doc_type=metric" in context
    assert messages[-1]["content"] == "What is the latest US GDP?"


def test_system_prompt_states_grounding_rules():
    prompt = RagAnswerer(vectorizer=FakeVectorizer(), llm=FakeLLM()).system_prompt
    lowered = prompt.lower()
    assert "only from the context" in lowered
    assert "never invent" in lowered
    assert "doc_key" in prompt
    assert "not available" in lowered  # covers arbitrary years / aggregates


def test_custom_system_prompt_is_used(tmp_path):
    path = tmp_path / "prompt.txt"
    path.write_text("Custom prompt.", encoding="utf-8")
    answerer = RagAnswerer(vectorizer=FakeVectorizer(), llm=FakeLLM(), system_prompt="Custom prompt.")
    messages = answerer.build_messages("q", [DOC_USA_GDP])
    assert messages[0]["content"] == "Custom prompt."


def test_build_messages_includes_history_in_order():
    history = [
        {"role": "user", "content": "hi"},
        {"role": "assistant", "content": "hello"},
    ]
    messages = _answerer().build_messages("and now?", [DOC_USA_GDP], history)
    assert [m["role"] for m in messages] == ["system", "user", "user", "assistant", "user"]
    assert messages[2]["content"] == "hi"
    assert messages[3]["content"] == "hello"
    assert messages[4]["content"] == "and now?"


def test_build_messages_caps_history():
    history = []
    for i in range(40):
        history.append({"role": "user" if i % 2 == 0 else "assistant", "content": f"turn {i}"})
    messages = _answerer().build_messages("q", [DOC_USA_GDP], history)
    # 2 structural messages + capped history + the question.
    assert len(messages) == 2 + 20 + 1
    assert messages[-2]["content"] == "turn 39"


# --------------------------------------------------------------------------- #
# Answer
# --------------------------------------------------------------------------- #
def test_answer_returns_text_and_docs():
    llm = FakeLLM()
    answerer = _answerer(llm=llm)
    text, docs = answerer.answer("What is the latest US GDP?")
    assert "[USA|gdp_usd]" in text
    assert [d["doc_key"] for d in docs] == ["USA|gdp_usd", "gdp_growth"]
    assert len(llm.messages) == 1


def test_answer_prompt_contains_retrieved_docs():
    llm = FakeLLM()
    _answerer(llm=llm).answer("gdp?")
    sent = llm.messages[0]
    assert sent[0]["role"] == "system"
    assert "USA|gdp_usd" in sent[1]["content"]


def test_answer_appends_exchange_to_history():
    history: List[Dict[str, Any]] = []
    _answerer().answer("first question", history=history)
    assert [m["role"] for m in history] == ["user", "assistant"]
    assert history[0]["content"] == "first question"

    _answerer().answer("second question", history=history)
    assert [m["role"] for m in history] == ["user", "assistant", "user", "assistant"]
    assert history[2]["content"] == "second question"


def test_answer_without_history_leaves_it_untouched():
    history: List[Dict[str, Any]] = []
    _answerer().answer("q")
    _answerer().answer("q2", history=history)
    assert len(history) == 2


def test_empty_retrieval_does_not_call_llm():
    llm = FakeLLM()
    answerer = _answerer(docs=[], llm=llm)
    text, docs = answerer.answer("what is the weather in Paris?")
    assert text == NO_CONTEXT_ANSWER
    assert docs == []
    assert llm.messages == []


def test_blank_question_is_rejected_without_llm_call():
    llm = FakeLLM()
    text, docs = _answerer(llm=llm).answer("   ")
    assert (text, docs) == ("", [])
    assert llm.messages == []


def test_llm_error_propagates():
    class Boom:
        model = "boom"

        def chat(self, messages, tools=None):
            raise LLMError("endpoint down")

    with pytest.raises(LLMError):
        _answerer(llm=Boom()).answer("q")


def test_empty_llm_content_gets_fallback_text():
    answerer = _answerer(llm=FakeLLM(content=""))
    text, _ = answerer.answer("q")
    assert "empty answer" in text
