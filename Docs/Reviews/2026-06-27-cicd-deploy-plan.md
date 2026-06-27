# CI/CD + Deploy + Local-Hooks — Decision Record

## DECISION (final, 2026-06-27)

The owner chose the **free, no-spend** path. What was actually implemented:

- **Killed CI** — deleted `.github/workflows/ci.yml`. (It cost $0 anyway on the self-hosted
  box, but it was toothless — agents merged over failures — and caused runner contention +
  disk creep. Removing it is free and removes a problem.)
- **Added free local git hooks** (`scripts/hooks/` + `scripts/install-git-hooks.sh`, armed via
  `core.hooksPath`, self-arming through `start-dev.sh` and web's `postinstall`):
  - `pre-commit` — blocks unresolved merge-conflict markers (fast).
  - `pre-push` — `pnpm check` (svelte-check) when `web/` changed, `dotnet build` when `.NET`
    changed, docs-only skipped; full `.NET` tests opt-in via `RC_PRE_PUSH_FULL=1`. This is the
    free catch for the `act.frame` class of error. **Advisory** — bypassable with `--no-verify`.
- **Deploy unchanged** — stays tag/manual-only (already correct); a broken build can't produce
  images, so it can't ship.

**Explicitly NOT done (rejected on cost):** GitHub Pro ($4/mo) and therefore the enforced
branch-protection/ruleset gate, CODEOWNERS enforcement, and the deploy `verify-green-on-main`
job. Consequence accepted: nothing *blocks* a bad merge — the hooks catch-and-warn, and we
fix forward. Revisit only if the owner later wants hard enforcement (which requires Pro).

The full multi-agent analysis below is retained as the rationale/record. Treat its
Pro/enforcement recommendations as **considered-and-declined**, not the plan of record.

---

# (Original proposal — analysis & rationale, for reference)

> Generated 2026-06-27 by a multi-agent design workflow (4 recon → 4 candidate
> architectures → 3 judges + red-team → synthesis).

---

# Rental Command CI/CD + Deploy + Local-Hooks Plan — Recommended (decision-ready)

**Recommendation in one line:** Adopt **GateBox** as the base architecture — one self-hosted PR gate, one tag-only deploy, one required check, self-defending workflow files — and **graft in four fixes** from the runners-up and the red team: (1) a deploy-time *verify-green-on-main* job, (2) a cheap *post-merge `push: main`* backstop, (3) *in-job* path filtering (never workflow-level `paths-ignore` with a required check), and (4) a *GitHub-hosted spending limit = $0* financial floor. Net new spend: **$4/mo (GitHub Pro)**. Net GitHub-hosted minutes: **0**.

Why GateBox wins the base: it was the maintainability/agent-proofness judge's top pick and is the *only* candidate that defends the gate's own definition non-self-referentially (CODEOWNERS on workflow files), so an agent can't both re-add `deploy-on-push` and delete the guard in the same PR — the exact mechanism behind the two 2,000-minute burns. The grafts close its three real gaps (soft prod gate, docs-only-PR deadlock, two-green-PRs-to-red-main) that the correctness judge and red team flagged.

---

## (A) THE BASIS — principles the plan rests on

1. **Enforcement lives server-side; everything local is an optimization.** Most commits are agent-authored and agents commit *as the owner*, so any gate they can `--no-verify` or merge over is theater. → *Priority 5 (gate must mean something); Priority 7 (agent-proof).* Recon: agents today "merge over failing .NET tests"; RC has **zero** hooks installed (only `.sample` files).

2. **Keep 100% of routine automation self-hosted; the burn only ever happens on *hosted* runners.** The fix for the 2,000-min blowout is to never use metered runners for high-frequency work, and to make that financially impossible too. → *Priorities 1, 6.* Recon: self-hosted = $0 metered minutes (confirmed); the blowout was deploy-on-every-push building 3 images × ~34 commits/day.

3. **Build/test exactly once, at the PR; deploy is a rare, deliberate, *verified* act.** Never re-test at deploy time, never run per-branch-push CI, never deploy on merge. → *Priorities 2, 3.* Recon: deploy.yml is already correctly tag/dispatch-only; ci.yml currently double-spends on `push:**` **and** `pull_request:**`.

4. **The gate must catch BOTH stacks, and `green` must be honest.** A required `web` job is non-negotiable, and the baseline must be genuinely green (quarantine-with-ticket, not silent skips) so red always means "this PR." → *Priorities 4, 5.* Recon: ci.yml has **no web job at all** (that's how the `act.frame` svelte-check error reached main); 4 Docker-gated `[SkippableFact]` RLS/view/concurrency tests **silently skip** without Postgres.

5. **The workflow files and the gate config are themselves protected.** A guard an agent can delete in the same PR is no guard. → *Priorities 1, 2, 7.* Recon: red team's top finding — `pull_request` runs from the merge ref, so one PR can both re-add `push:` to deploy.yml *and* neuter the inline grep. Only CODEOWNERS-reviewed workflow files stop this.

6. **One box, two repos, fewest moving parts.** Two workflows + notion-sync, one runner type, one required check — simple enough that a solo owner can keep it healthy. → *Priorities 6, 7.*

---

## (B) RECOMMENDED SETUP

### DEPLOY (`deploy.yml`) — keep as-is + one verify job
- **Trigger (unchanged, protected):** `push: tags: ['v*']` + `workflow_dispatch{ref}`. **Never** `push: branches` / `pull_request`.
- **Runner:** `self-hosted` (`rental-build-box`). **Images build on the box**, never the VPS: compile .NET once (warm NuGet + Docker layer cache), `docker build` api/engine runtime images + web in Docker (`--build-arg VITE_API_URL=/api/v1`), push `ghcr.io/jcoble/rentalcommand-{api,engine,web}:<short-sha>`+`:latest`.
- **GRAFT — add a `verify` job (runs first, gates `build-push`)** that aborts unless the tagged/dispatched SHA **is an ancestor of `origin/main`** AND its `ci-pass` check-run is `success`. Applies to **dispatch too**. Closes the "`git tag v9 && git push --tags` ships un-gated code" hole.
- **GRAFT — `environment: production`** on the deploy job so dispatch requires a reviewer approval (a second deliberate gesture).
- **Prod:** SSH, write `IMAGE_TAG`, prune, `compose pull` → `up -d` → prune; EF migrations self-apply on boot under the advisory lock; health-poll `/health` 30×5s. `concurrency: deploy-prod`. VPS still pull-only.

### CI (`ci.yml`) — PR gate + cheap main backstop, self-defending
- **Runner:** `self-hosted`.
- **Triggers:** `pull_request: [main]` + **GRAFT** `push: branches: [main]` (post-merge backstop) + `workflow_dispatch`. **Drop all per-branch `push` triggers** (biggest waste cut).
- **Path filtering — IN-JOB only.** Never workflow-level `paths-ignore` with a required check (docs-only PR would hang "Pending" forever). Workflow always runs; a `changes` job (`dorny/paths-filter`, default-true on ambiguity) emits `dotnet`/`web`/`mobile`/`workflows` booleans; jobs gate via `if:`.
- **Jobs:** `changes`; `dotnet` *(if changed)* — Postgres service, `dotnet build -c Release`, `dotnet test --filter "Quarantine!=true"` (Postgres present ⇒ the 4 skippable RLS/view tests actually run + meta-assert vs wholesale skip); `web` *(if changed)* — **the missing gate:** `pnpm install` → `svelte-kit sync` → `svelte-check --threshold error` → eslint → `vite build`; `mobile` *(if changed)* — `flutter analyze`; `guardrails` *(always)* — allowlist asserts every `runs-on:` == literal `self-hosted`, deploy.yml has no `push: branches`/`pull_request`, prod compose has no `build:`, quarantine filter matches allowed trait; `ci-pass` *(needs all, `if: always()`)* — the **single required check**, green iff all needed jobs succeeded or skipped-by-no-change.
- **`concurrency: cancel-in-progress: true`.** Add the disk-reclaim step (today only in deploy.yml) + orphan-postgres-container sweep.

### LOCAL HOOKS — port EDI's framework, **flip default to ON**, advisory only
- Tracked `scripts/hooks/{pre-commit,pre-push,commit-msg}` + `scripts/install-git-hooks.sh`, installed via `git config core.hooksPath scripts/hooks` (version-controlled, shared by worktrees); wired into `start-dev.sh` + a `web/` pnpm postinstall.
- **Default ON** (vs EDI's opt-in that "rarely runs"): `pre-commit` (<5s: secret/large-file scan, format, docs-root-clean — no build); `pre-push` (docs-only skip; else changed-area `dotnet build` + fast affected tests + `svelte-check` if web changed; full suite behind `RC_PRE_PUSH_FULL=1` per-invocation); `commit-msg` (conventional + assertion-flip/`TEST-CHANGE:` detector that blocks "going green by gutting `.Should()` asserts").
- **Do NOT port EDI's smart path→trait regex** (untracked, already drifted — would run zero tests while printing "passed"). Use a coarse regenerable affected-project map.
- **Framing:** hooks are an optimization that keeps CI green + saves box runs. **Never** enforcement — agents `--no-verify` / clone fresh / use Codex worktrees. The server gate is the only wall.

### ENFORCEMENT — exactly how broken code is stopped
- **Buy GitHub Pro ($4/mo).** The only unlock — branch protection **and** rulesets 403 on Free for these private personal repos (proven live).
- **`main` ruleset (both repos):** require a PR (0 approvals — solo); **require status check `ci-pass`**; require conversation resolution; block force-push + deletion; restrict direct push; **empty bypass / Include administrators ON** — an agent acting as owner *cannot* merge a red PR. Leave **"require branch up-to-date" OFF** (avoids rebase churn at 11 merges/day; the `push: main` backstop catches the rare semantic-merge red).
- **CODEOWNERS** over `.github/**`, `scripts/hooks/**`, `deploy/**` → owner review required. On a solo repo GitHub blocks self-approval, so an agent **cannot merge a workflow/trigger/runner edit unreviewed** — this makes `guardrails` non-removable.
- web break → `web` job red → `ci-pass` red → merge blocked. .NET break → `dotnet` job red → blocked. Prod → deploy `verify` refuses any non-green-on-main SHA + `environment` approval + `v*` tag ruleset.
- **Spending floor:** GitHub-hosted Actions spending limit = **$0** so any accidental hosted job *fails to start* instead of billing.
- **Nightly `gh api` probe:** assert the ruleset is active w/ empty bypass + `ci-pass` configured; page if enforcement silently drops (Pro lapse).
- **Remains bypassable (honest):** local hooks (`--no-verify`, by design); the layer above code (owner-token agent editing billing/ruleset/Pro itself — watched by the probe, not fully sealed); quarantine mislabeling (mitigated by CODEOWNERS-reviewed test changes + nightly).

---

## (C) MINUTES & BOX

- **GitHub-hosted metered minutes/month: 0.** Everything runs on `rental-build-box`. Hosted spending limit = $0 makes hosted spend impossible by construction. The 2,000-min burn **cannot recur**: deploy is tag/verify-gated, per-branch-push CI is gone, and re-adding either needs a CODEOWNERS-reviewed merge.
- **Shadow compute (what hosted *would* have billed) for RC at measured volume:** ~3,700–4,000 compute-min/mo → blows Free in ~8–10 days and exceeds even Pro, ~**$13–32/mo overage for RC alone**, plus EDI. The false economy to reject.
- **Self-hosted build box still needed? YES.** (1) At ~11 merges/day a hosted gate re-creates the exact blowout; the ~€14/mo Hetzner box (already paid) is the cheapest correct option. (2) The deploy *must* build 3 images somewhere that isn't the 3.7 GB VPS. **Explicitly reject the deploy.yml-header note about "moving CI to GitHub-hosted."**
- **Hedge** for the postponed $0.002/min self-hosted-private charge: if it returns (~$3–8/mo), raise the spending limit to a small non-zero + alert (a $0 limit would then fail-start self-hosted jobs and block merges).
- **Reduce SPOF/contention:** 2nd runner / account-level runner group for both repos; disk + uptime alerts; isolate untrusted PR-CI builds from the deploy GHCR token/cache. Do **not** add a hosted fallback (the minutes trap).

---

## (D) PREREQUISITE — make `green` honest *before* flipping the check to required

1. **Baseline triage (one box run):** categorize pass / hard-fail / flaky. RC is green on main today, so light; the known LLM-timeout race is the main flake.
2. **Quarantine, never silent-skip:** tag known-flaky/parked tests `[Trait("Quarantine","true")]` (single allowed value; guardrails asserts trait-value == filter string). Each lists owner + reason + a tracked Notion task in `QUARANTINE.md`. Gate runs `--filter "Quarantine!=true"`.
3. **Force the Docker-gated tests to run:** Postgres present ⇒ the 4 RLS/view/concurrency `[SkippableFact]`s execute; meta-assert fails the gate if the integration suite skips wholesale.
4. **Fix flakiness at source** (fake clock/LLM for the timeout race); **no auto-retry in the gate**.
5. **Hygiene nightly (self-hosted, $0, non-gating):** full suite incl. quarantine; fails if a quarantined test has passed N nights (forces un-quarantine) or its task closed; a CI check fails if `QUARANTINE.md` grows without a linked issue. Adding a quarantine trait is CODEOWNERS-reviewed.

Only after the default lane is genuinely green do you promote `ci-pass` to a **required** check.

---

## (E) EDIPLATFORM — same structure, *not* the same switch state

EDI needs it more (it **deleted ci.yml**, nightly is red, ~356 commits sit on an unmerged branch with **zero CI**) but its baseline is RED. **Copy the structure, not the switch state**:
1. Re-create `ci.yml` as an **advisory** (non-required) check first.
2. Quarantine the parked-red SAMSCLUB test + E2E PendingAudit skips with task ids; drive the lane green.
3. Land the 356-commit branch via smaller PRs through the advisory gate.
4. **Then** promote `ci-pass` to required + CODEOWNERS, and move EDI's staging deploy OFF `push: main` to the same tag/verify/dispatch model (**confirm with owner** — continuous staging may be relied on). One account-level runner group serves both repos.

---

## (F) IMPLEMENTATION CHECKLIST (ordered)

1. **Upgrade jcoble → GitHub Pro**; set Actions **spending limit = $0**.
2. **RC triage:** quarantine the LLM-timeout flake; add `QUARANTINE.md` + Docker-skip meta-assert; confirm default lane green on the box.
3. **Rewrite `ci.yml`:** `pull_request:[main]` + `push:[main]` + dispatch; `changes` + per-job `if`; `dotnet`(Postgres) / `web`(svelte-check + vite build) / `mobile` / `guardrails`(allowlist) / `ci-pass`; add disk-reclaim. **Delete `push:**`/`pull_request:**`.**
4. **Add deploy `verify` job** (ancestor-of-main + `ci-pass` green, tag *and* dispatch) + `environment: production`.
5. **Add `CODEOWNERS`** for `.github/**`, `scripts/hooks/**`, `deploy/**`; create the **`main` ruleset** + **`v*` tag-creation** ruleset; verify owner is *not* auto-bypassed.
6. **Port hooks** to tracked `scripts/hooks/` + installer (`core.hooksPath`), default-ON, with `commit-msg` assertion-flip guard; wire into `start-dev.sh` + pnpm postinstall.
7. **Add the nightly:** full-suite hygiene + quarantine-expiry + enforcement-active probe + disk/uptime alert. Debounce `notion-tasks.yml` (keep self-hosted).
8. **Harden the box:** 2nd runner/group, separate CI vs deploy workspace/token, orphan-container sweep cron.
9. **EdiPlatform:** repeat 2–7 as **advisory first**, green-up, land the branch, then promote + move staging off `push: main`.

---

## (G) TRADEOFFS — what we deliberately are NOT doing

- **Not moving CI to GitHub-hosted.** At this volume it re-creates the 2,000-min blowout (~$13–32/mo RC-only). Box is cheaper *and* mandatory for off-VPS image builds.
- **Not eliminating the box.** At ~11 merges/day it's the cheapest correct option + structurally required. Lever if volume drops ~3–4×: go hosted with CI reduced to `web` + `dotnet build` only (full tests to nightly), fitting Pro's 3,000 min — but only then.
- **Not enabling "require branch up-to-date before merge"** (churn at 11/day; backstop catches the rare two-green-to-red-main).
- **Not a `$0` spending limit once self-hosted becomes metered** (would fail-start the gate). Switch to small non-zero + alert if the charge returns.
- **Not relying on a self-referential inline guard** — CODEOWNERS review on workflow files is the real protection; `guardrails` grep is a second tripwire.
- **Not auto-retrying flaky tests; not porting EDI's drifted smart-filter regex.** Both hide failure.
- **Not porting EDI's required-gate switch to its red baseline** — advisory-first, green-up, then enforce.

**Residual risk accepted:** the single box is a SPOF — if offline, required `ci-pass` sits Pending and merges stall (mitigated by alerts, 2nd runner, scripted break-glass — not eliminated). The owner-token-edits-billing/ruleset layer is watched by the nightly probe but not fully sealable — inherent to a solo-owner private-repo setup.
