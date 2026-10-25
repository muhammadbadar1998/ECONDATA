# EconData — Architecture & Codebase Explainer

Developer/architecture manual for the .NET 10 + SQLite/DuckDB implementation of the
Global Economic Data Extraction Pipeline.

---

## Quick reference

| Task | Command |
| :--- | :--- |
| Build | `dotnet build EconData.slnx` |
| Init SQLite schema | `dotnet run --project src/EconData.Cli -- init-db` |
| Init gold schema | `dotnet run --project src/EconData.Cli -- lake init` |
| Check schema | `dotnet run --project src/EconData.Cli -- check-db` |
| Readiness check | `dotnet run --project src/EconData.Cli -- doctor` |
| Full pipeline into SQLite | `dotnet run --project src/EconData.Cli -- extract --metrics all --db` |
| Full pipeline into the lake | `dotnet run --project src/EconData.Cli -- extract --metrics all --bronze --silver --gold` |
| Lake status / rebuild | `dotnet run --project src/EconData.Cli -- lake status` · `... lake rebuild` |
| Backfill gold from SQLite | `dotnet run --project src/EconData.Cli -- lake import-from-sqlite` |
| Vectorize catalog | `dotnet run --project src/EconData.Cli -- vectorize` |
| Ask a question | `dotnet run --project src/EconData.Cli -- chat --question "..."` |
| Tests | `dotnet test EconData.slnx` |

---

## Solution layout

```
EconData.slnx
Directory.Build.props          # net10.0, nullable, analyzers, warnings-as-errors
global.json                    # pins the .NET SDK
config/
  metrics.json                 # metric catalog (unchanged from the Python era)
  entities.json                # canonical entities + aliases + memberships
  appsettings.json             # non-secret defaults (env vars override)
sql/sqlite/schema.sql          # normalized SQLite DDL + views
sql/duckdb/schema.sql          # DuckDB gold DDL + views
native/sqlite-vec/             # vendored sqlite-vec loadable extension (v0.1.9)
src/EconData.Core/             # class library: all domain + services
src/EconData.Core/Lake/        # bronze/silver Parquet + DuckDB gold
src/EconData.Cli/              # console app: command dispatch (Program.cs)
tests/EconData.Tests/          # xUnit tests
output/                        # econdata.db, lake/, logs (gitignored)
```

### Core modules (Python → C#)

| Python (former) | C# |
| :--- | :--- |
| `config.py` | `Configuration/AppConfig`, `MetricsCatalog` |
| `extractor.py` | `Extraction/DBnomicsExtractor` (implements `ISeriesSource`) |
| `helpers.py` | `Transform/PeriodParser`, `Transform/UnitNormalizer` |
| `transformer.py` | `Transform/DataTransformer` |
| `entities.py` | `config/entities.json` + `Entities/EntitySeed` |
| `entity_resolver.py` | `Entities/EntityResolver` |
| `db.py` / `db_loader.py` | `Data/SqliteConnectionFactory`, `Data/SchemaInitializer`, `Data/DatabaseLoader` |
| `embeddings.py` / `llm.py` | `Ai/OllamaEmbedder`, `Ai/LLMClient` |
| `vectorizer.py` | `Catalog/Vectorizer`, `Catalog/VectorStore` |
| `rag.py` / `chat.py` | `Chat/ChatOrchestrator`, `Chat/ChatTools`, `Chat/ChatToolValidator`, `Rag/RagAnswerer`, `Cli/Chat/ChatRepl`, `Cli/Chat/ChatCommand` |
| `main.py` | `Cli/Program`, `Cli/PipelineRunner` |
| `logging_config.py` | `Logging/LoggingSetup` (Serilog) |

---

## Idempotent UPSERT strategy

The pipeline is safe for recurring runs; it never wipes existing data and
incrementally upserts via `ON CONFLICT` clauses keyed on stable natural keys.

- `dbe_observations` dedupe key: `(metric_id, entity_id, start_date, end_date)`
  (`DO UPDATE` on `val`, `raw_val`, `unit`, `period_str`, `extracted_at`).
- Registry tables (`dbe_entities`, `dbe_entity_aliases`, `dbe_metrics`,
  `dbe_metric_synonyms`, `dbe_sources`, `dbe_entity_membership`) are upserted from
  `config/entities.json` + `config/metrics.json`.
- Unknown provider codes are registered deterministically (never fabricated names).
- Bulk upserts are chunked to stay under the bound-parameter limit of the target store
  (SQLite **and** DuckDB).
- Gold (DuckDB) uses the same natural keys but binds **positional `?`** parameters and
  declares no `FOREIGN KEY` constraints — DuckDB's `ON CONFLICT` rewrite trips them.
  Stale rows are reconciled with `lake rebuild` (temporary file, validate, swap), never
  with `DELETE`.

Typed `DATE` ranges (`start_date`, `end_date`, stored as ISO-8601 text) replace the
old free-text period column, so chronological ordering is correct.

---

## Extending via JSON (`config/metrics.json`)

All metrics, indicators, providers, and period ranges are defined in
`config/metrics.json`. No code changes are needed to add a metric.

| Field | Purpose |
| :--- | :--- |
| `frequency` | `Annual` / `Quarterly` / `Monthly` |
| `base_unit` | Canonical unit observations are normalized to |
| `unit_normalization` | Safe arithmetic expression over `x` (raw value) → `base_unit` |
| `dimension_key` | Which dimension carries the entity (`country` / `ref_area` / `LOCATION` / `weo-country-code`) |
| `entity_scope` | `country` or `aggregate` |
| `synonyms` | Keywords used to build the metric's semantic document |
| `aggregation` | `sum` / `mean` / `weighted` for aggregates |

### Adding a historical indicator

```json
{
  "metric_code": "fdi_inflows_gdp",
  "metric_name": "Foreign Direct Investment, Net Inflows (% of GDP)",
  "category": "historical",
  "provider": "WB",
  "dataset": "WDI",
  "indicator_code": "BX.KLT.DINV.WD.GD.ZS",
  "unit": "% of GDP",
  "base_unit": "% of GDP",
  "frequency": "Annual",
  "dimension_key": "country",
  "entity_scope": "country",
  "synonyms": ["FDI", "foreign direct investment"],
  "description": "Foreign direct investment are the net inflows of investment to acquire a lasting management interest.",
  "start_year": 1970,
  "end_year": 2026
}
```

### Adding a long-term forecast (OECD 2024–2060)

```json
{
  "metric_code": "forecast_gdp_longterm_oecd",
  "metric_name": "OECD Real GDP Long-Term Forecast (2024-2060)",
  "category": "forecast",
  "provider": "OECD",
  "dataset": "DP_LIVE",
  "indicator_code": "GDPLTFORECAST",
  "unit": "USD Millions (2015 PPP)",
  "base_unit": "USD Millions (2015 PPP)",
  "unit_normalization": "x*1000000",
  "frequency": "Annual",
  "dimension_key": "LOCATION",
  "entity_scope": "country",
  "description": "Long-term projections of real GDP from OECD up to 2060.",
  "start_year": 2024,
  "end_year": 2060
}
```

---

## Entity resolution

Provider dimension codes are resolved to canonical entities in this order:
curated alias cache (`config/entities.json`), live `dbe_entity_aliases` lookup, an
ISO-3 heuristic for WB/OECD/IMF, then deterministic registration of unknowns.
Non-entity codes (`ANNUAL`, `NA`, ...) are refused. No human name is fabricated
from period/series strings.

---

## Serving model (SQLite vs DuckDB gold)

`ECONDATA_SERVING_STORE` selects where **structured analytics** read:

| Value | Behaviour |
| :--- | :--- |
| `auto` (default) | Gold when `output/lake/gold/econdata.duckdb` exists, else SQLite |
| `gold` | Gold only; fail fast if it is missing |
| `sqlite` | SQLite only, even when gold exists |

SQLite always holds the `sqlite-vec` index, so `vectorize` and chat retrieval use it
regardless of mode. Gold holds the structured `dbe_*` tables, views, and derived
aggregates; analytics opens it read-only (`ACCESS_MODE=READ_ONLY`).

## Semantic catalog & vector search

- Documents (types `metric`, `entity`, `entity_metric`) live in `dbe_embeddings`,
  identified by `(doc_key, doc_type)` with a `doc_hash` (SHA-256 of the content).
- `entity_metric` documents carry a **value summary**: observation count and year
  range, the latest value/year, min/max with years, and the `RAG_RECENT_POINTS`
  most recent `year=value` pairs.
- `vectorize` embeds only documents whose hash changed plus orphans; `--reembed`
  recomputes every vector. Re-running reports `embedded=0` (idempotency check).
- Vectors are stored in a sqlite-vec `vec0` table and searched by cosine distance
  (`similarity = 1 - distance`).

## Chat (tool-first, with RAG)

Read-only. `Chat/ChatOrchestrator` classifies each question as `ChatIntent.Objective`
or `ChatIntent.Subjective` and takes one path:

- **Objective** — values, latest/earliest, comparisons, rankings, catalog lists — uses
  a tool-calling loop over `Chat/ChatTools`: six fixed, parameterized queries
  (`get_series`, `get_latest`, `compare_entities`, `rank_entities`, `list_metrics`,
  `list_entities`). `ChatToolValidator` checks names and arguments, the model may call
  up to three tools, and each result returns as text. No raw SQL and no writes.
- **Subjective** — definitions, explanations — uses `Rag/RagAnswerer`: embed the
  question, retrieve top-k documents, answer **from those documents only**, and cite
  the `doc_key`s used. Empty retrieval short-circuits with a canned "no data" answer
  and never calls the LLM.

If intent classification fails, the question falls back to the RAG path.

---

## Troubleshooting

| Symptom | Cause / fix |
| :--- | :--- |
| `Unknown metric code` | The code is not in `config/metrics.json`. |
| Unknown entity codes logged | Registered deterministically; review and add an alias to `config/entities.json` if needed. |
| `no /chat/completions route (HTTP 404)` | `LLM_BASE_URL` must point at the OpenAI-compatible root (usually `.../v1`); a missing Ollama model also returns 404. |
| Chat says "I don't have any catalog data…" | Nothing relevant was retrieved; run `vectorize` if `dbe_embeddings` is empty. |