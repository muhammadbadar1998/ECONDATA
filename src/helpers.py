"""
Period literal parsing and unit normalization helpers.

Period formats supported (case-insensitive separators):
  - "YYYY"                       -> Annual
  - "YYYY-Qn" / "YYYYQn" / "Qn-YYYY"  -> Quarterly
  - "YYYY-MM" / "YYYY/MM"        -> Monthly
  - "YYYY-MM-DD"                 -> Daily (Monthly frequency, exact range)

Each returns (start_date, end_date, frequency) as a typed range so that string
sorting no longer yields wrong chronological order.
"""

import logging
import re
from datetime import date, timedelta
from typing import Optional, Tuple

logger = logging.getLogger(__name__)


def _month_end(year: int, month: int) -> date:
    if month == 12:
        return date(year, 12, 31)
    nxt = date(year, month + 1, 1)
    return nxt - timedelta(days=1)


def _quarter_end(year: int, quarter: int) -> date:
    month = quarter * 3
    return _month_end(year, month)


def parse_period(period_str: str) -> Tuple[Optional[date], Optional[date], Optional[str]]:
    """
    Parse a DBnomics period literal into (start_date, end_date, frequency).
    Returns (None, None, None) if it cannot be parsed.
    """
    if period_str is None:
        return (None, None, None)
    s = str(period_str).strip()
    if not s:
        return (None, None, None)

    # YYYY-MM-DD
    m = re.fullmatch(r"(\d{4})[-/](\d{1,2})[-/](\d{1,2})", s)
    if m:
        y, mo, d = int(m.group(1)), int(m.group(2)), int(m.group(3))
        try:
            start = date(y, mo, d)
        except ValueError:
            return (None, None, None)
        return (start, start, "Monthly")

    # YYYY-MM or YYYY/MM
    m = re.fullmatch(r"(\d{4})[-/](\d{1,2})", s)
    if m:
        y, mo = int(m.group(1)), int(m.group(2))
        if not (1 <= mo <= 12):
            return (None, None, None)
        start = date(y, mo, 1)
        return (start, _month_end(y, mo), "Monthly")

    # YYYY-Qn or YYYYQn or YYYY-QtrN
    m = re.fullmatch(r"(\d{4})[-/ ]?Q(?:tr)?(\d{1,2})", s, re.IGNORECASE)
    if m:
        y, q = int(m.group(1)), int(m.group(2))
        if not (1 <= q <= 4):
            return (None, None, None)
        start = date(y, (q - 1) * 3 + 1, 1)
        return (start, _quarter_end(y, q), "Quarterly")

    m = re.fullmatch(r"Q(?:tr)?(\d{1,2})[-/ ]?(\d{4})", s, re.IGNORECASE)
    if m:
        q, y = int(m.group(1)), int(m.group(2))
        if not (1 <= q <= 4):
            return (None, None, None)
        start = date(y, (q - 1) * 3 + 1, 1)
        return (start, _quarter_end(y, q), "Quarterly")

    # YYYY
    m = re.fullmatch(r"(\d{4})", s)
    if m:
        y = int(m.group(1))
        if y < 1 or y > 9999:
            return (None, None, None)
        return (date(y, 1, 1), date(y, 12, 31), "Annual")

    logger.warning(f"Unrecognized period literal '{period_str}'; skipping row.")
    return (None, None, None)


def normalize_unit(raw_val: float, unit: str, unit_normalization: str) -> float:
    """
    Apply a unit normalization expression to convert a source value to base_unit.

    unit_normalization is a safe arithmetic expression over the symbol `x`
    (the raw value). Empty string means identity (no conversion).

    Raises ValueError if the expression is unsafe or fails, so the caller can
    skip the row rather than silently mis-normalize.
    """
    if not unit_normalization:
        return raw_val

    allowed = set("x0123456789.+-*/() ")
    if not all(ch in allowed for ch in unit_normalization):
        raise ValueError(f"Unsafe unit_normalization expression: {unit_normalization!r}")

    safe_globals = {"__builtins__": {}}
    try:
        return float(eval(unit_normalization, safe_globals, {"x": raw_val}))
    except Exception as e:
        raise ValueError(
            f"Failed to apply unit_normalization {unit_normalization!r} to value {raw_val}: {e}"
        ) from None
