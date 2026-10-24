# Issues

## Open

Plan of record: `.kilo/plans/1792630353409-sprint3-open-items-serving-store-and-runbook.md`.

- **Serving default differs from the approved decision.** Sprint 3 approved
  `ECONDATA_SERVING_STORE=gold` (SQLite only for vectors). The shipped default is
  `auto`, chosen so a fresh install and the Web test fixture work before a gold file
  exists. Decide: document `auto` as the dev default and `gold` for production, or
  flip the default together with a gold-aware Web fixture and a first-run path
  (`lake init` + `extract --gold`). The README currently documents the shipped
  behaviour (`auto`).
- **Backfill ergonomics and runbook missing.** Serving-mode wiring is done, but
  `lake import-from-sqlite` still has no `--dry-run` and prints no source-vs-gold
  parity counts, and `docs/DEPLOY.md` has no migration sequence
  (backup → `lake init` → dry-run → import → verify → switch serving mode →
  rollback to `sqlite`).
- **Intermittent Core test failure (unidentified).** One full run reported
  `Failed: 1, Passed: 216`; the same suite passed in isolation and in three
  consecutive full runs, and the failing test name was lost when output was
  overwritten. Suspected shared temp-SQLite/pool interaction
  (`SqliteConnection.ClearAllPools` running during another test). Needs reproduction
  with `--logger "console;verbosity=detailed"` and a fix: a flaky suite is a defect,
  not noise.

## Follow-ups (non-blocking)

- **`lake build-gold` rebuilds but never prunes.** It upserts from silver; observations for
  a metric removed upstream stay in gold until a fresh `--gold` run over a rebuilt lake.
  Matching SQLite's append-oriented behaviour is intentional for now.
- **Silver `RowCountsAsync` scans every partition.** Fine at current volumes; revisit if the
  metric count grows substantially.
- **Doctor bootstraps the gold schema implicitly.** `extract --gold` calls `EnsureSchema`,
  which is what makes `doctor` read gold successfully after only a gold write. Running
  `doctor` on a lake with bronze/silver but no gold reports a warn, by design.

## Resolved

- **`ECONDATA_SERVING_STORE` implemented for structured analytics only.** `ServingStore`
  (`auto | gold | sqlite`) is parsed by `AppConfig`, consumed by `AnalyticsService`, and
  wired through the Web host; strict `gold` fails fast with an actionable message.
  Covered by `ServingStoreTests` and `ServingModeTests`. Commit `4e6d7cc`.
- **Web/CLI Vectorizer built documents without gold.** The Web `IDocumentSearcher` and the
  CLI `vectorize`/`chat` paths now receive a `GoldTier` for document building; vectors
  remain in SQLite. Commit `4e6d7cc`.
- **Formatting drift in `AnalyticsService`.** 35 whitespace errors (class body indented
  four extra spaces) were found by re-running the gate; fixed whitespace-only in `6aa2976`.
- **Analytics SQL failed on DuckDB** (found by adding gold-backed tests):
  `GetFreshness` used `strftime` on VARCHAR dates with SQLite argument order, and
  `GetCorrelation` bound `@period` twice while the DuckDB adapter rewrites named
  parameters to positional `?`. Freshness now computes the lag in C#; the duplicate
  placeholder was removed. Covered by `GoldAnalyticsFeatureTests` (4 tests on DuckDB).
- **`lake import-from-sqlite` was broken and untested.** It selected a non-existent
  `metric_code` column from `dbe_observations`, read integer `metric_id` as a string, and
  copied SQLite surrogate ids into DuckDB. Rewritten to map every id through natural keys
  (metric/entity/source) and to leave derived aggregates to the normal pipeline. Covered by
  `ImportFromSqliteTests` (counts, value correctness, idempotency).
- **Bronze row count** is now reported in `lake status` (previously always 0).
- **Vectorizer crashes on a gold read error** — widened the catch in `Vectorizer.BuildAllDocuments`
  from `SqliteException` to `Exception when is not OperationCanceledException`, so `DuckDBException`
  (corrupt/half-written gold) no longer aborts vectorization. Covered by
  `VectorizerGoldSourceTests.CorruptGold_DegradesInsteadOfThrowing`.
- **`/api/overview` 503s in a lake-only deployment** — changed the guard from
  `!File.Exists(db.DatabasePath)` to `!File.Exists(db.DatabasePath) && !analytics.UsesGold`.
  Covered by `OverviewLakeOnlyTests.GetOverview_GoldPresentSqliteAbsent_Returns200`.
- DuckDB FK constraints versus `ON CONFLICT` upserts — FKs removed from gold DDL.
- Legacy runs writing gold — `PipelineTiers.None` default.
- Injected aggregator ignored on the gold path — `useGold` flag.
- SQLite missing `tiers_written` — column added + additive migration.
- Lake/Overview prerender 500 — fetch moved to `OnAfterRenderAsync`.
- DuckDB `strftime` on VARCHAR dates — explicit `CAST(... AS TIMESTAMP)`.
