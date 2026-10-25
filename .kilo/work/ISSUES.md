# Issues

## Open

- **`lake build-gold` rebuilds but never prunes.** It upserts from silver; observations for a metric removed upstream stay in gold until a fresh `--gold` run over a rebuilt lake. Matching SQLite's append-oriented behaviour is intentional for now.
- **Silver `RowCountsAsync` scans every partition.** Fine at current volumes; revisit if the metric count grows substantially.
- **Doctor bootstraps the gold schema implicitly.** `extract --gold` calls `EnsureSchema`, which is what makes `doctor` read gold successfully after only a gold write. Running `doctor` on a lake with bronze/silver but no gold reports a warn, by design.
- **No automated coverage of startup wiring.** The strict-`gold` rule is asserted at `AnalyticsService` (unit) and verified manually by starting the Web host; there is no host-level test because driving `Program`'s configuration requires process environment variables, which leak across parallel test classes.
- **CI workflow is unverified.** `.github/workflows/ci.yml` exists and its YAML parses, but it has never run because pushing was not requested. Confirm on the first push (ledger row 16 of the active plan).

## Resolved

- **CLI import path was untestable.** `Program` is `internal` with a `private Main`, so the
  `import-from-sqlite` wiring had no tests. Extracted to `LakeImportCommand.ExecuteAsync`
  with injected writers; covered by `LakeImportCommandTests` (5 tests). Mutation-proven:
  making the command ignore `dryRun` fails `DryRun_ReturnsZeroAndLeavesGoldEmpty`.
- **No CI and no single gate command.** Added `scripts/verify.sh` (build + test + format,
  optional `--uat` backfill check) and `.github/workflows/ci.yml`. Observed locally:
  `verify: ok`, 276 tests. **CI itself is unverified** — row 16 of the active plan is
  `not run: requires a push`.
- **Documented a command that does not exist.** `CHANGELOG.md` claimed
  `lake build-gold --rebuild`; the command is `lake rebuild`. Fixed, and
  `DocCommandTests.DocumentedLakeSubcommands_AllExist` now fails if a shipped doc names an
  unknown `lake <subcommand>` (mutation-proven with `lake frobnicate`).
- **Guidance described a non-existent config path.** `.kilo/WORKFLOW.md` pointed at
  `.kilo/agent/*.md`; replaced with the real per-call `model`/`variant` mechanism.
- **Verification rules were honour-system only.** Added a `Verification Protocol` (risk
  tiers 0–4 plus mutation and dry-run recipes) to `AGENTS.md` and `WORKFLOW.md`, a mandatory
  verification ledger to Plan Requirements, and `scripts/check-ledger.sh` which fails a plan
  marked complete while it still has `TODO` rows.
- **Serving-default decision documented.** Sprint 3 approved `ECONDATA_SERVING_STORE=gold` (SQLite only for vectors). The shipped default is `auto`, documented in `README.md` and `docs/DEV_GUIDE.md`. A fresh install and the Web test fixture work before gold exists; `gold` is recommended for production.
- **Startup validation for strict `gold`.** `AnalyticsService` is resolved before `app.Run()`; strict `gold` with no gold file aborts the host with the actionable message. Covered by `StartupValidationTests.StrictGold_WithoutGoldFile_ThrowsBeforeServing`.
- **Analytics UI: Builder widgets.** `growth`, `ranking`, `correlation`, `freshness` have type-list entries, config inputs, and `Build*OptionAsync` renderers wired into the dispatch switch. Overview freshness card is guarded so a failed fetch cannot break the page. Metrics page gained `Derived` and `Latest period`. DTOs are guarded by `AnalyticsDtoJsonTests`.
- **`--dry-run` wrote to gold.** Regression found in validation: the import ran before the `dryRun` check. The guard now returns before any write, reporting source counts from SQLite and gold counts from gold. Covered by `ImportFromSqliteTests.DryRun_WritesNothingToGold`.
- **Parity summary was self-referential.** `SourcesGold` held the observation count, source counts for metrics/sources were read from gold, and `MismatchKeys` was never populated, making the exit-1 branch unreachable. Now compares SQLite against gold on natural keys and reports capped mismatches. Covered by `ImportFromSqliteTests.Import_ReportsSourceVersusGoldParity`.
- **`--write-silver` was a no-op** that printed a false promise (`lake build-gold` then failed with "No silver partitions found."). Now materialises partitions through `WriteSilverFromSqliteAsync`. Covered by `ImportFromSqliteTests.WriteSilver_ThenBuildGold_ReproducesObservations` and end-to-end UAT.
- **Intermittent Core test failure.** `SqliteConnection.ClearAllPools()` removed from `Dispose()` in the remaining test classes; connection strings are unique per test, so pooling per file is harmless. Core suite green over three consecutive full runs.
- **`ECONDATA_SERVING_STORE` implemented for structured analytics only.** Commit `4e6d7cc`.
- **Web/CLI Vectorizer built documents without gold.** Commit `4e6d7cc`.
- **Formatting drift in `AnalyticsService`.** Fixed whitespace-only in `6aa2976`.
- **Analytics SQL failed on DuckDB.** Freshness lag computed in C#; duplicate named placeholder removed. Covered by `GoldAnalyticsFeatureTests`.
- **`lake import-from-sqlite` was broken and untested.** Rewritten to map ids through natural keys. Covered by `ImportFromSqliteTests`.
- **Bronze row count** is now reported in `lake status`.
- **Vectorizer crashes on a gold read error** — catch widened to `Exception when is not OperationCanceledException`. Covered by `VectorizerGoldSourceTests`.
- **`/api/overview` 503s in a lake-only deployment** — guard now also accepts `analytics.UsesGold`. Covered by `OverviewLakeOnlyTests`.
- DuckDB FK constraints versus `ON CONFLICT` upserts — FKs removed from gold DDL.
- Legacy runs writing gold — `PipelineTiers.None` default.
- Injected aggregator ignored on the gold path — `useGold` flag.
- SQLite missing `tiers_written` — column added + additive migration.
- Lake/Overview prerender 500 — fetch moved to `OnAfterRenderAsync`.
- DuckDB `strftime` on VARCHAR dates — explicit `CAST(... AS TIMESTAMP)`.

## Deferred to Sprint 4 — document ingestion

Design already agreed in discussion: raw files + chunk manifests archived in the lake
(bronze/silver), retrieval index (chunk text, FTS5, 768-dim `sqlite-vec`) in SQLite, gold
stays structured-only. Includes deterministic versioned chunking, citation offsets,
hybrid BM25+vector retrieval, and prompt-injection defenses. Not part of this plan.

## Out of scope

- Flipping the serving default to `gold`.
- Bronze synthesis during backfill.
- A standalone `lake verify` command (parity lives in the import summary).
- Gold pruning / reconciliation (`lake build-gold` remains upsert-only).
- Moving embeddings off `sqlite-vec`.