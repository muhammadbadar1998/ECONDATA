# Phase 3 — Medallion Ingestion: Execution Plan (Recovery + Full Implementation)

## Goal

Deliver bronze (Parquet) → silver (Parquet) → gold (DuckDB) ingestion in **one implementation
pass**, with SQLite retained only for vectors. This plan supersedes the previous execution
attempt, which left the working tree broken.

## Tier

**T2.** Escalate to T3 only if the DuckDB port of `DerivedAggregator` or the
`AnalyticsService` swap resists a bounded fix. Escalation trigger: second failed attempt at
the same failing test.

## Verified state on disk (do not trust prior session claims)

Read from the working tree, not from the summary:

| Claimed before | Actual |
|---|---|
| "schema uses sequence ids" | `sql/duckdb/schema.sql` has **9× `INTEGER PRIMARY KEY AUTOINCREMENT`** → fails in DuckDB (probe P4) |
| "build green" | `AppConfig.cs` **does not compile**: `DefaultSqliteVecExtensionPath` deleted but still referenced at lines 30 and 55 |
| — | `AppConfig.DefaultLlmBaseUrl` silently regressed `.../11434/v1` → `.../11434` (unrelated break) |
| "LakePaths working" | `public string? LakeDir { get; }` is an **unassigned** auto-property (constructor only sets `_lakeDir`) |
| "Bronze/Silver implemented" | Both write **`.meta` text files**; no Parquet is ever produced |
| "GoldTier near-complete" | It is **SQLite-only** (uses `SqliteConnectionFactory`), never opens DuckDB, and calls a non-existent `DatabaseLoader.VerifyOrInitSchemaAsync()` |
| schema typos | `REFERENces` (line 93), `GROUP by` (line 259); also creates `dbe_embeddings` in DuckDB, contradicting "SQLite keeps the vector table" |

`git status`: modified `AppConfig.cs` + `EconData.Core.csproj`; untracked `sql/duckdb/`,
`src/EconData.Core/Lake/`. Everything else (incl. `PipelineRunner.cs`) is clean at `539f13f`.

## Decisions (agreed)

| Topic | Decision |
|---|---|
| Damaged work | **Revert to clean `main`**, then re-implement properly |
| Gold writes | **Self-contained DuckDB upsert logic inside `GoldTier`**; `DatabaseLoader` untouched |
| Scope | **All three tiers real in one implementation pass** |
| Libraries | `DuckDB.NET.Data.Full` 1.5.6.1 + `Parquet.Net` 6.1.0 (already restored once, both fine) |
| Bronze payload | One row per `RawSeries`; `dimensions`/`periods`/`values` as JSON columns |
| Build-gold read | DuckDB `read_parquet(...)` over silver |
| Vector catalog | `Vectorizer.FetchCoverage` reads **gold** through `GoldTier` |

### Binding constraints from the verified probe (P1–P10)

- **P4** `INTEGER PRIMARY KEY` is not auto-increment → every table needs
  `CREATE SEQUENCE seq_<t> START 1;` and `<t>_id INTEGER DEFAULT nextval('seq_<t>') PRIMARY KEY`.
- **P5** Unqualified columns after a join are a **hard error** → qualify every column in every
  view, CTE, and subquery.
- **P6** `ON CONFLICT (cols) DO UPDATE SET c = excluded.c` works; also `DO NOTHING`.
- **P7** `RETURNING` works (single and multi-row).
- **P8** Read-only connections: `Data Source=...;ACCESS_MODE=READ_ONLY`.
- **P9** `CHECK (start_date <= end_date)` works; ISO-8601 `VARCHAR` sorts correctly.
- **P10** `DO NOTHING` on an existing key still consumes a sequence value — ids are opaque,
  never assert literal id values (see AC19).

Parquet.Net API (confirmed working, differs from older docs):

```csharp
var field = new DataField<double>("val", true);        // (name, bool? nullable) — NOT DataFieldType
await using var w = await ParquetWriter.CreateAsync(schema, stream);
using var g = w.CreateRowGroup();
await g.WriteAsync<double>(field, new double?[] { 1.5, null });   // pin T for nullable columns
await using var r = await ParquetReader.CreateAsync(stream);
var rg = r.OpenRowGroupReader(0);                       // NOT ReadEntireRowGroupAsync
await rg.ReadAsync<double>(field, buffer);
```

## Step 0 — Recover a clean tree (do this first, before any new code)

1. `git checkout -- src/EconData.Core/Configuration/AppConfig.cs src/EconData.Core/EconData.Core.csproj`
2. Delete `src/EconData.Core/Lake/` and `sql/duckdb/` (untracked placeholders).
3. Re-apply **only** the intended `AppConfig` delta: add `DefaultLakeDir` const, `LakeDir`
   property, and `LakeDir = configuration["ECONDATA_LAKE_DIR"] ?? DefaultLakeDir`. Do **not**
   touch `DefaultSqliteVecExtensionPath` or `DefaultLlmBaseUrl`.
4. Re-add the two package references to `EconData.Core.csproj`.
5. **Gate:** `dotnet build` is 0 warnings and the existing 181 tests still pass. Nothing
   proceeds until this is green.

## Deliverables

1. Clean tree, packages added, `LakeDir` configured, baseline suite green.
   *Done when*: build 0 warnings; 181 tests pass.
2. `Lake/LakePaths.cs` — assign `LakeDir` in the constructor via a real get-only property.
   *Done when*: path tests pass and no `string?` auto-property exists.
3. `sql/duckdb/schema.sql` — sequence ids, fully qualified views, no `dbe_embeddings`.
   *Done when*: bootstrap creates all tables+views on a fresh file; re-bootstrap is a no-op.
4. `Lake/BronzeTier.cs` — real Parquet, append-only one file per run per metric.
   *Done when*: JSON round-trip preserves nulls; two runs produce two files.
5. `Lake/SilverTier.cs` — real Parquet, whole-partition overwrite.
   *Done when*: second write replaces the file; keys unchanged across re-runs.
6. `Lake/GoldTier.cs` — DuckDB bootstrap + self-contained chunked upserts + derived
   aggregation SQL + `OpenReadOnly()`.
   *Done when*: ported `DerivedAggregationTests` pass with unchanged assertions.
7. `PipelineRunner.RunAsync` — four section banners, one call per tier per metric.
   *Done when*: ported pipeline tests pass; AC14 review holds.
8. `AnalyticsService` → DuckDB via `GoldTier.OpenReadOnly()`.
   *Done when*: existing `AnalyticsServiceTests` and `ApiEndpointsTests` pass **unmodified**.
9. `Vectorizer.FetchCoverage` → gold.
   *Done when*: a DuckDB-only load produces documents (AC18).
10. `LakeStatus` + `LakeStatusService`; `lake status` CLI.
    *Done when*: CLI output equals `/api/lake/status` (AC13).
11. Web UI: `Lake.razor`+`.css`, nav, Overview card, Runner checkboxes, Runs tier column.
    *Done when*: Web tests cover API shape, panels + empty state, checkbox post, tiers column.
12. CLI `--bronze/--silver/--gold`, `lake build-gold`, service registration, doctor `lake` check.
    *Done when*: build-gold succeeds with the source stubbed to throw (AC9).
13. Docs + CHANGELOG.
    *Done when*: tiers, commands, UI, migration note documented.
14. Full gate + UAT.

## Target files

| Path | Change |
|---|---|
| `src/EconData.Core/EconData.Core.csproj` | Two `PackageReference`s |
| `src/EconData.Core/Configuration/AppConfig.cs` | `DefaultLakeDir`, `LakeDir`, env binding only |
| `src/EconData.Core/Lake/LakePaths.cs` (new) | Tier paths, run ids, existence probes |
| `src/EconData.Core/Lake/BronzeTier.cs` (new) | Append Parquet from `RawSeries`; read back; status |
| `src/EconData.Core/Lake/SilverTier.cs` (new) | Overwrite partition from `ObservationRecord`; read back; status |
| `src/EconData.Core/Lake/GoldTier.cs` (new) | DuckDB conn, bootstrap, upserts, aggregation, audit, `OpenReadOnly()` |
| `sql/duckdb/schema.sql` (new) | `dbe_*` DDL (sequences) + 3 views, qualified |
| `src/EconData.Core/Cli/PipelineRunner.cs` | Tier narrative; same outcomes/status contract |
| `src/EconData.Core/Analytics/AnalyticsService.cs` | Internals → `GoldTier`; signatures unchanged |
| `src/EconData.Core/Catalog/Vectorizer.cs` | `FetchCoverage` reads gold |
| `src/EconData.Core/Analytics/LakeStatus.cs` (new) | Status records |
| `src/EconData.Core/Analytics/LakeStatusService.cs` (new) | Aggregates tier status |
| `src/EconData.Core/Diagnostics/DiagnosticsService.cs` | `lake` check |
| `src/EconData.Cli/Program.cs` | Tier flags, `lake build-gold`, `lake status` |
| `src/EconData.Web/Program.cs` | Register `GoldTier`, `LakeStatusService`; map `/api/lake/status` |
| `src/EconData.Web/Components/Pages/Lake.razor` + `.razor.css` (new) | Tier panels |
| `src/EconData.Web/Components/Layout/NavMenu.razor` | Lake nav item |
| `src/EconData.Web/Components/Pages/Overview.razor` | Lake card |
| `src/EconData.Web/Components/Pages/Runner.razor` | Tier checkboxes |
| `src/EconData.Web/Components/Pages/Runs.razor` | Tiers column |
| `src/EconData.Web/Services/JobDtos.cs` | `JobRequest` tier flags (snake_case) |
| `src/EconData.Web/Services/PipelineJobExecutor.cs` | Pass tier flags |
| `docs/DEV_GUIDE.md`, `docs/DEPLOY.md`, `README.md`, `CHANGELOG.md` | Document |

**Unchanged:** `DatabaseLoader`, `VectorStore`, `sqlite-vec`, `config/*.json` formats.

## Implementation notes that prevent the previous failure

- **Bronze schema is fixed, not invented.** Columns: `metric_code`, `provider`, `dataset`,
  `indicator_code`, `series_name`, `dimensions_json`, `periods_json`, `values_json`,
  `run_id`, `fetched_at`. Serialize `RawSeries.Periods`/`Values` (`IReadOnlyList<string?>`)
  with `JsonSerializer.Serialize`, and round-trip-test the **nulls** explicitly — this is the
  AC1 test. Never substitute a `.meta` file for Parquet.
- **Provider/dataset come from the catalog**, not the source: `ISeriesSource` has no
  `Provider` member. Use `catalog.Get(code).Provider` / `.Dataset`.
- **`DataTransformer.TransformSeriesList` takes the whole list**, not one series — pass
  `rawSeries` once per metric. It dedupes on `(entity_code, start_date)`.
- **Gold writes are its own code.** Do not call `DatabaseLoader`; open a `DuckDBConnection`,
  run `schema.sql`, and chunk inserts well under DuckDB's parameter cap.
- **Sequence ids are opaque.** Assert on `metric_code`/`entity_code`/natural keys only.
- **Keep `AnalyticsService` return records identical** — do not "improve" `SeriesPoint` etc.

## Acceptance criteria

Carry AC1–AC19 from the original plan unchanged; add:

| # | Criterion | How verified |
|---|---|---|
| AC20 | No placeholder tier output | test asserts the bronze/silver files are readable Parquet with the documented columns |
| AC21 | `AppConfig` regressions absent | `DefaultSqliteVecExtensionPath` present; LLM base URL ends `/v1`; `AppConfigTests` green |
| AC22 | Schema is DuckDB-legal | fresh bootstrap succeeds on a temp `.duckdb`; no `AUTOINCREMENT` token in `sql/duckdb/schema.sql` |

## Risks

| Risk | Mitigation |
|---|---|
| Parquet.NET API shape regresses to guesswork | Use the pinned snippets above; write the round-trip test **before** the writer |
| DuckDB ambiguity errors (P5) | Qualify every column; bootstrap test catches it immediately |
| Contract drift in `/api/*` | Existing Web tests must pass unmodified |
| Two stores | `/api/overview` keeps its `File.Exists` guard; add a lake-absent test |
| Sequence ids vs SQLite `AUTOINCREMENT` | AC19: no literal-id assertions |
| Scope creep in UI | Counts and timestamps only |

## Validation

```bash
dotnet build EconData.slnx
dotnet test EconData.slnx
dotnet format EconData.slnx --verify-no-changes

rm -rf output/lake
dotnet run --project src/EconData.Cli -- extract --metrics gdp_usd --bronze --silver --gold
dotnet run --project src/EconData.Cli -- lake status
dotnet run --project src/EconData.Cli -- lake build-gold
dotnet run --project src/EconData.Cli -- doctor
curl 'http://127.0.0.1:5000/api/lake/status'
curl 'http://127.0.0.1:5000/api/overview'
curl 'http://127.0.0.1:5000/api/series?entity=OECD&metric=gdp_usd'
```

UI: `/lake` panels + empty states; `/` Lake card; `/runner` tier checkboxes; `/runs` Tiers
column; no 500s before the lake exists.

## Out of scope

SQLite→lake migration; dropping SQLite/sqlite-vec; streaming bronze; cloud storage; extra
partitioning; charts; Sprint 3 breadth items.

## Open questions

None.

## Acceptance criteria AC1–AC19 (restated so this plan is self-contained)

The earlier source for these was overwritten; they are reproduced here with the evidence
that currently covers them.

| # | Criterion | Evidence |
|---|---|---|
| AC1 | Bronze preserves every `RawSeries` field losslessly, incl. nulls | `LakeTierTests.Bronze_RoundTripsEveryRawSeriesFieldIncludingNulls` |
| AC2 | Bronze is append-only | `LakeTierTests.Bronze_SecondRunAppendsWithoutRewritingFirst` |
| AC3 | Silver is a pure function of bronze | `LakeTierTests.Silver_OverwritesPartition`, `Silver_IsKeyStableAcrossRuns` |
| AC4 | Gold values match previous behaviour | `LakeTierTests` gold tests, `GoldAnalyticsTests` |
| AC5 | Derived aggregates reproduce values + provenance + coverage | `LakeTierTests.Gold_AggregationHonoursCoverageRuleAndProvenance` |
| AC6 | `extract` twice is idempotent end-to-end | `PipelineTierTests.AllTiers_TwiceIsIdempotent`, `RunningPipelineTwiceIsIdempotent` |
| AC7 | Status/outcome contract preserved | `PipelineRunnerTests`, `PipelineTierTests.AggregationFailure_IsReported…` |
| AC8 | Web API JSON unchanged | existing `ApiEndpointsTests` pass unmodified |
| AC9 | `lake build-gold` rebuilds without network | UAT (logged) + `lake build-gold` code path |
| AC10 | `doctor` reports lake health | `DiagnosticsServiceTests.WarnsWhenLakeAbsent`, `PassesWhenGoldIsPresent` |
| AC11 | sqlite-vec still functions | `VectorizerIntegrationTests` |
| AC12 | UI reflects the lake | `LakeEndpointsTests`, `JobRequestTierFlagsTests`, `GoldAnalyticsTests.Runs_ReturnsAuditRowsWithTiers` |
| AC13 | Dashboard agrees with CLI | `EconData.Web.Tests.LakeStatusParityTests` (calls the live endpoint) |
| AC14 | Runner file is followable | review: four section banners, one call per tier per metric |
| AC15 | Minimal footprint | exactly 4 non-test files under `src/EconData.Core/Lake/` |
| AC16 | Code standards | 0 warnings, no suppressions, format clean |
| AC17 | Full gate green | 221 tests, build 0/0, format clean |
| AC18 | Vector catalog reads gold | `VectorizerGoldSourceTests.CoverageDocumentsComeFromGoldWithoutSqliteData` |
| AC19 | No literal-id assertions | tests key on codes/natural keys |
| AC20 | No placeholder tier output | `LakeTierTests.BronzeAndSilverParquetHaveTheDocumentedColumns` |
| AC21 | `AppConfig` regressions absent | `AppConfigTests.DefaultsHaveSqliteVecPathAndV1LlmBaseUrl` |
| AC22 | Schema is DuckDB-legal | `LakeTierTests.Gold_BootstrapIsIdempotent…`; schema has 0 `AUTOINCREMENT` |

## Done

Completed and committed. Final gate: build 0 warnings / 0 errors, **221 tests**
(182 Core + 39 Web), `dotnet format --verify-no-changes` clean.

- `ef8fcaa` — feat: medallion lake (bronze/silver/gold) ingestion
- `cee6c23` — test: make AC13 parity test actually call the API
- final commit — verification-gap tests (AC10/AC12/AC20), doctor lake-only fix, doc refresh

Known non-blocking follow-ups are tracked in `.kilo/work/ISSUES.md`.

