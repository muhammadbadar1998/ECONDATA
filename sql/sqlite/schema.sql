-- SQLite schema for EconData (dbe_* tables).
--
-- Ported from the former PostgreSQL/Supabase DDL. Enums are CHECK constraints,
-- TIMESTAMPTZ is ISO-8601 TEXT, JSONB/arrays are JSON TEXT, and SERIAL is
-- INTEGER PRIMARY KEY AUTOINCREMENT. The 768-dim embedding lives in the
-- sqlite-vec virtual table created by the vectorizer, so dbe_embeddings here
-- stores document metadata only (no vector column).

PRAGMA foreign_keys = ON;

BEGIN;

-- dbe_entities: canonical entity registry
CREATE TABLE IF NOT EXISTS dbe_entities (
    entity_id INTEGER PRIMARY KEY AUTOINCREMENT,
    entity_code TEXT NOT NULL UNIQUE,
    entity_name TEXT NOT NULL,
    entity_type TEXT NOT NULL DEFAULT 'country'
        CHECK (entity_type IN ('country', 'aggregate', 'region', 'custom')),
    is_aggregate INTEGER NOT NULL DEFAULT 0,
    parent_entity_id INTEGER REFERENCES dbe_entities(entity_id) ON DELETE SET NULL,
    is_latest INTEGER NOT NULL DEFAULT 1,
    is_active INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now'))
);
CREATE INDEX IF NOT EXISTS idx_dbe_entities_code ON dbe_entities(entity_code);
CREATE INDEX IF NOT EXISTS idx_dbe_entities_type ON dbe_entities(entity_type);
CREATE INDEX IF NOT EXISTS idx_dbe_entities_active ON dbe_entities(is_active);

-- dbe_entity_aliases: provider code -> canonical entity mapping
CREATE TABLE IF NOT EXISTS dbe_entity_aliases (
    alias_id INTEGER PRIMARY KEY AUTOINCREMENT,
    entity_id INTEGER NOT NULL REFERENCES dbe_entities(entity_id) ON DELETE CASCADE,
    provider TEXT NOT NULL,
    provider_code TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    UNIQUE (provider, provider_code)
);
CREATE INDEX IF NOT EXISTS idx_dbe_entity_aliases_entity ON dbe_entity_aliases(entity_id);
CREATE INDEX IF NOT EXISTS idx_dbe_entity_aliases_provider ON dbe_entity_aliases(provider);

-- dbe_metrics: metadata registry for economic indicators
CREATE TABLE IF NOT EXISTS dbe_metrics (
    metric_id INTEGER PRIMARY KEY AUTOINCREMENT,
    metric_code TEXT NOT NULL UNIQUE,
    metric_name TEXT NOT NULL,
    category TEXT NOT NULL DEFAULT 'historical' CHECK (category IN ('historical', 'forecast')),
    frequency TEXT NOT NULL DEFAULT 'Annual' CHECK (frequency IN ('Annual', 'Quarterly', 'Monthly')),
    base_unit TEXT NOT NULL,
    unit_normalization TEXT,
    description TEXT,
    is_aggregatable INTEGER NOT NULL DEFAULT 0,
    aggregation_method TEXT,
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now'))
);
CREATE INDEX IF NOT EXISTS idx_dbe_metrics_code ON dbe_metrics(metric_code);
CREATE INDEX IF NOT EXISTS idx_dbe_metrics_category ON dbe_metrics(category);
CREATE INDEX IF NOT EXISTS idx_dbe_metrics_frequency ON dbe_metrics(frequency);

-- dbe_metric_synonyms: synonyms for text/catalog matching
CREATE TABLE IF NOT EXISTS dbe_metric_synonyms (
    synonym_id INTEGER PRIMARY KEY AUTOINCREMENT,
    metric_id INTEGER NOT NULL REFERENCES dbe_metrics(metric_id) ON DELETE CASCADE,
    synonym TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    UNIQUE (metric_id, synonym)
);
CREATE INDEX IF NOT EXISTS idx_dbe_metric_synonyms_synonym ON dbe_metric_synonyms(synonym);

-- dbe_sources: provider/dataset/indicator registration
CREATE TABLE IF NOT EXISTS dbe_sources (
    source_id INTEGER PRIMARY KEY AUTOINCREMENT,
    provider TEXT NOT NULL,
    dataset TEXT NOT NULL,
    indicator_code TEXT NOT NULL,
    metric_id INTEGER NOT NULL REFERENCES dbe_metrics(metric_id) ON DELETE CASCADE,
    dimension_key TEXT NOT NULL,
    entity_scope TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    UNIQUE (provider, dataset, indicator_code)
);
CREATE INDEX IF NOT EXISTS idx_dbe_sources_provider ON dbe_sources(provider);
CREATE INDEX IF NOT EXISTS idx_dbe_sources_dataset ON dbe_sources(dataset);

-- dbe_entity_membership: aggregate -> constituent entity relationships
CREATE TABLE IF NOT EXISTS dbe_entity_membership (
    membership_id INTEGER PRIMARY KEY AUTOINCREMENT,
    aggregate_entity_id INTEGER NOT NULL REFERENCES dbe_entities(entity_id) ON DELETE CASCADE,
    member_entity_id INTEGER NOT NULL REFERENCES dbe_entities(entity_id) ON DELETE CASCADE,
    start_date TEXT NOT NULL,
    end_date TEXT,
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    UNIQUE (aggregate_entity_id, member_entity_id, start_date)
);
CREATE INDEX IF NOT EXISTS idx_dbe_entity_membership_aggregate ON dbe_entity_membership(aggregate_entity_id);

-- dbe_observations: time-series core (typed ranges)
CREATE TABLE IF NOT EXISTS dbe_observations (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    metric_id INTEGER NOT NULL REFERENCES dbe_metrics(metric_id) ON DELETE CASCADE,
    entity_id INTEGER NOT NULL REFERENCES dbe_entities(entity_id) ON DELETE CASCADE,
    source_id INTEGER NOT NULL REFERENCES dbe_sources(source_id) ON DELETE CASCADE,
    start_date TEXT NOT NULL,
    end_date TEXT NOT NULL,
    val REAL NOT NULL,
    raw_val REAL,
    unit TEXT,
    period_str TEXT,
    extracted_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    UNIQUE (metric_id, entity_id, start_date, end_date),
    CHECK (start_date <= end_date)
);
CREATE INDEX IF NOT EXISTS idx_dbe_observations_metric ON dbe_observations(metric_id);
CREATE INDEX IF NOT EXISTS idx_dbe_observations_entity ON dbe_observations(entity_id);
CREATE INDEX IF NOT EXISTS idx_dbe_observations_dates ON dbe_observations(start_date, end_date);
CREATE INDEX IF NOT EXISTS idx_dbe_observations_extracted ON dbe_observations(extracted_at);

-- dbe_pipeline_runs: audit log
CREATE TABLE IF NOT EXISTS dbe_pipeline_runs (
    run_id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_timestamp TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    dag_id TEXT,
    task_id TEXT,
    metrics_extracted INTEGER NOT NULL,
    observations_upserted INTEGER NOT NULL,
    observations_skipped INTEGER NOT NULL DEFAULT 0,
    providers TEXT,
    source_ids TEXT,
    status TEXT NOT NULL,
    execution_seconds REAL NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_dbe_pipeline_runs_timestamp ON dbe_pipeline_runs(run_timestamp);

-- dbe_embeddings: document metadata for the semantic catalog layer
CREATE TABLE IF NOT EXISTS dbe_embeddings (
    doc_id INTEGER PRIMARY KEY AUTOINCREMENT,
    doc_key TEXT NOT NULL,
    doc_type TEXT NOT NULL,
    content TEXT NOT NULL,
    doc_hash TEXT NOT NULL,
    metadata TEXT NOT NULL DEFAULT '{}',
    embedding_model TEXT NOT NULL DEFAULT 'nomic-embed-text',
    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    UNIQUE (doc_key, doc_type)
);
CREATE INDEX IF NOT EXISTS idx_dbe_embeddings_key ON dbe_embeddings(doc_key, doc_type);
CREATE INDEX IF NOT EXISTS idx_dbe_embeddings_hash ON dbe_embeddings(doc_hash);
CREATE INDEX IF NOT EXISTS idx_dbe_embeddings_model ON dbe_embeddings(embedding_model);

-- Latest metrics snapshot (long format)
DROP VIEW IF EXISTS v_latest_metrics_snapshot;
CREATE VIEW IF NOT EXISTS v_latest_metrics_snapshot AS
SELECT
    e.entity_code AS country_code,
    e.entity_name AS country_name,
    m.metric_code,
    m.metric_name,
    m.category,
    m.frequency,
    m.base_unit,
    lo.val AS value,
    lo.start_date AS period,
    date('now') AS snapshot_date
FROM dbe_entities e
JOIN dbe_observations lo ON e.entity_id = lo.entity_id
JOIN dbe_metrics m ON lo.metric_id = m.metric_id
JOIN (
    SELECT metric_id, entity_id, MAX(start_date) AS latest_date
    FROM dbe_observations
    GROUP BY metric_id, entity_id
) latest ON lo.metric_id = latest.metric_id
    AND lo.entity_id = latest.entity_id
    AND lo.start_date = latest.latest_date
WHERE e.is_active = 1;

-- Dated execution snapshot (full history)
DROP VIEW IF EXISTS v_dated_execution_snapshot;
CREATE VIEW IF NOT EXISTS v_dated_execution_snapshot AS
SELECT
    e.entity_code AS country_code,
    e.entity_name AS country_name,
    m.metric_code,
    m.metric_name,
    m.category,
    m.frequency,
    m.base_unit,
    o.val,
    o.start_date AS start_period,
    o.end_date AS end_period,
    o.unit,
    o.period_str,
    o.extracted_at,
    date('now') AS snapshot_date
FROM dbe_entities e
JOIN dbe_observations o ON e.entity_id = o.entity_id
JOIN dbe_metrics m ON o.metric_id = m.metric_id
WHERE e.is_active = 1;

-- dbe_derived_observations: aggregated (aggregate-entity) observations, computed
-- from raw dbe_observations. Provenance columns record member coverage.
CREATE TABLE IF NOT EXISTS dbe_derived_observations (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    metric_id INTEGER NOT NULL REFERENCES dbe_metrics(metric_id) ON DELETE CASCADE,
    entity_id INTEGER NOT NULL REFERENCES dbe_entities(entity_id) ON DELETE CASCADE,
    start_date TEXT NOT NULL,
    end_date TEXT NOT NULL,
    val REAL NOT NULL,
    unit TEXT,
    aggregation_method TEXT NOT NULL CHECK (aggregation_method IN ('sum', 'mean')),
    member_count INTEGER NOT NULL,
    covered_count INTEGER NOT NULL,
    computed_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    UNIQUE (metric_id, entity_id, start_date, end_date),
    CHECK (start_date <= end_date)
);
CREATE INDEX IF NOT EXISTS idx_dbe_derived_obs_metric ON dbe_derived_observations(metric_id);
CREATE INDEX IF NOT EXISTS idx_dbe_derived_obs_entity ON dbe_derived_observations(entity_id);

-- Unified read view over raw + derived observations.
DROP VIEW IF EXISTS v_observations_all;
CREATE VIEW v_observations_all AS
SELECT o.id AS id, o.metric_id AS metric_id, o.entity_id AS entity_id,
       o.start_date AS start_date, o.end_date AS end_date, o.val AS val,
       o.unit AS unit, o.period_str AS period_str, o.extracted_at AS extracted_at,
       'raw' AS source_kind
FROM dbe_observations o
UNION ALL
SELECT d.id AS id, d.metric_id AS metric_id, d.entity_id AS entity_id,
       d.start_date AS start_date, d.end_date AS end_date, d.val AS val,
       d.unit AS unit, NULL AS period_str, d.computed_at AS extracted_at,
       'derived' AS source_kind
FROM dbe_derived_observations d;

-- Rebuild snapshot views on the unified read surface.
DROP VIEW IF EXISTS v_latest_metrics_snapshot;
CREATE VIEW IF NOT EXISTS v_latest_metrics_snapshot AS
SELECT
    e.entity_code AS country_code,
    e.entity_name AS country_name,
    m.metric_code,
    m.metric_name,
    m.category,
    m.frequency,
    m.base_unit,
    lo.val AS value,
    lo.start_date AS period,
    date('now') AS snapshot_date
FROM dbe_entities e
JOIN v_observations_all lo ON e.entity_id = lo.entity_id
JOIN dbe_metrics m ON lo.metric_id = m.metric_id
JOIN (
    SELECT metric_id, entity_id, MAX(start_date) AS latest_date
    FROM v_observations_all
    GROUP BY metric_id, entity_id
) latest ON lo.metric_id = latest.metric_id
    AND lo.entity_id = latest.entity_id
    AND lo.start_date = latest.latest_date
WHERE e.is_active = 1;

DROP VIEW IF EXISTS v_dated_execution_snapshot;
CREATE VIEW IF NOT EXISTS v_dated_execution_snapshot AS
SELECT
    e.entity_code AS country_code,
    e.entity_name AS country_name,
    m.metric_code,
    m.metric_name,
    m.category,
    m.frequency,
    m.base_unit,
    o.val,
    o.start_date AS start_period,
    o.end_date AS end_period,
    o.unit,
    o.period_str,
    o.extracted_at,
    date('now') AS snapshot_date
FROM dbe_entities e
JOIN v_observations_all o ON e.entity_id = o.entity_id
JOIN dbe_metrics m ON o.metric_id = m.metric_id
WHERE e.is_active = 1;

COMMIT;
