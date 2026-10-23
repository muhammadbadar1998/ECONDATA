# Sprint 3 — Lake Hardening, Serving Migration, Data Breadth, Analytics

## Goal

Build on the completed medallion lake (Phase 3, `66b58c7`) across four workstreams:

| WS | Focus | Why now |
|---|---|---|
| **A** | Lake hardening | Close the four tracked Phase 3 follow-ups |
| **B** | Lake-only serving + SQLite backfill | Gold is the serving store; SQLite keeps only vectors |
| **C** | Data breadth | More metrics and aggregate groups, config-only |
| **D** | Analytics + dashboard features | Turn the lake into user-facing insight |

## Tier

**T2** by default. Escalate to **T3** for three decisions only:
- `lake build-gold --rebuild` atomic swap (A3)
- `lake import-from-sqlite` natural-key mapping (B2)
- strict serving mode semantics (B1)

Escalation trigger: second failed attempt at the same failing test.

## Non-negotiable constraints (from AGENTS.md)

- **No destructive `DELETE`/`DROP` in the load path.** A3 therefore rebuilds into a new
  DuckDB file and swaps, rather than pruning in place.
- **sqlite-vec stays.** SQeries continues to hold `dbe_embeddings` + `dbe_embeddings_vec`.
  "Lake-only serving" means *data* reads come from gold, not that SQLite is removed.
- **Config, not code, for indicators.** New metrics are `config/metrics.json` entries; no
  extractor branches.
- **Idempotent upserts on natural keys**; chunked writes; typed ISO dates.
- **Existing `/api/*` JSON stays compatible**; existing tests pass unmodified where the
  behaviour is unchanged.

---

## Workstream A — Lake hardening

### A1. Real bronze row counts
`TierStatus.Rows` for bronze is currently hard-coded `0`. Scan Parquet row-group metadata
(cheap: no column decode) across the metric's bronze files.

- `BronzeTier.RowCountsAsync(metric)` / `TotalRowsAsync()` summing `RowGroup.RowCount`.
- `LakeStatusService.Bronze()` uses it.
- *Done when* `LakeTierTests` asserts bronze `Rows == 3` for the fixture, and
  `lake status` shows non-zero bronze rows.

### A2. Cheaper silver row counts
`SilverTier.RowCountsAsync` opens every partition each call. Write a sidecar
`<metric>.count` (JSON: `{rows, bytes, written_at}`) in `WriteAsync`, read it in
`RowCountsAsync`, fall back to a Parquet scan when the sidecar is missing or stale.

- *Done when* a second status call performs no Parquet open (assert via a test seam or
  sidecar mtime), counts remain correct, and a missing sidecar still yields correct counts.

### A3. Safe gold reconciliation without DELETE
Replace "never prunes" with an explicit, non-destructive rebuild:

```
lake build-gold --rebuild
```

1. Build `gold/econdata.rebuild.duckdb` from silver (same code path as today).
2. Validate: open read-only, row counts per metric match silver.
3. Swap atomically (`File.Move` with overwrite) after the Web job slot is free.

- *Done when* a test deletes one silver row, rebuilds, and gold reflects the reduced count;
  no `DELETE`/`DROP` is introduced in `GoldTier`; the old file is only replaced after
  validation succeeds.

### A4. `lake init` + honest doctor
Add `lake init` (idempotent `EnsureSchema`) and make the doctor `lake` check say exactly
what is wrong (absent / present-uninitialized / readable) with the command to run.

- *Done when* a fresh directory → `lake init` → `doctor` reports the lake check `Pass`;
  `doctor` on a gold file without the schema reports an actionable `Warn`, not an exception.

### A5. Doctor lake check on both paths (already fixed in `e4250f6`)
Regression tests exist. Keep them; extend to the `lake init` guidance.

---

## Workstream B — Lake-only serving + SQLite backfill

### B1. Serving-store config
Add `ServingStore` (`auto|gold|sqlite`, env `ECONDATA_SERVING_STORE`, default `auto`).

- `auto` (today): gold when the file exists, else SQLite.
- `gold`: require gold; if absent, fail with a clear error — **no silent fallback**.
- `sqlite`: force SQLite (escape hatch for legacy hosts).
- `AnalyticsService` and `Vectorizer` both honour it via one helper.

- *Done when* three tests cover each mode; `gold` + missing file throws with a message that
  names `lake build-gold`; existing `auto` tests pass unmodified.

### B2. `lake import-from-sqlite` (one-way backfill)
Backfill gold from an existing SQLite `dbe_*` database so deployments do not have to
re-extract:

- Copy entities, aliases, memberships, metrics, synonyms, sources, observations.
- Map ids through **natural keys** (`entity_code`, `metric_code`, provider/dataset/indicator),
  never by copying surrogate ids.
- Recompute derived aggregates in gold (not copied).
- Idempotent; `--dry-run` prints what would change.

- *Done when* a SQLite DB and its gold backfill report identical entity/metric/observation
  counts; running twice changes nothing; `--dry-run` writes nothing.

### B3. Optional silver backfill
`lake import-from-sqlite --write-silver` also materialises silver partitions from
`dbe_observations` so `lake build-gold` works afterwards.

- *Done when* after backfill, `lake build-gold` succeeds with the network stubbed to throw.

### B4. Docs
Migration runbook in `docs/DEPLOY.md`: backfill, verify, switch `ECONDATA_SERVING_STORE`,
roll back to `sqlite`. State plainly that SQLite remains for sqlite-vec.

---

## Workstream C — Data breadth (config-only)

### C1. Add ~15 historical metrics
Candidate WDI indicators — **every code must be verified against DBnomics before merge**
(a failed lookup drops the entry, it does not ship broken):

| Code | Indicator | Unit | Aggregation |
|---|---|---|---|
| gdp_per_capita | NY.GDP.PCAP.CD | Current US$ | none |
| gdp_real | NY.GDP.MKTP.KD | Constant 2015 US$ | sum |
| gdp_ppp | NY.GDP.MKTP.PP.CD | Int'l $ | sum |
| gross_capital_formation_gdp | NE.GDI.TOTL.ZS | % of GDP | mean |
| govt_consumption_gdp | NE.CON.GOVT.ZS | % of GDP | mean |
| exports_gdp | NE.EXP.GNFS.ZS | % of GDP | mean |
| imports_gdp | NE.IMP.GNFS.ZS | % of GDP | mean |
| fdi_gdp | BX.KLT.DINV.WD.GD.ZS | % of GDP | mean |
| remittances_gdp | BX.TRF.PWKR.DT.GD.ZS | % of GDP | mean |
| military_gdp | MS.MIL.XPND.GD.ZS | % of GDP | mean |
| health_gdp | SH.XPD.CHEX.GD.ZS | % of GDP | mean |
| life_expectancy | SP.DYN.LE00.IN | years | mean |
| urban_population_pct | SP.URB.TOTL.IN.ZS | % | mean |
| internet_users_pct | IT.NET.USER.ZS | % | mean |
| poverty_headcount | SI.POV.DDAY | % | mean |

Rules: percentage/index/per-capita metrics use `mean` (never `sum`); absolute totals use
`sum`; no artificial normalisation.

- *Done when* each new metric extracts at least one observation for one known country, the
  catalog validation test passes, and the count in `/api/coverage` reflects the new total.

### C2. Aggregate entity groups
Add config-only aggregate entities with memberships where the provider does not already
supply the aggregate: **EU, Euro Area, G7, G20, BRICS, ASEAN**.

- Each needs a `config/entities.json` entry plus membership rows pointing at existing
  entity codes; aliases only for real provider codes.
- *Done when* the seed-integrity test rejects unknown members, and derived aggregation
  produces rows for at least one aggregate group.

### C3. Vector catalog parity
New metrics must appear as `metric` + `entity_metric` documents with no code change.

- *Done when* document counts scale with the catalog and a smoke test asserts a new metric's
  `doc_key` is present.

---

## Workstream D — Analytics and dashboard features

### D1. Analytics methods (both engines via the existing `AnalyticsConnection` seam)
- `GetGrowth(entity, metric, kind: yoy|cagr, periods)` → `GrowthPoint(Period, Value, GrowthPct)`.
- `GetRanking(metric, period, direction, limit, excludeAggregates)` → `RankingRow`.
- `GetCorrelation(metricA, metricB, period, excludeAggregates)` → `CorrelationResult(N, R)`
  (Pearson over aligned entity/period pairs; computed in C# from one portable SQL query).
- `GetFreshness()` → per metric: latest `start_date`, lag in years, row count, `source_kind`.

- *Done when* each has unit tests against real DuckDB **and** SQLite (the `auto` fallback),
  with no engine-specific SQL proliferation.

### D2. Endpoints
`GET /api/analytics/growth`, `/ranking`, `/correlation`, `/freshness`, returning
snake_case JSON consistent with existing endpoints.

- *Done when* Web tests cover shape, validation (bad metric/period → 400) and empty data.

### D3. Builder widgets
Add widget types: `growth` (line), `ranking` (bar), `correlation` (scatter), `freshness`
(table). They reuse the vendored ECharts bundle — **no CDN**.

- *Done when* widget DTO round-trips through the layout store, the Builder palette offers
  them, and page render tests pass.

### D4. Existing pages
- **Overview**: add a Data freshness card (latest period per metric, worst lag).
- **Metrics**: show derived share and latest period already present in coverage.

- *Done when* both pages render with an empty lake and with populated data.

---

## Deliverables

| # | Deliverable | Done when |
|---|---|---|
| 1 | Bronze row counts | `LakeTierTests` asserts non-zero bronze rows; `lake status` shows them |
| 2 | Silver count sidecar | second status call avoids a Parquet scan; correct with/without sidecar |
| 3 | `lake build-gold --rebuild` | prune-by-rebuild test passes; no DELETE/DROP added |
| 4 | `lake init` + doctor guidance | fresh dir → init → doctor Pass; uninitialized gold → actionable Warn |
| 5 | `ServingStore` config | auto/gold/sqlite tests; strict gold errors clearly |
| 6 | `lake import-from-sqlite` | counts match SQLite; idempotent; `--dry-run` writes nothing |
| 7 | Silver backfill | `build-gold` succeeds after backfill with source stubbed to throw |
| 8 | Migration runbook | DEPLOY documents backfill, switch and rollback |
| 9 | ~15 new metrics | verified codes extract; catalog test green; coverage count updated |
| 10 | 6 aggregate groups | seed-integrity test green; derived rows produced |
| 11 | Vector parity for new metrics | new metric appears as metric + entity_metric docs |
| 12 | Analytics methods (4) | unit tests on DuckDB and SQLite |
| 13 | Analytics endpoints (4) | Web tests: shape, 400s, empty |
| 14 | Builder widgets (4) | DTO/round-trip + palette + render tests |
| 15 | Overview/Metrics additions | render with empty and populated lake |
| 16 | Docs + CHANGELOG + plan/progress/issues | updated, tier recorded |
| 17 | Full gate + UAT + push | all checks green |

## Target files

| Path | Change |
|---|---|
| `src/EconData.Core/Lake/BronzeTier.cs` | row-group counting |
| `src/EconData.Core/Lake/SilverTier.cs` | count sidecar; read-through fallback |
| `src/EconData.Core/Lake/GoldTier.cs` | rebuild-to-temp + validation; no DELETE |
| `src/EconData.Core/Lake/SqliteBackfill.cs` (new) | one-way import with natural-key mapping |
| `src/EconData.Core/Analytics/AnalyticsService.cs` | growth/ranking/correlation/freshness |
| `src/EconData.Core/Analytics/AnalyticsModels.cs` (new) | new records with snake_case JSON |
| `src/EconData.Core/Analytics/LakeStatusService.cs` | bronze/silver counts, guidance |
| `src/EconData.Core/Analytics/ServingStore.cs` (new) | mode resolution + error |
| `src/EconData.Core/Catalog/Vectorizer.cs` | honour serving mode; no fallback in `gold` |
| `src/EconData.Core/Configuration/AppConfig.cs` | `ServingStore`, `ECONDATA_SERVING_STORE` |
| `src/EconData.Cli/Program.cs` | `lake init`, `lake import-from-sqlite`, `--rebuild` |
| `src/EconData.Web/Program.cs` | new endpoints |
| `src/EconData.Web/Services/DashboardDtos.cs` | widget types + config |
| `src/EconData.Web/Components/Pages/Builder.razor` | palette + chart rendering |
| `src/EconData.Web/Components/Pages/Overview.razor` | freshness card |
| `src/EconData.Web/Components/Pages/Metrics.razor` | derived share / latest period |
| `config/metrics.json` | ~15 new metrics |
| `config/entities.json` | 6 aggregate groups + memberships |
| `docs/DEPLOY.md`, `docs/DEV_GUIDE.md`, `README.md` | migration, commands, widgets |
| `CHANGELOG.md` | user-facing changes |
| `tests/EconData.Tests/*`, `tests/EconData.Web.Tests/*` | per deliverable |

## Sequencing

1. **A** (A1 → A2 → A4 → A3) — small, independent, unblocks status accuracy.
2. **B** (B1 → B2 → B3 → B4) — architecture; B1 is the contract everything else assumes.
3. **C** (C1 → C2 → C3) — config breadth; can run in a separate worktree in parallel with B.
4. **D** (D1 → D2 → D3 → D4) — depends on B1's serving contract.

One commit per workstream (A, B, C, D) plus a final docs/gate commit; each commit must build,
test and format green on its own.

## Definition of Done

- [ ] `dotnet build EconData.slnx` — 0 warnings, 0 errors
- [ ] `dotnet test EconData.slnx` — all green (baseline 221 + new)
- [ ] `dotnet format EconData.slnx --verify-no-changes` — clean
- [ ] Deliverables 1–17 satisfied
- [ ] No `DELETE`/`DROP` added to the load path
- [ ] `config/metrics.json` codes verified against DBnomics
- [ ] Existing `/api/*` contracts unchanged; legacy `auto` behaviour preserved
- [ ] `PROGRESS.md`, `ISSUES.md`, `PLAN.md` updated; tier recorded; pushed

## UAT

```bash
dotnet build EconData.slnx
dotnet test EconData.slnx
dotnet format EconData.slnx --verify-no-changes

# A: hardening
dotnet run --project src/EconData.Cli -- lake init
dotnet run --project src/EconData.Cli -- lake status        # bronze rows non-zero
dotnet run --project src/EconData.Cli -- lake build-gold --rebuild

# B: migration
dotnet run --project src/EconData.Cli -- lake import-from-sqlite --dry-run
dotnet run --project src/EconData.Cli -- lake import-from-sqlite
ECONDATA_SERVING_STORE=gold dotnet run --project src/EconData.Cli -- lake status

# C: breadth
dotnet run --project src/EconData.Cli -- extract --metrics gdp_per_capita --bronze --silver --gold
curl 'http://127.0.0.1:5000/api/coverage'

# D: analytics
curl 'http://127.0.0.1:5000/api/analytics/growth?entity=USA&metric=gdp_usd'
curl 'http://127.0.0.1:5000/api/analytics/ranking?metric=gdp_usd&period=2024'
curl 'http://127.0.0.1:5000/api/analytics/correlation?metricA=gdp_usd&metricB=life_expectancy'
curl 'http://127.0.0.1:5000/api/analytics/freshness'
```

UI: Builder offers the four new widgets and renders them offline; Overview shows freshness;
Metrics shows derived share. Empty lake must render empty states, never 500s.

## Risks

| Risk | Mitigation |
|---|---|
| Candidate WDI codes invalid/renamed | Probe DBnomics per code; drop failures rather than ship broken entries |
| Rebuild swap while Web holds a DuckDB connection | Do it through the single job slot; fail fast if gold is open |
| Backfill id drift | Map by natural keys only; assert counts and spot-check values |
| `gold` mode breaks legacy hosts | Default stays `auto`; `sqlite` escape hatch; docs rollback |
| Correlation cost on large tables | Single aligned SQL join; limit to non-aggregate entities and one period |
| Widget scope creep | Exactly four widget types, counts/trends only, no new chart libraries |

## Out of scope

- Removing SQLite or moving embeddings off `sqlite-vec`.
- Cloud/object storage, streaming ingestion, real-time updates.
- New providers beyond WB/OECD, or extractor code branches for metrics.
- Authentication/multi-user; the app stays localhost single-user.

## Decisions (approved 2026-10-23)

1. **Metric list**: the 15 WDI candidates, no forecast set this sprint.
2. **Aggregate groups**: EU, Euro Area, G7, G20, BRICS, ASEAN.
3. **Serving default**: `gold` (strict). `auto` and `sqlite` remain selectable; legacy hosts
   without a lake must set `ECONDATA_SERVING_STORE=sqlite` (documented rollback in DEPLOY).
   The B1 "`auto` behaviour preserved" wording still applies to explicit `auto`; the default
   changes to `gold`, so `ServingStore` tests cover all three modes and the default.
4. **Rebuild UX**: CLI `lake build-gold --rebuild` **and** a Runner job option. Both route
   through the existing single job slot so the atomic swap never races an open connection.

## Approval

Approved 2026-10-23. Work proceeds A → B → C → D with the gates above.
