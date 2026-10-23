# Close Sprint 2: Pipeline-Level Idempotency Test

## Goal

Close the last unmet Sprint 2 acceptance criterion. S2-US2 requires "a test runs extract
twice against a stubbed source and asserts identical row counts, identical observation
keys, and unchanged `dbe_embeddings` hashes." Today only loader-level and
aggregator-level idempotency are covered — nothing runs `PipelineRunner.RunAsync` twice.

## Verified state (already done — do not redo)

- S2-US1: `docs/DEPLOY.md` has cron with non-zero-exit alerting, a systemd unit + timer
  (`OnFailure=`), logrotate + journald retention, and run-history notes.
- S2-US3: `backup --to <path>` CLI command (`VACUUM INTO`), verified restore procedure in
  `DEPLOY.md`, and `BackupRestoreTests` round-trip test.
- S2-US2 partial: `DatabaseLoaderIntegrationTests.FullSchemaSeedAndUpsertIsIdempotent`
  (identical reload), `ChangedValueUpdatesRowInPlaceWithoutDuplicating` (revised value),
  `DerivedAggregationTests.Idempotent_SecondRunUnchanged` (aggregator called twice),
  `VectorDocumentTests.BuildEntityMetricDocumentsHashStableAndChangesOnValue` (hash stability).
- `main` == `origin/main` at `819327b`; 180 tests green; 0 warnings; format clean.

## Deliverables

| # | Deliverable | Done when |
|---|---|---|
| D1 | Pipeline double-run test | New test in `PipelineRunnerTests` runs `RunAsync(["gdp_usd"], loadToDb: true)` **twice** against one temp DB with `FakeSource`, and asserts: `dbe_observations` count identical after both runs (3), the multiset of `(metric_id, entity_id, start_date, end_date)` is identical, `dbe_entities`/`dbe_metrics`/`dbe_sources`/`dbe_derived_observations` counts unchanged between runs, and `dbe_pipeline_runs` has exactly 2 rows |
| D2 | Derived rows stable across runs | Derived row count and values are identical after the second run (guards the aggregator path in-pipeline, not just standalone) |
| D3 | Work doc SHAs corrected | `.kilo/work/ISSUES.md` rows `S2-US1`/`S2-US2`/`S2-US3` replace `(this commit)` with `819327b` |
| D4 | Gate | `dotnet build` 0 warnings, `dotnet test EconData.slnx` green, `dotnet format EconData.slnx --verify-no-changes` clean |

## Target files

| File | Change |
|---|---|
| `tests/EconData.Tests/PipelineRunnerTests.cs` | Add `RunningPipelineTwiceIsIdempotent`; reuse existing `FakeSource`, `_factory`, `ScalarLong`; add a small helper to read the sorted observation keys as a string |
| `.kilo/work/ISSUES.md` | Replace `(this commit)` placeholders with `819327b` |

## Implementation tasks (ordered)

1. **Add the test.** In `PipelineRunnerTests`, after `RunsExtractTransformLoadAndAudits`:
   - Build one runner on the shared `_factory` with `FakeSource`.
   - `await runner.RunAsync(new[] { "gdp_usd" }, loadToDb: true)` twice.
   - Capture counts and keys after run 1, re-capture after run 2, assert equality:
     `dbe_observations`, `dbe_entities`, `dbe_metrics`, `dbe_sources`,
     `dbe_derived_observations`.
   - Observation key stability: compare
     `SELECT metric_id || '|' || entity_id || '|' || start_date || '|' || end_date FROM dbe_observations ORDER BY 1`
     joined into one string, before vs after.
   - Assert `dbe_pipeline_runs` count is exactly 2 (audit grows once per run — that is
     expected, not a duplication defect) and both rows have status `SUCCESS`.
   - Assert `SELECT COUNT(*) FROM dbe_created...` **not** needed; keep scope tight.
2. **Run the new test alone**, then the full suite.
3. **Fix `.kilo/work/ISSUES.md`** placeholders to `819327b`.
4. **Verify D4 gate.**

## Invariants at risk

- Do not weaken the existing `RunsExtractTransformLoadAndAudits` assertions.
- `dbe_pipeline_runs` legitimately grows by one per run — assert 2, do not assert
  "unchanged".
- The test must use the real schema file and real `PipelineRunner`; no mocks.
- `gdp_usd` is an opt-in aggregation metric (`sum`), so derived rows are exercised too.

## Risks

- `FakeSource` always returns the same three USA points, so this test cannot detect a
  *value* change regression — that is S2-US2's second AC, already covered by
  `ChangedValueUpdatesRowInPlaceWithoutDuplicating`. Keep the two tests distinct.
- Aggregation coverage needs ≥50% of members-with-data; with a single USA entity and no
  matching membership rows, derived rows may be zero. If so, assert derived count is
  stable (equal before/after) rather than `> 0`, and note it in a comment.

## Definition of Done

- [ ] D1 pipeline runs twice; counts and observation keys identical; 2 audit rows
- [ ] D2 derived row count/values stable across runs
- [ ] D3 `ISSUES.md` SHAs corrected to `819327b`
- [ ] D4 build 0 warnings, full suite green, format clean
- [ ] Single conventional commit, e.g. `test(core): prove pipeline re-runs are idempotent`
- [ ] Push `main` (or open PR)
