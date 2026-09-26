import logging
import os

from sqlalchemy import create_engine, text
from sqlalchemy.engine import Engine

from src.config import get_default_db_connection_string

logger = logging.getLogger(__name__)


def get_sqlalchemy_engine(connection_string: str = "") -> Engine:
    """
    Creates a SQLAlchemy engine for PostgreSQL (Supabase) only.
    Raises if a non-Postgres connection string is supplied (SQL Server removed).

    An empty/none ``connection_string`` defers to the lazily-resolved default
    from :func:`src.config.get_supabase_connection_string`, which is only
    evaluated when credentials are actually present.
    """
    if not connection_string:
        connection_string = get_default_db_connection_string()
    if "postgresql" in connection_string or "postgres" in connection_string:
        engine = create_engine(
            connection_string,
            pool_pre_ping=True,
            pool_size=10,
            max_overflow=20,
            connect_args={"connect_timeout": 15}
        )
    else:
        raise ValueError(
            "Only PostgreSQL/Supabase connections are supported. "
            "SQL Server support was removed during the platform refactor."
        )
    return engine


def init_db_schema(connection_string: str = "", schema_script_path: str = "sql/dbe_postgres_schema.sql") -> bool:
    """
    Initializes the normalized dbe_* schema by executing the PostgreSQL DDL script.
    """
    if not connection_string:
        connection_string = get_default_db_connection_string()
    if not os.path.exists(schema_script_path):
        logger.error(f"DDL schema script not found at path: {schema_script_path}")
        return False

    with open(schema_script_path, "r", encoding="utf-8") as f:
        sql_script = f.read()

    logger.info(f"Connecting to database to initialize dbe schema ({schema_script_path})...")
    try:
        engine = get_sqlalchemy_engine(connection_string)
        with engine.begin() as conn:
            conn.execute(text(sql_script))

        logger.info("Database schema successfully initialized with dbe_* tables!")
        return True
    except Exception as e:
        logger.error(f"Failed to initialize database schema: {e}", exc_info=True)
        return False
