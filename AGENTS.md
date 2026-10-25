# AGENTS.md

EconData is a C# / .NET 10 pipeline that pulls macroeconomic indicators from DBnomics,
resolves entities and units, and loads them idempotently into a local SQLite database
and/or a DuckDB gold tier through an optional bronze → silver → gold lake. It includes a
tool-first chat layer (fixed, parameterized queries) with a vector-backed RAG path,
analytics APIs, a CLI, and a localhost Blazor dashboard.

- ALWAYS USE PARALLEL TOOLS WHEN APPLICABLE.
- The default branch in this repo is `main`.
- Prefer automation: execute requested actions without confirmation unless blocked by missing info or safety/irreversibility.
- You may be running in a git worktree. All changes must be made in your current working directory — never modify files in the main repo checkout.
- **Workflow**: read [`.kilo/WORKFLOW.md`](./.kilo/WORKFLOW.md) before planning any change. It defines the six-phase loop (plan → test first → implement → shrink → verify → commit & push), tiered model selection, prompt caching, and the persistent working documents in `.kilo/work/`.

## Architecture Facts

| Path | Purpose |
|---|---|
| `src/EconData.Core/` | Class library: ETL, SQLite, DuckDB lake tiers, analytics, vector catalog, chat orchestration, pipeline runner. All domain logic lives here. |
| `src/EconData.Core/Lake/` | Bronze/silver Parquet tiers and DuckDB gold (`GoldTier`), lake paths and schema bootstrap. |
| `src/EconData.Cli/` | Console entry point (`Program.cs` command dispatch, `Cli/PipelineRunner`). |
| `src/EconData.Web/` | Blazor Server UI + Minimal API. Localhost only, single user, one pipeline job at a time. |
| `tests/EconData.Tests/` | xUnit tests for Core (hermetic temporary SQLite and DuckDB, real `sqlite-vec` extension). |
| `tests/EconData.Web.Tests/` | Web API, job, and builder tests. |
| `config/metrics.json` | Metric catalog. Adding a metric must not require a code change. |
| `config/entities.json` | Canonical entities, aliases, memberships. |
| `sql/sqlite/schema.sql` | SQLite DDL + views (structured store and legacy path). |
| `sql/duckdb/schema.sql` | DuckDB gold DDL + views: sequence-backed ids, fully qualified columns. |
| `native/sqlite-vec/<rid>/` | Vendored `sqlite-vec` loadable extension (v0.1.9). Do not rebuild or replace it. |
| `output/` | `econdata.db`, `lake/` (bronze/silver/gold), logs, dashboards — gitignored. Never commit it. |

Read [`EXPLAINER.md`](./EXPLAINER.md) for module-level architecture and
[`docs/DEV_GUIDE.md`](./docs/DEV_GUIDE.md) before touching Web services, the builder, or
configuration plumbing.

## Build and Dev

- **SDK**: .NET SDK **10.0.4xx** is pinned by `global.json` (`10.0.401`, `rollForward: latestFeature`). Do not change the pin to make a build work; install the SDK instead.
- **Build**: `dotnet build EconData.slnx`
- **Test**: `dotnet test EconData.slnx` (all suites)
- **Single test**: `dotnet test EconData.slnx --filter "FullyQualifiedName~ClassName"`
- **Format**: `dotnet format EconData.slnx --verify-no-changes` (CI-style check); drop `--verify-no-changes` to apply.
- **Init schema**: `dotnet run --project src/EconData.Cli -- init-db`
- **Check schema**: `dotnet run --project src/EconData.Cli -- check-db`
- **Pipeline**: `dotnet run --project src/EconData.Cli -- extract --metrics all --db`
- **Lake**: `lake init` (gold schema), `lake status`, `lake build-gold`, `lake rebuild` (rebuild-and-swap), `lake import-from-sqlite` (one-way backfill)
- **Web UI**: `dotnet run --project src/EconData.Web` → `http://127.0.0.1:5000`
- **Vectorize / chat**: require a running Ollama (`ollama serve`, `nomic-embed-text`); they are not needed for the test suite.

## Quality Checks

Before saying an implementation is ready, run the smallest relevant checks for everything you touched. `TreatWarningsAsErrors` and `EnforceCodeStyleInBuild` are on, so warnings are build failures — do not suppress them to get green.

| Area | Checks |
|---|---|
| Core / CLI / cross-cutting | `dotnet build EconData.slnx`, `dotnet test EconData.slnx --filter "FullyQualifiedName~EconData.Tests"` |
| Web | `dotnet build EconData.slnx`, `dotnet test EconData.slnx --filter "FullyQualifiedName~EconData.Web.Tests"` |
| Formatting | `dotnet format EconData.slnx --verify-no-changes` |
| Schema or data-path change | `dotnet run --project src/EconData.Cli -- check-db` against a scratch `--db-path`; for gold, `lake status` and `doctor` against a scratch `ECONDATA_LAKE_DIR` |
| Analytics or lake change | Tests must cover **both** SQLite and DuckDB (engine parity), e.g. `--filter "FullyQualifiedName~GoldAnalytics|FullyQualifiedName~LakeTier"` |

If a check cannot run (for example no .NET SDK or no Ollama in the environment), state exactly which check was not run — never claim green without running it.

## Conventions

### Data pipeline invariants

- The pipeline is idempotent. Never add destructive `DELETE`/`DROP` paths to the load; use `ON CONFLICT` upserts keyed on stable natural keys (`dbe_observations` dedupes on `metric_id, entity_id, start_date, end_date`).
- Bulk writes stay chunked under SQLite's bound-parameter limit.
- Dates are typed ISO-8601 text (`start_date`, `end_date`); never reintroduce free-text periods.
- Units are normalized to `base_unit` through the declarative `unit_normalization` expression in `config/metrics.json`. No ad-hoc arithmetic in code.
- Unknown provider codes are registered deterministically — never fabricate entity names.
- New indicators ship as `config/metrics.json` entries, not new extractor branches. If a metric needs code, explain why in the plan.
- **Gold is DuckDB**: never add destructive `DELETE`/`DROP` to the load path. Reconcile with
  `lake rebuild` (build into a temporary file, validate, then swap).
- DuckDB has no auto-increment integer primary keys — use `CREATE SEQUENCE` plus
  `DEFAULT nextval(...)`. DuckDB also rejects unqualified columns in joins; qualify every
  column in views and queries.
- `DuckDB.NET` binds **only positional `?`** parameters and has no `AddWithValue`; construct
  `DuckDBParameter` objects. Chunk writes under DuckDB's bound-parameter limit too.
- Do not declare `FOREIGN KEY` constraints in gold: DuckDB's `ON CONFLICT` rewrite trips
  them. Natural `UNIQUE` keys provide the stability.
- Gold dates are VARCHAR; cast before DuckDB date functions
  (`strftime(CAST(start_date AS TIMESTAMP), '%Y')`).

### SQL and vector storage

- Schema changes land in the matching DDL: `sql/sqlite/schema.sql` for SQLite and
  `sql/duckdb/schema.sql` for gold. SQLite changes must work on a fresh `init-db` **and** on
  an existing database (additive `ALTER TABLE` migrations live in `SchemaInitializer`).
- `sqlite-vec` tables are 768-dim cosine and stay in **SQLite**; gold holds structured data
  only. Keep embedding model, dimension, and distance metric consistent between vectorizer
  and query path.
- Load the native extension through the configured `ECONDATA_SQLITE_VEC_PATH`; never
  hardcode a platform RID.
- Analytics opens gold read-only (`ACCESS_MODE=READ_ONLY`); the single Web job is the only
  writer.

### Configuration

- Precedence is `config/appsettings.json` → environment variables → safe defaults. New settings follow the same shape and are documented in `README.md` and `docs/DEV_GUIDE.md`.
- Serving store: `ECONDATA_SERVING_STORE` (`auto | gold | sqlite`) selects where structured
  analytics read. SQLite stays required for `sqlite-vec` and chat; `auto` prefers gold when
  the file exists. `ECONDATA_LAKE_DIR` relocates the lake.
- No secrets in `config/`; `.env` is gitignored.

### Web / Blazor

- The app is localhost-only and single-user. One pipeline job at a time; cancellation must stay responsive.
- Browser assets are vendored for offline use — do not add a CDN reference.
- Long-running work goes through the existing job/runner services, not ad-hoc `Task.Run` in a component.

### Chat and analytics

- Chat is tool-first: `ChatOrchestrator` routes objective questions to six fixed,
  parameterized `ChatTools` and explanatory questions to the vector RAG path. No raw SQL or
  text-to-SQL; arguments are validated before dispatch.
- Analytics reads through `AnalyticsService`, which resolves SQLite or DuckDB gold from
  `ECONDATA_SERVING_STORE`. Endpoints live under `/api/analytics/*`; the JSON contract is
  part of the public API and must stay backward compatible.

## Style Guide

- Keep things in one function unless composable or reusable.
- Avoid unnecessary destructuring; use `obj.a` over `const { a } = obj`.
- Avoid `try`/`catch` where possible; no empty catch blocks. If a catch is needed, handle explicitly or log with context.
- Avoid `else` after a returning `throw`/`return`; prefer early returns.
- Prefer `const` and expression-bodied members; reassignment is the only reason for a mutable local.
- Avoid `null!` and nullable suppression to satisfy the compiler. Model the state instead.
- Prefer `IReadOnlyList<T>`/`IReadOnlyDictionary<K,V>` in public signatures over `List<T>`/`Dictionary<K,V>`.
- Use `async`/`await` all the way; no `.Result`/`.Wait()`. Pass `CancellationToken` through every async path that can be cancelled.
- Default to a single-word name for locals, parameters, and helpers when it is unambiguous in context (`db`, `cfg`, `dir`, `path`, `rows`, `sql`). Avoid introducing camelCase compounds where a short name is clear.
- Respect existing naming: file-scoped namespaces, one public type per file, namespaces mirroring folders (`EconData.Core.<Area>`).
- Keep public APIs minimal; anything not used outside its assembly stays `internal`.

## Testing

- You MUST avoid using mocks as much as possible. Tests MUST exercise real implementation — do not duplicate production logic inside a test.
- Tests are hermetic: temporary SQLite and DuckDB files under a per-test directory, fake HTTP handlers instead of live DBnomics calls, no Ollama, no network.
- The vectorizer test uses the real `sqlite-vec` extension — keep it that way.
- Analytics and lake features must be exercised on **both** SQLite and DuckDB — engine-specific SQL otherwise fails only in production.
- Write the test before the implementation whenever the behavior is testable that way.

## Markdown Tables

Do not pad markdown table cells for column alignment. Use the compact form with single-space-padded content cells and a minimal separator row:

```
| Command | What it runs |
|---|---|
| `dotnet build` | Compiles the solution. |
```

Do **not** right-pad cells to line up columns. Padding makes every content change rewrite the entire table.

## Commits and PRs

- Conventional commit-style messages and PR titles: `type(scope): summary`. Types: `feat`, `fix`, `docs`, `chore`, `refactor`, `test`.
- Scopes match the area touched: `core`, `cli`, `web`, `tests`, `config`, `sql`, `docs`. Omit the scope when spanning several.
- Examples: `feat(core): add quarterly OECD forecast metric`, `fix(web): release job slot on cancel`.
- PR descriptions explain **what** changed, **why**, and the constraints a reviewer cannot infer from the diff. Skip file-by-file inventories and test-result summaries.
- Update `CHANGELOG.md` for user-facing changes.
- Only commit, amend, push, or open PRs when explicitly requested.
