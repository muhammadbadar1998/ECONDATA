# Progress

- Phase: Phase 3 — Medallion ingestion
- Status: **COMPLETE**, committed and pushed.
- Commits: `ef8fcaa` (feat: medallion lake), `cee6c23` (test: real AC13 parity),
  `e4250f6` (verification-gap tests + doctor lake-only fix), `f7976d9`/`a6a61d7` (docs).

## Gate

| Check | Result |
|---|---|
| `dotnet build EconData.slnx` | 0 warnings, 0 errors |
| `dotnet test EconData.slnx` | **221 passed** (182 Core + 39 Web) |
| `dotnet format --verify-no-changes` | clean |

## UAT (live DBnomics run, scratch lake)

- `extract --metrics gdp_usd --bronze --silver --gold`: 266 series → bronze Parquet,
  13,980 silver observations, 13,980 gold rows, 192 derived aggregate rows.
- Second identical run: silver/gold unchanged, bronze 2 files (append-only), audit 2 rows.
- `lake build-gold`: rebuilt 13,980 observations from silver with no network.
- `lake status`: all three tiers + 8 gold tables.
- `doctor`: `PASS lake tiers: bronze, silver, gold`; 0 blockers.
- Empty lake: all tiers report `not built yet`, no failures.

## Verification gaps closed (final pass)

- **AC10** doctor lake check now has automated tests (`WarnsWhenLakeAbsent`,
  `PassesWhenGoldIsPresent`). Found and fixed a real defect: `doctor` returned early when
  SQLite was absent, skipping the lake check — the lake-only deployment case.
- **AC12** Runner tier flags proven to reach `JobRequest` over HTTP
  (`JobRequestTierFlagsTests`); Runs `Tiers` data path asserted in
  `GoldAnalyticsTests.Runs_ReturnsAuditRowsWithTiers`.
- **AC13** real parity test hits `/api/lake/status` and compares to
  `LakeStatusService.GetAsync`; mutation-proven (broken endpoint fails it).
- **AC20** bronze/silver Parquet column sets asserted explicitly
  (`BronzeAndSilverParquetHaveTheDocumentedColumns`).
- **AC21** `AppConfigTests.DefaultsHaveSqliteVecPathAndV1LlmBaseUrl` guards the
  sqlite-vec path and `/v1` LLM base URL; mutation-proven.
- Core test file renamed `LakeStatusServiceTests.cs` to match its class.

## Known non-blocking follow-ups

See `ISSUES.md`: bronze `Rows=0` in status; `build-gold` never prunes; silver
`RowCountsAsync` scans all partitions; doctor relies on gold schema bootstrap.
