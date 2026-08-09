"""
Entity resolution service: maps a provider's raw dimension code (plus optional
series label) to a canonical entity_id in dbe_entities.

Resolution order:
  1. Exact lookup in dbe_entity_aliases (provider, provider_code).
  2. Curated alias map in src/entities.py.
  3. For WB/OECD/IMF country codes that look like ISO-3 alpha-3, accept as canonical.
  4. Otherwise register a new entity deterministically (never fabricate a name).

This replaces the old heuristic that derived country_name from
`series_name.split(" - ")[-1]`, which created bogus entities like "Annual".
"""

import logging
from typing import Dict, List, Optional, Tuple

from src.entities import ENTITIES, ENTITY_ALIASES

logger = logging.getLogger(__name__)


class EntityResolver:
    def __init__(self, engine=None):
        # engine: optional SQLAlchemy engine for live alias table lookup.
        self.engine = engine
        # Cache of (provider, provider_code) -> canonical entity_code
        self._alias_cache: Dict[Tuple[str, str], str] = {}
        self._canonical_codes = {code for (code, *_rest) in ENTITIES}
        for (provider, pcode), ccode in ENTITY_ALIASES.items():
            self._alias_cache[(provider, pcode)] = ccode
        self._entity_names = {code: name for (code, name, *_r) in ENTITIES}

    def _lookup_db_alias(self, provider: str, provider_code: str) -> Optional[str]:
        if self.engine is None:
            return None
        from sqlalchemy import text
        try:
            with self.engine.connect() as conn:
                row = conn.execute(
                    text(
                        "SELECT e.entity_code FROM dbe_entity_aliases a "
                        "JOIN dbe_entities e ON e.entity_id = a.entity_id "
                        "WHERE a.provider = :p AND a.provider_code = :c"
                    ),
                    {"p": provider, "c": provider_code},
                ).fetchone()
                if row:
                    return row[0]
        except Exception as e:  # pragma: no cover - live DB optional
            logger.warning(f"Alias DB lookup failed for {(provider, provider_code)}: {e}")
        return None

    def resolve_code(self, provider: str, provider_code: str) -> Optional[str]:
        """Return canonical entity_code for a (provider, provider_code), or None."""
        key = (provider, provider_code)
        if key in self._alias_cache:
            return self._alias_cache[key]

        db_code = self._lookup_db_alias(provider, provider_code)
        if db_code:
            self._alias_cache[key] = db_code
            return db_code

        if provider in ("WB", "OECD", "IMF") and self._looks_like_iso3(provider_code):
            self._alias_cache[key] = provider_code.upper()
            return provider_code.upper()

        return None

    @staticmethod
    def _looks_like_iso3(code: str) -> bool:
        return bool(code) and len(code) == 3 and code.isalpha() and code.isupper()

    def is_known_entity(self, entity_code: str) -> bool:
        return entity_code in self._canonical_codes

    def entity_name(self, entity_code: str) -> str:
        return self._entity_names.get(entity_code, entity_code)

    def register_unknown(self, provider: str, provider_code: str, label: Optional[str] = None) -> str:  # noqa: E501
        """
        Deterministically register an unknown provider code.

        Returns the canonical entity_code. The entity name is the provider label
        if supplied and plausible, otherwise "<PROVIDER>:<CODE>". We never
        fabricate a human name from period/series strings.
        """
        code = provider_code.upper()
        if code in ("ANNUAL", "QUARTERLY", "MONTHLY", "YEAR", "NA", "N/A"):
            logger.warning(f"Refusing to register non-entity code '{code}' from {provider}.")
            return code

        name = label.strip() if label else f"{provider}:{code}"
        self._canonical_codes.add(code)
        self._entity_names[code] = name
        self._alias_cache[(provider, provider_code)] = code
        logger.info(
            f"Resolved unknown {provider} code '{provider_code}' -> entity '{code}' (name='{name}')."
        )
        return code

    def seed_rows(self) -> Tuple[List[dict], List[dict]]:
        """
        Returns (entities_rows, aliases_rows) for seeding the registry tables.
        """
        entities_rows = []
        for (code, name, etype, is_agg, parent) in ENTITIES:
            entities_rows.append({
                "entity_code": code,
                "entity_name": name,
                "entity_type": etype,
                "is_aggregate": is_agg,
                "parent_entity_code": parent,
            })

        aliases_rows = []
        for (provider, pcode), ccode in ENTITY_ALIASES.items():
            aliases_rows.append({
                "provider": provider,
                "provider_code": pcode,
                "entity_code": ccode,
            })
        return entities_rows, aliases_rows
