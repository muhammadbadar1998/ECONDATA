# Progress

## Next — Sprint 3 open items (ACTIVE, not started)

Plan of record: `.kilo/plans/1792630353409-sprint3-open-items-serving-store-and-runbook.md`

Scope:
- `ECONDATA_SERVING_STORE` (`auto | gold | sqlite`) for **structured analytics only**
  (`AnalyticsService`). Strict `gold` fails fast when gold is missing.
- `Vectorizer`/chat keep SQLite for the vector index under every mode; pass a `GoldTier`
  to Web chat and CLI `vectorize`/`chat` for document building only.
- `lake import-from-sqlite --dry-run` + source-vs-gold parity summary.
- Backfill runbook in `docs/DEPLOY.md`.

Explicitly deferred to Sprint 4: document ingestion (speeches/transcripts). Design agreed:
raw files + chunk manifests in the lake, retrieval index (chunk text, FTS5, 768-dim
`sqlite-vec`) in SQLite, gold stays structured-only.

## Current state

- Head `4b7a85f`, `main` in sync with `origin/main`, working tree clean.
- Gate: build 0 warnings / 0 errors; **236 tests** (197 Core + 39 Web);
  `dotnet format --verify-no-changes` clean.
- Sprint 3 features landed: A1–A4 (bronze/silver counts, `build-gold --rebuild`,
  `lake init`, doctor), B shell (`import-from-sqlite`, `ServingStore` config), C (26 metrics,
  aggregate groups), D (growth/ranking/correlation/freshness + endpoints).
- Open gaps: see `ISSUES.md` — `ServingStore` inert, Web/CLI vectorizer missing gold,
  backfill runbook missing.

## History (archived)

- Phase 3 medallion ingestion: complete and pushed — `ef8fcaa`, `cee6c23`, `e4250f6`,
  `f7976d9`, `a6a61d7`.
- Sprint 3 implementation: `5b72255`, `7b4cdad`, `d10b46b`.
- Verification fixes (DuckDB analytics SQL, `import-from-sqlite`): `4b7a85f`.
