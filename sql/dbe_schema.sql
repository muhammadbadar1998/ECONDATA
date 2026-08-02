IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'dbe_countries')
CREATE TABLE dbe_countries (
    country_code VARCHAR(10) PRIMARY KEY,
    country_name NVARCHAR(255) NOT NULL,
    provider VARCHAR(50) DEFAULT 'WB',
    dataset VARCHAR(50) DEFAULT 'WDI',
    is_aggregate BIT NOT NULL DEFAULT 0,
    created_at DATETIME2 DEFAULT SYSDATETIME(),
    updated_at DATETIME2 DEFAULT SYSDATETIME()
)
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'dbe_metrics')
CREATE TABLE dbe_metrics (
    metric_code VARCHAR(50) PRIMARY KEY,
    metric_name NVARCHAR(255) NOT NULL,
    provider VARCHAR(50),
    dataset VARCHAR(50),
    indicator_code VARCHAR(100),
    unit NVARCHAR(100),
    frequency VARCHAR(50),
    description NVARCHAR(MAX),
    updated_at DATETIME2 DEFAULT SYSDATETIME()
)
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'dbe_economic_observations')
CREATE TABLE dbe_economic_observations (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    country_code VARCHAR(10) NOT NULL FOREIGN KEY REFERENCES dbe_countries(country_code),
    metric_code VARCHAR(50) NOT NULL FOREIGN KEY REFERENCES dbe_metrics(metric_code),
    period VARCHAR(20) NOT NULL,
    val FLOAT NOT NULL,
    unit NVARCHAR(100),
    frequency VARCHAR(50),
    extracted_at DATETIME2 NOT NULL,
    CONSTRAINT UQ_dbe_obs UNIQUE (country_code, metric_code, period)
)
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'dbe_latest_country_snapshots')
CREATE TABLE dbe_latest_country_snapshots (
    country_code VARCHAR(10) PRIMARY KEY FOREIGN KEY REFERENCES dbe_countries(country_code),
    country_name NVARCHAR(255) NOT NULL,
    is_aggregate BIT NOT NULL DEFAULT 0,
    gdp_usd_latest FLOAT,
    gdp_usd_period VARCHAR(20),
    gdp_growth_latest FLOAT,
    gdp_growth_period VARCHAR(20),
    inflation_cpi_latest FLOAT,
    inflation_cpi_period VARCHAR(20),
    unemployment_rate_latest FLOAT,
    unemployment_rate_period VARCHAR(20),
    population_latest FLOAT,
    population_period VARCHAR(20),
    current_account_balance_latest FLOAT,
    current_account_balance_period VARCHAR(20),
    govt_debt_gdp_latest FLOAT,
    govt_debt_gdp_period VARCHAR(20),
    snapshot_date DATE NOT NULL,
    updated_at DATETIME2 DEFAULT SYSDATETIME()
)
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'dbe_pipeline_runs')
CREATE TABLE dbe_pipeline_runs (
    run_id INT IDENTITY(1,1) PRIMARY KEY,
    run_timestamp DATETIME2 DEFAULT SYSDATETIME(),
    metrics_extracted INT NOT NULL,
    total_observations INT NOT NULL,
    status VARCHAR(50) NOT NULL,
    execution_seconds FLOAT NOT NULL
)
GO
