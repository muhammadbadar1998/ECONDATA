# Plan — Phase 3 Medallion Ingestion (ARCHIVED)

**Status: DONE.** This file was the execution copy for an intermediate attempt. It is kept
only for history; the source of record is
`.kilo/plans/1792630353407-medallion-ingestion.md`, and the shipped behaviour is defined by
the code and tests.

## Correction to the earlier execution copy

An earlier version of this file claimed `lake build-gold` reads silver through DuckDB
`read_parquet(...)`. That was **not** how it was built. The shipped path is:

- `SilverTier.ReadAsync(metric)` reads the silver Parquet with `Parquet.Net`.
- `GoldTier.UpsertObservations(...)` writes those rows into DuckDB with chunked,
  positional-parameter `ON CONFLICT` upserts.

## What shipped

- Bronze: append-only raw Parquet per provider/dataset/metric/run (`BronzeTier`).
- Silver: per-metric Parquet partition, overwritten per run (`SilverTier`).
- Gold: DuckDB `dbe_*` tables + views; bootstrap, idempotent upserts, derived aggregation,
  run audit, `OpenReadOnly()` (`GoldTier`).
- `PipelineRunner` four-banner narrative + `PipelineTiers` (`--bronze/--silver/--gold`).
- `lake status`, `lake build-gold`, doctor `lake` check.
- `AnalyticsService` and `Vectorizer` read gold, falling back to SQLite.
- Lake Web page, Overview card, Runner checkboxes, Runs Tiers column, `/api/lake/status`.

## Verification

See `PROGRESS.md` (gate: 221 tests, 0 warnings, format clean) and `ISSUES.md`
(known non-blocking follow-ups).
