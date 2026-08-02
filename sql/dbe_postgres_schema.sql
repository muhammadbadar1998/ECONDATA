-- Supabase PostgreSQL DDL Script for DBnomics Economic Data Pipeline (dbe_ Tables)

CREATE TABLE IF NOT EXISTS dbe_countries (
    country_code VARCHAR(10) PRIMARY KEY,
    country_name VARCHAR(255) NOT NULL,
    provider VARCHAR(50) DEFAULT 'WB',
    dataset VARCHAR(50) DEFAULT 'WDI',
    is_aggregate BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS dbe_metrics (
    metric_code VARCHAR(50) PRIMARY KEY,
    metric_name VARCHAR(255) NOT NULL,
    provider VARCHAR(50),
    dataset VARCHAR(50),
    indicator_code VARCHAR(100),
    unit VARCHAR(100),
    frequency VARCHAR(50),
    description TEXT,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS dbe_economic_observations (
    id BIGSERIAL PRIMARY KEY,
    country_code VARCHAR(10) NOT NULL REFERENCES dbe_countries(country_code) ON DELETE CASCADE,
    metric_code VARCHAR(50) NOT NULL REFERENCES dbe_metrics(metric_code) ON DELETE CASCADE,
    period VARCHAR(20) NOT NULL,
    val DOUBLE PRECISION NOT NULL,
    unit VARCHAR(100),
    frequency VARCHAR(50),
    extracted_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT uq_dbe_obs UNIQUE (country_code, metric_code, period)
);

CREATE TABLE IF NOT EXISTS dbe_latest_country_snapshots (
    country_code VARCHAR(10) PRIMARY KEY REFERENCES dbe_countries(country_code) ON DELETE CASCADE,
    country_name VARCHAR(255) NOT NULL,
    is_aggregate BOOLEAN NOT NULL DEFAULT FALSE,
    gdp_usd_latest DOUBLE PRECISION,
    gdp_usd_period VARCHAR(20),
    gdp_growth_latest DOUBLE PRECISION,
    gdp_growth_period VARCHAR(20),
    inflation_cpi_latest DOUBLE PRECISION,
    inflation_cpi_period VARCHAR(20),
    unemployment_rate_latest DOUBLE PRECISION,
    unemployment_rate_period VARCHAR(20),
    population_latest DOUBLE PRECISION,
    population_period VARCHAR(20),
    current_account_balance_latest DOUBLE PRECISION,
    current_account_balance_period VARCHAR(20),
    govt_debt_gdp_latest DOUBLE PRECISION,
    govt_debt_gdp_period VARCHAR(20),
    snapshot_date DATE NOT NULL,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS dbe_pipeline_runs (
    run_id SERIAL PRIMARY KEY,
    run_timestamp TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    metrics_extracted INT NOT NULL,
    total_observations INT NOT NULL,
    status VARCHAR(50) NOT NULL,
    execution_seconds DOUBLE PRECISION NOT NULL
);
