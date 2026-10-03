"""Central logging configuration: stdout plus date-stamped text files.

Handlers are attached to the **root** logger on purpose. Every module logs via
``logging.getLogger(__name__)`` (e.g. ``src.extractor``, ``src.db_loader``), and
those records propagate to the root logger. Attaching handlers to a single named
logger (the previous approach) captured only that logger's records and silently
dropped everything else, so the pipeline's DB-load and embedding steps never
reached ``output/pipeline.log``.

All log lines use the same format and land in two files:
  - ``<output_dir>/logs/<basename>_<UTC-date>.log`` (per-day)
  - ``<output_dir>/<basename>.log``                 (rolling latest)
"""

import logging
import os
import sys
from datetime import datetime, timezone
from logging.handlers import RotatingFileHandler
from typing import Optional, TextIO

from src.config import DEFAULT_OUTPUT_DIR, LOGS_DIR_NAME

LOG_FORMAT = "%(asctime)s [%(levelname)s] %(name)s: %(message)s"

# Bound the rolling "latest" file so a daily scheduled run cannot grow it without
# limit; the date-stamped file rotates naturally by day.
LOG_MAX_BYTES = 5 * 1024 * 1024
LOG_BACKUP_COUNT = 5

# Third-party libraries are chatty at INFO/DEBUG; keep only warnings and above.
_QUIET_LOGGERS = ("urllib3", "requests", "sqlalchemy.engine")


def build_log_paths(output_dir: str, basename: str) -> tuple[str, str]:
    """Return ``(dated_log_path, latest_log_path)``, creating the logs dir."""
    logs_dir = os.path.join(output_dir, LOGS_DIR_NAME)
    os.makedirs(logs_dir, exist_ok=True)
    date_str = datetime.now(timezone.utc).strftime("%Y-%m-%d")
    dated = os.path.join(logs_dir, f"{basename}_{date_str}.log")
    latest = os.path.join(output_dir, f"{basename}.log")
    return dated, latest


def _resolve_level(level: Optional[int]) -> int:
    """Use ``level`` when given, else the ``LOG_LEVEL`` env var (default INFO)."""
    if level is not None:
        return level
    name = os.getenv("LOG_LEVEL", "INFO").upper()
    return getattr(logging, name, logging.INFO)


def setup_logging(
    output_dir: str = DEFAULT_OUTPUT_DIR,
    basename: str = "pipeline",
    level: Optional[int] = None,
    console: bool = True,
    console_stream: Optional[TextIO] = None,
) -> logging.Logger:
    """Configure root logging to stdout (optional) and two text files.

    Idempotent: the root logger's existing handlers are closed and replaced on
    every call, so repeated invocations within one process never duplicate
    lines. Returns the root logger; callers may keep using their own
    ``getLogger(__name__)`` and their records will be captured too.

    ``console_stream`` defaults to ``sys.stdout``; pass ``sys.stderr`` to keep
    stdout clean (as :mod:`src.chat` does for piped answers).
    """
    root = logging.getLogger()
    root.setLevel(_resolve_level(level))

    for handler in list(root.handlers):
        root.removeHandler(handler)
        handler.close()

    formatter = logging.Formatter(LOG_FORMAT)

    if console:
        stream_handler = logging.StreamHandler(console_stream or sys.stdout)
        stream_handler.setFormatter(formatter)
        root.addHandler(stream_handler)

    dated_path, latest_path = build_log_paths(output_dir, basename)

    # Per-day file: rotates naturally by date.
    dated_handler = logging.FileHandler(dated_path, encoding="utf-8")
    dated_handler.setFormatter(formatter)
    root.addHandler(dated_handler)

    # Rolling "latest" file: capped in size so it cannot grow without bound.
    latest_handler = RotatingFileHandler(
        latest_path,
        maxBytes=LOG_MAX_BYTES,
        backupCount=LOG_BACKUP_COUNT,
        encoding="utf-8",
    )
    latest_handler.setFormatter(formatter)
    root.addHandler(latest_handler)

    for name in _QUIET_LOGGERS:
        logging.getLogger(name).setLevel(logging.WARNING)

    root.info(
        "Logging initialized (%s, level=%s). Files: %s and %s",
        basename,
        logging.getLevelName(root.level),
        dated_path,
        latest_path,
    )
    return root
