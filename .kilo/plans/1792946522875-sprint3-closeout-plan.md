# Plan — Verification hardening, CI close-out & complexity reduction

**Status:** Part 1 (verification hardening) is implemented and verified locally; ledger row 16
(CI) is `not run` pending a push. Part 2 (complexity reduction) is ready to start only after
Part 1's close-out is observed.

## Validation snapshot (before this implementation)

This is the starting point, not the current state — the ledger at the bottom records what
now holds.

| Item | Command | Observed | Verdict |
|---|---|---|---|
| `--dry-run` guard precedes all writes | mutation: move guard after `ImportObservations` → `dotnet test --filter ImportFromSqliteTests` | `DryRun_WritesNothingToGold [FAIL]` | **done, load-bearing** |
| Parity compares source vs gold | mutation: `SourcesGold = CountGold("dbe_observations")` → same filter | `Import_ReportsSourceVersusGoldParity [FAIL]` | **done, load-bearing** |
| `--write-silver` writes real partitions | `dotnet test --filter WriteSilver` | pass | **done** |
| Dry-run mutates nothing (end-to-end) | `lake import-from-sqlite --dry-run` then `lake status` | `dbe_observations = 0` | **done** |
| Backfilled silver rebuilds gold | `--write-silver` then `lake build-gold` | `1 silver partition → 1 observation`, parity all `ok` | **done** |
| Suite green | `dotnet test EconData.slnx` | `220` Core + `49` Web, 0 failed | **done** |
| Build + format | `dotnet build` / `dotnet format --verify-no-changes` | `0` warnings, exit `0` | **done** |
| `SqliteImporter` extracted | `find src -name SqliteImporter.cs` | no file | **dropped — see Scope change** |
| `LakeImportCommand` added | `find src -name LakeImportCommand.cs` | no file | **not done** |
| `LakeCommands` canonical set | `find src -name LakeCommands.cs` | no file | **not done** |
| `scripts/verify.sh`, `scripts/check-ledger.sh` | `ls scripts` | no such directory | **not done** |
| CI workflow | `ls .github/workflows` | no such directory | **not done** |
| CHANGELOG drift corrected | `grep -n "build-gold --rebuild" CHANGELOG.md` | `CHANGELOG.md:14` still present | **not done** |
| `.kilo/agent` fiction removed | `grep -n ".kilo/agent" .kilo/WORKFLOW.md` | `.kilo/WORKFLOW.md:35` still present | **not done** |
| Ledger rule in guidance | `grep -c "Verification ledger" AGENTS.md .kilo/WORKFLOW.md` | `0` in both | **not done** |
| Internal widening (side effect) | `grep -n "internal int Upsert" src/.../GoldTier.cs` | present at `GoldTier.cs:916` (+4 helpers) | **should be reverted** |

## Lesson recorded (read this before starting)

The previous attempt marked every todo complete and reported "plan implementation is
complete" while only one of the twelve numbered deliverables actually existed. It stalled on a
tooling/path problem and wrote the plan's conclusions from intent instead of from commands.
Two guards follow from that:

1. **No todo flips to complete until its ledger command has been run and its output pasted
   into the ledger.** If a command cannot run, say which one and leave the cell `TODO`.
2. **Never describe a file as added without `ls`/`find` proof.** A green test suite says
   nothing about work that was skipped.

## Verification protocol — how to prove an item is done

### The rule

Evidence is a command you ran and the output it produced. Not "the tests pass", not "the plan
said so", not "it should work". Four claims need four different proofs:

| Claim | Insufficient | Sufficient |
|---|---|---|
| "File X was added/changed" | you edited it | `git status --short` / `git diff --stat` shows it |
| "Test T passes" | the suite is green | run `--filter T`, paste the result count |
| "Test T protects behavior B" | T passes | break B, T fails, restore, T passes |
| "Command C behaves like Y" | unit tests pass | run C for real in a scratch dir; check exit code, stdout, and state |

### Risk tiers — do only what the change needs

Apply the highest tier that matches the change; skip the rest. A comment-only edit needs
Tier 0. A doc-only edit needs Tier 0 + Tier 3. Mutation proof (Tier 1) is **not** required
for a rename. The tiers exist to stop false claims, not to add ritual.

**Tier 0 — every change, no exceptions**
- `dotnet build EconData.slnx` → 0 warnings (`TreatWarningsAsErrors` is on)
- the targeted test filter for the touched area
- `dotnet format EconData.slnx --verify-no-changes`
- `git status --short` to confirm the change set is only what you intended

**Tier 1 — any new or modified test**
- Prove it can fail (mutation recipe below). A test that cannot fail is decoration.
- **Required** when the test asserts an *absence*: dry-run, no-op, "nothing written",
  "does not throw". **Recommended** for ordinary positive assertions.

**Tier 2 — any change to observable behavior** (CLI command or flag, API endpoint, lake/file
output, exit code)
- Run the real thing end-to-end in a scratch dir. Unit tests alone are not proof.
- Assert three things: exit code, the user-visible line, and the side-effect state.
- For "does nothing" flags, capture state before *and* after and compare.

**Tier 3 — any documentation claim**
- Every documented command must have been run once, or be covered by a test that checks it.
- Do not document behavior in the same commit before its Tier 2 check passes.

**Tier 4 — before declaring an item or the plan complete**
- The ledger cell contains the observed output, not a promise
- No `TODO` cells remain
- `git status` shows only intended files
- If a check could not run, name it and mark it **un-runnable** — never "green"

### Mutation recipe (proves a test is load-bearing)

```bash
cp src/<file>.cs /tmp/kilo/<file>.cs.bak
# reintroduce the exact bug the test claims to catch: move the guard, swap a field, skip a write
dotnet test EconData.slnx --filter "FullyQualifiedName~<TestClass>"   # must FAIL
cp /tmp/kilo/<file>.cs.bak src/<file>.cs
dotnet test EconData.slnx --filter "FullyQualifiedName~<TestClass>"   # must PASS
git diff --stat src/<file>.cs                                        # must be empty
```

Never leave the tree dirty after a mutation run. If `git diff` is not empty, restore before
doing anything else.

### Dry-run / no-op recipe (proves non-mutation)

```bash
export ECONDATA_LAKE_DIR=/tmp/verify/lake
export ECONDATA_SQLITE_PATH=/tmp/verify/econdata.db
dotnet run --project src/EconData.Cli -- lake init
before=$(dotnet run --project src/EconData.Cli -- lake status | grep dbe_observations)
dotnet run --project src/EconData.Cli -- <command> --dry-run ; echo "exit=$?"
after=$(dotnet run --project src/EconData.Cli -- lake status | grep dbe_observations)
test "$before" = "$after"
```

Also test the **absent** case: if the gold file did not exist beforehand, it must not exist
afterwards. "(no change)" in a table is not evidence until the before/after values are shown.

### What you may claim, and how

- Lead with the command and its output, per deliverable. One line of observed output beats a
  paragraph of intent.
- Never convert "not run" into "passed". Write `not run: <reason>`.
- Do not mark a todo complete unless its ledger cell already has output.
- Do not summarise a deliverable as done if its row says `TODO`. If it is deferred, say
  **deferred** — never "completed" and "deferred" for the same item.

### Anti-patterns observed in this repo (do not repeat)

- Marked every todo complete without running anything.
- Reported "plan implementation is complete" while only one of the twelve numbered
  deliverables (the helper widening) actually existed.
- Repeated a doc's claim that `--dry-run` "writes nothing" instead of testing it.
- Tested the data layer but not the CLI wiring that called it.
- Listed deferred work under "remaining" while marking it complete above.

## Scope change (supersedes the earlier draft)

**Drop the `SqliteImporter` extraction.** The extraction was justified as a testability
seam, but the validation pass showed the data-layer behavior is *already* guarded: the
dry-run and parity tests fail when their bugs are reintroduced. The genuinely untested
surface is the **CLI wiring** — flag → behavior → exit code → output. Moving 180 lines
between files would not have covered it, and it is what stalled the last attempt.

Consequences:

- Keep `GoldTier.ImportFromSqlite` and `GoldTier.WriteSilverFromSqliteAsync` where they are.
- **Revert** `Upsert`, `LoadMetricCodeToId`, `LoadEntityCodeToId`, `LoadSourceKeyToId`,
  `SourceKey` from `internal` back to `private` (`GoldTier.cs:873–916`). Nothing outside the
  class uses them; AGENTS.md says keep the surface as small as possible.
- Add `LakeImportCommand` instead, which is the actual gap.

Complexity reduction is **not dropped, it is resequenced**: do it after CI exists, as its
own plan (see Deferred).

## Remaining work (ordered)

### 1. Revert the unnecessary widening — T1

Set the five helpers back to `private`.
**Done when:** `grep -c "internal int Upsert" src/EconData.Core/Lake/GoldTier.cs` returns `0`
and the suite is still green.

### 2. Add `LakeImportCommand` — T2

New `src/EconData.Core/Cli/LakeImportCommand.cs` (public static, matching the existing
`PipelineRunner` precedent in `EconData.Core.Cli`). Move the `import-from-sqlite` branch out
of `src/EconData.Cli/Program.cs` into it.

```
Execute(bool dryRun, bool writeSilver, GoldTier gold, SqliteConnectionFactory sqlite,
        SilverTier silver, TextWriter stdout, TextWriter stderr)
  -> (int ExitCode, ImportSummary Summary)
```

Rules: no `Console` access inside the command (writers injected); dry-run returns `0` before
any write; write-silver runs before parity printing; mismatch returns `1` and prints the
first five `MismatchKeys`. `Program.cs` keeps only flag parsing and delegation.

**Done when:** CLI output is byte-identical to today for the three paths and build is clean.

### 3. Command regression tests — T2

New `tests/EconData.Tests/LakeImportCommandTests.cs` using a temp SQLite + gold + silver
(existing `ImportFromSqliteTests` fixture pattern; no mocks):

- dry-run → exit `0`, gold observation count `0`, output contains `dry run`
- apply → exit `0`, output rows all `ok`
- forced divergence (delete a row from gold between runs) → exit `1`, a mismatch key printed
- `--write-silver` → `silver.HasPartition("gdp_usd")` is true

**Done when:** each of the four fails if its guard is removed (verify at least the dry-run
one by temporary revert, as was done for `ImportFromSqliteTests`).

### 4. Fix documentation drift — T1

- `CHANGELOG.md:14`: `lake build-gold --rebuild` → `lake rebuild`.
- Re-read the `--dry-run` / `--write-silver` CHANGELOG bullets against shipped behavior.
- Spot-check `README.md` and `docs/DEPLOY.md` command invocations.

**Done when:** `grep -rn "build-gold --rebuild" . --include=*.md` returns nothing.

### 5. Write the verification rule into guidance — T1

- `AGENTS.md`: add a **Verification protocol** section — copy the rule, the risk tiers, and
  the mutation/dry-run recipes from this plan (trim prose, keep the commands).
- `.kilo/WORKFLOW.md`: add a mandatory `## Verification ledger` row to *Plan Requirements*;
  fold Tier 0–4 into Phase 4 (Verify); add the "what you may claim" rules to *Definition of
  Done*.
- Remove the `.kilo/agent/*.md` routing table at `WORKFLOW.md:35` (the directory does not
  exist) or create the directory. **Recommended: remove** and state the real mechanism
  (pass `model`/`variant` on the `Task` call).

**Done when:** `grep -c "Verification ledger" AGENTS.md .kilo/WORKFLOW.md` returns non-zero
for both; `grep -c "Verification protocol" AGENTS.md .kilo/WORKFLOW.md` returns non-zero for
both; `grep -c ".kilo/agent" .kilo/WORKFLOW.md` returns `0`.

### 6. Gate scripts — T1

- `scripts/verify.sh`: `dotnet build EconData.slnx`, `dotnet test EconData.slnx`,
  `dotnet format EconData.slnx --verify-no-changes`; `set -euo pipefail`; optional `--uat`
  runs the lake backfill in a temp `ECONDATA_LAKE_DIR`.
- `scripts/check-ledger.sh`: fail when a plan whose status line says `complete` contains a
  ledger row whose observed cell is empty or `TODO`. Grep-based, no parser.

**Done when:** `scripts/verify.sh` exits `0` here; `scripts/check-ledger.sh` fails on a
seeded blank cell and passes on this plan (which is `in progress`, so it should skip).

### 7. CI — T2

`.github/workflows/ci.yml` on `push` + `pull_request`: checkout → `actions/setup-dotnet`
`10.0.x` (satisfies `global.json` `10.0.401`, `rollForward: latestFeature`) → `scripts/verify.sh`.
`native/sqlite-vec/linux-x64` is vendored, so `ubuntu-latest` can load the real extension.

**Done when:** a deliberately broken commit is red and a good commit is green.

### 8. Doc-command drift test (recommended) — T2

Add a canonical `LakeCommands` set in `EconData.Core.Cli`, use it from `Program.PrintUsage`,
and add `tests/EconData.Tests/DocCommandTests.cs` asserting every `lake <subcommand>` token
in `README.md`, `CHANGELOG.md`, `docs/DEPLOY.md` is a member. Match subcommands only, never
flags.

**Done when:** adding `lake frobnicate` to a doc fails the test.

### 9. Close the loop — T1

Fill every ledger cell, then update `.kilo/work/ISSUES.md` (record the process findings with
commit SHAs) and `.kilo/work/PROGRESS.md` with the observed gate output.

**Done when:** no `TODO` remains in this file and both work docs cite commits.

## Verification ledger

Rows 1–7 were observed during validation; they are recorded here so the work is not redone.

| # | Deliverable | Command | Expected | Observed |
|---|---|---|---|---|
| 1 | Dry-run non-mutating (data layer) | mutation revert → `--filter ImportFromSqliteTests` | test fails, then passes on restore | `DryRun_WritesNothingToGold [FAIL]` under mutation; restored green |
| 2 | Parity source-vs-gold (data layer) | mutation revert → same filter | test fails, then passes on restore | `Import_ReportsSourceVersusGoldParity [FAIL]` under mutation; restored green |
| 3 | Write-silver materialises partitions | `--filter WriteSilver` | pass | pass |
| 4 | Dry-run CLI end-to-end | `--dry-run` then `lake status` | `dbe_observations = 0` | `0` |
| 5 | Silver → gold rebuild | `--write-silver` then `lake build-gold` | same observation count | `1 → 1`, parity `ok` |
| 6 | Suite green | `dotnet test EconData.slnx` | 0 failed | `220 + 49`, 0 failed |
| 7 | Build + format | `dotnet build`; `dotnet format --verify-no-changes` | clean | `0` warnings, exit `0` |
| 8 | Widening reverted | `grep -c "internal int Upsert" src/EconData.Core/Lake/GoldTier.cs` | `0` | `0` |
| 9 | `LakeImportCommand` exists | `find src -name LakeImportCommand.cs` | 1 file | `src/EconData.Core/Cli/LakeImportCommand.cs` |
| 10 | Command tests guard their paths | `--filter LakeImportCommandTests` | 5 pass; mutation breaks dry-run | 5 passed; with `ImportFromSqlite(sqlite, false)` mutation `DryRun_ReturnsZeroAndLeavesGoldEmpty [FAIL]`, restored green |
| 11 | Docs accurate | `grep -n "build-gold --rebuild"` over shipped docs | no matches | no matches in README/CHANGELOG/EXPLAINER/AGENTS/DEPLOY/DEV_GUIDE (historical plan files retain the old wording intentionally) |
| 12 | Guidance updated | `grep -c "Verification Protocol" AGENTS.md .kilo/WORKFLOW.md` | both non-zero | `1` and `1`; "Verification ledger" in WORKFLOW `1` |
| 13 | `.kilo/agent` fiction gone | `grep -c ".kilo/agent" .kilo/WORKFLOW.md` | `0` | `0` |
| 14 | Gate script | `bash scripts/verify.sh` | exit `0` | `verify: ok` (build 0/0, 276 tests, format clean) |
| 15 | Ledger check | `bash scripts/check-ledger.sh` | skips `in progress`, fails blank cell | `ledger: ok` on this plan; seeded complete plan with `TODO` → exit `1` |
| 16 | CI | push broken commit | red, then green | **not run: requires a push (not requested); YAML parses** |
| 17 | Doc-drift test | add `lake frobnicate` to a doc | test fails | `DocumentedLakeSubcommands_AllExist [FAIL]`; README restored, `git diff` empty |

## Definition of Done

- [x] Rows 8–15 and 17 observed; row 16 is `not run` (needs a push)
- [x] `scripts/verify.sh` green locally (`verify: ok`, 276 tests) — CI green **not run**
- [x] Reverting the dry-run guard makes a test fail (data layer and command layer)
- [x] No `lake <subcommand>` in docs is missing from `LakeCommands` (mutation-proven)
- [x] `AGENTS.md` + `WORKFLOW.md` carry the ledger rule **and** the verification protocol;
      `.kilo/agent` reference removed
- [x] No deliverable claimed complete without observed output (Tier 4)
- [ ] `ISSUES.md` / `PROGRESS.md` cite commits and observed output — updated, but there are no
      commits yet (the tree is uncommitted; no push was requested)

## UAT

```bash
scripts/verify.sh

export ECONDATA_LAKE_DIR=/tmp/econdata-vh/lake
export ECONDATA_SQLITE_PATH=/tmp/econdata-vh/econdata.db
rm -rf /tmp/econdata-vh && mkdir -p /tmp/econdata-vh
dotnet run --project src/EconData.Cli -- init-db
dotnet run --project src/EconData.Cli -- lake init
dotnet run --project src/EconData.Cli -- lake import-from-sqlite --dry-run   # exit 0
dotnet run --project src/EconData.Cli -- lake status                          # gold rows still 0
dotnet run --project src/EconData.Cli -- lake import-from-sqlite --write-silver
dotnet run --project src/EconData.Cli -- lake build-gold                      # parity holds
```

## Risks

| Risk | Mitigation |
|---|---|
| Path/tooling friction stalls progress again | If a path looks wrong, use `find`/`ls` proof and say so in the ledger; never claim a file exists without it |
| `.NET 10` unavailable on `ubuntu-latest` | `setup-dotnet` `10.0.x`; fall back to `dotnet-install.sh`; if neither works, record CI as un-runnable |
| Command extraction changes CLI output | Assert byte-identical output for dry-run/apply/mismatch before deleting the inline branch |
| CI exposes latent hermeticity issues | Fix root cause; never skip or weaken a test |
| Doc-drift test too brittle | Match `lake <subcommand>` tokens only |
| Ledger becomes ceremony | Only manual rows need prose; automated rows cite a test name |

## Out of scope

- Broad decomposition of `GoldTier`, `Builder.razor`, `AnalyticsService`, or the CLI switch
  (see Deferred).
- Sprint 4 document ingestion.
- Flipping the serving default to `gold`.
- Reworking model selection or the tiered routing model itself (only the stale
  `.kilo/agent` pointer is removed, in deliverable 5).
- A bespoke markdown linter beyond `check-ledger.sh`.

## Deferred — complexity reduction

Superseded by Part 2 below, which lists the waves, seams, and ledger.

## Open questions

None blocking. Confirm SDK availability on the first CI push (risk row 2).

---

# Part 1a — Close-out (execute first)

Requires an implementation-capable agent: these are mutating git operations, explicitly
requested by the user.

Current state: **33 paths uncommitted** (22 modified, 11 untracked), `git log
origin/main..HEAD` = 0 commits. Nothing is on the branch yet.

1. **Commit by area, not one blob.** Suggested grouping (adjust to the actual diff):
   - `feat(core)`: `ImportSummary`, backfill hardening (`GoldTier` import/parity/dry-run).
   - `refactor(cli)`: `LakeImportCommand` + `LakeCommands`; `Program.cs` delegation.
   - `feat(web)`: analytics widgets, freshness card, metrics columns, Web DTOs, startup guard.
   - `test`: `LakeImportCommandTests`, `DocCommandTests`, `AnalyticsDtoJsonTests`,
     `StartupValidationTests`, `ClearAllPools` removals.
   - `chore`: `scripts/verify.sh`, `scripts/check-ledger.sh`, `.github/workflows/ci.yml`.
   - `docs`: `AGENTS.md` + `.kilo/WORKFLOW.md` verification protocol, `CHANGELOG` drift fix,
     `.kilo/work/*` notes, this plan.
   After each commit check `git status --short` to confirm nothing intended was left behind.

2. **Push** to `origin/main`.

3. **Watch CI**: `gh run list --limit 3`, then `gh run watch <id>`.

4. **If CI is red**: fix the root cause. Do not weaken, skip, or `[Skip]` a test, and do not
   edit `global.json`. If the failure is environment-only (no .NET 10 runner image), record
   it as `not run` with the reason.

5. **Close row 16** in the ledger with the observed run id and conclusion; then set Part 1
   status to `complete`.

6. **Reconcile the stale work docs**: `.kilo/work/PLAN.md` still says "Sprint 3 close-out
   (ACTIVE)" and lists 7 shipped items as **Open**. Archive it per `WORKFLOW.md` and record
   the new SHAs in `PROGRESS.md`.

**Done when:** CI is green on the pushed commit, `bash scripts/check-ledger.sh` passes with
the plan marked complete, and `.kilo/work/PLAN.md` no longer contradicts `PROGRESS.md`.

---

# Part 2 — Complexity reduction (after Part 1 closes)

Three waves, each independently shippable and each ending with `bash scripts/verify.sh`.
Baseline: 276 tests, CI green. **Behaviour must not change; only structure.**

## Wave 1 — extract `SqliteImporter` from `GoldTier`

Why now: `GoldTier.cs` is 980 lines with 19 public members spanning schema, seeding, upserts,
aggregation, rebuild, import, and silver export. Import is the most separable block and its
behaviour is already guarded by mutation-proven tests.

Exact seam (verified):

- **Move** into `src/EconData.Core/Lake/SqliteImporter.cs` (public sealed; ctor takes
  `GoldTier`, `SqliteConnectionFactory`, `ILogger`):
  `ImportFromSqlite`, `WriteSilverFromSqliteAsync`, `ImportEntities`, `ImportMetrics`,
  `ImportSources`, `ImportObservations`, `Count*`, `Mismatches`, `Missing`, `Keys`,
  `ReadIdMap`, `ReadSourceKeyMap`, `LoadSourceKeyToId`, `SourceKey`.
- **Keep in `GoldTier`, widen to `internal`** (used by seeding/upserts, not only import):
  `Upsert`, `LoadEntityCodeToId`, `LoadMetricCodeToId`, `LoadCodeToId`. Same assembly, so no
  public surface change. Add a one-line comment saying why they are internal.
- **Update callers**: `LakeImportCommand` (`importer.Import(...)` →
  `importer.WriteSilverAsync(...)`), `ImportFromSqliteTests`, `LakeImportCommandTests`.

**Done when:**
- `grep -c "ImportFromSqlite" src/EconData.Core/Lake/GoldTier.cs` → `0`
- `wc -l src/EconData.Core/Lake/GoldTier.cs` → under 780
- suite green; re-run the dry-run mutation (pass `false` instead of `dryRun`) and confirm
  `DryRun_ReturnsZeroAndLeavesGoldEmpty` still fails, then restore

## Wave 2 — extract `DashboardChartBuilder` from `Builder.razor`

Why: `Builder.razor` is 764 lines, roughly 200 of which are chart-option construction with no
test coverage (no bUnit is vendored).

- **Move** into `src/EconData.Web/Services/DashboardChartBuilder.cs`: `BuildSeriesOptionAsync`,
  `BuildCoverageOptionAsync`, `BuildSnapshotOptionAsync`, `BuildRunsOptionAsync`,
  `BuildGrowthOptionAsync`, `BuildRankingOptionAsync`, `BuildCorrelationOptionAsync`,
  `BuildFreshnessOptionAsync`, plus `LineOption` / `BarOption`.
- The page keeps only: the dispatch call, the `null` → "configuration is incomplete" error
  mapping, and `WebJson`.
- **Do not change the emitted JSON shape** — saved `dashboard.json` layouts depend on it.
- New `tests/EconData.Web.Tests/DashboardChartBuilderTests.cs` using the existing
  `StubHttpHandler` with canned payloads for all eight widget types; assert title text,
  series `type`, and axis labels.

**Done when:** `Builder.razor` under 620 lines; 8 builder tests pass; adding each widget in the
UI and reloading still renders (Tier 2 UAT).

## Wave 3 — extract a `LakeCommand` dispatcher from the CLI switch

Why: `Program.cs` is 486 lines (down from 518 after the import branch moved out) and
`LakeAsync` still inlines four commands.

- **Move** the `init`, `status`, `build-gold`, `rebuild` cases into
  `src/EconData.Core/Cli/LakeCommand.cs` (public static, mirrors `LakeImportCommand`), taking
  `action`, `AppConfig`, `root`, `Arguments`, `ILogger`, and injected writers.
- `Program.LakeAsync` becomes: resolve paths → delegate → return exit code. `LakeCommands`
  stays the canonical subcommand set (already tested by `DocCommandTests`).
- Tests: extend the `LakeImportCommandTests` pattern — `status` and `init` emit the expected
  lines and exit `0`; `build-gold` with no silver prints the actionable message and returns
  non-zero.

**Done when:** `Program.cs` under 430 lines; CLI UAT output byte-identical for
`lake init|status|build-gold|rebuild`.

## Deferred beyond this plan — `AnalyticsService`

`AnalyticsService.cs` (592 lines) mixes SQLite and DuckDB SQL behind one type. Splitting it is
the highest-risk change in the repo: every query has two engines and engine parity is only
partly covered. **Do not attempt it in this plan.** It needs its own design note with a
per-method parity matrix first.

## Part 2 verification ledger

| # | Wave | Command | Expected | Observed |
|---|---|---|---|---|
| 18 | 1 | `grep -c "ImportFromSqlite" src/EconData.Core/Lake/GoldTier.cs` | `0` | TODO |
| 19 | 1 | `wc -l src/EconData.Core/Lake/GoldTier.cs` | `< 780` | TODO |
| 20 | 1 | dry-run mutation → `--filter LakeImportCommandTests` | test fails, restore green | TODO |
| 21 | 2 | `wc -l src/EconData.Web/Components/Pages/Builder.razor` | `< 620` | TODO |
| 22 | 2 | `--filter DashboardChartBuilderTests` | 8 pass | TODO |
| 23 | 3 | `wc -l src/EconData.Cli/Program.cs` | `< 430` | TODO |
| 24 | 3 | `lake init/status/build-gold/rebuild` scratch UAT | output unchanged | TODO |
| 25 | all | `bash scripts/verify.sh` after each wave | `verify: ok`; CI green | TODO |

## Part 2 risks

| Risk | Mitigation |
|---|---|
| Structural refactor silently changes behaviour | No behaviour edits in a refactor commit; rely on the 276-test net plus per-wave tests written first |
| `internal` widening is mistaken for API growth | Same assembly only; no `public` signatures change; comment the reason |
| Builder JSON shape drifts and breaks saved dashboards | Assert option shape in tests **before** moving the code |
| Wave 3 churns the CLI while Part 1 CI is still unproven | Wave 3 starts only after CI is green (Part 1 close-out) |
| Waves get bundled into one commit | One wave per commit; `git status` clean between waves |

## Part 2 out of scope

- `AnalyticsService` split (see Deferred above).
- Gold pruning / `lake verify` (product behaviour, not structure).
- Sprint 4 document ingestion.
- Flipping the serving default to `gold`.


---

## Archived — Sprint 3 close-out (completed, superseded by this plan)

Delivered: startup validation for strict `gold`; Builder widgets (`growth`, `ranking`,
`correlation`, `freshness`) with renderers; Overview freshness card; Metrics
`Derived`/`Latest period`; `--dry-run` + real parity summary; `--write-silver` backfill;
`ImportSummary`; DEPLOY runbook; `ClearAllPools()` removal.

Validation corrections applied: dry-run no longer writes to gold; parity compares SQLite
against gold on natural keys (not itself); `--write-silver` materialises real partitions;
widgets have dispatch cases; DTOs are guarded by `AnalyticsDtoJsonTests`.

Gate at close: build 0/0, **269 tests** (220 Core + 49 Web), format clean, Core suite green
three consecutive runs. Residual gap: no host-level startup test (process env vars leak
across parallel test classes); the rule is asserted at `AnalyticsService` instead.
