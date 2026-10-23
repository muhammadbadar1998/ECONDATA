# Plan — Sprint 3 (ACTIVE)

**Status: ACTIVE.** Source of record:
`.kilo/plans/1792630353408-sprint3-hardening-serving-breadth-analytics.md`.

Tier: **T2** default; T3 for A3 atomic rebuild, B2 natural-key mapping, B1 serving semantics.

## Approved decisions

- C1: 15 WDI metrics, no forecast set.
- C2: EU, Euro Area, G7, G20, BRICS, ASEAN.
- B1: `ECONDATA_SERVING_STORE` defaults to `gold` (strict); `auto`/`sqlite` selectable.
- A3: `lake build-gold --rebuild` plus a Runner job option, both via the single job slot.

## Sequencing

A (A1 → A2 → A4 → A3) → B (B1 → B2 → B3 → B4) → C → D. One commit per workstream.
