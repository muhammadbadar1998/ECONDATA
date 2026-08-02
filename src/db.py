import os
import logging
import urllib.parse
from sqlalchemy import create_engine, text
from sqlalchemy.engine import Engine

from src.config import DEFAULT_DB_CONNECTION_STRING

logger = logging.getLogger(__name__)


def get_sqlalchemy_engine(connection_string: str = DEFAULT_DB_CONNECTION_STRING) -> Engine:
    """
    Creates a SQLAlchemy engine for PostgreSQL (Supabase) or SQL Server dynamically based on connection scheme.
    """
    if connection_string.startswith("postgresql") or connection_string.startswith("postgres"):
        engine = create_engine(
            connection_string,
            pool_pre_ping=True,
            pool_size=10,
            max_overflow=20,
            connect_args={"connect_timeout": 15}
        )
    elif "Driver=" in connection_string or "driver=" in connection_string.lower():
        params = urllib.parse.quote_plus(connection_string)
        engine_url = f"mssql+pyodbc:///?odbc_connect={params}"
        engine = create_engine(engine_url, fast_executemany=True, pool_pre_ping=True)
    else:
        engine = create_engine(connection_string, pool_pre_ping=True)

    return engine


def init_db_schema(connection_string: str = DEFAULT_DB_CONNECTION_STRING, schema_script_path: str = "sql/dbe_postgres_schema.sql") -> bool:
    """
    Initializes database tables (dbe_countries, dbe_metrics, dbe_economic_observations, dbe_latest_country_snapshots, dbe_pipeline_runs)
    by executing the SQL DDL script.
    """
    if "postgresql" in connection_string or "postgres" in connection_string:
        schema_script_path = "sql/dbe_postgres_schema.sql"
    elif "Driver=" in connection_string:
        schema_script_path = "sql/dbe_schema.sql"

    if not os.path.exists(schema_script_path):
        logger.error(f"DDL schema script not found at path: {schema_script_path}")
        return False

    with open(schema_script_path, "r", encoding="utf-8") as f:
        sql_script = f.read()

    logger.info(f"Connecting to database to initialize dbe schema ({schema_script_path})...")
    try:
        engine = get_sqlalchemy_engine(connection_string)
        
        with engine.begin() as conn:
            if "postgresql" in connection_string or "postgres" in connection_string:
                # Execute full PostgreSQL DDL script block
                conn.execute(text(sql_script))
            else:
                # SQL Server batch execution split by GO
                raw_statements = sql_script.split("GO")
                for raw_stmt in raw_statements:
                    stmt = raw_stmt.strip()
                    if stmt:
                        conn.execute(text(stmt))

        logger.info("Database schema successfully initialized with dbe_* tables!")
        return True
    except Exception as e:
        logger.error(f"Failed to initialize database schema: {e}", exc_info=True)
        return False
