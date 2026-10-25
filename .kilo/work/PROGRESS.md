# Progress

## Next — Sprint 3 close-out (in progress)

Plan of record: `.kilo/plans/1792630353409-sprint3-open-items-serving-store-and-runbook.md`

Done:
- `ECONDATA_SERVING_STORE` (`auto | gold | sqlite`) for structured analytics only
  (`AnalyticsService`), strict `gold` fails fast; Web wiring; CLI/Web Vectorizer now
  receives a `GoldTier` for document building. Commit `4e6d7cc`.
- Analytics API (`growth`, `ranking`, `correlation`, `freshness`) with Web tests.
  Commit `b5f7128`.
- Format drift fixed (`6aa2976`); README/AI-first/CHANGELOG refreshed (`2b2d40f`).

Remaining:
- Resolve `AnalyticsService` before `app.Run()` so strict `gold` fails at startup.
- Analytics UI: Builder widgets (`growth`, `ranking`, `correlation`, `freshness`),
  Overview freshness card, Metrics derived-share/latest-period.
- `lake import-from-sqlite --dry-run` + source-vs-gold parity counts.
- `--write-silver` backfill so `lake build-gold` works after an import.
- Backfill runbook in `docs/DEPLOY.md`.
- Reproduce and fix the intermittent Core test failure recorded in `ISSUES.md`.

## Current state

- Gate: build 0 warnings / 0 errors; **260 tests** (217 Core + 43 Web);
  `dotnet format --verify-no-changes` clean.
- SQLite stays required for `sqlite-vec` and chat; gold serves structured analytics.

## History (archived)

- Phase 3 medallion ingestion: complete and pushed — `ef8fcaa`, `cee6c23`, `e4250f6`,
  `f7976d9`, `a6a61d7`.
- Sprint 3 implementation: `5b72255`, `7b4cdad`, `d10b46b`.
- Verification fixes: `4b7a85f`, `6aa2976`.
- Docs: `ae3e8f5`, `2b2d40f`.
