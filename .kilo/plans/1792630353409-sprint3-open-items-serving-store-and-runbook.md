# Plan — Close Sprint 3 Open Items (Serving Mode + Backfill Runbook)

## Goal

Finish the open items left after verification, with the corrected scope that **SQLite stays
required for chat/retrieval**:

1. **`ServingStore` is inert** — implement `auto | gold | sqlite` for **structured analytics
   only** (`AnalyticsService`). Chat is not forced off SQLite: the `sqlite-vec` index (and,
   later, ingested document chunks) live there.
2. **`lake import-from-sqlite` is undocumented** — add a verified backfill runbook, plus
   `--dry-run` and a post-import parity check.
3. **Noisy commit message `d10b46b`** — leave history alone (already pushed).

## Scope correction (important)

`ECONDATA_SERVING_STORE` controls **where structured indicator queries read from**. It does
**not** remove the SQLite dependency:

- `AnalyticsService` (the `/api/*` analytics endpoints, tools, rankings) honours the mode.
- `Vectorizer` still always uses SQLite for the vector index (`sqlite-vec`).
- Chat therefore continues to require SQLite even when `ECONDATA_SERVING_STORE=gold`.
- `Vectorizer` may still build metric/entity documents from gold (document *content*), but
  that is a separate concern from "which store serves analytics".

Future document ingestion (speeches, etc.) is **Sprint 4** and is intentionally out of scope;
it reinforces this boundary because the retrieval index is SQLite.

## Tier

**T2.** Escalate to **T3** only if the strict-mode failure semantics change (D1).

## Current state (verified)

| Fact | Evidence |
|---|---|
| `AppConfig.ServingStore` exists, normalised, default `auto` | `AppConfig.cs:23,46,72` |
| Nothing reads it | `grep ServingStore src tests` → only AppConfig |
| `AnalyticsService(factory, gold)` always resolves `gold if exists else sqlite` | `AnalyticsService.cs:17-30` |
| Web chat vectorizer is built **without** gold (doc-building bug, not a mode issue) | `Web/Program.cs:85-91` |
| Pipeline job vectorizer does pass gold | `PipelineJobExecutor.cs:167` |
| CLI `vectorize` / `chat` build Vectorizer without gold | `Cli/Program.cs:357, 376` |
| CLI import has no `--dry-run` and prints no counts | `Cli/Program.cs:287-298` |
| `docs/DEPLOY.md` titled "Deployment Runbook (SQLite)", no backfill section | `DEPLOY.md:1` |

## Design decisions

- **D1 — Strict `gold` with no gold file: fail fast at startup.** `AnalyticsService`
  construction throws `InvalidOperationException` naming the fix. Web resolves it during
  startup. *Alternative:* per-request 503.
- **D2 — `sqlite` forces SQLite even when gold exists** (rollback escape hatch).
- **D3 — Invalid mode strings fail fast** (`auto|gold|sqlite`), not silently `auto`.
- **D4 — No git history rewrite** (`d10b46b` is pushed; force-push is disallowed).
- **D5 — Import prints a parity summary and exits non-zero on mismatch.**
- **D6 — `ServingStore` does not apply to `Vectorizer`.** SQLite remains the retrieval store.
- **D7 — Pass a `GoldTier` to CLI/Web Vectorizer** so document building works on a
  lake-only deployment (structured SQLite tables may be empty there).

## Deliverables

| # | Deliverable | Done when |
|---|---|---|
| 1 | `ServingStore` enum + validated parser | tests: `auto/gold/sqlite/AUTO/ sqlite ` parse; `mongo` rejected |
| 2 | `AnalyticsService` honours the mode | tests: `auto` prefers gold; `sqlite` ignores existing gold; `gold` throws when absent |
| 3 | `Vectorizer` keeps SQLite vectors, gains gold for document building | `sqlite-vec` still used; a lake-only fixture builds documents from gold |
| 4 | Web wiring | `AnalyticsService` gets `config.ServingStore`; `IDocumentSearcher` Vectorizer gets the gold tier; job vectorizer unchanged (already has gold) |
| 5 | Startup validation | Web resolves `AnalyticsService` before `app.Run()`; strict misconfiguration fails with a clear message |
| 6 | CLI wiring | `vectorize` / `chat` pass a `GoldTier` (document building only); no mode plumbing |
| 7 | `lake import-from-sqlite --dry-run` | prints source counts, writes nothing (gold file unchanged) |
| 8 | Import parity summary | source vs imported counts for entities/metrics/sources/observations; exit 1 on mismatch |
| 9 | Backfill runbook | `DEPLOY.md`: backup → `lake init` → dry-run → import → verify → switch mode → rollback |
| 10 | Docs + changelog | README/DEV_GUIDE list `ECONDATA_SERVING_STORE` and state SQLite remains required for chat |
| 11 | Gate | build 0/0, tests green, format clean, pushed |

## Target files

| Path | Change |
|---|---|
| `src/EconData.Core/Analytics/ServingStore.cs` (new) | enum + `Parse` with validation |
| `src/EconData.Core/Analytics/AnalyticsService.cs` | mode-aware resolution; strict validation; `Mode` property |
| `src/EconData.Core/Catalog/Vectorizer.cs` | no mode; ensure gold is used for coverage docs when supplied (already the case) |
| `src/EconData.Core/Lake/GoldTier.cs` | `ImportFromSqlite(..., bool dryRun = false)` returning per-table counts |
| `src/EconData.Core/Analytics/ImportSummary.cs` (new) | parity record |
| `src/EconData.Cli/Program.cs` | pass gold to vectorize/chat; `--dry-run`; print parity summary |
| `src/EconData.Web/Program.cs` | pass `ServingStore` to AnalyticsService; give chat Vectorizer the gold tier; startup validation |
| `src/EconData.Core/Configuration/AppConfig.cs` | validate the mode value |
| `docs/DEPLOY.md`, `docs/DEV_GUIDE.md`, `README.md`, `CHANGELOG.md` | mode + runbook + SQLite-required-for-chat note |
| `tests/EconData.Tests/ServingStoreTests.cs` (new) | parser |
| `tests/EconData.Tests/ServingModeTests.cs` (new) | AnalyticsService per mode |
| `tests/EconData.Tests/ImportFromSqliteTests.cs` | dry-run, parity summary, mismatch exit |
| `tests/EconData.Web.Tests/*` | chat searcher has gold; startup validation |

## Strict-mode semantics (analytics only)

```
auto   : gold if the file exists, else SQLite            (today's behaviour)
gold   : analytics read gold only; missing file => construction throws
sqlite : analytics read SQLite only, even if gold exists
```

Error text names the fix:

```
ECONDATA_SERVING_STORE=gold but no gold database at <path>.
Run `lake build-gold` or `lake import-from-sqlite`, or set the mode to `auto`.
```

Chat/vectorize are unaffected by the mode and still need SQLite.

## Import parity summary

```
entities      source 85    gold 85    ok
metrics       source 26    gold 26    ok
sources       source 26    gold 26    ok
observations  source 13980 gold 13980 ok
```

Exit `1` on mismatch. `--dry-run` prints source counts and "dry run: nothing written".

## Tests

1. `ServingStoreTests` — parsing and rejection.
2. `ServingModeTests` — `AnalyticsService` across 3 modes × gold present/absent.
3. `VectorizerGoldSourceTests` or a Web test — chat `IDocumentSearcher` receives gold and
   builds documents on a lake-only deployment (regression for `Web/Program.cs:85`).
4. `ImportFromSqliteTests` — dry-run writes nothing; parity values; forced mismatch exits 1.
5. Existing suites pass unmodified under the default `auto`.

## Definition of Done

- [ ] build 0 warnings/0 errors; all tests green; format clean
- [ ] Deliverables 1–11 satisfied
- [ ] `gold` with no gold refuses to start with an actionable message
- [ ] `sqlite` serves analytics from SQLite even when gold exists
- [ ] Chat still uses SQLite for vectors under every mode
- [ ] Runbook executed end-to-end on a scratch SQLite DB
- [ ] `PROGRESS.md` / `ISSUES.md` updated (ISSUES currently describes Vectorizer mode
      support, which is now explicitly out of scope); commit pushed

## UAT

```bash
dotnet build EconData.slnx && dotnet test EconData.slnx && dotnet format EconData.slnx --verify-no-changes

export ECONDATA_LAKE_DIR=/tmp/econdata-uat/lake
rm -rf /tmp/econdata-uat
dotnet run --project src/EconData.Cli -- lake init
dotnet run --project src/EconData.Cli -- lake import-from-sqlite --dry-run
test ! -f "$ECONDATA_LAKE_DIR/gold/econdata.duckdb"
dotnet run --project src/EconData.Cli -- lake import-from-sqlite
dotnet run --project src/EconData.Cli -- doctor

ECONDATA_SERVING_STORE=sqlite dotnet run --project src/EconData.Cli -- lake status
ECONDATA_LAKE_DIR=/tmp/empty ECONDATA_SERVING_STORE=gold \
  dotnet run --project src/EconData.Cli -- doctor   # must fail with actionable message
```

## Risks

| Risk | Mitigation |
|---|---|
| Strict mode breaks existing hosts | Default `auto`; `sqlite` rollback documented |
| Users assume `gold` removes SQLite | Docs and error text state SQLite is required for chat |
| Web startup validation surprises | Only strict `gold` fails; `auto` never does |
| Import parity false positives | Compare natural-key sets, not global totals |
| History rewrite temptation | Out of scope |

## Deferred to Sprint 4 — document ingestion

Design already agreed in discussion: raw files + chunk manifests archived in the lake
(bronze/silver), retrieval index (chunk text, FTS5, 768-dim `sqlite-vec`) in SQLite, gold
stays structured-only. Includes deterministic versioned chunking, citation offsets,
hybrid BM25+vector retrieval, and prompt-injection defenses. Not part of this plan.

## Out of scope

- Document ingestion (Sprint 4).
- `ServingStore` for `Vectorizer` / chat retrieval.
- Rewriting pushed history.
- `DELETE`-based gold reconciliation.
- Moving embeddings off `sqlite-vec`.

## Open questions (approval)

1. **D1**: fail-fast startup (recommended) or per-request 503?
2. Parity summary enough, or also a standalone `lake verify` command?
3. Should `import-from-sqlite` require an explicit `--apply` (dry-run by default)?
