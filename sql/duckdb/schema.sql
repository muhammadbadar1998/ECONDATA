-- DuckDB gold schema for EconData (dbe_* tables + views).
--
-- Ported from sql/sqlite/schema.sql. Differences required by DuckDB:
--   * INTEGER PRIMARY KEY is NOT auto-increment, so every surrogate id is backed by
--     a sequence and defaulted with nextval(...).
--   * Unqualified column references after a join are a hard error, so every column
--     in the views below is fully qualified.
--   * Referential integrity is NOT declared as FOREIGN KEY constraints: DuckDB's
--     ON CONFLICT rewrite deletes-and-inserts, which trips FK checks on an idempotent
--     re-seed. Ids stay stable through the natural keys and UNIQUE constraints instead.
--   * dbe_embeddings / dbe_embeddings_vec deliberately do NOT exist here; the vector
--     catalog stays in SQLite alongside the sqlite-vec extension.

BEGIN TRANSACTION;

-- dbe_entities: canonical entity registry
-- parent_entity_id is intentionally not a FOREIGN KEY: DuckDB's ON CONFLICT
-- implementation rewrites rows, which trips self-referential FK checks on re-seed.
-- The relationship is still expressible and validated when seeded.
CREATE SEQUENCE IF NOT EXISTS seq_dbe_entities START 1;
CREATE TABLE IF NOT EXISTS dbe_entities (
    entity_id INTEGER DEFAULT nextval('seq_dbe_entities') PRIMARY KEY,
    entity_code VARCHAR NOT NULL UNIQUE,
    entity_name VARCHAR NOT NULL,
    entity_type VARCHAR NOT NULL DEFAULT 'country',
    is_aggregate INTEGER NOT NULL DEFAULT 0,
    parent_entity_id INTEGER,
    is_latest INTEGER NOT NULL DEFAULT 1,
    is_active INTEGER NOT NULL DEFAULT 1,
    created_at VARCHAR NOT NULL DEFAULT strftime(now(), '%Y-%m-%dT%H:%M:%SZ'),
    updated_at VARCHAR NOT NULL DEFAULT strftime(now(), '%Y-%m-%dT%H:%M:%SZ')
);

-- dbe_entity_aliases: provider code -> canonical entity mapping
CREATE SEQUENCE IF NOT EXISTS seq_dbe_entity_aliases START 1;
CREATE TABLE IF NOT EXISTS dbe_entity_aliases (
    alias_id INTEGER DEFAULT nextval('seq_dbe_entity_aliases') PRIMARY KEY,
    entity_id INTEGER NOT NULL,
    provider VARCHAR NOT NULL,
    provider_code VARCHAR NOT NULL,
    created_at VARCHAR NOT NULL DEFAULT strftime(now(), '%Y-%m-%dT%H:%M:%SZ'),
    UNIQUE (provider, provider_code)
);

-- dbe_metrics: metadata registry for economic indicators
CREATE SEQUENCE IF NOT EXISTS seq_dbe_metrics START 1;
CREATE TABLE IF NOT EXISTS dbe_metrics (
    metric_id INTEGER DEFAULT nextval('seq_dbe_metrics') PRIMARY KEY,
    metric_code VARCHAR NOT NULL UNIQUE,
    metric_name VARCHAR NOT NULL,
    category VARCHAR NOT NULL DEFAULT 'historical',
    frequency VARCHAR NOT NULL DEFAULT 'Annual',
    base_unit VARCHAR NOT NULL,
    unit_normalization VARCHAR,
    description VARCHAR,
    is_aggregatable INTEGER NOT NULL DEFAULT 0,
    aggregation_method VARCHAR,
    created_at VARCHAR NOT NULL DEFAULT strftime(now(), '%Y-%m-%dT%H:%M:%SZ'),
    updated_at VARCHAR NOT NULL DEFAULT strftime(now(), '%Y-%m-%dT%H:%M:%SZ')
);

-- dbe_metric_synonyms: synonyms for text/catalog matching
CREATE SEQUENCE IF NOT EXISTS seq_dbe_metric_synonyms START 1;
CREATE TABLE IF NOT EXISTS dbe_metric_synonyms (
    synonym_id INTEGER DEFAULT nextval('seq_dbe_metric_synonyms') PRIMARY KEY,
    metric_id INTEGER NOT NULL,
    synonym VARCHAR NOT NULL,
    created_at VARCHAR NOT NULL DEFAULT strftime(now(), '%Y-%m-%dT%H:%M:%SZ'),
    UNIQUE (metric_id, synonym)
);

-- dbe_sources: provider/dataset/indicator registration
CREATE SEQUENCE IF NOT EXISTS seq_dbe_sources START 1;
CREATE TABLE IF NOT EXISTS dbe_sources (
    source_id INTEGER DEFAULT nextval('seq_dbe_sources') PRIMARY KEY,
    provider VARCHAR NOT NULL,
    dataset VARCHAR NOT NULL,
    indicator_code VARCHAR NOT NULL,
    metric_id INTEGER NOT NULL,
    dimension_key VARCHAR NOT NULL,
    entity_scope VARCHAR NOT NULL,
    created_at VARCHAR NOT NULL DEFAULT strftime(now(), '%Y-%m-%dT%H:%M:%SZ'),
    UNIQUE (provider, dataset, indicator_code)
);

-- dbe_entity_membership: aggregate -> constituent entity relationships
CREATE SEQUENCE IF NOT EXISTS seq_dbe_entity_membership START 1;
CREATE TABLE IF NOT EXISTS dbe_entity_membership (
    membership_id INTEGER DEFAULT nextval('seq_dbe_entity_membership') PRIMARY KEY,
    aggregate_entity_id INTEGER NOT NULL,
    member_entity_id INTEGER NOT NULL,
    start_date VARCHAR NOT NULL,
    end_date VARCHAR,
    created_at VARCHAR NOT NULL DEFAULT strftime(now(), '%Y-%m-%dT%H:%M:%SZ'),
    UNIQUE (aggregate_entity_id, member_entity_id, start_date)
);

-- dbe_observations: time-series core (typed ranges)
CREATE SEQUENCE IF NOT EXISTS seq_dbe_observations START 1;
CREATE TABLE IF NOT EXISTS dbe_observations (
    id INTEGER DEFAULT nextval('seq_dbe_observations') PRIMARY KEY,
    metric_id INTEGER NOT NULL,
    entity_id INTEGER NOT NULL,
    source_id INTEGER NOT NULL,
    start_date VARCHAR NOT NULL,
    end_date VARCHAR NOT NULL,
    val DOUBLE NOT NULL,
    raw_val DOUBLE,
    unit VARCHAR,
    period_str VARCHAR,
    extracted_at VARCHAR NOT NULL DEFAULT strftime(now(), '%Y-%m-%dT%H:%M:%SZ'),
    UNIQUE (metric_id, entity_id, start_date, end_date),
    CHECK (start_date <= end_date)
);

-- dbe_derived_observations: aggregated (aggregate-entity) observations with provenance
CREATE SEQUENCE IF NOT EXISTS seq_dbe_derived_observations START 1;
CREATE TABLE IF NOT EXISTS dbe_derived_observations (
    id INTEGER DEFAULT nextval('seq_dbe_derived_observations') PRIMARY KEY,
    metric_id INTEGER NOT NULL,
    entity_id INTEGER NOT NULL,
    start_date VARCHAR NOT NULL,
    end_date VARCHAR NOT NULL,
    val DOUBLE NOT NULL,
    unit VARCHAR,
    aggregation_method VARCHAR NOT NULL,
    member_count INTEGER NOT NULL,
    covered_count INTEGER NOT NULL,
    computed_at VARCHAR NOT NULL DEFAULT strftime(now(), '%Y-%m-%dT%H:%M:%SZ'),
    UNIQUE (metric_id, entity_id, start_date, end_date),
    CHECK (start_date <= end_date)
);

-- dbe_pipeline_runs: audit log, now recording which tiers a run wrote
CREATE SEQUENCE IF NOT EXISTS seq_dbe_pipeline_runs START 1;
CREATE TABLE IF NOT EXISTS dbe_pipeline_runs (
    run_id INTEGER DEFAULT nextval('seq_dbe_pipeline_runs') PRIMARY KEY,
    run_timestamp VARCHAR NOT NULL DEFAULT strftime(now(), '%Y-%m-%dT%H:%M:%SZ'),
    dag_id VARCHAR,
    task_id VARCHAR,
    metrics_extracted INTEGER NOT NULL,
    observations_upserted INTEGER NOT NULL,
    observations_skipped INTEGER NOT NULL DEFAULT 0,
    providers VARCHAR,
    source_ids VARCHAR,
    status VARCHAR NOT NULL,
    execution_seconds DOUBLE NOT NULL,
    tiers_written VARCHAR
);

-- Unified read view over raw + derived observations.
CREATE OR REPLACE VIEW v_observations_all AS
SELECT
    o.id AS id,
    o.metric_id AS metric_id,
    o.entity_id AS entity_id,
    o.start_date AS start_date,
    o.end_date AS end_date,
    o.val AS val,
    o.unit AS unit,
    o.period_str AS period_str,
    o.extracted_at AS extracted_at,
    'raw' AS source_kind
FROM dbe_observations o
UNION ALL
SELECT
    d.id AS id,
    d.metric_id AS metric_id,
    d.entity_id AS entity_id,
    d.start_date AS start_date,
    d.end_date AS end_date,
    d.val AS val,
    d.unit AS unit,
    CAST(NULL AS VARCHAR) AS period_str,
    d.computed_at AS extracted_at,
    'derived' AS source_kind
FROM dbe_derived_observations d;

-- Latest observation per (metric, entity) across raw + derived.
CREATE OR REPLACE VIEW v_latest_metrics_snapshot AS
SELECT
    e.entity_code AS country_code,
    e.entity_name AS country_name,
    m.metric_code AS metric_code,
    m.metric_name AS metric_name,
    m.category AS category,
    m.frequency AS frequency,
    m.base_unit AS base_unit,
    lo.val AS value,
    lo.start_date AS period,
    lo.source_kind AS source_kind,
    CAST(current_date AS VARCHAR) AS snapshot_date
FROM dbe_entities e
JOIN v_observations_all lo ON lo.entity_id = e.entity_id
JOIN dbe_metrics m ON m.metric_id = lo.metric_id
JOIN (
    SELECT v.metric_id AS metric_id, v.entity_id AS entity_id, MAX(v.start_date) AS latest_date
    FROM v_observations_all v
    GROUP BY v.metric_id, v.entity_id
) latest ON latest.metric_id = lo.metric_id
    AND latest.entity_id = lo.entity_id
    AND latest.latest_date = lo.start_date
WHERE e.is_active = 1;

-- Dated execution snapshot (full history).
CREATE OR REPLACE VIEW v_dated_execution_snapshot AS
SELECT
    e.entity_code AS country_code,
    e.entity_name AS country_name,
    m.metric_code AS metric_code,
    m.metric_name AS metric_name,
    m.category AS category,
    m.frequency AS frequency,
    m.base_unit AS base_unit,
    o.val AS val,
    o.start_date AS start_period,
    o.end_date AS end_period,
    o.unit AS unit,
    o.period_str AS period_str,
    o.extracted_at AS extracted_at,
    CAST(current_date AS VARCHAR) AS snapshot_date
FROM dbe_entities e
JOIN v_observations_all o ON o.entity_id = e.entity_id
JOIN dbe_metrics m ON m.metric_id = o.metric_id
WHERE e.is_active = 1;

COMMIT;
