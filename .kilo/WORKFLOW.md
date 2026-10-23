# Kilo Workflow: Execution Loop, Model Selection, and Persistent State

Project-level behavior management for EconData. This file governs **how** work is done;
`AGENTS.md` governs **what** the codebase is and which commands to run. When they conflict,
`AGENTS.md` wins on repository facts (layout, commands, conventions) and this file wins on
process.

Every change — feature, fix, refactor, doc — goes through the loop below, in this order.

## Tiered Model Orchestration

Match model capability to task complexity. The cheapest model that can do the job correctly
does the job.

| Tier | Task types | Model guidance | Cost discipline |
|---|---|---|---|
| T1 — Mechanical | Scaffolding, boilerplate, docs, `CHANGELOG.md` text, renaming, formatting, test fixtures, mechanical edits across many files | `kilo/google/gemini-3.5-flash-lite` | Default tier. Never escalate just for speed. |
| T2 — Standard | Feature implementation to an approved plan, test writing, Blazor UI wiring, `config/metrics.json` entries, small refactors, bug fixes with a known cause | `kilo/anthropic/claude-sonnet-5.5` (session default) | Escalate to T3 on the second failed attempt at the same problem. |
| T3 — Deep | Hard reasoning: schema design, SQLite/`sqlite-vec` integration, data-access race conditions, Blazor concurrency, root-causing failures with no reproduction, cross-project architecture | `kilo/deepseek/deepseek-v4.1-flash` with `variant: high` | Use deliberately and briefly; hand results back to T2 for execution. |

Rules:

- **Classify before starting.** State the tier at the top of the plan (Step 0). If unsure,
  start T2; the first failed verification escalates one tier.
- **T3 is for decisions, T2/T1 is for typing.** Use T3 to produce a design, a diagnosis, or a
  diff sketch; execute the resulting edits on a cheaper tier. Do not leave long mechanical
  sequences on T3.
- **Never downgrade verification.** A T1 edit is still built, formatted, and tested.
- **Escalation is one-way within a task.** Do not bounce between tiers; go up on evidence
  (failed builds, failing tests, unresolved repro), not on vibes.
- **Record the tier** in the plan and in the commit summary so the model split is auditable.

### Agent routing

Model routing for subagents is configured in `.kilo/agent/*.md` frontmatter (`model:`), not
guessed in prompts:

| Agent | Suggested tier | Use for |
|---|---|---|
| `code` (default) | T2 | Normal implementation work. |
| `debug` | T3 | Failing tests, pipeline/idempotency bugs, Blazor job lifecycle issues. |
| `explore` | T1 | Read-only searches before planning. |

When a task needs a different model than the agent default, pass `model` (and `variant`) on
the `Task` tool call rather than re-tiering the whole session.

## Prompt Caching

Keep provider prompt caches warm so long sessions stay cheap and consistent.

- Put stable context first: this file, `AGENTS.md`, the plan. Do not paste large logs or file
  dumps ahead of them.
- Reference files and symbols by path instead of re-pasting their contents; re-read via tools
  only what changed.
- Keep the working documents (`PROGRESS.md`, `ISSUES.md`, the active plan) short and append
  to the bottom — churn at the top of the context invalidates the cache.
- Do not rewrite or reorder previously-sent sections mid-session. Amendment, not rewrite.
- Start a fresh session per phase when the loop changes tier or when the prior conversation
  is only background; the working documents carry the state instead of the transcript.

## Plan Requirements

Every plan MUST include, before it is considered approved:

- **Deliverables** — numbered list, each with a "done when" criterion.
- **Target files** — table of `path` → change.
- **Definition of Done** — checklist of verifiable outcomes (build/test/format/UAT).

Plans missing any of these are incomplete and must not be executed.

## Persistent Working Documents

Three files are the compact source of truth. Together they let any session resume with
minimal context instead of re-reading the codebase. All live in `.kilo/work/` and are
committed with the repo, as are phase plans in `.kilo/plans/`.

| File | Purpose | Updated |
|---|---|---|
| `.kilo/work/PLAN.md` | Active plan: requirements, architecture, tests, UAT criteria, tier | Step 0; amended when scope is approved |
| `.kilo/work/PROGRESS.md` | Execution progress: phase, checklist, current step, next action | Every step boundary |
| `.kilo/work/ISSUES.md` | Issue tracker: blockers, discovered bugs, follow-ups, broken links | Whenever an issue appears or closes |

Rules:

- **Resume from the docs, not the repo.** New session: read `PROGRESS.md`, then `ISSUES.md`,
  then only the files named as the current step's targets.
- **Write before you act.** Mark the next step in `PROGRESS.md` before editing code so a
  session cut can be recovered.
- **Be terse.** Bullet points, file paths, decisions, commit SHAs. No narration, no diffs.
- **Archive on completion.** When a plan ships, move its summary to the bottom of `PLAN.md`
  under "Done" and reset `PROGRESS.md` for the next task.
- One active plan per worktree. Parallel work uses separate worktrees, each with its own
  `.kilo/work/`.

## The Six-Phase Loop

Every change runs all six phases. No phase may be skipped; if a phase is genuinely
inapplicable (e.g. no UAT surface for a pure refactor), say so in the plan.

### 0. Plan

- Classify tier (T1/T2/T3) and name the escalation trigger.
- Write requirements, architecture, test strategy, and UAT criteria into `.kilo/work/PLAN.md`.
- Keep scope minimal; state what is explicitly out of scope.
- Check the invariants in `AGENTS.md` → Conventions that the change can violate (idempotent
  upserts, chunked writes, typed dates, declarative unit normalization, 768-dim cosine).
- **Pause for explicit approval.** Do not write production code before the user approves the
  plan. Approval of the plan is the starting gun for Step 1.

### 1. Test first

- Write unit/integration tests **and** UAT assertions before implementation. Express UAT as
  executable checks where possible (`check-db` against a scratch `--db-path`, `curl` against
  the Web API) and as a written checklist otherwise.
- Tests must exercise real implementation — hermetic temp SQLite, fake HTTP handlers for
  DBnomics, no mocks of Core logic, real `sqlite-vec` for vector paths.
- Confirm the new tests fail for the right reason, and record that in `PROGRESS.md`.

### 2. Implement

- Write the minimum production code needed to make the tests pass.
- Add metrics as `config/metrics.json` entries before considering extractor changes.
- Follow the style guide: single-word names, no `else` after return, no empty `catch`, no
  nullable suppression, `const` and expression-bodied members, `IReadOnly*` in public
  signatures, `CancellationToken` through every async path.

### 3. Shrink

- Re-read the full diff and reduce complexity and footprint: delete dead code, collapse
  helpers, drop speculative options, shorten identifiers, remove duplicated logic.
- Re-run tests after shrinking; behavior must not change.

### 4. Verify

- Run the smallest relevant checks from the `AGENTS.md` quality table for every touched
  project (build, targeted test filter, `dotnet format --verify-no-changes`, `check-db` for
  schema/data-path changes).
- Fix the **root cause** of every failure. Do not weaken tests, suppress warnings, skip
  checks, or add exclusions to get green.
- Iterate until everything is green; record the exact commands and results in
  `PROGRESS.md`. If a check cannot run here (no SDK, no Ollama), say so explicitly.

### 5. Commit & push

- Conventional commit: `type(scope): summary` (types: `feat`, `fix`, `docs`, `chore`,
  `refactor`, `test`; scopes `core`, `cli`, `web`, `tests`, `config`, `sql`, `docs`).
- Update `CHANGELOG.md` for user-facing changes.
- Commit and push only when the user asked for it; otherwise stop after Verify and report.
- Summarize in `PROGRESS.md`/`ISSUES.md`: what changed, UAT results, tier used, and the
  commit SHA.
- Report back to the user with the same summary and SHA.

## Model and Phase Matrix

| Phase | Default tier | Notes |
|---|---|---|
| 0 Plan | T3 for architecture-heavy work, else T2 | Cheap models can draft the plan; T3 approves hard designs |
| 1 Test first | T2 | T1 for fixtures and boilerplate |
| 2 Implement | T2, T1 for mechanical bulk | T3 only for the hard core (schema, `sqlite-vec`, concurrency) |
| 3 Shrink | T2/T3 | A T3 pass here is often the highest-value single use |
| 4 Verify | T1 | Run commands, triage failures, escalate on surprises |
| 5 Commit & push | T1 | Commit message quality matters more than model tier |

## Definition of Done

A change is done when: the plan is approved and implemented, tests and UAT pass, all quality
gates for touched projects are green (or the un-runnable ones are named), `PROGRESS.md` and
`ISSUES.md` are current, and the user has the summary — plus the SHA when a commit was
requested.
