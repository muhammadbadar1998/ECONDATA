-- New Normalized Schema for Supabase PostgreSQL (dbe_ Tables)
-- Replaces the old wide tables with a fully normalized, extensible, and typed design.

BEGIN;

-- Creates the vector extension required for dbe_embeddings (pgvector).
CREATE EXTENSION IF NOT EXISTS "vector";

-- Create extension for uuid if needed
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";

-- Create enum types for entity_type and metric_category
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'entity_type') THEN
        CREATE TYPE entity_type AS ENUM ('country', 'aggregate', 'region', 'custom');
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'metric_category') THEN
        CREATE TYPE metric_category AS ENUM ('historical', 'forecast');
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'frequency') THEN
        CREATE TYPE frequency AS ENUM ('Annual', 'Quarterly', 'Monthly');
    END IF;
END $$;

-- dbe_entities: Canonical entity registry
CREATE TABLE IF NOT EXISTS dbe_entities (
    entity_id SERIAL PRIMARY KEY,
    entity_code VARCHAR(20) UNIQUE NOT NULL,
    entity_name VARCHAR(255) NOT NULL,
    entity_type entity_type NOT NULL DEFAULT 'country',
    is_aggregate BOOLEAN NOT NULL DEFAULT FALSE,
    parent_entity_id INT NULL,
    is_latest BOOLEAN DEFAULT TRUE,
    is_active BOOLEAN DEFAULT TRUE,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT fk_parent_entity FOREIGN KEY (parent_entity_id) REFERENCES dbe_entities(entity_id) ON DELETE SET NULL
);

CREATE INDEX IF NOT EXISTS idx_dbe_entities_code ON dbe_entities(entity_code);
CREATE INDEX IF NOT EXISTS idx_dbe_entities_type ON dbe_entities(entity_type);
CREATE INDEX IF NOT EXISTS idx_dbe_entities_active ON dbe_entities(is_active);

-- dbe_entity_aliases: Provider code → canonical entity mapping
CREATE TABLE IF NOT EXISTS dbe_entity_aliases (
    alias_id SERIAL PRIMARY KEY,
    entity_id INT NOT NULL,
    provider VARCHAR(50) NOT NULL,
    provider_code VARCHAR(50) NOT NULL,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT fk_alias_entity FOREIGN KEY (entity_id) REFERENCES dbe_entities(entity_id) ON DELETE CASCADE,
    CONSTRAINT uq_provider_code UNIQUE (provider, provider_code)
);

CREATE INDEX IF NOT EXISTS idx_dbe_entity_aliases_entity ON dbe_entity_aliases(entity_id);
CREATE INDEX IF NOT EXISTS idx_dbe_entity_aliases_provider ON dbe_entity_aliases(provider);

-- dbe_metrics: Metadata registry for economic indicators
CREATE TABLE IF NOT EXISTS dbe_metrics (
    metric_id SERIAL PRIMARY KEY,
    metric_code VARCHAR(100) UNIQUE NOT NULL,
    metric_name VARCHAR(255) NOT NULL,
    category metric_category DEFAULT 'historical',
    frequency frequency DEFAULT 'Annual',
    base_unit VARCHAR(100) NOT NULL,
    unit_normalization TEXT, -- SQL expression to convert source unit to base_unit
    description TEXT,
    is_aggregatable BOOLEAN DEFAULT FALSE,
    aggregation_method VARCHAR(50), -- 'sum', 'mean', 'weighted'
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_dbe_metrics_code ON dbe_metrics(metric_code);
CREATE INDEX IF NOT EXISTS idx_dbe_metrics_category ON dbe_metrics(category);
CREATE INDEX IF NOT EXISTS idx_dbe_metrics_frequency ON dbe_metrics(frequency);

-- dbe_metric_synonyms: Synonyms for text-to-SQL matching
CREATE TABLE IF NOT EXISTS dbe_metric_synonyms (
    synonym_id SERIAL PRIMARY KEY,
    metric_id INT NOT NULL,
    synonym VARCHAR(255) NOT NULL,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT fk_synonym_metric FOREIGN KEY (metric_id) REFERENCES dbe_metrics(metric_id) ON DELETE CASCADE,
    CONSTRAINT uq_metric_synonym UNIQUE (metric_id, synonym)
);

CREATE INDEX IF NOT EXISTS idx_dbe_metric_synonyms_synonym ON dbe_metric_synonyms(synonym);

-- dbe_sources: Provider/dataset/indicator registration
CREATE TABLE IF NOT EXISTS dbe_sources (
    source_id SERIAL PRIMARY KEY,
    provider VARCHAR(50) NOT NULL,
    dataset VARCHAR(100) NOT NULL,
    indicator_code VARCHAR(100) NOT NULL,
    metric_id INT NOT NULL,
    dimension_key VARCHAR(50) NOT NULL, -- 'country', 'ref_area', 'LOCATION', 'weo-country-code'
    entity_scope VARCHAR(50) NOT NULL, -- 'country', 'aggregate'
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT fk_source_metric FOREIGN KEY (metric_id) REFERENCES dbe_metrics(metric_id) ON DELETE CASCADE,
    CONSTRAINT uq_provider_dataset_indicator UNIQUE (provider, dataset, indicator_code)
);

CREATE INDEX IF NOT EXISTS idx_dbe_sources_provider ON dbe_sources(provider);
CREATE INDEX IF NOT EXISTS idx_dbe_sources_dataset ON dbe_sources(dataset);

-- dbe_entity_membership: Aggregate → constituent entity relationships
CREATE TABLE IF NOT EXISTS dbe_entity_membership (
    membership_id SERIAL PRIMARY KEY,
    aggregate_entity_id INT NOT NULL,
    member_entity_id INT NOT NULL,
    start_date DATE NOT NULL,
    end_date DATE,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT fk_membership_aggregate FOREIGN KEY (aggregate_entity_id) REFERENCES dbe_entities(entity_id) ON DELETE CASCADE,
    CONSTRAINT fk_membership_member FOREIGN KEY (member_entity_id) REFERENCES dbe_entities(entity_id) ON DELETE CASCADE,
    CONSTRAINT uq_membership UNIQUE (aggregate_entity_id, member_entity_id, start_date)
);

CREATE INDEX IF NOT EXISTS idx_dbe_entity_membership_aggregate ON dbe_entity_membership(aggregate_entity_id);

-- dbe_observations: Time-series core (typed ranges)
CREATE TABLE IF NOT EXISTS dbe_observations (
    id BIGSERIAL PRIMARY KEY,
    metric_id INT NOT NULL,
    entity_id INT NOT NULL,
    source_id INT NOT NULL,
    start_date DATE NOT NULL,
    end_date DATE NOT NULL,
    val DOUBLE PRECISION NOT NULL,
    raw_val DOUBLE PRECISION,
    unit VARCHAR(100),
    period_str VARCHAR(50),
    extracted_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT fk_observation_metric FOREIGN KEY (metric_id) REFERENCES dbe_metrics(metric_id) ON DELETE CASCADE,
    CONSTRAINT fk_observation_entity FOREIGN KEY (entity_id) REFERENCES dbe_entities(entity_id) ON DELETE CASCADE,
    CONSTRAINT fk_observation_source FOREIGN KEY (source_id) REFERENCES dbe_sources(source_id) ON DELETE CASCADE,
    CONSTRAINT uq_observation_unique UNIQUE (metric_id, entity_id, start_date, end_date),
    CONSTRAINT chk_date_order CHECK (start_date <= end_date)
);

CREATE INDEX IF NOT EXISTS idx_dbe_observations_metric ON dbe_observations(metric_id);
CREATE INDEX IF NOT EXISTS idx_dbe_observations_entity ON dbe_observations(entity_id);
CREATE INDEX IF NOT EXISTS idx_dbe_observations_dates ON dbe_observations(start_date, end_date);
CREATE INDEX IF NOT EXISTS idx_dbe_observations_extracted ON dbe_observations(extracted_at);

-- dbe_pipeline_runs: Extended audit log
CREATE TABLE IF NOT EXISTS dbe_pipeline_runs (
    run_id SERIAL PRIMARY KEY,
    run_timestamp TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    dag_id VARCHAR(255),
    task_id VARCHAR(255),
    metrics_extracted INT NOT NULL,
    observations_upserted INT NOT NULL,
    observations_skipped INT DEFAULT 0,
    providers TEXT[], -- array of provider names
    source_ids INT[], -- array of source_ids used
    status VARCHAR(50) NOT NULL,
    execution_seconds DOUBLE PRECISION NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_dbe_pipeline_runs_timestamp ON dbe_pipeline_runs(run_timestamp);

-- dbe_embeddings: Document store + vector index for the semantic catalog layer.
-- Documents are built from metric/entity/summary data; embeddings are produced
-- by Ollama-served nomic-embed-text (768-dim). See src/vectorizer.py.
CREATE TABLE IF NOT EXISTS dbe_embeddings (
    doc_id BIGSERIAL PRIMARY KEY,
    doc_key VARCHAR(255) NOT NULL,
    doc_type VARCHAR(64) NOT NULL,
    content TEXT NOT NULL,
    doc_hash VARCHAR(64) NOT NULL,
    metadata JSONB DEFAULT '{}'::jsonb,
    embedding vector(768) NOT NULL,
    embedding_model VARCHAR(128) NOT NULL DEFAULT 'nomic-embed-text',
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT uq_embedding_doc UNIQUE (doc_key, doc_type)
);

CREATE INDEX IF NOT EXISTS idx_dbe_embeddings_key ON dbe_embeddings(doc_key, doc_type);
CREATE INDEX IF NOT EXISTS idx_dbe_embeddings_hash ON dbe_embeddings(doc_hash);
CREATE INDEX IF NOT EXISTS idx_dbe_embeddings_model ON dbe_embeddings(embedding_model);
-- HNSW index for fast approximate nearest-neighbour search (cosine distance).
CREATE INDEX IF NOT EXISTS idx_dbe_embeddings_hnsw
    ON dbe_embeddings
    USING hnsw (embedding vector_cosine_ops)
    WITH (m = 16, ef_construction = 200);

-- Views for cross-sectional snapshots (replace wide tables)
--
-- The old v_latest_country_snapshot view was dropped: it was hardcoded to exactly
-- 7 metrics and used COALESCE(...,0), which conflated "no data" with "zero".
-- Use v_latest_metrics_snapshot (long format) + dbe_embeddings for catalog search instead.

-- Latest metrics snapshot (long format)
CREATE OR REPLACE VIEW v_latest_metrics_snapshot AS
SELECT 
    e.entity_code AS country_code,
    e.entity_name AS country_name,
    m.metric_code,
    m.metric_name,
    m.category,
    m.frequency,
    m.base_unit,
    COALESCE(lo.val, 0) AS value,
    lo.start_date AS period,
    CURRENT_DATE AS snapshot_date
FROM dbe_entities e
JOIN dbe_observations lo ON e.entity_id = lo.entity_id
JOIN dbe_metrics m ON lo.metric_id = m.metric_id
JOIN (
    SELECT 
        metric_id,
        entity_id,
        MAX(start_date) AS latest_date
    FROM dbe_observations
    GROUP BY metric_id, entity_id
) latest ON lo.metric_id = latest.metric_id 
   AND lo.entity_id = latest.entity_id 
   AND lo.start_date = latest.latest_date
WHERE e.is_active = TRUE;

-- Dated execution snapshot (full history)
CREATE OR REPLACE VIEW v_dated_execution_snapshot AS
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
    CURRENT_DATE AS snapshot_date
FROM dbe_entities e
JOIN dbe_observations o ON e.entity_id = o.entity_id
JOIN dbe_metrics m ON o.metric_id = m.metric_id
WHERE e.is_active = TRUE;

COMMIT;