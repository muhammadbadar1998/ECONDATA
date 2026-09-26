"""
Semantic catalog layer: builds embedding documents from the normalized ``dbe_*``
registry and observation data, embeds them via :class:`src.embeddings.OllamaEmbedder`,
and persists the result into ``dbe_embeddings`` (a pgvector table).

Document types (stored in ``doc_type``):
  - ``metric``         : one document per metric (name + synonyms + description).
  - ``entity``         : one document per entity (name + type + parent).
  - ``entity_metric``  : one document per (entity, metric) coverage summary
                         ("United States — GDP (Current USD), annual, 156 obs, 1960–2024").

``--vectorize`` embeds only documents whose SHA-256 content hash changed (plus
orphans). ``--reembed`` recomputes every vector.

Embeddings use Ollama-served ``nomic-embed-text`` (768-dim). The HNSW index
(``idx_dbe_embeddings_hnsw``) accelerates cosine nearest-neighbour search.
"""

import hashlib
import json
import logging
from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional, Sequence, Tuple

import psycopg2
import psycopg2.extras

from src.config import get_default_db_connection_string
from src.db import get_sqlalchemy_engine
from src.embeddings import OllamaEmbedder

logger = logging.getLogger(__name__)
EMBEDDING_DIM = 768


def _vector_to_literal(vec: Sequence[float]) -> str:
    """Format a vector as a pgvector-bracket literal: ``[1.0,2.0,3.0]``.

    pgvector parses this via the ``::vector`` cast, so we pass the already-formatted
    string rather than a Python list (which psycopg2 would adapt to PG array syntax
    that pgvector cannot directly cast).
    """
    return "[" + ",".join(_format_scalar(v) for v in vec) + "]"


def _format_scalar(v: float) -> str:
    """Render a float for pgvector's bracket input; non-finite -> 0."""
    if isinstance(v, bool):
        return "1" if v else "0"
    f = float(v)
    if f != f or f in (float("inf"), float("-inf")):
        return "0"
    return repr(f)


@dataclass
class Document:
    doc_key: str
    doc_type: str
    content: str
    metadata: Dict[str, Any] = field(default_factory=dict)

    @property
    def doc_hash(self) -> str:
        return hashlib.sha256(self.content.encode("utf-8")).hexdigest()


class Vectorizer:
    """Builds, embeds, and persists catalog documents into ``dbe_embeddings``."""

    def __init__(
        self,
        connection_string: str = "",
        embedder: Optional[OllamaEmbedder] = None,
        force_reembed: bool = False,
    ):
        self.connection_string = connection_string or get_default_db_connection_string()
        self.engine = get_sqlalchemy_engine(self.connection_string)
        self.embedder = embedder or OllamaEmbedder()
        self.force_reembed = force_reembed

    # ------------------------------------------------------------------ #
    # Document builders (pure, testable)
    # ------------------------------------------------------------------ #
    @staticmethod
    def build_metric_documents(catalog: Dict[str, Dict[str, Any]]) -> List[Document]:
        """One document per metric: name + synonyms + description + metadata."""
        docs: List[Document] = []
        for code, info in catalog.items():
            synonyms = info.get("synonyms", []) or []
            parts = [
                info.get("metric_name", code),
                "\n".join(synonyms) if synonyms else None,
                info.get("description", ""),
            ]
            content = "\n".join(p for p in parts if p).strip()
            docs.append(
                Document(
                    doc_key=code,
                    doc_type="metric",
                    content=content,
                    metadata={
                        "metric_code": code,
                        "category": info.get("category", "historical"),
                        "frequency": info.get("frequency", "Annual"),
                        "base_unit": info.get("base_unit", info.get("unit", "")),
                        "provider": info.get("provider", ""),
                        "dataset": info.get("dataset", ""),
                        "indicator_code": info.get("indicator_code", ""),
                    },
                )
            )
        return docs

    @staticmethod
    def build_entity_documents(entities: Sequence[Tuple]) -> List[Document]:
        """One document per entity: name + type + parent aggregate.

        ``entities`` is a sequence of ``(entity_code, entity_name, entity_type,
        is_aggregate, parent_entity_code)`` tuples (as produced by
        ``src.entities.ENTITIES``).
        """
        docs: List[Document] = []
        for code, name, etype, is_agg, parent in entities:
            parts = [name, f"Type: {etype}", f"Aggregate: {is_agg}"]
            if parent:
                parts.append(f"Parent: {parent}")
            content = "\n".join(parts).strip()
            docs.append(
                Document(
                    doc_key=code,
                    doc_type="entity",
                    content=content,
                    metadata={"entity_code": code, "entity_type": etype, "is_aggregate": is_agg},
                )
            )
        return docs

    @staticmethod
    def build_entity_metric_documents(
        coverage_rows: Sequence[Dict[str, Any]],
    ) -> List[Document]:
        """One document per (entity, metric) coverage summary.

        Each ``coverage_rows`` entry must have keys:
        ``entity_code``, ``entity_name``, ``metric_code``, ``metric_name``,
        ``frequency``, ``base_unit``, ``obs_count``, ``min_year``, ``max_year``.
        """
        docs: List[Document] = []
        for r in coverage_rows:
            content = (
                f"{r['entity_name']}\n"
                f"{r['metric_name']}\n"
                f"Metric code: {r['metric_code']}\n"
                f"Frequency: {r['frequency']} Unit: {r['base_unit']}\n"
                f"Observations: {r['obs_count']}, range {r['min_year']}–{r['max_year']}"
            )
            docs.append(
                Document(
                    doc_key=f"{r['entity_code']}|{r['metric_code']}",
                    doc_type="entity_metric",
                    content=content.strip(),
                    metadata={
                        "entity_code": r["entity_code"],
                        "entity_name": r["entity_name"],
                        "metric_code": r["metric_code"],
                        "metric_name": r["metric_name"],
                        "frequency": r["frequency"],
                        "base_unit": r["base_unit"],
                        "obs_count": r["obs_count"],
                        "min_year": r["min_year"],
                        "max_year": r["max_year"],
                    },
                )
            )
        return docs

    # ------------------------------------------------------------------ #
    # DB helpers
    # ------------------------------------------------------------------ #
    def _fetch_catalog(self) -> Dict[str, Dict[str, Any]]:
        """Load the metrics catalog from ``config/metrics.json`` via config loader."""
        from src.config import load_pipeline_config

        catalog = load_pipeline_config()["metrics"]
        return catalog

    def _fetch_entity_metric_coverage(self) -> List[Dict[str, Any]]:
        """Query the DB for per-(entity, metric) observation coverage."""
        sql = """
            SELECT
                e.entity_code,
                e.entity_name,
                m.metric_code,
                m.metric_name,
                m.frequency,
                m.base_unit,
                COUNT(o.val) AS obs_count,
                MIN(EXTRACT(YEAR FROM o.start_date)) AS min_year,
                MAX(EXTRACT(YEAR FROM o.start_date)) AS max_year
            FROM dbe_observations o
            JOIN dbe_metrics m        ON m.metric_id = o.metric_id
            JOIN dbe_entities e       ON e.entity_id = o.entity_id
            WHERE e.is_active = TRUE
            GROUP BY e.entity_code, e.entity_name,
                     m.metric_code, m.metric_name, m.frequency, m.base_unit
            HAVING COUNT(o.val) > 0
            ORDER BY e.entity_code, m.metric_code
        """
        rows: List[Dict[str, Any]] = []
        with self.engine.raw_connection() as conn:
            with conn.cursor() as cur:
                cur.execute(sql)
                cols = [d[0] for d in cur.description]
                for row in cur.fetchall():
                    rows.append(dict(zip(cols, row, strict=True)))
        return rows

    # ------------------------------------------------------------------ #
    # Public API
    # ------------------------------------------------------------------ #
    def build_all_documents(self) -> List[Document]:
        """Build the complete document set: metrics + entities + coverage."""
        from src.entities import ENTITIES

        catalog = self._fetch_catalog()
        docs: List[Document] = []
        docs.extend(self.build_metric_documents(catalog))
        docs.extend(self.build_entity_documents(ENTITIES))

        try:
            coverage = self._fetch_entity_metric_coverage()
            docs.extend(self.build_entity_metric_documents(coverage))
        except psycopg2.Error as exc:
            logger.warning("Skipping entity_metric documents: %s", exc)
        return docs

    def vectorize_all(self) -> Dict[str, int]:
        """Embed changed/orphan docs and persist into ``dbe_embeddings``.

        Returns a stats dict: ``embedded``, ``updated``, ``orphans_removed``,
        ``errors``.
        """
        if not self.embedder.verify_connectivity():
            raise RuntimeError(
                "Ollama is not reachable at the configured OLLAMA_HOST. "
                "Run `ollama serve` and `ollama pull nomic-embed-text`."
            )

        docs = self.build_all_documents()
        if not docs:
            return {"embedded": 0, "updated": 0, "orphans_removed": 0, "errors": 0}

        # Load existing (doc_key, doc_type) -> doc_hash.
        existing: Dict[Tuple[str, str], str] = {}
        with self.engine.raw_connection() as conn:
            with conn.cursor() as cur:
                cur.execute(
                    "SELECT doc_key, doc_type, doc_hash FROM dbe_embeddings"
                )
                for key, dtype, h in cur.fetchall():
                    existing[(key, dtype)] = h

        new_set: Dict[Tuple[str, str], str] = {}
        to_embed: List[Document] = []
        for doc in docs:
            new_set[(doc.doc_key, doc.doc_type)] = doc.doc_hash
            if self.force_reembed or existing.get((doc.doc_key, doc.doc_type)) != doc.doc_hash:
                to_embed.append(doc)

        embedded = 0
        errors = 0
        if to_embed:
            texts = [d.content for d in to_embed]
            try:
                vectors = self.embedder.embed(texts)
            except Exception as exc:
                logger.error("Embedding failed: %s", exc)
                errors = len(to_embed)
                return {
                    "embedded": 0,
                    "updated": 0,
                    "orphans_removed": 0,
                    "errors": errors,
                }
            self._upsert_vectors(to_embed, vectors)
            embedded = len(to_embed)

        # Orphan cleanup: rows in DB not produced by build_all_documents.
        orphans = [
            k for k in existing if k not in new_set
        ]
        if orphans:
            self._delete_orphans(orphans)

        return {
            "embedded": embedded,
            "updated": embedded,
            "orphans_removed": len(orphans),
            "errors": errors,
        }

    def _upsert_vectors(self, docs: List[Document], vectors: List[List[float]]) -> None:
        sql = """
            INSERT INTO dbe_embeddings
                (doc_key, doc_type, content, doc_hash, metadata, embedding)
            VALUES %s
            ON CONFLICT (doc_key, doc_type)
            DO UPDATE SET
                content = EXCLUDED.content,
                doc_hash = EXCLUDED.doc_hash,
                metadata = EXCLUDED.metadata,
                embedding = EXCLUDED.embedding,
                updated_at = CURRENT_TIMESTAMP
        """
        rows = []
        for doc, vec in zip(docs, vectors, strict=True):
            rows.append((
                doc.doc_key,
                doc.doc_type,
                doc.content,
                doc.doc_hash,
                json.dumps(doc.metadata, sort_keys=True, ensure_ascii=False),
                _vector_to_literal(vec),
            ))
        with self.engine.raw_connection() as conn:
            with conn.cursor() as cur:
                psycopg2.extras.execute_values(
                    cur,
                    sql,
                    rows,
                    template="(%s, %s, %s, %s, %s, %s::vector)",
                    page_size=100,
                )
            conn.commit()
        logger.info("Upserted %d embedding vectors.", len(rows))

    def _delete_orphans(self, keys: List[Tuple[str, str]]) -> None:
        if not keys:
            return
        with self.engine.raw_connection() as conn:
            with conn.cursor() as cur:
                # Stage the orphan keys in a temp table, then delete by join.
                cur.execute("DROP TABLE IF EXISTS _vec_orphans;")
                cur.execute(
                    "CREATE TEMP TABLE _vec_orphans (doc_key text, doc_type text);"
                )
                psycopg2.extras.execute_values(
                    cur,
                    "INSERT INTO _vec_orphans (doc_key, doc_type) VALUES %s",
                    keys,
                    page_size=500,
                )
                cur.execute(
                    "DELETE FROM dbe_embeddings e USING _vec_orphans o "
                    "WHERE e.doc_key = o.doc_key AND e.doc_type = o.doc_type;"
                )
                cur.execute("DROP TABLE _vec_orphans;")
            conn.commit()
        logger.info("Removed %d orphan embedding rows.", len(keys))

    # ------------------------------------------------------------------ #
    # Search
    # ------------------------------------------------------------------ #
    def search_documents(self, query: str, k: int = 10) -> List[Dict[str, Any]]:
        """Semantic search over ``dbe_embeddings`` by cosine similarity."""
        query_vec = self.embedder.embed_query(query)
        sql = """
            SELECT
                doc_key,
                doc_type,
                content,
                metadata,
                1 - (embedding <=> %s::vector) AS similarity
            FROM dbe_embeddings
            ORDER BY embedding <=> %s::vector
            LIMIT %s
        """
        results: List[Dict[str, Any]] = []
        query_literal = _vector_to_literal(query_vec)
        with self.engine.raw_connection() as conn:
            with conn.cursor() as cur:
                cur.execute(sql, (query_literal, query_literal, k))
                for doc_key, doc_type, content, metadata, similarity in cur.fetchall():
                    results.append(
                        {
                            "doc_key": doc_key,
                            "doc_type": doc_type,
                            "content": content,
                            "metadata": metadata,
                            "similarity": float(similarity),
                        }
                    )
        return results
