# Plan — Sprint 3 close-out (ACTIVE)

Source of record: `.kilo/plans/1792630353409-sprint3-open-items-serving-store-and-runbook.md`
(earlier phase plan: `1792630353408-sprint3-hardening-serving-breadth-analytics.md`).

Tier: **T2**; T3 only if the strict-mode semantics change.

## Shipped

- `ECONDATA_SERVING_STORE` (`auto | gold | sqlite`) for structured analytics only
  (`AnalyticsService`); strict `gold` fails fast; Web wiring. Commit `4e6d7cc`.
- Web chat and CLI `vectorize`/`chat` receive a `GoldTier` for document building;
  vectors stay in SQLite.
- Lake: `lake init`, `lake build-gold`, `lake rebuild`, `lake import-from-sqlite`.
- Analytics API: growth/ranking/correlation/freshness, with Web tests.
- Data breadth: 26 metrics; aggregate groups ASEAN, Mercosur and CPTPP added
  (EU, Euro Area, G7, G20 and OECD already existed in the entity seed).

## Open

- `lake import-from-sqlite --dry-run` + source-vs-gold parity counts.
- Backfill runbook in `docs/DEPLOY.md`.
- Reproduce and fix the intermittent Core test failure (`ISSUES.md`).
- **Serving-default decision mismatch.** The approved Sprint 3 decision was
  `ECONDATA_SERVING_STORE=gold` (SQLite only for vectors). The shipped default is
  `auto`, to keep fresh installs and the Web test fixture working without a gold
  file. Flipping the default requires (a) a gold-aware Web fixture and (b) a
  documented first-run path (`lake init` + `extract --gold`). Tracked in `ISSUES.md`.

## Corrections to the earlier plan text

- The CLI command is `lake rebuild`, not `lake build-gold --rebuild`.
- The shipped default serving mode is `auto`, not `gold` (see the open item above).
