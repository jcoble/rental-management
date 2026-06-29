# E2E Orchestrator State — Rental Command

## Run
- ID: 2026-06-28-001
- Status: COMPLETED — 19 found · 18 fixed+merged+gated+verified · 1 bucketed (OP-2). Summary: Results/2026-06-28-001/summary.md
- Verification: wave-1 8/8 PASS, wave-2/3 10/10 PASS (both High each pass). No FAILs, no regressions.
- Mode: no-containers — single running dev app, isolated playwright-cli sessions per agent
- App: Web https://localhost:5667 · API https://localhost:5666 (RESTARTED with wave-1 fixes merged)
- Exploration budget: 6. Used: 4 (01, 03, 05, 06). Remaining: wave 3 = 02, 04.

## ⚠ Environment & API restart recipe (LEARNED — incident fixed)
- Dev stack = tmux session `rental-main-stack` running scripts/start-dev.sh (with ALLOW_EXTERNAL_NOTIFICATIONS=1).
- start-dev.sh does NOT hot-reload backend, so to run merged API code I run MY OWN API on :5666.
- INCIDENT: my first API restart omitted the mkcert cert → API served self-signed `CN=localhost` →
  web SSR (strict Node fetch to /auth/me) rejected it → every protected route bounced to /login →
  all browser testers blocked. FIXED by relaunching with the Kestrel mkcert cert. Correct recipe:
    export ASPNETCORE_ENVIRONMENT=Development DOTNET_ENVIRONMENT=Development
    export Upload__BasePath="$PWD/uploads"
    export Kestrel__Certificates__Default__Path="$PWD/web/.cert/api-cert.pem"   # <-- THE FIX
    export Kestrel__Certificates__Default__KeyPath="$PWD/web/.cert/api-key.pem"
    export Notifications__<all providers>=""        # blank — no real mail/SMS during tests
    # do NOT export ConnectionStrings (User-Secrets supplies it)
    dotnet build RentalCommand.Api   # only if code changed
    dotnet run --no-build --project RentalCommand.Api -- --urls "https://localhost:5666;http://localhost:5665"  (background)
  VERIFY: `curl --cacert $(mkcert -CAROOT)/rootCA.pem https://localhost:5666/health` = 200 (strict TLS),
  AND GET https://localhost:5667/units with a valid rc_access_token cookie = 200 (not 303). Both pass now.
  CONFIRMED via real browser: verifier logged into :5667 ("Fill dev login" + Sign In) → Dashboard, no
  /login bounce; all 8 checks re-passed on canonical web; the earlier SignalR negotiate-500 was a
  workaround-instance artifact, NOT a defect (canonical negotiate=200).
- Current API = my relaunch (bg task b0ich7yjb): merged code, mkcert cert, notifications BLANKED.
- Current ENGINE = my relaunch (bg task bjmv23bql): RentalCommand.Engine died ~23:02 in the stack churn
  (no scan extraction → uploads stuck Pending). Restarted with Development env + Upload__BasePath="$PWD/uploads"
  (MUST match the API's upload path so the worker reads the blob the API wrote) + notifications BLANKED +
  Engine user-secrets (ConnectionStrings + Assistant:Provider LLM). Confirmed: drained stuck draft 270 →
  Reviewing; OutboxDispatchWorker logs NotificationDeliverySuppressedException = mail safely suppressed.
- Web (:5667) = tmux-managed; HMR already serves merged frontend. (API + Engine are now MY processes.)

## Wave-2 bugs preview (from tester1b, before its full report)
- SCAN-1 [Low] GET /scans/{id}/file?full=1 → HTTP 400 ("'1' is not valid"); only ?full=true works, but
  ScanController.cs:547 documents ?full=1. Fix: accept 1/0 (bind bool from "1") or update the doc string.
  (Expense scan path fully verified end-to-end by tester1b: draft 269 → expense 1034, all fields persisted.)
- NOTE for user: the tmux stack had ALLOW_EXTERNAL_NOTIFICATIONS=1 (real providers). My API blanks them
  so the test run can't send real emails/SMS to real addresses (e.g. redacted-person@example.invalid on portfolio 1).

## ⚠ Shared/volatile DB environment (discovered mid-run)
The dev Postgres `rentalcommand` is SHARED with other concurrent agentic sessions. Mid-run it was
reseeded/expanded to 18 portfolios (`Explore Money/WO/A/B/Lease/UX`, `Fix UX`, `FixFH UI`, `Scan *`,
`TSK397 *`, …); lease IDs now max ~219 and admin's portfolio 1 is empty of leases. **I did NOT cause
this** — API restart did NOT reseed (log: "No migrations applied; users present, skipping seed") and
the unit tests use SQLite/Testcontainers, not this DB. No real data lost (regenerable dev data).
Consequence: testers/verifiers must CREATE their own data and not rely on pre-existing IDs.

## Operating rules
- R1 (RAM): ≤2 fixers (dotnet builds) at once. R2 (merge gate): merge+restart API only in a quiet
  window (no tester/verifier exploring). R3 (batch merges, one restart). R4 (remove worktree right
  after its merge). R5 (volatile DB): agents create + uniquely-mark their own data.

## Slots / active agents (exploration budget 6/6 used)
- tester1 (explorer-tester1b): DONE — 05 scan: 2 bugs (SCAN-1/2) + 2 potential (P-1/P-2 bucketed) → fixer-scan
- tester2 (explorer-tester2b): DONE — 06 portal: 2 bugs (Med+Low) → fixer-portal
- tester3 (explorer-tester3): DONE — 02 deposits: 3 bugs (1H/1M/1L) → fixer-deposits
- fixer-deposits: DONE — e2e-fix-deposits: 3 commits (cd86b3d status-inversion +tests, 062af6d 1099-year-scope [frontend path, no AccountingService change], da039f0 cents), clean — awaiting batch merge
- tester4 (explorer-tester4): DONE — 04 maintenance: 3 bugs (1H/1M/1L) → fixer-maintenance
- fixer-maintenance: running — WO-1/2/3 in worktree e2e-fix-maintenance
=> ALL 4 TESTERS IDLE → QUIET WINDOW OPEN. Exploration budget done (6/6). Do NOT dispatch new testers;
   keep the window open so I can batch-merge + restart once fixer-maintenance commits.
- verifier (verifier-wave1): idle/available (wave-1 verification done 8/8 PASS) — reusable for wave-2/3 verify
- fixer-portal: DONE — e2e-fix-portal: 2 commits (c07e2fe balance-Failed +test, 92a179f portal-redirect), clean — awaiting batch merge
- fixer-scan: DONE — e2e-fix-scan: 2 commits (b438e45 full=1/true, 1807117 open-original), clean — awaiting batch merge

## ✅ Wave 2/3 — ALL 4 BRANCHES MERGED to main (HEAD beb458a) + gate green
Merge commits: 1c605a3 (portal), 48ed367 (scan), 9d4973e (deposits), beb458a (maintenance).
Gate: dotnet build 0 err · 70 focused tests pass · svelte-check 0 err. Worktrees removed + branches
deleted (R4). API restarted with mkcert cert (bg task bjmd5ufua) — health 200, strict-TLS 200, SSR
auth /units=200, SCAN-1 ?full=1=200 confirmed. Engine NOT restarted (no Core/Data/Engine changes).
LIVE VERIFICATION: verifier-wave23 running (10 fixes; prioritizing the two High: WO-1, DEP-1).
- PORTAL-1/2, SCAN-1/2, DEP-1/2/3, WO-1/2/3 — merged + gated + ✅ VERIFIED 10/10 (verification-wave23.md).
Then: merge serially → remove worktrees → rebuild+restart API & Engine (mkcert + Upload__BasePath
recipe) → verify (reuse verifier-wave1) → Phase 3 report.

## Scenario Tracker
- 01-money-leases.md: completed — 5 bugs → 5 fixed+verified (BUG-1..5)
- 03-operations-core.md: completed — 4 bugs → 3 fixed+verified (OP-1/3/4), OP-2 bucketed
- 05-scan-draft-confirm.md: completed — 2 bugs (SCAN-1/2) → fixer-scan; Expense path verified clean
- 06-portal-public.md: completed — 2 bugs (Portal BUG-1 Med, BUG-2 Low) → fixer-portal
- 02-deposits-owners-tax.md: completed — 3 bugs (DEP-1 H, DEP-2 M, DEP-3 L) → fixer-deposits; owner stmt reconciles clean
- 04-maintenance-appointments.md: completed — 3 bugs (WO-1 H, WO-2 M, WO-3 L) → fixer-maintenance

## Bug Pipeline

### ✅ VERIFIED — merged to main + gated + live-verified (verifier-wave1: 8/8 PASS)
Merge commits: 95ff4ab (esign+copy), e981e59 (partial), 7d96241 (ops). Gate: dotnet build 0 err,
svelte-check 0 err, 46 unit tests. Live verification report: Results/2026-06-28-001/verification-wave1.md.
- BUG-1 [H] Unit outstanding for Partial → remainder — PASS (300/300/300 reconciled; re-conf 400/400/400)
- BUG-2 [H] signature-queue 500→200 on null envelope — PASS LIVE (GET /leases/240/signature-queue=200; the
  Postgres-specific bug SQLite unit tests could NOT catch — this live check is the real proof)
- BUG-3 [M] RentTab quick-post drops "Partial" — PASS (no Partial in Status options)
- BUG-4 [M] ledger shows collected portion of a Partial — PASS (companion +AmountPaid line shown)
- BUG-5 [L] "Total Collected" tooltip copy — PASS
- OP-1 [H] property delete guarded — PASS (DELETE→400 "remove the unit first", property survives)
- OP-3 [M] clear 409 for duplicate unit number — PASS (`Unit number "101" already exists…`)
- OP-4 [M] tenant delete blocked on NoticeGiven — PASS LIVE (DELETE→400, tenant survives)
=> WAVE 1 CLOSED: 9 found · 8 fixed+merged+gated+verified · 1 bucketed (OP-2).

### Needs User (see NEEDS-USER.md)
- OP-2 [M] soft-deleted unit number unreusable — needs EF MIGRATION (auto-applies in prod) — deferred.
- BUG-1 current-lease scoping (product call); Money KPI vs date-filter (design); minor polish; sandbox
  orphan rows; 7 pre-existing worktrees with uncommitted work.

### Wave 2/3 — Auto-Resolving (in worktrees, awaiting batch merge in next quiet window)
- PORTAL-1 [Med] tenant balance/overdue excludes Failed payments (self-contradictory $0/1 card) —
  PortalService.cs:75-86 + portal/+page.svelte:96-101/277-281 — fixer-portal — implementing.
  SEMANTICS FLAG: fix encodes "Failed = still owed" (matches app's isPayable/UI). Confirm if intended.
- PORTAL-2 [Low] staff users get a broken tenant portal (6×403) — (portal)/+layout.server.ts:10-21 —
  fixer-portal — implementing (redirect non-tenants to staff /).
- SCAN-1 [Low-Med] /scans/{id}/file?full=1 → 400 (bool bind) — ScanController.cs:535 — fixer-scan — implementing
- SCAN-2 [Med] image originals unreachable in UI (proxy/Open-in-new-tab serve thumbnail only) —
  scan-file/[id]/+server.ts:31 + scan/[draftId]/+page.svelte — fixer-scan — DONE (committed)
- DEP-1 [H] deposit return status INVERTED (partial→Returned, full-withhold→PartiallyReturned) —
  SecurityDepositService.cs:211 (+audit log) — fixer-deposits — implementing
- DEP-2 [M] Tax 1099 checklist ignores year (all-time sum → false $600 flags) — AccountingService.cs:911
  + tax/+page.svelte:34 — fixer-deposits — implementing
- DEP-3 [L] owner-statement/tax whole-dollar rounding (cols don't sum) — owners-report/+page.svelte:37 +
  tax/+page.svelte:51 — fixer-deposits — DONE (committed)
- WO-1 [H] future-scheduled WO can't be Completed (auto-stamp trips scheduled-guard) —
  WorkOrderService.cs:454-461/544 — fixer-maintenance — implementing
- WO-2 [M] "vendor has the job" banner on mere assignment (no dispatch) — work-order-dispatch.ts:41 +
  WorkOrderDetail.svelte:414 — fixer-maintenance — implementing
- WO-3 [L] WO edit truncates RequestedAt/CompletedAt to UTC-midnight — WorkOrderDetail.svelte:87 +
  schemas/index.ts:332 — fixer-maintenance — implementing

### Found (awaiting triage)
(tester1b scan, tester3 deposits, tester4 maintenance still running)

## Worktrees (this run)
- None active (e2e-fix-partial-payments / -esign-queue-copy / -ops merged + removed + branches deleted).
- Pre-existing 7 (other sessions, uncommitted) — untouched, in NEEDS-USER.md.

## Next actions (compaction recovery)
1. When verifier-wave1 returns: record PASS/FAIL per fix. Any FAIL → re-open (new worktree → fix →
   re-gate → re-verify). Confirm BUG-2 is 200 on live Postgres.
2. When tester1b/tester2b return: triage wave-2 bugs (same criteria), dispatch ≤2 fixers in worktrees.
3. Next quiet window (no tester/verifier running): batch-merge wave-2 fixes → remove worktrees →
   rebuild+restart API → verify.
4. Wave 3: dispatch 02-deposits (tester1) + 04-maintenance (tester2) — exploration budget reaches 6.
5. Then Phase 3 report: summary of bugs found/fixed/verified + the NEEDS-USER bucket.

## Report Queue
- Exploration scenarios: 4 dispatched (01, 03 done; 05, 06 running). Verification: 1 done (8/8 PASS).
- Bugs found: 9 · fixed+merged+gated+VERIFIED: 8 · needs-user: 1 (OP-2) + design/polish · failed: 0
- Pending: wave-2 reports (05 scan, 06 portal) → triage → fix → verify; then wave 3 (02, 04).
