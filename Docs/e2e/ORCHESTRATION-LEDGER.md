# E2E True-to-Life Test — Orchestration Ledger

> **Hard-copy source of truth for the whole initiative.** If a context is lost or things go
> sideways, START HERE. Keep it current.

- **Last updated:** 2026-07-01 (2nd compaction — foundations merged, gaps building in worktrees).
- **Orchestrator:** main session (this conversation). Repo: `/Users/blackcolours/dev/work/rental-management`.

## ⚡ CURRENT STATE — READ FIRST (compaction snapshot, 2026-07-01)

**WHERE WE ARE (2026-07-01, 2nd compaction — "before we run the tests"):**
- **Foundations MERGED to `origin/main` (`a9c0e033`), verified clean:** Clock **TSK-615** (PR #452 `b03c3433`),
  SignalR-backplane **TSK-624** (PR #453 `3e7d46af`), Data-access-hardening **TSK-626** (PR #454 `a9c0e033`).
  main verified 4 ways: only these 3 lanes, **NO Codex/grid contamination**. SignalR sign-off still **OPEN** →
  **Codex runs the live web+phone/Flutter test** (it has the phone); TSK-624 kept **Doing** until it passes. Fix-ownership
  if it breaks: backend/backplane = me, Flutter client = Codex.
- **Now building: the 7 domain gaps**, each in an ISOLATED WORKTREE (see WORKTREE RULE below), incremental-merge per lane.
  **GapLane1 = F1 foundation + gap5 (app-fee income, TSK-621)** RUNNING (background) in worktree
  `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-621-gap5-appfee` off `a9c0e033`, full-stack backend+web+mobile.
  Task list #10-17 = its subtasks. Plan: `Docs/superpowers/plans/2026-07-01-domain-gaps-implementation.md` (49 tasks, seq
  **F1-4→G5→G1→G2→G3→G4→G6→G7**; locked: sequential, gap3=prorate-down, app-fee=Schedule-E income, gap4/6 values=placeholders,
  **MOBILE=BUILD-NOW full parity**; gap4 must precede gap6; corpus-revision triggers = gaps 3,4,5,6,7).
- **This session (orchestrator) sits in the PRIMARY checkout** `/Users/blackcolours/dev/work/rental-management` on the
  **inert** `tsk-616-626-grids-dataaccess` branch (local HEAD `117d4420` = merged-626 + stranded local grid commits +
  `117d4420` OwnerStatement-sargable to **cherry-pick to main later**). **DO NOT push this branch.** Verify state against
  `origin/main` (GitHub), never local.
- **CODEX (separate, parallel — user drives it):** owns grids (TSK-616), the mobile modules (TSK-633/634/635), + the SignalR
  phone verification, on its own branches/worktrees. **My gap builders stay OFF Codex's files** + flag any shared-file touch or
  hook contamination. Notion auto-syncs Done on merge but wrongly closes tasks whose id is in a shared branch name → correct
  manually (kept TSK-624 + TSK-616 Doing).
- **NEXT = user's "run the tests."** Deterministic **Playwright spine is DEFERRED until the app stabilizes** (post-gaps —
  user: "things regress a lot right now"); exploratory/negative track uses an LLM agent (adaptive during churn). SA's **Tester
  Scenario Catalog v1 DONE** (`e2e/corpus/scenarios/`, 118 scenarios, surface-tagged [UI]/[DEV-CLOCK]/[WORKER]/[SEED], 5
  product-gap findings PG-1..5) = the blueprint the Playwright spine compiles from.
- **RESUME:** read this block → WORKTREE RULE + MERGED-TO-MAIN + DEV-CONTRACT blocks below → `git log origin/main` → check
  `GapLane1` (its commits in the worktree) → Notion "Rental Command". Merge each gap lane via normal `gh pr merge` (**NO --admin**;
  user approves merges), then next lane off updated main. Integrations TSK-625 gated on user's Stripe-test + QB-sandbox logins.

**Master Simulation Clock (TSK-615) build status:**
- ✅ **Phase A** backend infra DONE + verified (13 tests): A1 `c074e7a6`, A2 `9c53ca97`, A3 `1b813db7`,
  A4 `2a1206f1`, A5 `ae3e75b9`, A6 `39a650ec`, A7 `571e69d2`. Files: `Core/Time/*`
  (ClockMode, TimeProviderExtensions, ClockState, IClockStateProvider, IAppTimeZoneProvider),
  `Core/Entities/SimulationClock.cs`, `Api/Simulation/*` (ClockStateProvider, ClockStateRefresher,
  SimulationTimeProvider, AppTimeZoneProvider, SimulationClockServiceCollectionExtensions,
  SimulationGate), migration `AddSimulationClock`. Gated `Simulation:Enabled && !IsProduction()`;
  S1 auth-pin (cookie + security-stamp → System) API-only.
- ✅ **Phase B** control + command-bridge DONE + verified (12 Engine + 10 Api): B1 `ceed68cc`
  (DevClockController), B2 `9ddb265e` (SimWorkerCommand + migration), B3 `e884e560`
  (SimWorkerCommandWorker + SimWorkerRegistry), B4 `ef175208` (DevWorkersController long-poll).
  `[SimulationOnly]` convention strips dev routes in prod; `SimWorkerKeys` in Core.
- ✅ Plan must-fixes folded in: `df978d53` (see plan "Review incorporated" section).
- ✅ **Phase C** web shim + dev panel DONE + committed + verified: C1 `4e45b948` (`$env/dynamic/public`
  gate M2 + inline `app.html` Date shim via `hooks.server` `transformPageChunk`), C2 `98d40bfe`
  (`sim-clock-client.ts` ~1s anon poll + `hooks.client.ts` + **RealDate for client auth-expiry M3** in
  `auth.svelte.ts:127`), C3 `38accd6f` (`SimClockPanel.svelte` mounted+gated in root `+layout.svelte`).
  Orchestrator-verified: `pnpm check` 0/0, `pnpm build` ✓ (flag unset = M2-critical); placeholder above
  `%sveltekit.head%`; panel reachable (DoD). Live flag-on browser verify deferred to Phase E.
- ✅ **Phase D** the ~355-site wall-clock sweep — **COMPLETE + verified** (ClockSweeper, D1–D5; 919 tests green;
  whole-sln build 0 errors; final coverage 355→47 = 7 comments + 37 documented keep-real + 3 POCO/static follow-ups;
  D5 `ab9d6e78`; caught+reverted 2 auth-adjacent slips — LastLoginAt + e-sign link-TTL). Baseline 355 sites (Core 41, Data 1, Api 276, Engine 37). **Progress: D1 ✅ `5d4f2706`** (8 Engine
  automation svcs virtualized + business-tz routed via `IAppTimeZoneProvider`; NoticeDraftGen left untouched =
  no wall-clock site, delegates to Api NoticeDraftService swept in D4; 93/93 Engine.Tests green; Engine 37→16
  left). **D2 ✅ `dd16e2ba`** (Engine workers + OutboxMessagePublisher; EF hoist on ScanProcessingWorker.MarkFailedAsync;
  watchdog/heartbeat + outbox backoff + OAuth horizon correctly kept-real — **Engine now fully swept**, 10 remaining
  hits all verified keep-real; 93/93 green). **D3 ✅ `c5a0c691`** (~80 sites across 9 Api money/report services;
  2 static-context sites fixed correctly — `ReportsService.ResolveRange` threads `now`, `AccountingImportService.MarkImported`
  de-static'd — both verified virtualized not reverted; OAuth state-row keep-real extended to `:125`; 806/806 Api.Tests
  green; Api 276→196 left). **D4 ✅ `caa8539e`** (130 files, 58 prod+72 tests; recovered from the mid-D4 stop; auth-batch keep-real
  VERIFIED — LastLoginAt(both)/JWT/e-sign-link-expiry/OAuth-token real, record CreatedAt virtualized; a subagent's
  wrongly-virtualized LastLoginAt was caught+reverted; Api NoticeDraftService now virtualized; 806/806 green; Api
  196→30 left, all documented keep-real + 2 POCO follow-ups + NotificationHub). **On D5** (Data AuditSaveChangesInterceptor
  ctor+TimeProvider underway + NotificationHub:138 + Core POCOs) → `dotnet build RentalCommand.sln` + final coverage
  re-grep, then Phase E. Sequence D1 Engine automation svcs (9) + route business-tz via `IAppTimeZoneProvider` → D2 Engine workers → D3 API
  money/reports → D4 API remainder+controllers+scanning → D5 Data audit-interceptor+NotificationHub+Core
  POCOs. Charter carries: classification (keep-real auth/token/backoff/perf/log incl. M6 list), M5 test-ctors
  same commit + add `Microsoft.Extensions.TimeProvider.Testing` to Engine.Tests+Api.Tests, EF hoist-to-local
  (`ScanService.cs:1701`, `Engine/Workers/ScanProcessingWorker.cs:379`), `TodayUtc()`=DateOnly type-care,
  Core POCOs→move default-timestamps to service layer or list as documented follow-up (no silent skip),
  explicit-path commits only. Pings main after each task commit; final coverage re-grep must leave only
  documented keep-real/deferred hits.
- ✅ **Phase E** verify — **COMPLETE + all green** (2026-07-01, isolated stack: DB `rentalcommand_e2e` on
  edi-postgres:5432, API :5766 / web :5767, both sim flags on). Proven live: (1) `GET /dev/clock` mapped when
  sim-enabled; (2) `SimulationClocks` row persists `Frozen@2025-01-01` (cross-process sync); (3) portfolio
  created while frozen → `createdAt 2025-01-01` vs boot-seeded default `2026-07-01` (service-layer virtualization);
  (4) AuditLog row `Timestamp 2025-01-01` (D5 interceptor "story of record"); (5) advance 2025-01-01→2025-02-05;
  (6) `run-due` command-bridge → Engine `Done`, `requestedSimUtc 2025-02-05`, `created:0` (fresh DB) — API→Engine
  worker trigger fires at sim-time; (7) web Date shim injected into served HTML when flag on; (8) **prod-guard**:
  boot with `Simulation:Enabled=false` → `/health` 200 but all `/dev/*` routes 404. **CLOCK TSK-615 A–E DONE.**
  SA replay-driver wiring (corpus steps 4/5) DEFERRED to the testing phase (gated on the 7 gaps + SignalR landing);
  don't wake SA for it yet.

**DEV ENDPOINT CONTRACTS (final, Phase B) — for SA's replay driver + the tester.** All `/api/v1/dev/*`,
mapped only when `Simulation:Enabled && non-prod`:
- `GET /dev/clock` [AllowAnonymous] → `{simNowUtc, mode(Real|Frozen|Offset), timeZoneId?, offsetSeconds}`.
- `POST /dev/clock/set` [Admin] `{instantUtc?|date?, timeZoneId?, mode?="offset"|"frozen"}` (instantUtc wins; 400 if neither).
- `POST /dev/clock/advance` [Admin] `{days?,hours?,minutes?,seconds?}` (negatives ok).
- `POST /dev/clock/{freeze|unfreeze|reset}` [Admin] no body. All mutations return ClockStateResponse.
- `POST /dev/workers/{key}/run-once` [Admin]; keys: rent-charge, notice-draft, lease-expiry-reminder,
  late-fee, autopay, debt-service, recurring-expense, recurring-maintenance, daily-briefing. Enqueue +
  long-poll (250ms→30s) → SimWorkerCommandResponse; 503 on timeout.
- `POST /dev/workers/run-due` [Admin] → order: rent-charge→notice-draft→lease-expiry-reminder→late-fee→
  autopay→debt-service→recurring-expense→recurring-maintenance (daily-briefing excluded); `{created:sum}`.
- `GET /dev/workers/commands/{id}` → SimWorkerCommandResponse `{id, workerKey, status(Pending|Running|
  Done|Error), requestedSimUtc, result?, error?, createdRealUtc, completedRealUtc}`.
- Replay-driver invariants (SA): set `ownerEntityId` on every property; `RentTrackingStartMode=BackfillFromLeaseStart`;
  per-payment mark-paid; `PUT /notifications/settings` to enable at T0; scan-month rent-check "replace";
  business tz default America/New_York.

**MERGED TO MAIN (2026-07-01):** clock PR #452 `b03c3433` · SignalR PR #453 `3e7d46af` · data-access PR #454 `a9c0e033`
→ `origin/main` now = `a9c0e033`. **Gap lanes must branch off UPDATED main** (fetch first). Notion: TSK-615 + TSK-626 →
Done; **TSK-624 stays Doing** (backplane merged but **live phone+web/Flutter client test PENDING** before sign-off);
**TSK-616 stays open** (Codex owns grids — don't let the branch-name auto-sync close it). Merge used normal `gh pr merge`
(no admin bypass). SignalRBuilder/GapsPlanner stood down; DataAccessBuilder reverting its uncommitted grid edits then done.

**⛔ WORKTREE RULE (learned the hard way 2026-07-01):** ALL implementation lanes run in **ISOLATED git worktrees** under
`/Users/blackcolours/dev/work/worktrees/rental-management/<task-slug>` off clean `origin/main` — **NEVER** on a branch checked
out in the primary checkout `/Users/blackcolours/dev/work/rental-management`. **Root cause of the grid/hook contamination:** the
primary checkout is the shared default location, so hooks/Codex write into whatever branch is checked out there (that's how the
TSK-616 grid rollout landed in DataAccessBuilder's tree unprompted). `origin/main` stayed clean only because merges came from the
*pushed* branch, not the local tree. **Fix: each lane in its own worktree.** Verify state against `origin/main` (GitHub), not local.
Clean up each worktree the moment its lane merges (disk rule). **Gaps IN PROGRESS: `GapLane1` (F1 foundation + gap5/TSK-621 app-fee)
in worktree `tsk-621-gap5-appfee` off `a9c0e033`.**

**Build order (serialized, one .NET build slot):** clock **✅ A–E DONE (verified)** → **SignalR (TSK-624) ✅ DONE**
(SignalRBuilder, 3 commits `fbf599f7`/`18458b9d`/`3c967fe5` on `tsk-624-signalr-backplane`; Option A LISTEN/NOTIFY +
API-hosted broadcaster; **cross-process Testcontainers-verified**; live-browser E2E folded to the testing phase) →
**data-access (TSK-626) 🟡 IN PROGRESS** (`DataAccessBuilder` on `tsk-616-626-grids-dataaccess` off
tsk-624; C1✅ `d2b4df85` → H2/H3✅ `e892396e` → H1✅ full-stack `b673e362`/`a8726721`/`6bc8734a` → 10 indexes✅ `5c43c540`
→ M1-6+LOW+watch-items in flight; **slotted BEFORE gaps** to harden+index the base) · **Grids date-filter wiring
(TSK-616) → CODEX** (user 2026-07-01; separate branch — DataAccessBuilder told to SKIP grid-wiring #6 to avoid collision;
its H1 lease-payments paging fix stays). ⚠️ **Codex now doing active writes (grids + mobile)** — watch for overlap when
gap builders touch web/mobile. → **7 gaps (TSK-617-623) 🟡 PLANNING** (`GapsPlanner` →
`Docs/superpowers/plans/2026-07-01-domain-gaps-implementation.md`; seq F1-4→5→1→2→3→4→6→7; dispatch builders when plan
lands + slot frees) → integrations (TSK-625) **GATED on user's Stripe-test + QB-sandbox OAuth logins** → corpus revision (SA) →
tester harness → bug pipeline. **Rule: 1 build lane at a time; read-only planning runs parallel.** Post-clock
build-out started 2026-07-01; each lane branches to stack on the prior toward an "E2E-ready" integration line
(recommend merging clock→main soon to keep the stack manageable). Testing GATED on gaps+SignalR+grids landing.

**Agents (resume by NAME via SendMessage):** **ClockSweeper** (`ClockSweeper@session-11accc31`) — running
Phase D sweep (background). **ClockBuilder** — DONE (built+committed Phases A/B/C), stood down.
**SA/ScenarioArchitect** — **Tester Scenario Catalog v1 DONE**
by `ScenarioArchitect-2@session-11accc31` (now re-parked). `e2e/corpus/scenarios/` = README + act1/2/3 md +
`scenarios.json` machine index; **118 scenarios + 4 templates** (Act1 onboarding 9, Act2 operations 92, Act3
reconciliation 17), cent-exact cross-refs to `expected/*.json`+`events.csv`, **16 gap-flagged `PENDING TSK-*`**.
Stale-lease-numbering errata added to calendar + design §3.2 (catalog is now authoritative tester+replay set).
Gap placeholders to confirm w/ user at corpus-revision time: GAP4 P09 roof $9.9k@08-01 (~$135 partial-yr depr,
off-spine), GAP6 P08 sale $135k@10-31 (off-spine + §1250), GAP7 eviction = ALTERNATE superseding L14 Sep cure
(never both on one DB). This was NEW authoring, distinct from the still-PAUSED corpus-DATA regen. NOTE: original
`ScenarioArchitect` name still registered (parked) — `-2` was the active one. Phase-1 + Phase-2 corpus DONE on disk (`e2e/corpus/`, incl. `events.csv`
1573 rows, `expected/*.json` cent-exact, 110 artifacts); resume file `e2e/corpus/SA-STATE.md`; still awaiting the
"clock endpoints live" ping (replay steps 4/5) + the post-features "revise corpus" re-brief (owner-distribution-as-record,
real Stripe autopay, + the 6 gap capabilities to exercise). Reviewers (Spec/Plan/SignalRDoctor/GridFilterScout/
DomainGapsArchitect/DataAccessAuditor) — DONE, stood down.

**Data-access hardening backlog** (all mechanical, no decisions): C1 outbox-dedup, H1 lease-ledger paging
(= the grid lease-payments defect), H2/H3 worker N+1s, M1-M6, 10 indexes. See
`investigations/2026-07-01-data-access-audit.md`.

**RESUME AFTER COMPACTION:** read this section → `investigations/README.md` + its 6 reports →
`superpowers/plans/2026-07-01-master-simulation-clock.md` ("Review incorporated") → `git log tsk-615` →
Notion project "Rental Command". Next action: monitor **ClockSweeper**'s per-task Phase D pings → review
each commit (keep-real discipline, test-ctors compile, build+test green) → after D5 + coverage check,
run **Phase E** verification (start dev stack with `Simulation:Enabled=true`+`PUBLIC_SIMULATION_ENABLED=true`,
browser-verify panel/shim, set→advance→run-due→assert sim-dated records+audit, prod-guard 404, then **ping SA
"clock endpoints live"**). **Commit ONLY clock code to tsk-615 — never `git add -A`** (untracked `Docs/e2e/`,
`e2e/`, corpus specs, `web/_pw*.mjs` must NOT be swept into clock commits).

---

## North star

Run a true-to-life, **web-only** end-to-end exercise of Rental Command: a landlord comes in cold —
registers, confirms email, onboards ~2 years of history from scans/imports, then runs ~1 year of
day-to-day operations — while we **advance simulated time**. At year-end, reconcile the app's own
reports against an independently-kept **ground-truth ledger**. Testers drive the real web app like
real users; bugs → triage → fix → validate → Notion Done. **First make the system complete and
correct** (features + infra below), THEN test.

## Hard execution constraint (READ THIS)

**One working tree; .NET builds must serialize** (RAM rule + concurrent same-`.csproj` builds
corrupt bin/obj). So: **design/scoping runs in parallel (read-only agents); feature IMPLEMENTATION
serializes — one branch building at a time.** Never dispatch a swarm of building/testing agents.

## Definition of Done — NO half-done features (standing rule, applies to EVERY task)

The recurring failure to prevent: backend building-blocks get built but the **UI is never wired**, so
the feature is hidden and never used. A feature/task is DONE only when ALL of these hold:
1. **Backend** — entity/migration/service/API endpoint(s), portfolio-scoped, all queries DB-side.
2. **Web UI actually built AND wired into a reachable place** — the screen/control exists *and* is
   linked from the nav / menu / record page / grid toolbar so a user can navigate to it and use it.
   **No orphan components.** (This is the clause that's usually skipped.)
3. **Mobile (Flutter) parity** where the app already has that surface — build it, OR log an explicit
   deferral task with the id. **Never a silent skip.**
4. **Reachability verified in the RUNNING app** — open it in the browser and actually use the feature
   (create the record, see it render, see the filter work, see the live update arrive). Evidence, not
   assumption (superpowers:verification-before-completion + the /verify skill). "Builds + tests pass"
   is necessary, not sufficient.
5. Tests where meaningful; solution builds green.

Every implementation dispatch MUST carry this DoD. Reject any task that stops at "the endpoint exists."

## Subsystems & status

| # | Subsystem | Status | Notion |
|---|---|---|---|
| 1 | **Master Simulation Clock** (TSK-615) | 🟡 spec ✅ + plan ✅ committed on `tsk-615-master-simulation-clock`; **in plan-review** → then implement (holds the build slot first) | TSK-615 |
| 2 | **Scenario Corpus** (SA) | 🟠 Phase 1 ✅; **PAUSED** — 7 gaps will change it; awaiting post-features re-brief | (corpus design doc) |
| 3 | **7 domain feature gaps** (must ship before testing) | 🟡 captured; **in design** (DomainGapsArchitect) | 7 tasks |
| 4 | **Grid date/period filtering + server-side audit** | 🟡 captured; **in scoping** (GridFilterScout) | 1 task |
| 5 | **SignalR real-time fix** | 🟡 captured; **in investigation** (SignalRDoctor) | 1 task |
| 6 | **Real integrations test-env** (Stripe test + QB sandbox) | ⚪ captured; deferred to integration-testing phase (user does OAuth logins) | 1 task |
| 7 | **Tester harness** (browser-driving) | ⚪ not started (needs system complete) | — |
| 8 | **Bug triage/fix/validation pipeline** | ⚪ not started | — |
| 9 | **EdiPlatform clock port** | ⚪ deferred (mirror of #1) | — |

**Build order (implementation, serialized):** clock (#1, plan ready) → then the 7 gaps (#3, as designs land, dependency-ordered) + SignalR (#5) + grids (#4), interleaved one-build-at-a-time → integrations (#6) → corpus revision (#2) → tester (#7) → bug pipeline (#8). Testing is GATED on 1,3,4,5 landing.

## Agent roster (resume by NAME; never respawn fresh — loses context)

- **ScenarioArchitect** (SA, blue) — corpus + ground-truth ledger; long-lived; **parked**, awaiting "features landed → revise corpus" brief. Durable state on disk under `Docs/` + `e2e/corpus/`.
- **DomainGapsArchitect** (running) — designing all 7 gaps coherently + sequenced plan (read-only).
- **GridFilterScout** (running) — scoping grid server-side date/period filtering vs EdiPlatform + in-memory audit (read-only).
- **SignalRDoctor** (running) — root-causing flaky SignalR; prime suspect = no Engine→hub backplane (read-only).
- **PlanReviewer** (running) — reviewing the clock implementation plan (read-only).
- **SpecReviewer** (done) — reviewed the clock spec (approve-with-changes; folded in).
- **main** — orchestrator: triage, dispatch, decisions, ledger, Notion, and the serialized implementation.

## Decisions log

- Program of ~9 subsystems, not one project. Clock first (foundation); SA corpus in parallel. (User)
- Clock = .NET `TimeProvider` (not OS/DB clock); web = dev-only global `Date` shim (no client site edits); SSR + JWT/TLS stay real. Non-prod gated. (User-approved; spec reviewed.)
- Clock worker triggers = **command-bridge** (API enqueues → Engine executes) because API can't ref Engine, and to avoid destabilizing prod services. (Review MF-1.)
- **7 app gaps are real → implement BEFORE testing.** (User) Owner-distribution becomes a real record (resolves the "computed net" question).
- **Real integrations in testing:** Stripe **test mode** + QuickBooks **sandbox** (user does OAuth logins in Chrome); kept OUT of the cent-exact reconciliation spine (async timing) — tested as dedicated scenarios. (User)
- Reconciliation spine = **CY2025 cent-exact**; CY2023–24 imported + spot-checked. (User Q1=Y)
- SignalR "barely working" → **worth fixing before test** (would pollute test with false 'no live update' noise); likely a cross-process backplane gap. (User flag + orchestrator call)
- Grids: date/period filter + sort + paging must be **server-side / one SQL statement** (HARD rule); audit + fix any in-memory grid ops. (User + global rule)
- Guidance: "do it the best way / make the change if needed — rather fix now than have it silently break later." (User) → prefer robust structural fixes over shortcuts.
- **Tester Scenario Catalog** (`e2e/corpus/scenarios/`, SA) is now the **authoritative tester + replay instruction set** — the operations-calendar doc + design §3.2 prose defer to it on conflict. Principle: author to the **machine-verified data** (`scenario.json`→`events.csv`→`expected/*.json`), not drifted prose. Verified data-truth correction (2026-07-01): **L08 (P05·3) & L16 (P09·4) are the CY2025 lease-ups, L20 the P04 turnover; L21/L22 are EXISTING P13 tenants** (events back to 2023) — the calendar/§3.2 calling the lease-ups "L21/L22" is stale (errata added).
- **True-to-life pass DONE** (SA, 2026-07-01): every API-shortcut-around-a-real-screen converted to `[UI]`; each step tagged by execution surface (`[UI]`/`[DEV-CLOCK]`/`[WORKER]`/`[SEED]`) in the act md + a `surfaces` array in `scenarios.json`. Live CY2025 = 100% UI; only the 2-yr history stays `[SEED]` (=GAP1). **5 product-gap findings (flagged, not API'd around):** **PG-1** back-fill control ("Backfill from lease start") present in `/scan/new-rental` + manual lease form but MISSING from generic `/scan/[draftId]` confirm → ONB-04 uses `/scan/new-rental`; **fix = add the control to `/scan/[draftId]`**. **PG-4** lease-detail one-tap "Mark paid" may not capture `Method`/`PayerName` on non-scanned rows (verify in running app; scanned checks carry method via `/scan`). PG-2 worker-only history-gen (inherent), PG-3 owner-dist UI (=GAP2), PG-5 bulk mark-paid (=GAP1). **Fold PG-1/PG-4 into the app-completion DoD sweep.**
- **Surgical-spine execution model = Playwright, NOT an LLM agent** (user proposal 2026-07-01, orchestrator strongly concurs). The spine is deterministic (exact steps/docs/sim-dates/cent-exact) → Playwright's sweet spot; free/CI/repeatable (solves agent token-burn). Clock/worker fired via `request.post()` to `/dev/*` interleaved with UI actions (standard pattern). The catalog + `scenarios.json` (order/artifacts/expectedRefs/workerFires/surfaces) IS the test blueprint; SA's true-to-life pass = the selector spec. Wrinkle: scan→LLM-extraction non-determinism → set fields to known corpus values at confirm (soft-assert extraction separately). LLM **agent reserved for the exploratory/negative track** (improv + adversarial, on scratch DB). Build the Playwright spine at the harness phase (after clock + 7 gaps). Repo already has Playwright (`web` `test:e2e`/`test:e2e:list`, `_pw*.mjs`).
- **Gap mobile = BUILD NOW / full Flutter parity** (user, 2026-07-01) — each of the 4 gaps with a mobile surface
  (owner-dist TSK-618 / proration 619 / app-fee 621 / eviction 623) ships backend+web+**mobile** together; NO
  deferral. Gap1 (bulk import) has no mobile surface → its lone mobile deferral (TSK-617-M) stands. Coordinate
  gap-mobile edits to avoid colliding with the in-flight mobile tasks (TSK-633/634/635) + Codex's mobile pass.
- **Gaps plan DONE** (GapsPlanner): `Docs/superpowers/plans/2026-07-01-domain-gaps-implementation.md`, **49 tasks**
  (9 foundation + 40 gap), order F1-4→G5→G1→G2→G3→G4→G6→G7. Orchestrator decisions locked: sequential stacked
  per-gap branches (NOT 6 parallel — Api builds serialize); gap3 move-out = prorate-existing-charge-down;
  app-fee = Schedule-E income; GAP4/GAP6 values = corpus-revision placeholders. Hazards: F1 precedes {5,1,2};
  F2 precedes {4,6}; **gap4 precedes gap6**; F3 precedes gap3; F4 precedes gap1; gap7 independent. Corpus-revision
  triggers: gaps 3,4,5,6,7 shift the cent spine (SA re-authors on merge); 1,2 do not.

## Key facts / grounding

- Clock: ~349 .NET wall-clock sites, ~123 web; 12+ Engine workers; **zero DB-side time defaults**; `Engine→Api→{Core,Data}`; business tz read from `App:TimeZone` (not provider); audit timestamp in `Data/Auditing/AuditSaveChangesInterceptor.cs:235`.
- SA corpus: Okafor Property Group; 13 properties / 21 units / 11 mortgages / 4 owner entities; income defined 12 ways → ground truth is an event log with per-report predicates; 3 deliberate divergences (assert, don't bug-flag). Workers OFF by default (`NotificationSettings`); firing order RentCharge→notices→LateFee→Autopay→DebtService/Recurring→Outbox; all idempotent.

## Notion task URLs (Rental Command project)

- TSK-615 clock: `notion.so/p/390394b0689d81edb32bd53623e7827d`
- Grids: `…/390394b0689d815da198df9a74991856`
- Gap1 bulk import: `…/390394b0689d8145a6b7db2fd605f004` · Gap2 owner distribution: `…/390394b0689d8149b906d099889ed32f` · Gap3 proration: `…/390394b0689d81afa611c9ecd6289db8` · Gap4 depreciation: `…/390394b0689d81a4a91cedfd9a77a0af` · Gap5 app-fee income: `…/390394b0689d81b4a5cde08d2e894ce6` · Gap6 sale/disposition: `…/390394b0689d81b3ba3fc75459f29b7c` · Gap7 eviction: `…/390394b0689d81d3a215e7d0bfbf667c`
- SignalR: `…/390394b0689d81e9b36fc213d482b425` · Integrations test-env: `…/390394b0689d818ab71cc6d9d74b1b63`

## Current status / in-flight (2026-07-01, updated)

- **Clock (TSK-615)** on `tsk-615-master-simulation-clock` — docs `deb59b99`/`6d6f2b52`/`3f93b41f`; plan-review folded into the plan's "Review incorporated" section. **Code landing:** A1 `c074e7a6` (ClockMode+extensions, 3 tests), A2 `9c53ca97` (entity+interfaces), A3 `1b813db7` (DbSet+migration+UTC seed). **ClockBuilder** subagent implementing Phase A completion (A4–A7) on the build slot NOW. Then B (control+command-bridge) → C (web shim+panel) → D (349-site sweep) → E (verify).
- **Corpus (SA)** — Phase-1 + **Phase-2 steps 1–3 DONE** (ran in a PROCEED-NOW window before the pause): `ledger/loans.csv`, `loan-payments.csv` (720 rows), `depreciation.csv` (from the app's real calculators); `ledger/events.csv` (**1,573 rows** CY2023–25, running balances); `expected/*.json` (**all 13 properties reconcile to the cent**, 4 asserted divergences); 110 artifacts (9.5 MB, git-ignored). Replay-driver design drafted: `Docs/superpowers/specs/2026-07-01-e2e-replay-driver-design.md`. Built vs the CURRENT app → **needs the post-features revision** (edit `scenario.json` + re-run 4 scripts). **SA fully parked** for the "clock endpoints live" ping (steps 4/5) + the post-features "revise corpus" re-brief. Spine CY2025: Sch-E income $265,818.72 / net $65,717.15; NOI $247,356.39.
- **All 5 investigations DONE + saved** (`investigations/`): clock spec review, clock plan review, SignalR (Engine no-op backplane → Option A LISTEN/NOTIFY), grids (RC already clean; narrow gap + 1 lease-payments defect), 7-gap blueprint (F1–F4 + sequence). Those agents stood down.
- **DataAccessAuditor** running (read-only): broad API+Engine in-memory-agg / N+1 / index / slow-query sweep (user directive).
- **DoD locked** — no half-done features (UI wired + reachable + running-app verify; mobile parity or explicit deferral).
- Build-slot: held by ClockBuilder. Corpus + this ledger + investigations UNTRACKED on disk (durable; committed home TBD, not the clock PR).

## Next steps

1. Fold PlanReviewer must-fixes → implement the clock (Phases A–E), holding the build slot.
2. As DomainGapsArchitect / GridFilterScout / SignalRDoctor report → review, sequence, implement one-branch-at-a-time.
3. When clock control+worker endpoints are live → **ping SA** ("clock endpoints live") + brief it to revise the corpus for the new features.
4. Integration test-env (Stripe/QB) → corpus revision → tester harness (#7) → bug pipeline (#8). Expand this ledger as each stands up.

## Recovery notes (if context is lost)

- Read this file → the two clock docs (`Docs/superpowers/specs|plans/2026-07-01-master-simulation-clock*`) → SA's corpus doc (`Docs/superpowers/specs/2026-06-30-e2e-scenario-corpus-design.md`) → `git log tsk-615-master-simulation-clock`.
- Resume agents **by name** via SendMessage. Notion Command Center (project "Rental Command") tracks tasks.
- Rules: never build .NET on the VPS; never swarm dotnet builds (serialize); clean up any worktrees immediately; no AI-attribution commit trailers; all filtering/sort/paging DB-side.
