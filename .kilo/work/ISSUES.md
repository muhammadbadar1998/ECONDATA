# Issues

## Open

- **`ServingStore` is defined but never consumed.** `AppConfig.ServingStore` /
  `ECONDATA_SERVING_STORE` exists, but `AnalyticsService` and `Vectorizer` still use the
  hard-coded `auto` behaviour (gold if present, else SQLite). Strict `gold` (fail if gold
  is missing) and forced `sqlite` modes from the Sprint 3 plan (deliverable B1) are **not**
  implemented. The config value is currently inert.
- **`docs/DEPLOY.md` has no backfill runbook.** `lake import-from-sqlite` now works, but the
  documented migration sequence (backfill → verify → switch serving → roll back) is missing.

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
