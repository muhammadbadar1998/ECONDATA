# EconData

A **C# / .NET 10** pipeline for macroeconomic data: it pulls indicators from
[DBnomics](https://db.nomics.world/), resolves entities and units, and loads them
idempotently into a **local SQLite** database or a **DuckDB gold tier** via an
optional bronze → silver → gold lake. On top of that it provides a **tool-calling
chat layer** for exact data questions, a vector-backed RAG path for explanatory
questions, analytics endpoints, a CLI, and a localhost Blazor dashboard with a
pipeline runner.

No database server, Docker, or credentials are required — everything lives under
`output/`: SQLite holds the vector index and remains the default structured store,
and DuckDB holds the gold tier. The `sqlite-vec` extension is vendored.

## Features

- **ETL**: 26 indicators — 24 historical (World Bank WDI) and 2 forecast (World Bank
  GEP, OECD). The WDI metrics cover ~265 economies and aggregates. Typed dates, unit
  normalization, and bulk UPSERTs throughout.
- **Medallion lake** (optional): append-only **bronze** Parquet, typed **silver**
  partitions, and a **DuckDB gold** serving database with idempotent upserts,
  derived aggregates, and run auditing.
- **Chat (tool-first)**: an intent classifier routes objective questions — values,
  latest/earliest, comparisons, rankings, catalog lists — to six fixed, parameterized
  tool calls over gold/SQLite. Explanatory questions use a vector RAG path instead.
  Answers cite the tool results or the document keys used.
- **Vector catalog (RAG)**: embeds metric/entity/series summaries into `sqlite-vec`
  (768-dim cosine) for explanatory questions.
- **Analytics API**: growth (YoY/CAGR), rankings, correlations, and data freshness.
- **CLI**: `init-db`, `check-db`, `backup`, `doctor`, `extract`, `lake`, `vectorize`, `chat`.
- **Web UI**: Overview, Runner with live progress/logs/cancel, Data, Metrics,
  Entities, Lake, Runs, and a drag/resize dashboard builder (ECharts + GridStack).
- **Hermetic tests**: temporary SQLite and DuckDB databases, including real
  `sqlite-vec` kNN and gold-backed analytics tests.

### How chat retrieves data

`ChatOrchestrator` classifies the question first, then takes one of two paths:

| Question | Path | Source of truth |
| :--- | :--- | :--- |
| Values, latest/earliest, comparisons, rankings, catalog lists | **Tool calls** | Gold or SQLite analytics |
| Definitions, explanations, "why"/"how" questions | **RAG** | Embedded metric/entity/series documents |

- **Tool calls are the primary retrieval path for data.** There are six fixed tools —
  `get_series`, `get_latest`, `compare_entities`, `rank_entities`, `list_metrics`,
  `list_entities` — each a parameterized query with validated arguments. The model may
  call up to three in sequence. There is no raw SQL and no text-to-SQL; tool failures
  are returned to the model as text rather than surfaced as a guessed answer.
- **RAG is the secondary path**, used when the question needs prose rather than a
  lookup. It embeds the question, retrieves top-k documents from `sqlite-vec`, and
  answers from those documents, citing the `doc_key`s used.
- If intent classification fails, the question falls back to RAG rather than guessing
  a tool.

## How this was built (AI-first)

This project was developed AI-first: planning, implementation, verification, and
issue tracking were driven by an orchestrated set of AI agents rather than one long
chat session. The defining trait is not that models wrote the code — it is that the
work was **written down, scoped, and verified** at every step.

### The document system

Several small, purpose-built files are kept in the repo instead of one growing
transcript. Each has a single job and is updated at a specific point in the loop:

| File | Holds | Updated |
| :--- | :--- | :--- |
| `AGENTS.md` | Repository facts: layout, commands, invariants, conventions, quality gates | When repo reality changes |
| `.kilo/WORKFLOW.md` | The process itself: tiers, plan requirements, six phases, Definition of Done | Rarely — it governs every change |
| `.kilo/plans/<id>-<phase>.md` | One file per approved plan (see below) | Step 0; amended on approved scope changes |
| `.kilo/work/PLAN.md` | The active plan, in one screen: scope, sequence, open questions | Step 0; archived when the plan ships |
| `.kilo/work/PROGRESS.md` | Phase, checklist, current step, next action, exact gate results | Every step boundary |
| `.kilo/work/ISSUES.md` | Open blockers, known bugs, non-blocking follow-ups, resolved items with test citations | When an issue appears or closes |
| `CHANGELOG.md` | User-facing changes | Before each commit that changes behaviour |
| `docs/DEV_GUIDE.md`, `docs/DEPLOY.md` | Architecture detail and the operations runbook | With the feature they describe |

Rules that make the system work:

- **Write before acting.** The next step is recorded in `PROGRESS.md` before code is
  edited, so a session cut at any point is recoverable.
- **Append, don't rewrite.** Working documents are amended at the bottom; rewriting
  the top invalidates the agent's cached context.
- **One active plan per worktree.** Parallel work gets a separate worktree with its
  own `.kilo/work/` state.
- **Archive on completion.** A shipped plan is summarised under "Done" and
  `PROGRESS.md` is reset for the next task.
- **Resume from the documents, not the repo.** A new session reads `PROGRESS.md`,
  then `ISSUES.md`, then only the files named by the current step — no re-reading the
  whole codebase.

### Deliverables defined before any code

Every plan must contain, and cannot start without:

1. **Numbered deliverables**, each with an explicit *"done when"* criterion.
2. A **target-files table** (`path` → change) so the blast radius is known up front.
3. A **Definition of Done** checklist (build, tests, format, UAT, docs, push).
4. **Executable UAT** — commands to run and the UI states to check.
5. A **tier** and the escalation trigger.
6. Explicit **out-of-scope** items and **open questions**.

Planning pauses for approval. That single gate is what kept scope from drifting: if
something was not a deliverable, it did not get built, and anything found mid-flight
was written into `ISSUES.md` rather than silently absorbed.

### Tiered model orchestration

Capability is matched to task complexity, and escalation happens on evidence — a
second failed attempt at the same problem — not on vibes:

| Tier | Used for | Cost discipline |
| :--- | :--- | :--- |
| **T1 — Mechanical** | Scaffolding, docs, renames, formatting, config entries, fixtures | Default; never escalate for speed |
| **T2 — Standard** | Features to an approved plan, tests, UI wiring, known-cause fixes | Session default |
| **T3 — Deep** | Schema design, SQLite/`sqlite-vec`/DuckDB integration, concurrency, failures without a repro | Brief and deliberate; hand the design back to T2 to execute |

T3 is for **decisions, not typing**. Agent routing follows the same split (`code` =
T2, `debug` = T3, `explore` = T1), and verification is never downgraded because an
edit was "mechanical".

### The six-phase loop

1. **Plan** — deliverables, architecture, test strategy, target files, DoD, UAT;
   pause for approval.
2. **Test first** — write the failing test and confirm it fails for the right reason,
   recording that in `PROGRESS.md`.
3. **Implement** — the minimum production code that makes the tests pass.
4. **Shrink** — re-read the full diff, delete dead code and speculative options, then
   re-run the tests.
5. **Verify** — run the smallest relevant build/test/format/UAT checks for every
   touched project; fix root causes rather than weakening tests or suppressing warnings.
6. **Commit & push** — conventional commit, changelog entry, then summarise what
   changed, the UAT results, the tier used, and the commit SHA.

Each workstream lands in reviewable commits that are independently green: any commit
on `main` builds, tests, and formats cleanly on its own.

### Context discipline

Long multi-agent sessions only stay coherent if the context is managed deliberately:

- Stable context (instructions, plan, conventions) goes first; files and symbols are
  referenced by path instead of re-pasted, and only what changed is re-read.
- Working documents are **appended** to rather than rewritten, so the cached prefix
  stays valid and cheap.
- A fresh session is started when the phase or tier changes — the documents, not the
  transcript, carry the state forward.

### Verification is adversarial

Reports are not taken at face value — including an agent's own summary. Claims are
re-checked with independent builds and test runs, and new tests are **mutation
tested**: the implementation is deliberately broken to prove the test fails.

| What was caught | How it was caught |
| :--- | :--- |
| `strftime` on VARCHAR dates and a duplicated bound parameter broke analytics on DuckDB | Engine-parity tests: the same feature exercised on **both SQLite and DuckDB** |
| `lake import-from-sqlite` read a non-existent column and copied surrogate ids | Writing the missing integration test before trusting the feature |
| `doctor` skipped the lake check when SQLite was absent | An acceptance test for the lake-only deployment |
| Pre-render 500s on Lake/Overview | Page-render tests in the Web suite |
| Parquet and DuckDB API mismatches | A throwaway probe program before any production code |
| Format drift (35 whitespace errors) | Re-running the gate instead of trusting the prior report |
| Lake-status "parity" test that never called the API | Mutation: breaking the endpoint and watching the test still pass |

A bug found this way always ships with a regression test in the same change. A flaky
test is treated as a defect and recorded, not re-run until green.

### Handoff and resumability

Because state lives in committed files, work moves cleanly between models and
sessions: a planner writes the plan, a code agent executes the next deliverable, a
debug agent verifies it, and any of them can stop mid-task without losing context.
The repo — not the conversation — is the source of truth.

The canonical specification of this process lives in
[`.kilo/WORKFLOW.md`](.kilo/WORKFLOW.md), with the active plan summary in
[`.kilo/work/PLAN.md`](.kilo/work/PLAN.md).

The initial build — planning, implementation, tests, and documentation — cost
**under $4** for **207.5M tokens** across **2,401 requests**.

## Requirements

- **.NET SDK 10** (`10.0.4xx`).
- **Ollama** (only for `vectorize` and `chat`):
  ```bash
  ollama serve
  ollama pull nomic-embed-text
  ollama pull qwen2.5:7b-instruct   # any chat-capable model works
  ```

## Quick start

```bash
dotnet build EconData.slnx

# Create the schema (SQLite + vector table)
dotnet run --project src/EconData.Cli -- init-db

# Extract all metrics and load them into SQLite
dotnet run --project src/EconData.Cli -- extract --metrics all --db

# Embed the catalog (requires Ollama)
dotnet run --project src/EconData.Cli -- vectorize

# Ask a question grounded in the catalog
dotnet run --project src/EconData.Cli -- chat --question "What is the latest US GDP?"
```

Common options: `--config PATH`, `--db-path PATH`, `--entities PATH`,
`--vec PATH`, `--output-dir DIR`. `extract` also accepts
`--metrics all|gdp_usd,population`, `--db`, and the lake tier flags
`--bronze --silver --gold`; `vectorize` accepts `--reembed` and
`--recent-points N`; `chat` accepts `--k N --show-sources --system FILE`.

### Medallion lake

Data can be staged through a medallion layout instead of a single SQLite load:

```bash
# Create the gold schema (idempotent)
dotnet run --project src/EconData.Cli -- lake init

# All three tiers, one pass
dotnet run --project src/EconData.Cli -- extract --metrics all --bronze --silver --gold

# Inspect and rebuild the serving tier without the network
dotnet run --project src/EconData.Cli -- lake status
dotnet run --project src/EconData.Cli -- lake rebuild

# Backfill gold from an existing SQLite database (no re-extraction)
dotnet run --project src/EconData.Cli -- lake import-from-sqlite
```

| Tier | Location | Format | Refresh |
| :--- | :--- | :--- | :--- |
| Bronze | `output/lake/bronze/<provider>/<dataset>/<metric>/` | Parquet, raw series | Append-only, one file per run |
| Silver | `output/lake/silver/<metric>.parquet` | Parquet, typed observations | Partition overwritten per run |
| Gold | `output/lake/gold/econdata.duckdb` | DuckDB `dbe_*` tables + views | Idempotent upsert from silver |

A run with no tier flags keeps the original behaviour and writes only SQLite.

**What reads from where.** `ECONDATA_SERVING_STORE` controls where *structured
analytics* read from:

| Value | Behaviour |
| :--- | :--- |
| `auto` (default) | Gold when the file exists, otherwise SQLite |
| `gold` | Gold only; startup fails with an actionable message if it is missing |
| `sqlite` | SQLite only, even when gold exists (rollback escape hatch) |

SQLite is still required: the `sqlite-vec` index lives there, so `vectorize` and
`chat` always use it regardless of serving mode. `ECONDATA_LAKE_DIR` relocates the
lake. To migrate an existing SQLite deployment without re-extraction, run
`lake import-from-sqlite`.

### Operations

```bash
# Read-only readiness check; exits non-zero only on blockers
dotnet run --project src/EconData.Cli -- doctor

# Verify database integrity and the schema/view descriptor
dotnet run --project src/EconData.Cli -- check-db

# Consistent snapshot without stopping the app (no external sqlite3 needed)
dotnet run --project src/EconData.Cli -- backup --to backups/econdata-$(date +%F).db
```

`doctor` checks the runtime, configuration, database presence, SQLite integrity, the
schema/view descriptor, `sqlite-vec`, Ollama/LLM reachability, and lake health.

### Run status

A run is `SUCCESS` only when every requested *known* metric succeeds, `PARTIAL` when at
least one succeeded and one failed (including derived-aggregation failures), and
`FAILED` when none did. A metric absent from the catalog is skipped without changing
the status. `dbe_pipeline_runs.metrics_extracted` counts successful metrics, so a run
that extracts nothing is recorded as `FAILED` — never `SUCCESS`.

## Web UI

```bash
dotnet run --project src/EconData.Web
# http://127.0.0.1:5000
```

| Page | What it does |
| :--- | :--- |
| `/` | Row counts, lake health, and the latest pipeline run |
| `/runner` | Check / Init / Extract / Vectorize / Run All with tier checkboxes, live progress, log tail, cancel |
| `/data` | Series explorer and latest snapshot |
| `/metrics` | Per-metric coverage and year ranges |
| `/entities` | Searchable entity registry |
| `/runs` | Pipeline run history, including which tiers each run wrote |
| `/lake` | Bronze/silver/gold state: rows, files, size, newest, gold tables |
| `/builder` | Drag/resize ECharts widgets; layout saved to `output/dashboards.json` |

The app is localhost-only, single-user, and runs one pipeline job at a time.
Browser assets (Bootstrap, ECharts, GridStack) are vendored for offline use.

### Analytics API

| Endpoint | Returns |
| :--- | :--- |
| `GET /api/analytics/growth?metric=&entity=&kind=yoy\|cagr` | Period, value, and growth % |
| `GET /api/analytics/ranking?metric=&period=&direction=desc\|asc` | Ranked entities with values |
| `GET /api/analytics/correlation?metricA=&metricB=&period=` | Pearson `r` over aligned entities |
| `GET /api/analytics/freshness` | Latest period, row count, and lag per metric |

The same data also backs `/api/overview`, `/api/coverage`, `/api/series`,
`/api/snapshot`, `/api/entities`, `/api/runs`, and `/api/lake/status`.

## Configuration

Settings come from `config/appsettings.json`, then environment variables (which
win), with safe defaults. Main variables:

| Variable | Default |
| :--- | :--- |
| `ECONDATA_SQLITE_PATH` | `./output/econdata.db` |
| `OUTPUT_DIR` | `./output` |
| `ECONDATA_LAKE_DIR` | `./output/lake` |
| `ECONDATA_SERVING_STORE` | `auto` (`auto\|gold\|sqlite`) |
| `CONFIG_PATH` | `./config/metrics.json` |
| `ECONDATA_ENTITIES_PATH` | `./config/entities.json` |
| `DBNOMICS_BASE_URL` | `https://api.db.nomics.world/v22` |
| `ECONDATA_SQLITE_VEC_PATH` | `native/sqlite-vec/<rid>/vec0.{dll,so,dylib}` |
| `OLLAMA_HOST` | `http://127.0.0.1:11434` |
| `OLLAMA_EMBEDDING_MODEL` | `nomic-embed-text` |
| `LLM_BASE_URL` | `http://127.0.0.1:11434/v1` |
| `LLM_MODEL` | `qwen2.5:7b-instruct` |
| `LOG_LEVEL` | `INFO` |

## Project layout

```
src/EconData.Core   ETL, SQLite, DuckDB lake tiers, analytics, vector catalog, pipeline runner
src/EconData.Cli    Command-line entry point
src/EconData.Web    Blazor Server UI + Minimal API
tests/              Core integration tests + Web API/job/builder tests
config/             Metrics and entity seed data
sql/sqlite/         SQLite schema DDL
sql/duckdb/         DuckDB gold schema DDL
native/sqlite-vec/  Vendored vector extension
docs/               Developer guide and deploy notes
```

## Tests

```bash
dotnet test EconData.slnx                    # 260 tests (217 Core + 43 Web)
dotnet format EconData.slnx --verify-no-changes
```

Tests are hermetic (temporary SQLite and DuckDB files, fake services). The vectorizer
test uses the real `sqlite-vec` extension, and the analytics tests run against both
SQLite and DuckDB. No network or Ollama is needed.

## Documentation

- [`docs/DEV_GUIDE.md`](docs/DEV_GUIDE.md) — architecture, Web services, builder,
  configuration details, troubleshooting.
- [`docs/DEPLOY.md`](docs/DEPLOY.md) — operational runbook.
- [`EXPLAINER.md`](EXPLAINER.md) — architecture and codebase explainer.
- [`CHANGELOG.md`](CHANGELOG.md) — release history.

## Derived aggregates

Metrics opt in via an `"aggregation": "sum" | "mean"` key in `config/metrics.json` (default `"none"`). For each aggregate entity in `config/entities.json` memberships, the pipeline computes rows from member observations when coverage is at least 50% of members with data. Derived rows live in `dbe_derived_observations` and surface through `v_observations_all` with `source_kind = "derived"`; raw rows keep `source_kind = "raw"`. Analytics endpoints expose `source_kind` on series/snapshot points and separate derived counts in coverage.
