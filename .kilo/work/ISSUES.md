# Issues

## Open

Plan of record: `.kilo/plans/1792630353409-sprint3-open-items-serving-store-and-runbook.md`.

- **Implement `ECONDATA_SERVING_STORE` for structured analytics only.**
  `AppConfig.ServingStore` exists but nothing reads it; `AnalyticsService` always uses
  gold-if-present-else-SQLite. Add `auto | gold | sqlite`, with strict `gold` failing fast
  when the gold file is missing. **`Vectorizer` and chat are intentionally out of scope:**
  the `sqlite-vec` index — and future ingested document chunks — live in SQLite, so chat
  must keep using SQLite under every serving mode.
- **Web chat builds documents without gold.** `Web/Program.cs:85-91` constructs the
  `IDocumentSearcher` `Vectorizer` with no `GoldTier`, so a lake-only deployment builds
  coverage documents from empty SQLite tables. CLI `vectorize`/`chat`
  (`Cli/Program.cs:357, 376`) have the same gap. Pass a gold tier for *document building
  only*; vectors stay in SQLite.
- **Backfill ergonomics and runbook missing.** `lake import-from-sqlite` has no
  `--dry-run` and prints no source-vs-gold parity counts; `docs/DEPLOY.md` has no migration
  sequence (backup → `lake init` → dry-run → import → verify → switch serving mode →
  rollback to `sqlite`).

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
