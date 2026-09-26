import logging
from typing import Dict, List

import psycopg2
import psycopg2.extras

from src.config import get_default_db_connection_string
from src.db import get_sqlalchemy_engine, init_db_schema
from src.entities import AGGREGATE_MEMBERSHIP

logger = logging.getLogger(__name__)


class DatabaseLoader:
    """Loads normalized dbe_* tables into PostgreSQL (Supabase) only."""

    def __init__(self, connection_string: str = ""):
        self.connection_string = connection_string or get_default_db_connection_string()
        self.engine = get_sqlalchemy_engine(self.connection_string)

    def verify_or_init_schema(self) -> bool:
        return init_db_schema(self.connection_string)

    # ---- Low-level psycopg2 upsert helper ----
    def _upsert(self, table: str, columns: List[str], rows: List[tuple], conflict_cols: List[str], update_cols: List[str], page_size: int = 200):
        if not rows:
            return 0
        conflict = ", ".join(conflict_cols)
        if update_cols:
            updates = ", ".join(f"{c} = EXCLUDED.{c}" for c in update_cols)
            conflict_action = f"DO UPDATE SET {updates}"
        else:
            conflict_action = "DO NOTHING"
        sql = f"""
            INSERT INTO {table} ({', '.join(columns)})
            VALUES %s
            ON CONFLICT ({conflict}) {conflict_action};
        """
        conn = self.engine.raw_connection()
        try:
            cur = conn.cursor()
            psycopg2.extras.execute_values(cur, sql, rows, page_size=page_size)
            conn.commit()
        finally:
            conn.close()
        return len(rows)

    # ---- Entities / aliases ----
    def seed_entities(self, entities_rows: List[Dict], aliases_rows: List[Dict]):
        # Entities first (no parent yet to avoid FK ordering issues).
        ent_cols = ["entity_code", "entity_name", "entity_type", "is_aggregate"]
        ent_rows = [(r["entity_code"], r["entity_name"], r["entity_type"], r["is_aggregate"]) for r in entities_rows]
        self._upsert("dbe_entities", ent_cols, ent_rows,
                     conflict_cols=["entity_code"],
                     update_cols=["entity_name", "entity_type", "is_aggregate"])

        # Set parent_entity_id via join on entity_code.
        parent_rows = [(r["parent_entity_code"], r["entity_code"]) for r in entities_rows if r.get("parent_entity_code")]
        if parent_rows:
            sql = """
                UPDATE dbe_entities AS t
                SET parent_entity_id = p.entity_id
                FROM dbe_entities AS p
                WHERE p.entity_code = %s AND t.entity_code = %s;
            """
            conn = self.engine.raw_connection()
            try:
                cur = conn.cursor()
                psycopg2.extras.execute_batch(cur, sql, parent_rows, page_size=200)
                conn.commit()
            finally:
                conn.close()

        # Aliases.
        alias_rows = [(r["entity_code"], r["provider"], r["provider_code"]) for r in aliases_rows]
        # Map entity_code -> entity_id for alias insert.
        with self.engine.raw_connection() as conn:
            cur = conn.cursor()
            cur.execute("SELECT entity_id, entity_code FROM dbe_entities")
            code_to_id = {code: eid for eid, code in cur.fetchall()}
        alias_final = [(code_to_id[c], p, pc) for (c, p, pc) in alias_rows if c in code_to_id]
        self._upsert("dbe_entity_aliases", ["entity_id", "provider", "provider_code"], alias_final,
                     conflict_cols=["provider", "provider_code"],
                     update_cols=["entity_id"])
        logger.info(f"Seeded {len(entities_rows)} entities and {len(alias_final)} aliases.")

    def seed_memberships(self):
        with self.engine.raw_connection() as conn:
            cur = conn.cursor()
            cur.execute("SELECT entity_id, entity_code FROM dbe_entities")
            code_to_id = {code: eid for eid, code in cur.fetchall()}

        rows = []
        for agg, members in AGGREGATE_MEMBERSHIP.items():
            agg_id = code_to_id.get(agg)
            if agg_id is None:
                continue
            for m in members:
                m_id = code_to_id.get(m)
                if m_id is None:
                    continue
                rows.append((agg_id, m_id, "1900-01-01"))
        if not rows:
            return
        self._upsert(
            "dbe_entity_membership",
            ["aggregate_entity_id", "member_entity_id", "start_date"],
            rows,
            conflict_cols=["aggregate_entity_id", "member_entity_id", "start_date"],
            update_cols=[],
        )
        logger.info(f"Seeded {len(rows)} aggregate memberships.")

    # ---- Metrics + synonyms ----
    def upsert_metrics(self, metrics_records: List[Dict]):
        code_to_id = {}
        if metrics_records:
            cols = ["metric_code", "metric_name", "category", "frequency", "base_unit", "unit_normalization", "description", "is_aggregatable", "aggregation_method"]
            rows = [(
                r["metric_code"], r["metric_name"], r["category"], r["frequency"],
                r["base_unit"], r["unit_normalization"], r["description"],
                r.get("is_aggregatable", False), r.get("aggregation_method", "mean"),
            ) for r in metrics_records]
            self._upsert("dbe_metrics", cols, rows,
                         conflict_cols=["metric_code"],
                         update_cols=["metric_name", "category", "frequency", "base_unit",
                                      "unit_normalization", "description", "is_aggregatable", "aggregation_method"])

        with self.engine.raw_connection() as conn:
            cur = conn.cursor()
            cur.execute("SELECT metric_id, metric_code FROM dbe_metrics")
            code_to_id = {code: mid for mid, code in cur.fetchall()}

        # Synonyms.
        syn_rows = []
        for r in metrics_records:
            mid = code_to_id.get(r["metric_code"])
            if mid is None:
                continue
            for syn in r.get("synonyms", []):
                syn_rows.append((mid, str(syn)))
        if syn_rows:
            self._upsert("dbe_metric_synonyms", ["metric_id", "synonym"], syn_rows,
                         conflict_cols=["metric_id", "synonym"], update_cols=[])
        logger.info(f"Upserted {len(metrics_records)} metrics and {len(syn_rows)} synonyms.")

    # ---- Sources ----
    def upsert_sources(self, metrics_records: List[Dict], metric_id_by_code: Dict[str, int]):
        rows = []
        for r in metrics_records:
            mid = metric_id_by_code.get(r["metric_code"])
            if mid is None:
                continue
            rows.append((
                r.get("provider", "WB"),
                r.get("dataset", "WDI"),
                r.get("indicator_code", ""),
                mid,
                r.get("dimension_key", "country"),
                r.get("entity_scope", "country"),
            ))
        self._upsert("dbe_sources", ["provider", "dataset", "indicator_code", "metric_id", "dimension_key", "entity_scope"], rows,
                     conflict_cols=["provider", "dataset", "indicator_code"],
                     update_cols=["metric_id", "dimension_key", "entity_scope"])
        logger.info(f"Upserted {len(rows)} sources.")

    def get_metric_and_source_ids(self) -> (Dict[str, int], Dict[str, int]):
        with self.engine.raw_connection() as conn:
            cur = conn.cursor()
            cur.execute("SELECT metric_id, metric_code FROM dbe_metrics")
            metric_ids = {code: mid for mid, code in cur.fetchall()}
            cur.execute("SELECT source_id, metric_code FROM dbe_sources s JOIN dbe_metrics m ON m.metric_id = s.metric_id")
            source_ids = {code: sid for sid, code in cur.fetchall()}
        return metric_ids, source_ids

    # ---- Observations ----
    def upsert_observations(self, metric_code: str, obs_records: List[Dict], metric_id: int, source_id: int) -> int:
        if not obs_records:
            return 0
        rows = []
        for r in obs_records:
            rows.append((
                metric_id,
                r["entity_code"],
                r.get("entity_name") or r["entity_code"],
                source_id,
                r["start_date"],
                r["end_date"],
                r["val"],
                r.get("raw_val"),
                r.get("unit", ""),
                r.get("period_str", ""),
                r.get("extracted_at"),
            ))
        # Fetch current entities.
        with self.engine.raw_connection() as conn:
            cur = conn.cursor()
            cur.execute("SELECT entity_id, entity_code FROM dbe_entities")
            code_to_id = {code: eid for eid, code in cur.fetchall()}

        # Auto-register any entity code not yet present (self-seeding universe).
        seen = set()
        missing = []
        for (_m, ecode, ename, *_rest) in rows:
            if ecode not in code_to_id and ecode not in seen:
                seen.add(ecode)
                missing.append((ecode, ename, "country", False))
        if missing:
            self._upsert(
                "dbe_entities",
                ["entity_code", "entity_name", "entity_type", "is_aggregate"],
                missing,
                conflict_cols=["entity_code"],
                update_cols=["entity_name", "entity_type", "is_aggregate"],
            )
            with self.engine.raw_connection() as conn:
                cur = conn.cursor()
                cur.execute("SELECT entity_id, entity_code FROM dbe_entities")
                code_to_id = {code: eid for eid, code in cur.fetchall()}
            logger.info(f"Auto-registered {len(missing)} new entities for '{metric_code}'.")

        final = []
        for (mid, ecode, _ename, sid, sd, ed, val, rval, unit, pstr, ext) in rows:
            eid = code_to_id.get(ecode)
            if eid is None:
                continue
            final.append((mid, eid, sid, sd, ed, val, rval, unit, pstr, ext))

        cols = ["metric_id", "entity_id", "source_id", "start_date", "end_date", "val", "raw_val", "unit", "period_str", "extracted_at"]
        self._upsert("dbe_observations", cols, final,
                     conflict_cols=["metric_id", "entity_id", "start_date", "end_date"],
                     update_cols=["val", "raw_val", "unit", "period_str", "extracted_at"],
                     page_size=500)
        logger.info(f"Upserted {len(final)} observations for '{metric_code}'.")
        return len(final)

    def log_pipeline_run(self, dag_id: str, task_id: str, metrics_extracted: int,
                         observations_upserted: int, observations_skipped: int,
                         providers: List[str], source_ids: List[int], status: str, execution_seconds: float):
        sql = """
            INSERT INTO dbe_pipeline_runs
            (dag_id, task_id, metrics_extracted, observations_upserted, observations_skipped, providers, source_ids, status, execution_seconds)
            VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s)
        """
        try:
            with self.engine.raw_connection() as conn:
                cur = conn.cursor()
                cur.execute(sql, (dag_id, task_id, metrics_extracted, observations_upserted,
                                  observations_skipped, providers, source_ids, status, execution_seconds))
                conn.commit()
            logger.info("Pipeline run audit logged into dbe_pipeline_runs.")
        except Exception as e:
            logger.error(f"Failed to log pipeline run: {e}")
