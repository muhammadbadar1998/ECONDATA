"""Tests for lazy Supabase connection-string resolution in src/config.

These verify the fix for the import-time crash: simply importing ``src.config``
must never raise, even when no database credentials are present.
"""

import os

import pytest

_DB_ENV_VARS = (
    "ECONDATA_DATABASE_URL", "DATABASE_URL", "SHAREDPOOLER", "sharedpooler",
)


def _clear_db_env(monkeypatch):
    """Remove every DB env var without reloading (reload re-runs load_dotenv)."""
    for var in list(os.environ):
        if var.startswith("SUPABASE_") or var in _DB_ENV_VARS:
            monkeypatch.delenv(var, raising=False)


def test_config_import_does_not_raise(monkeypatch):
    """Importing src.config with no DB env vars must not raise."""
    _clear_db_env(monkeypatch)
    import src.config  # noqa: F401


def test_get_default_db_connection_string_raises_without_creds(monkeypatch):
    """With no DB env vars, resolution raises a clear ValueError (not at import)."""
    _clear_db_env(monkeypatch)
    from src.config import get_default_db_connection_string

    with pytest.raises(ValueError, match="Supabase connection credentials"):
        get_default_db_connection_string()


def test_connection_string_from_env_url(monkeypatch):
    """A full URL env var is preferred and normalized to the psycopg2 scheme."""
    _clear_db_env(monkeypatch)
    monkeypatch.setenv("ECONDATA_DATABASE_URL", "postgres://user:pw@host:5432/db")
    from src.config import get_supabase_connection_string

    cs = get_supabase_connection_string()
    assert cs.startswith("postgresql+psycopg2://")


def test_connection_string_from_discrete_vars(monkeypatch):
    """Discrete SUPABASE_* vars produce a valid connection string."""
    _clear_db_env(monkeypatch)
    monkeypatch.setenv("SUPABASE_HOST", "db.example.supabase.co")
    monkeypatch.setenv("SUPABASE_PASSWORD", "secret123")
    from src.config import get_supabase_connection_string

    cs = get_supabase_connection_string()
    assert cs == "postgresql+psycopg2://postgres:secret123@db.example.supabase.co:5432/postgres"

