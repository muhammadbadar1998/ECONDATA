# Issues

## Open

## Follow-ups (non-blocking)

- **Bronze row count is reported as 0.** `TierStatus.Rows` for bronze is 0 because files
  are appended and not scanned. Files/bytes/newest are accurate. If a row count is wanted,
  read row-group counts per file (cheap, but needs a scan).
- **`lake build-gold` rebuilds but never prunes.** It upserts from silver; observations for
  a metric removed upstream stay in gold until a fresh `--gold` run over a rebuilt lake.
  Matching SQLite's append-oriented behaviour is intentional for now.
- **Silver `RowCountsAsync` scans every partition.** Fine at current volumes; revisit if the
  metric count grows substantially.
- **Doctor bootstraps the gold schema implicitly.** `extract --gold` calls `EnsureSchema`,
  which is what makes `doctor` read gold successfully after only a gold write. Running
  `doctor` on a lake with bronze/silver but no gold reports a warn, by design.

## Resolved

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
