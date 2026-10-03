"""Regression tests for the shared text-file logging configuration.

These guard the fix for the propagation bug where only the ``EconDataPipeline``
named logger reached ``output/pipeline.log`` while every ``logging.getLogger(__name__)``
module record was silently dropped.
"""

import logging
from datetime import datetime, timezone
from pathlib import Path

import pytest

from src.logging_config import setup_logging


@pytest.fixture(autouse=True)
def _reset_root_logging():
    """Close handlers so they do not leak into other tests' tmp dirs."""
    yield
    root = logging.getLogger()
    for handler in list(root.handlers):
        root.removeHandler(handler)
        handler.close()


def _dated_log(tmp_path: Path, basename: str = "pipeline") -> Path:
    date_str = datetime.now(timezone.utc).strftime("%Y-%m-%d")
    return tmp_path / "logs" / f"{basename}_{date_str}.log"


def test_module_logger_records_reach_both_files(tmp_path: Path) -> None:
    setup_logging(str(tmp_path), basename="pipeline", console=False)

    # Emit from a module logger (not the entry-point logger) — the case that was
    # previously lost because its records propagated to an unconfigured root.
    logging.getLogger("src.extractor").info("PROBE-fetch-12345")

    dated = _dated_log(tmp_path)
    latest = tmp_path / "pipeline.log"
    assert dated.exists(), f"missing dated log: {dated}"
    assert latest.exists(), f"missing latest log: {latest}"

    dated_text = dated.read_text(encoding="utf-8")
    latest_text = latest.read_text(encoding="utf-8")
    for text in (dated_text, latest_text):
        assert "PROBE-fetch-12345" in text
        assert "src.extractor" in text  # logger name is preserved in the format


def test_setup_logging_is_idempotent(tmp_path: Path) -> None:
    setup_logging(str(tmp_path), basename="pipeline", console=False)
    # A second configuration must replace the handlers, not stack them.
    setup_logging(str(tmp_path), basename="pipeline", console=False)

    logging.getLogger("src.db_loader").info("PROBE-dedupe-67890")
    latest_text = (tmp_path / "pipeline.log").read_text(encoding="utf-8")
    assert latest_text.count("PROBE-dedupe-67890") == 1


def test_connection_secret_never_written_to_log(tmp_path: Path, monkeypatch) -> None:
    secret = "super-secret-password"
    monkeypatch.setenv("SUPABASE_HOST", "db.example.com")
    monkeypatch.setenv("SUPABASE_PASSWORD", secret)
    for var in ("ECONDATA_DATABASE_URL", "DATABASE_URL", "SHAREDPOOLER", "sharedpooler"):
        monkeypatch.delenv(var, raising=False)

    setup_logging(str(tmp_path), basename="pipeline", console=False)

    from src.config import get_supabase_connection_string

    url = get_supabase_connection_string()
    assert secret in url  # sanity: config does resolve the credential

    latest_text = (tmp_path / "pipeline.log").read_text(encoding="utf-8")
    assert secret not in latest_text
