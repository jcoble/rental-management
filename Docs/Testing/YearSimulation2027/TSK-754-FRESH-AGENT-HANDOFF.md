# TSK-754 Fresh-Agent Handoff

Prepared 2026-07-27 and last updated 2026-07-31 for continuation of the 2027
full-company simulation.

## Authoritative pause checkpoint — 2026-07-31, February 15 after RUN-05

The user paused TSK-754 at a clean Git checkpoint so the accounting backbone and explainable Money
ledgers can be implemented before the simulation creates more financial history. Do not resume the
February schedule yet. Follow
`Docs/superpowers/plans/2026-07-31-tsk-803-accounting-backend-and-money-ui-contract.md` for the
`TSK-803` backend, `TSK-800` Money surfaces, Fable handoff, and resume gates. `TSK-801` remains the
later additive rent-roll report.

Implementation checkpoint commit: `5c7a1c60` (`Checkpoint TSK-754 through February 15`). It
captures the accumulated Atomic cutover, migrations, authorization repairs, source changes, tests,
simulation documents, and fixtures through this stop. Transient `output/qa`, `output/playwright`,
`output/phone`, and `output/pdf` evidence remains preserved locally and ignored rather than bloating
Git. The documentation commit immediately after `5c7a1c60` adds this pause and the accounting plan.

This section supersedes every older continuation checkpoint below it.

### Exact chronology

- Schedule rows: 1,981.
- Execution-ledger rows: 259.
- Unique ledger run IDs: 241.
- Completed schedule runs: 228.
- Remaining schedule runs: 1,753.
- Preserved historical duplicate groups: 13; blocking duplicate groups: zero.
- Last completed run: `RUN-20270215-05`.
- First untouched run: `RUN-20270215-06`.
- Frozen clock: `2027-02-15T05:00:14.867058Z`, `America/New_York`.

The read-only checkpoint helper proves those counts:

```bash
python3 scripts/qa/tsk754_checkpoint.py summary
python3 scripts/qa/tsk754_checkpoint.py remaining --date 2027-02-15 --only-remaining
```

### Completed work after the older February 12 checkpoint

- `RUN-20270212-07` through `RUN-20270212-09` are complete. CRUD-003 required five bounded Owner
  repairs (`YS-299` through `YS-303`), all independently reverified with database, receipt, audit,
  outbox, authorization, web, and physical-Samsung evidence.
- `RUN-20270215-01` is complete after append-only vendor dispatch cancellation/reassignment repair
  `YS-307`, independently verified and live proven on the physical Samsung.
- `RUN-20270215-02` through `RUN-20270215-05` are complete with exact paid property-expense,
  attachment, line-item, allocation, due-date, audit, outbox, web, and phone proof.
- `YS-308` is fixed and independently real-PostgreSQL verified. Paid scan confirmation now preserves
  the reviewed `DueDate` independently of `Paid` and `PaidAt`, including full rollback injection.
- `YS-309` was reproduced before `RUN-20270215-06`: the mobile manual expense form could not select
  a Property when no Work Order was chosen. No financial mutation occurred. The bounded source fix
  now requires/selects the property, locks work-order scope correctly, and sends the complete
  property-only expense payload. Focused Flutter analysis and four widget tests pass independently.
  It is not live complete: rebuild/install from checkpoint source and prove the exact physical-phone
  flow before appending `RUN-20270215-06`.

### Accounting boundary

The existing Atomic kernel is finished and remains the only write path. TSK-803 must add balanced
journal postings inside the same scoped `RentalCommandDbContext` transaction; it must not introduce
a new host, compatibility bridge, factory, versioned Atomic runtime, message broker, or second unit
of work. Business rows, journal rows, receipt, required audits, and required outbox rows commit or
roll back together.

`YS-295` remains Critical and open. Preserve LoanPayments 67 and 68 and their original audits as
historical facts. The accounting implementation must not hide the discrepancy: Loans 3 and 10 have
the wrong recurring due day, Loan 3 lacks the statement escrow component, and the preserved February
occurrences total 2,450.00 versus 2,752.00 expected. Reconcile through the supported append-only
correction plan before the February 20 loan run and prove balanced journal effects.

### Runtime and device at pause

- Worktree:
  `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-754-year-simulation-execution`
- Branch: `tsk-754-year-simulation-execution`.
- PostgreSQL: `rentalcommand-tsk754-db`, database `rentalcommand_tsk754`, port 5754, running.
- API and web health return HTTP 200.
- tmux sessions: `tsk754-crud003-api`, `tsk754-crud003-web`, and
  `tsk754-run02-engine`. The Engine is intentionally in command-bridge-only operation; do not enable
  autonomous due-work processing while the clock is frozen.
- Physical Samsung: `100.73.198.93:34825`, model `SM_S906U`, connected by wireless ADB.
- `adb reverse tcp:5666 tcp:5666` is not currently present. Restore and verify it before installing
  or launching the local-API APK.
- The last known installed clean dev-profile APK used for February 15 proof has SHA-256
  `73979815ddbc8a90ac5cef221fa1b64d74758d13960fbee814110dc781c2850e`.
- The read-only doctor passes API, web, clock, scan corpus, ledger/evidence access, and ADB device
  checks; it fails only the missing ADB reverse check.

### Safe next actions

1. Hand the accounting plan to Fable. Create `tsk-800-803-accounting-ledgers` from the current
   `tsk-754-year-simulation-execution` HEAD containing this handoff, in a separate worktree under
   `/Users/blackcolours/dev/work/worktrees/rental-management/`. `5c7a1c60` is the preserved
   implementation checkpoint immediately before the handoff documentation.
2. Implement and verify the backend/DTO contract before Fable completes the web/mobile redesign.
3. Reconcile the preserved frozen database to the seeded COA and balanced journals; do not reset or
   replace the database.
4. Merge the accounting work, migrate this preserved stack, rebuild the physical-phone APK, restore
   ADB reverse, and live verify YS-309.
5. Complete exactly `RUN-20270215-06`, then continue `RUN-20270215-07` in schedule order only after
   the new ledger/journal/database/UI acceptance gates agree.

Do not close `TSK-754`; it remains Doing and paused, not abandoned. Do not mark `TSK-800` or
`TSK-803` Done until backend, database conversion, web, and physical-phone evidence all pass.

## Authoritative continuation checkpoint — 2026-07-30, February 12 after RUN-06

Stop point: `RUN-20270212-06` is complete. Do not redo it. The first unstarted schedule row is
`RUN-20270212-07`, the dedicated disposable Owner lifecycle certification. The simulation clock
remains Frozen at `2027-02-12T05:00:00Z`, `America/New_York`.

This section supersedes every older continuation checkpoint below it. Older sections are retained
only as investigation history. In particular, do not follow any older versioned Atomic naming,
dark-runtime, bridge, factory, category-registry, handler/session-abstraction, fallback, or retry
proposal. The only supported write path is the already-cut-over thin Atomic runtime using the same
scoped `RentalCommandDbContext`, explicit transaction, and shared SaveChanges boundary. Business
mutation, receipt, required audits, and required outbox rows must commit together or roll back
together.

### Exact chronology

- Schedule rows: 1,981.
- Unique completed schedule runs: 220.
- Execution-ledger rows: 251.
- Remaining schedule runs: 1,761.
- First remaining: `RUN-20270212-07`.
- The raw ledger still contains 13 preserved historical duplicate run IDs. They predate this
  checkpoint and were not silently deleted. Reconcile them only through evidence; do not remove
  history just to make a counter green.

February 12 completed here:

- `RUN-20270212-04`: Workspace Administrator certified Expense 55 through normal Money
  navigation, the exact direct URL, all safe action/validation/cancel paths, search, filter, sort,
  paging, reload, stale-scope correction, tenant denial, and one-statement database readback.
  Expense 55 remains 940.00 Paid, undeleted, with one line item, original audit 4978, and no
  capital asset.
- `RUN-20270212-05`: Property Manager independently certified scoped Expense 54. Morgan sees 111
  scoped money rows and nine scoped expenses and cannot find the administrator-only Expense 55.
  Expense 54 remains 690.25 Paid, undeleted, with original audit 4568 and no capital asset.
- `RUN-20270212-06`: Leasing Agent certified Agreement 81 fields `FLD-0051` through `FLD-0053`.
  Negative deposit and late fee and fractional grace days stayed unsaved. A boundary double-click
  created exactly revision 5. An offline final submit showed no partial mutation; restoring
  connectivity created exactly revision 6. A completely fresh browser session reloaded 1,375.25
  deposit, 82.50 late fee, and six grace days. The agreement remains unissued and uncanceled.

### Atomic proof at this stop

Agreement revision 5 is receipt `97d62843-b621-487d-9a7c-dad719a0e70a`, audit 5090, and outbox
1370. Revision 6 is receipt `9ff52800-8c80-48bb-be33-21b7ca14e586`, audit 5093, and outbox 1371.
A single PostgreSQL idempotency join matched both receipt/outbox pairs to `LeaseAgreement` 81.

`YS-298` is explicitly invalidated, not open: the first diagnostic query compared the frozen
simulation timestamp to the outbox wall-clock timestamp and therefore excluded valid rows 1370
and 1371. No Atomic code was changed. Preserve the invalidated ledger row so the investigation is
auditable.

`YS-297` is fixed and live web verified. The Unit Property-expense detail used stale shallow
navigation state after Back. `ExpensesTab.svelte` now owns the mounted selection state, clears it
before shallow navigation, and synchronizes browser Back/Forward through `popstate`. Both the
Back-to-expenses button and browser Back immediately render the list without reload. The fix is
uncommitted shared WIP and has live browser proof plus `git diff --check`; no heavy build was run
after this UI-only repair.

The earlier canonical target-first tenant-payment allocation and zero-open-balance charge-picker
repairs remain in shared WIP. Their focused real-PostgreSQL/generated-SQL tests and live Azure
proof passed before this checkpoint. Do not replace either repair with an alternate payment path.

### Runtime and device state

- Worktree:
  `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-754-year-simulation-execution`
- Branch: `tsk-754-year-simulation-execution`
- HEAD: `8f0b3c5e1d0c0893161fccd85526133ec00f45a1`
- Shared worktree is intentionally very dirty: 711 status entries at this checkpoint. Never reset,
  stash, clean, switch, or remove it. Preserve all unrelated WIP.
- PostgreSQL: `rentalcommand-tsk754-db`, database `rentalcommand_tsk754`, port 5754, running.
- API tmux: `tsk754-api`, HTTPS 5666 / HTTP 5665, running.
- Web tmux: `tsk754-web`, `https://localhost:5667`, running.
- Azure API tunnel tmux: `tsk754-azure-api-tunnel`, running.
- Engine: off. Start it only for an exact scheduled worker/scan extraction, wait for the expected
  idempotent result, then stop it immediately.
- Azure emulator: `emulator-5554`, available through the existing Tailscale-only verification
  runner. It is signed in with the standard password as
  `qa.tsk754.jan19.operator@example.local` and stopped on Leasing Rentals, Dogwood Duplex Unit B.
- Installed Azure APK remains the locally built dev x64 APK with SHA-256
  `47a33cacae6206c4f365859d425998e6c8474b6c6c8a0012dde5b4f0702a98fa`.
- Physical phone is unavailable for interactive biometric/PIN proof. The user explicitly deferred
  biometric work. Do not block on it or ask for thumb interaction; use passwords and the Azure
  emulator.
- All Playwright sessions opened by this checkpoint are closed. `playwright-cli list` reports no
  browsers.
- No new worktree was created by either bounded fixer.

Stable credentials:

- Administrator: `qa.tsk754.azure.mobile@example.local` / `Admin123!`
- Property Manager: `qa.tsk754.property.manager@example.local` / `Admin123!`
- Leasing Agent: `qa.tsk754.jan19.operator@example.local` / `Admin123!`
- Tenant Yara Brooks: `tenant.008@example.local` / `Tenant123!`

### Open issue that matters before later February automation

`YS-295` remains Critical and open. The February 12 Engine start created premature LoanPayments 67
and 68 because Loans 3 and 10 still use due day 12 even though the authoritative statements expect
February 20; Loan 3 also lacks the statement escrow component. The preserved occurrences total
2,450.00 versus 2,752.00 expected and reduced recorded principal eight days early. Do not delete or
rewrite those rows. Reconcile the loan schedule and escrow source of truth before the February 20
loan verification run, using append-only correction/reconciliation where required.

The older bug backlog remains preserved in `output/qa/tsk-754-bug-ledger.csv`. A completed schedule
row is not made incomplete merely because it exposed a separately recorded bug, but a bug that
invalidates the row's acceptance evidence must be fixed and re-proved before advancing.

### Evidence added at this checkpoint

- `output/playwright/RUN-20270212-04-admin-expense55-detail.png`
- `output/playwright/RUN-20270212-04-admin-expense55-validation.png`
- `output/playwright/RUN-20270212-04-admin-expense55-direct-route.png`
- `output/playwright/RUN-20270212-04-adjacent-tenant-denied.png`
- `output/playwright/RUN-20270212-05-property-manager-expense54-direct.png`
- `output/playwright/RUN-20270212-05-property-manager-expense54-validation.png`
- `output/playwright/RUN-20270212-05-adjacent-tenant-denied.png`
- `output/playwright/YS-297-property-manager-back-path-fixed.png`
- `output/playwright/RUN-20270212-06-deposit-negative.png`
- `output/playwright/RUN-20270212-06-late-fee-negative.png`
- `output/playwright/RUN-20270212-06-grace-days-invalid.png`
- `output/playwright/RUN-20270212-06-boundary-duplicate-submit.png`
- `output/playwright/RUN-20270212-06-network-interruption.png`
- `output/playwright/RUN-20270212-06-fresh-session-reloaded.png`
- `output/qa/tsk754-evidence/RUN-20270212-06-mobile-dogwood-unit-b.png`

### Exact next run

Resume with `RUN-20270212-07`, `CRUD-003`: Workspace Administrator, Web + Mobile, dedicated
disposable Owner record. Through normal navigation:

1. Inventory every Owner field referenced by `field-inventory.csv`.
2. Create the disposable Owner and prove exact persisted readback.
3. Edit it, assign properties, issue the portal invite, revoke the invite/access, and perform the
   supported deactivate-safe transition.
4. Cancel the first destructive confirmation, then repeat and confirm.
5. Retry one mutation with the same idempotency key and prove no duplicate transition or orphan.
6. Verify web, Azure mobile, history/audits, notifications, authorized and adjacent unauthorized
   roles, and one-statement database state.
7. Append exactly `RUN-20270212-07` to the existing execution ledger. Do not invent a substitute
   run ID or begin `RUN-20270212-08` until the Owner lifecycle is fully reconciled.

Before acting:

```bash
cd /Users/blackcolours/dev/work/worktrees/rental-management/tsk-754-year-simulation-execution
git status --short
docker ps --filter name=rentalcommand-tsk754-db
tmux ls
curl -sk https://localhost:5666/api/v1/dev/clock
```

Expected clock response starts with
`{"simNowUtc":"2027-02-12T05:00:00Z","mode":"Frozen","timeZoneId":"America/New_York"}`.
If any expectation differs, diagnose without resetting persistent state.

## Authoritative continuation checkpoint — 2026-07-29, February 8 closeout

The single-path Atomic cutover remains complete and is the only supported write runtime.
Do not add a compatibility bridge, factory, category registry, retry layer, versioned
Atomic name, or second handler/session abstraction. Every workflow continues through the
same scoped `RentalCommandDbContext`, explicit transaction, and Atomic command context;
every `SaveChanges` in a workflow participates in that transaction.

The simulation clock is Frozen at `2027-02-08T05:00:00Z`,
`America/New_York`. All five February 8 runs are complete:

- `RUN-20270208-01`: corrected scanned check 000115 is fully allocated to rent and late fee;
  the refunded-correction uniqueness defect is fixed and live web verified.
- `RUN-20270208-02`: scanned money order SCN-0123 produced confirmed Draft 166, Payment 973,
  and exact 1300.00 rent plus 50.00 late-fee allocations. The Scan History authorization
  query, confirmed-result Navigator defect, and misleading terminal-result copy are fixed
  and live Azure-emulator verified.
- `RUN-20270208-03`: exact scan SCN-0356 produced Work Order 25 and Appointment 19 at
  Property 3 / Unit 3 / Tenant 3 / LeaseManagement 3 / Vendor 22. The normal work-order UI
  saved the February 9 09:00-10:00 service window and Scheduled status. The synchronized
  appointment is Confirmed at the same window, and the tenant portal shows both records.
  The flow exposed `YS-289`: the status-only Scheduled-to-Confirmed appointment promotion
  emitted a byte-identical second tenant notification. The bounded repair now preserves
  the appointment audit/outbox while suppressing that duplicate notification; focused real
  PostgreSQL regression proof passes.
- `RUN-20270208-04`: revoked Tenant access now automatically reaches `/portal/unlinked`
  through normal password login without relationship metadata; an adjacent administrator is
  denied the tenant route; supported Lease 35 access restoration returned the tenant to
  `/portal`. `YS-287` is fixed and live browser verified.
- `RUN-20270208-05`: the February 8 addendum field certification is complete.

The chronology checkpoint reports 204 completed schedule runs, zero incomplete earlier
runs, zero blocking duplicate-run groups, and `RUN-20270209-01` as the first remaining row.
The normal gate still reports the preserved historical January bug backlog; that backlog
does not make a completed schedule row incomplete and must not be concealed or deleted.

Current live evidence includes:

- `output/playwright/RUN-20270208-03-appointment19-linked-workorder25.png`
- `output/playwright/RUN-20270208-03-tenant-appointment-notification.png`
- `output/playwright/RUN-20270208-03-workorder25-scheduled-window.png`
- `output/playwright/RUN-20270208-03-appointment19-workorder25-synchronized.png`
- `output/playwright/RUN-20270208-03-tenant-confirmed-appointment-duplicate-notification.png`
- `output/playwright/RUN-20270208-04-tenant-unlinked-auto-redirect-fixed.png`
- `output/playwright/RUN-20270208-04-adjacent-staff-unlinked-denied.png`
- `output/playwright/RUN-20270208-04-tenant-access-restored.png`
- `output/qa/tsk754-evidence/RUN-20270208-02-scan-history-repaired-azure.png`
- `output/qa/tsk754-evidence/RUN-20270208-02-scan-166-confirmed-readonly-azure.png`
- `output/qa/tsk754-evidence/RUN-20270208-02-scan-166-confirmed-copy-fixed-azure.png`

Runtime state:

- preserved PostgreSQL container `rentalcommand-tsk754-db` is on port 5754;
- API tmux session `tsk754-api` and web tmux session `tsk754-web` are running;
- Engine is off unless an exact scheduled-worker run requires it;
- the physical Samsung is unavailable for interactive fingerprint/PIN proof; do not block on
  biometric authentication or ask the user to operate it;
- use the existing Azure Android emulator for mobile checkpoints, with standard password
  login, and use the canonical remote-verification handoff for any renewed tunnel/build work;
- the primary browser session is `tsk754-feb05-web-receipts`; its opener owns closing the
  exact daemon and Chrome helper tree when browser work finishes.

Rebuild and restart the API with the `YS-289` repair before advancing the clock. Then advance
the frozen clock through the normal simulation-clock UI to February 9 and begin
`RUN-20270209-01`, scanned invoice SCN-0357 for 940.00 at Property 3 / Unit 3 / Work Order 25.
February 9 continues in exact schedule order through the two `/` role certifications, the
three AddendumDraft fields, and the mobile Property surface pass. The primary agent owns live
UI/device proof and both ledgers while bounded fixers work independently.

## Authoritative continuation checkpoint — 2026-07-29, February 3 post-cutover verification

The single-path Atomic cutover is implemented and verified on the current dirty worktree. Do
not reintroduce a compatibility bridge, handler factory, versioned Atomic path, or second
runtime. Production scans found no reference to any of those rejected designs. The preserved
historical inventory remains
`output/qa/tsk781-atomic-cutover-manifest.csv`, SHA-256
`9d6a15845a9011745f79945946038a59be86fc67793cad291b300c239b017ed6`.

Current-source proof completed after the cutover:

- whole solution build: 0 errors;
- full API suite: 1,101/1,101;
- formerly failing API cluster: 44/44;
- dedicated security/permission slices: Data 63/63, Core 23/23, API 216/216,
  real-PostgreSQL RLS/authorization 38/38, workspace authorization 43/43,
  Atomic/session envelope 48/48, foundation baseline 41/41, Engine RLS/registration 3/3,
  and migrated-PostgreSQL Portfolio QA 12/12;
- focused mobile security/permission slices: 94/94 across access-envelope restoration,
  authority-transition cache clearing, stale-capability denial, money-action visibility,
  role-specific navigation, restricted-shell isolation, typed push routing, and
  missing/unknown/expired/revoked/cross-context/cross-experience push rejection. Two stale
  source-text assertions were corrected to match the already stricter production contract;
  explicit missing and unknown experience rejection cases were added;
- the branch had fallen behind the already-merged biometric work on `origin/main`
  (`2f0fc228`, `2188959e`). Those exact biometric/auth files are integrated here and pass
  43/43 focused controller, service, login, settings, and interceptor tests. Biometric success
  now revalidates the one stored session path; transport/408/5xx failures preserve enrollment
  and return to the locked retry state, while only a definitive 401 clears tokens and biometric
  opt-in. The physical-device dev APK is compiled for
  `https://localhost:5666/api/v1`, matching ADB reverse, rather than the emulator-only
  `10.0.2.2` endpoint that caused the observed post-fingerprint 502/login loop. After
  integration, the combined mobile biometric/session/access/capability/role/navigation/push
  regression matrix passes 128/128 and targeted static analysis reports zero issues. The
  Samsung-installed base APK and local repaired APK are byte-identical at SHA-256
  `474bf15f87a6cba8889890ce5b4295870c8696ae87fa014a8137dc51927b27d0`;
- serialized real-PostgreSQL transactional workflows: 169/169 across tenant money,
  scheduled charges, provider payments, banking, stored documents, scan upload,
  scan confirmation, scheduled finance, accounting mapping, payment CSV import, demo seed,
  inbound messaging, and vendor W-9;
- the restored deposit-funding path now proves exact deposit-charge allocation, injected
  companion rollback, exact replay, and linked compensating allocation on reversal;
- live rebuilt API proof: anonymous portfolio access 401; verified administrator login,
  `/auth/me`, portfolio list, and accounting summary 200; cross-portfolio id 1 concealed as
  404; tenant login and relationship-scoped lease read 200; tenant accounting access 403.

The first attempt to run every PostgreSQL transactional class concurrently exhausted Docker
Desktop's 8 GiB virtual disk (`No space left on device`). No product failure was inferred from
that run. No container, volume, cache, user data, or worktree was deleted. Rerunning one
fixture class at a time passed 169/169 and left the preserved database intact. Continue heavy
PostgreSQL fixtures serially unless Docker's disk allocation is changed through an explicitly
approved infrastructure action.

The live stack is ready:

- database `rentalcommand-tsk754-db` remains preserved on port 5754;
- API `tsk754-api` was rebuilt/restarted and is healthy;
- web `tsk754-web` remains healthy;
- Engine remains off;
- clock remains Frozen at `2027-02-03T05:00:00Z`, `America/New_York`;
- Samsung `SM_S906U` is connected and `adb reverse tcp:5666 tcp:5666` is intact, but the
  secure lockscreen is still showing. Do not guess or bypass its PIN.

The execution ledger has 195 rows and 177 unique run IDs. Exactly eight February 3 schedule
runs remain before the clock may advance: mobile receipt runs `RUN-20270203-01`, `-03`, `-04`,
`-06`, `-08`, `-11`, and `-13`, plus the physical-phone readback for
`RUN-20270203-18`. The chronology gate correctly blocks February 4. As soon as the phone is
unlocked, complete those eight through the normal mobile UI, append/upsert their existing
`run_id` rows with database and visual evidence, rerun the checkpoint gate, then continue
February 4 in schedule order.

## Previous authoritative continuation checkpoint — 2026-07-29, February 2 atomic stop

**This section supersedes every older checkpoint in this file.** The 2027 simulation is
complete and reconciled through the scheduled February 2 runs. The database clock is
intentionally frozen at **2027-02-02 00:00 America/New_York**
(`2027-02-02T05:00:00Z`). The single-path Atomic cutover and source verification are complete.
The preserved database has now been snapshotted, migrated, and passed the live
authorization/replay/financial smoke gate. The next action is the February 3 prerequisite
check followed by `RUN-20270203-01`.

The terminal goal is still the **whole 2027 year through December 31**, including every
scheduled run, scan/manual alternation, role and field certification, monthly financial close,
and year-end reconciliation. Atomic replacement is a prerequisite, not the final deliverable.

### Exact repository, runtime, and evidence state

| Item | State at handoff |
| --- | --- |
| Notion task | `TSK-754`; reopened to **Doing** and verified because the user explicitly resumed this active simulation/atomic prerequisite |
| Worktree | `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-754-year-simulation-execution` |
| Branch | `tsk-754-year-simulation-execution` |
| HEAD | `8f0b3c5e1d0c0893161fccd85526133ec00f45a1` |
| Worktree state | **Dirty shared WIP:** 640 status entries: 474 modified, 34 deleted, 132 untracked |
| Database | `rentalcommand-tsk754-db`, preserved, migrated through `20260729017000_AddAtomicReadOnlyRuntimeRole`, running on local port `5754` |
| Verified clock | `Frozen`, `2027-02-02T05:00:00Z`, `America/New_York` |
| API / web / Engine | API `tsk754-api` and web `tsk754-web` running; Engine stopped and remains opt-in |
| Phone | Samsung `SM-S906U` connected by wireless ADB; `tcp:5666` reverse installed |
| Browser automation | No TSK-754-owned browser session. A long-lived generic `playwright-mcp` process exists; ownership is unclear, so do not stop it without identifying its session/process tree |
| Subagents | None running; all cutover fixers completed and stopped |
| Execution ledger | 183 rows, 165 unique run IDs, 18 historical duplicate IDs; last row `RUN-20270202-05` |
| Schedule position | First 153 of 1,981 schedule rows reached; 1,828 remain; next run is `RUN-20270203-01` |
| Bug ledger | 268 rows; do not infer open-work count from the raw `status` strings without normalizing them |

Do not clean, reset, stash, switch branches, or remove this worktree. It contains the active
year evidence plus overlapping implementation from earlier agents and sessions. Before editing
an existing file, inspect its current diff and preserve unrelated work. No new worktree is
needed.

### Completed February 2 checkpoint

The final five ledger rows are:

1. `RUN-20270202-01`: Prime Pest Control expense `54` recorded once for P028/U036/
   WO-2027-005, amount `$690.25`, with exact replay and DB-side financial proof.
2. `RUN-20270202-02`: Yara Brooks tenant account history certified on desktop and phone
   viewport with DB-computed periods and running balances.
3. `RUN-20270202-03`: lease-term field certification created future LeaseManagement `81`,
   Agreement `85`, TenantAccount `60`, and Unit `84`; exact replay created no duplicate and
   **zero 2027 charges or back-payments**.
4. `RUN-20270202-04`: Property `29` field edit, exact replay, and restoration were certified.
5. `RUN-20270202-05`: Tenant `90` field certification completed through the supported
   lease-first scan and exact replay.

The UI findings raised during the run are already represented in the bug ledger:

- `YS-228`: tenant mobile conversation cards now show the actual counterparty instead of the
  signed-in tenant.
- `YS-229`: web and mobile now share the canonical DB-computed tenant account-history contract.
- `YS-230`, `YS-265`: role-aware maintenance detail/activity plus contact, resident-presence,
  permission-to-enter, pet, access-warning, comment/photo, cancel/edit, and status capabilities
  are implemented. The remaining acceptance item is role-by-role physical-phone proof.
- Mobile top-tab restoration is owned by another session. Do not create a competing fix; verify
  its resulting source/build before resuming phone certification.

### Atomic cutover truth — single thin path complete and tested

The rejected parallel runtime/compatibility design is gone. Production now has one supported
Atomic command path; there is no second host, versioned lane, handler factory, service locator,
compatibility bridge, or feature flag.

The supported shape is deliberately small:

- the scoped `RentalCommandDbContext` is the persistence boundary;
- an ordinary one-off save uses that scoped context directly;
- a composite action uses the same scoped context inside one explicit EF execution-strategy
  transaction, so every `SaveChangesAsync` participates in that physical transaction;
- RLS/session activation and Atomic audit enforcement remain separate interceptors;
- domain handlers inject the scoped context directly and own authorization, invariants,
  DB-side queries, mutations, semantic audit content, and outbox content;
- the thin transaction runner owns only identity/fingerprint, exact committed-result replay,
  advisory locks, transaction coordination, receipt, audit, and outbox finalization;
- audit, receipt, inbox, outbox, and business rows roll back together;
- external calls occur only outside the database transaction.

Repository scans on 2026-07-29 found zero production or test references to the removed
`AtomicHost`, versioned Atomic, parallel runtime, or handler-factory designs. The remaining
scripts named `atomic_runtime` are stale QA generators only and are not compiled or executed by
the product.

### Cutover verification evidence

The final whole solution build passed with **0 errors**. Known package-advisory and existing
nullability/obsolete-API warnings remain warnings; they were not introduced by the cutover.

Security, permission, authorization, RLS, role, and session verification passed **445 tests**:

- Atomic kernel architecture: 5/5
- Data-focused Atomic/security: 20/20
- Core authorization/security: 16/16
- API authentication/session/RLS: 153/153
- API domain authorization/Atomic contracts: 70/70
- PostgreSQL RLS, runtime roles, ownership, and household access: 36/36
- Workspace authorization kernel: 43/43
- Atomic transaction runner: 13/13
- Auth-session command envelope: 21/21
- Session refresh: 12/12
- Canonical auth PostgreSQL: 2/2
- Foundation baseline: 41/41
- Engine RLS: 1/1
- Portfolio QA on migrated PostgreSQL: 12/12

The exact transactional workflow gate passed **152/152** across tenant charges, scheduled
charges, provider payments, banking, stored documents, scan upload, scan confirmation,
scheduled finance, accounting mapping, payment CSV import, demo seed, inbound messaging, and
vendor W-9 processing. Two additional real-PostgreSQL import tests passed for unit and core
CSV authorization/query execution.

The focused runs proved success, exact replay, changed-payload conflict, unauthorized replay,
session revocation, audit/receipt/outbox companion rollback, storage/finalization rollback,
deterministic retry, cleanup-claim concurrency, database-clock behavior, and DB-side
authorization query shape.

The gate found and fixed three real dormant SQL defects:

1. scan-upload admission composed a data-modifying CTE below the top query level;
2. payment, unit, and core CSV imports used a mismatched `AuthSessions` alias;
3. unit/core imports used the reserved word `authorization` as an unquoted CTE name.

### Historical cutover ledger — preserve, do not regenerate

`output/qa/tsk781-atomic-cutover-manifest.csv` is the preserved historical inventory of what
had been wired into the old Atomic layer and where each item was classified to go. It has
**2,736 data rows / 2,737 lines** and SHA-256:

`9d6a15845a9011745f79945946038a59be86fc67793cad291b300c239b017ed6`

The old generator expects the deleted registration/handler model and now fails against the
single-path source. It was hardened so a failed scan cannot overwrite the preserved CSV or its
summary. Do not use a new post-cutover scan as a replacement for this historical ledger.

### Preserved-database migration and live smoke proof

Before touching the preserved database, an exact stopped-container snapshot was written to:

`output/qa/tsk754-db-snapshots/rentalcommand-tsk754-db-frozen-20270202-pre-atomic-migration-20260729T1213EDT.tar.gz`

Its SHA-256 is:

`0242c40867993023961ce8d7d7cf13d32e1546fa6e56fa6be44f662ceeb93b97`

A disposable restored copy migrated successfully first and was removed immediately afterward.
The preserved database then migrated successfully. Before and after migration, the frozen
clock and principal financial counts were unchanged: 2,251 Atomic receipts, 924 tenant-ledger
entries, and 53 expenses. The runtime roles are `NOINHERIT`; the Atomic read-only role is
`NOLOGIN`.

The live API smoke used two real identities:

- property manager user 10 received HTTP 404 when attempting to replay tenant user 6's
  notification command;
- tenant user 6 replayed its own committed command twice and received HTTP 204 both times.

Across the authorized replay, the exact receipt count, tenant-ledger entries, expenses,
payment attempts, allocations, and Atomic audit rows were identical before and after:

`1 | 924 | 53 | 42 | 209 | 4607`

This proves exact committed-result replay without duplicate business, financial, receipt, or
audit writes under the correct authorization boundary.

### Exact continuation sequence

1. Resume at `RUN-20270203-01`, not at January. Run the daily prerequisite check, stage the
   exact February 3 scan assets, and follow schedule order with execution-ledger `run_id`
   idempotency.
2. Continue through December 31. Reconcile run IDs, scans, CRUD rows, notifications, audits,
   and financial entries daily. Reconcile cash, AR, deposit trust/liability, loans, income,
   expenses, and net result at each month end before advancing.

### Resume commands and cleanup truth

```bash
cd /Users/blackcolours/dev/work/worktrees/rental-management/tsk-754-year-simulation-execution
git status --short
git rev-parse HEAD
docker ps -a --filter name=rentalcommand-tsk754-db
adb devices -l
shasum -a 256 output/qa/tsk781-atomic-cutover-manifest.csv
```

- Database runtime: container running; persistent database retained and migrated.
- API/web/Engine runtime: API and web running; Engine stopped.
- Browser cleanup: no TSK-754-owned browser session remains. The generic `playwright-mcp`
  process was preserved because ownership is not established.
- Build cleanup: no known build/test batch is running.
- Subagent cleanup: no subagent is running.
- Worktree cleanup: **not removed** — active dirty WIP on
  `tsk-754-year-simulation-execution`; the next agent owns preservation and continuation.

## Authoritative continuation checkpoint — 2026-07-28, January 29 stop

This section supersedes every older checkpoint below. The preserved database is reconciled
through the completed January 29 runs listed here, and the simulation clock is intentionally
frozen at **2027-01-29 00:00 America/New_York** (`2027-01-29T05:00:00Z`). Do not advance it
until `YS-212` and `RUN-20270129-04` are resolved.

### Exact repository and runtime state

| Item | State at handoff |
| --- | --- |
| Notion task | `TSK-754` — **Doing**, High, verified 2026-07-28 |
| Worktree | `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-754-year-simulation-execution` |
| Branch | `tsk-754-year-simulation-execution` |
| Latest implementation commit | `d921bf1391f010d1c1d72266a4288eb5cb9918bf`; the handoff-only commit is immediately above it |
| Tracked worktree state | Clean |
| Database | `rentalcommand-tsk754-db`, healthy on local port `5754` |
| API | `tsk754-api`, rebuilt from `d921bf13`, healthy on `https://localhost:5666` |
| Web | `tsk754-web`, healthy on `https://localhost:5667` |
| Engine | Stopped; keep it off until the exact reminder-worker run |
| Browser automation | No browser session running |
| Physical phone | Samsung `SM_S906U`, wireless ADB online |
| Android package | `com.rentalcommand.rental_command.dev`, foreground |
| ADB reverse | `host-24 tcp:5666 tcp:5666` |
| Heavy tests/builds | None running |

The installed physical-phone APK is still the clean local-API artifact built from source SHA
`46a9ae36`:

```text
/Users/blackcolours/.codex/remote-artifacts/tsk754-mobile-jan27-46a9ae36-20260728t1259z/app-dev-profile-arm64-local-api-clean.apk
SHA-256 9d426f...b6604c5
```

The changes after that SHA in this checkpoint are API, web, and planner-script work; no new
mobile binary was required for the completed January 29 web acceptance.

### January 29 execution status

| Run | State |
| --- | --- |
| `RUN-20270129-01` | **Completed.** Tenant Messages is live-proved after `YS-210`; final send returned in 375 ms and rendered exactly once. |
| `RUN-20270129-02` | Safely blocked by the `YS-211` generated-field planner correction; `FLD-0030` is `Select.Content`, not a saveable control. |
| `RUN-20270129-03` | **Completed.** Real Property Type persisted, reloaded, and was restored; rental structure remained correctly locked. |
| `RUN-20270129-04` | Blocked by `YS-212`; the rent-due-in-five-days reminder correctly excludes Yara while her lease has a possession reconciliation exception. |
| `RUN-20270129-05` | Safely blocked by the existing `YS-207` setup planner correction; the live portfolio is already 100% configured. |

The ignored execution ledger has 10 valid columns on every row after these updates. The bug
ledger still has 14 older malformed legacy rows, but all newly written `YS-210` through
`YS-212` rows have the canonical 9 columns. Continue editing both ignored ledgers only with
`apply_patch`.

### `YS-210` completed realtime repair

The original tenant reply committed once but held the HTTP response for 36.7 seconds. The
measured repair sequence is:

| Commit | Result |
| --- | --- |
| `cecab709` | Bounded SignalR delivery. |
| `9151f505` | Aligned notification fanout. |
| `3ae8f77e` | Used the durable saved access context for targeted notification authorization. |
| `96a31cd6` | Batched conversation and notification invalidations; live response still took 22.1098 seconds. |
| `fbffebec` | Resolved all saved-context notification recipients in one DB-side authorized query; focused suite passed 6/6, but live response still took 15.517 seconds. |
| `d921bf13` | Moved only post-commit realtime invalidation onto a bounded nonblocking hosted queue and removed the post-commit notification reload. |

`d921bf13` passed 22 focused tests covering the conversation path, the saved-context
authorization query, bounded-queue overflow, and continuation after a queued batch failure.
The final real Yara UI send:

- returned HTTP 200 in **375 ms**;
- logged API action completion in **314.1066 ms**;
- cleared the composer and rendered the exact text once without reload;
- created exactly one `ConversationMessage 18`, sender `Tenant`, at the frozen
  `2027-01-29T05:00:00Z`;
- left no browser or test process running.

`YS-210` and `RUN-20270129-01` are recorded as fixed/completed in the ignored QA ledgers.

### Other completed January 28–29 work

- `f7cb24c2` fixed Lease Template Designer width/height validation; the invalid height stayed
  unsaved in live proof and the canonical field was restored.
- `5512384e` removed ignored Status and Notes controls from scan-first lease review.
- `0324d354` fixed the field-inventory parser so only exact native `input`, `select`, and
  `textarea` tags are inventoried. The regression ignores Svelte `Select.Root`, `Trigger`,
  `Content`, and `Item`. Active year artifacts were intentionally not regenerated mid-run.
- `RUN-20270129-03` used the real Property Manager UI on Arbor House Property 1. Property Type
  changed Single-family to Condo, survived reload, and was restored to Single-family.
  `AtomicAuditLogs 3579` and `3580` record the frozen-time transitions.

### Current blocker: `YS-212` historical possession reconciliation

Do not treat this as a notice-worker bug. The DB-side candidate filter is correctly excluding
a contradictory lease.

Yara Brooks has:

```text
LeaseManagementId: 8
RelationshipNumber: LM-SCAN-00000009
AgreementId / AgreementNumber: 8 / SCN-0008
PlannedPossessionAtUtc: 2026-03-01T00:00:00Z
PossessionGivenAtUtc: NULL
Agreement term: 2026-03-01 through 2027-08-31
Agreement fully executed: yes
Derived lifecycle: Upcoming
Exception: GoverningAgreementWithoutPossession
HasReconciliationException: true
```

A DB-side portfolio aggregation found **13** legacy `Upcoming` relationships with this same
exception across LeaseManagement IDs 1 through 14. The current production scan confirmation
path already validates and persists a reviewed `PossessionGivenAtUtc` for a newly imported,
fully signed, already-active lease. Do not duplicate or loosen that safeguard.

The required smallest repair is a supported staff reconciliation action exposed only when the
DB-derived exception is exactly `GoverningAgreementWithoutPossession`:

1. An authorized rentals-management user enters the real historical possession date.
2. The server requires a fully executed governing agreement.
3. The date must fall within that agreement term and cannot be after effective business time.
4. Future/upcoming relationships and every other reconciliation state fail closed.
5. Possession, semantic audit, and data-update outbox persist atomically and idempotently in
   one explicit transaction.
6. Authorization and eligibility remain DB-side; no materialized-row filtering, grouping,
   aggregation, joins, N+1, or per-row follow-up queries.
7. Add PostgreSQL failure-injection proof and a focused web contract test.

No implementation for this action is in progress at handoff. The focused coding agents were
stopped cleanly before this document was written, and the tracked tree is clean. Do not use the
existing generic **Give possession** action for Yara: it would stamp January 29 and destroy the
true March 1, 2026 occupancy history. Do not patch the database directly.

### Exact next sequence

1. Verify the state without mutating it:

   ```bash
   git status --short
   git rev-parse HEAD
   curl -sk https://localhost:5666/api/v1/dev/clock
   tmux list-sessions
   adb devices -l
   adb reverse --list
   ```

   Expected history has the handoff-only commit immediately above implementation commit
   `d921bf13`; clock is Frozen `2027-01-29T05:00:00Z`; API and web are running; Engine is
   absent.

2. Dispatch one focused coder for `YS-212` using the exact reconciliation contract above. Do
   not create a reviewer swarm or another worktree.
3. Run only focused PostgreSQL/failure-injection and web contract tests, with
   `MSBUILDDISABLENODEREUSE=1`, then shut down build servers.
4. Rebuild and restart only `tsk754-api`. Confirm the frozen clock again.
5. In the real Property Manager web UI, open LeaseManagement 8 through normal navigation and
   reconcile possession to **2026-03-01**. Live-prove:
   - a future date is rejected without mutation;
   - the valid historical date commits once;
   - duplicate replay is idempotent;
   - DB-derived lifecycle becomes `Occupied`;
   - the reconciliation exception clears;
   - exactly one semantic audit and one data-update outbox fact exist.
6. Reconcile the other 12 affected legacy leases only through the same supported action or a
   separately proven supported batch command. Do not run direct SQL updates.
7. Only after LeaseManagement 8 is repaired, advance the frozen clock to the policy's genuine
   Jan 29 send time, **09:00 America/New_York** (`2027-01-29T14:00:00Z`).
8. Start the Engine only for the rent-reminder worker. Policy 10 is `rent-reminder`, five lead
   days, send hour 9, email + portal enabled, push disabled, and currently Draft. Follow the
   product's actual draft/approval lifecycle; do not invent a delivered notice if approval is
   required.
9. Prove `NTF-002` once on web and the connected physical phone, including safe copy, scoped
   deep link, read synchronization, configured-channel outcome, retry, duplicate suppression,
   and adjacent-role denial. No screenshots are required for this continuation.
10. Stop the Engine immediately, finish `RUN-20270129-04`, reconcile January 29, and only then
    advance to the January 31 close.

`RUN-20270122-03` still has physical-phone proof pending for the earlier rent-charge
notification. It can be paired with the `NTF-002` phone pass after `YS-212` rather than opening
another mobile setup lane.

### Cleanup state

- Browser cleanup: no Playwright browser is running.
- Build cleanup: no Rental Command build/test process is running.
- Worktree cleanup: no new worktree was created. Preserve the active TSK-754 worktree and the
  user-owned TSK-749 worktree.
- Engine cleanup: stopped.

## Authoritative continuation checkpoint — 2026-07-28

This section supersedes the older January 14 checkpoint below. The preserved database was
reconciled through January 21 and the simulation clock is now intentionally frozen at
**2027-01-22 00:00 America/New_York** (`2027-01-22T05:00:00Z`). Do not roll it back or advance it
until all three January 22 runs are reconciled.

- Local API and web are running in `tsk754-api` and `tsk754-web`; Engine is stopped.
- The Rental Command web URL is **`https://localhost:5667`**. Port 5173 belongs to another local
  application and must not be used for this simulation.
- `RUN-20270122-01` is complete. `YS-165` was fixed in `adfe0ab2` and live-proved with generic
  signed-lease copy while the authorized stored JPEG downloaded successfully.
- `RUN-20270122-02` remains blocked by planner data. `7525fa80` and `70bdc625` added the
  role-specific Leasing Agent application route and canonical PrepareMoveInDialog without the
  unauthorized management/screening calls. The rebuilt endpoint correctly fails closed for the
  only application because the test user is assigned Properties 21 and 23 while the application
  belongs to Property 30.
- `YS-168` records an independent planner contradiction: the only application was atomically
  prepared on January 7, while the same one-submit Prepare-move-in dialog fields are split across
  January 21, 22, 25, and 26. Do not fabricate a replacement application.
- `RUN-20270122-03` remains blocked. `YS-163` is committed in `ed5c5567`, but no safe unposted
  charge exists on January 22. A DB-side prospective query found the next genuine event on
  January 26 for one January 31 charge, then 37 February 1 charges on January 27.
- `YS-167` is fixed in `275be564` with focused PostgreSQL proof. Live proof is queued for the
  first genuine January 26 charge because no safe unposted charge exists on January 22.
- `YS-169` is fixed and live-proved in `d00da2af`: the preference update, atomic audit, and
  data-update outbox all used the frozen January 22 business timestamp.
- `YS-157`, `YS-158`, `YS-159`, and `YS-164` are fixed and live-proved. The latest committed
  sequence is `d00da2af`, `275be564`, `70bdc625`, `7525fa80`, `adfe0ab2`, `4ac5d65a`,
  `ed5c5567`, and `9ee3738d`.
- No TSK-754 browser automation session is intentionally retained between proof runs. Each
  named session must still be closed and its process tree verified immediately after use.

## Outcome and current checkpoint

Continue TSK-754 from the preserved January database. Do not restart the year, replace the
database, or treat the historical January 14 checkpoint below as current.

The latest chronologically executed scenario is `RUN-20270121-04`, but the authoritative
simulation clock is intentionally frozen at **2027-01-14 00:00 America/New_York**. The clock
was rolled back after the run exposed missing January 8-15 property, ownership, deed, and
mortgage prerequisites. Complete those prerequisites and the critical loan-payment workflow
before resuming after January 21.

## Task and repository state

| Item | Current state |
| --- | --- |
| Notion task | `TSK-754` — **Doing**, High |
| Task URL | <https://app.notion.com/p/TSK-754-Execute-the-2027-full-company-simulation-in-Rental-Command-3a9394b0689d81b7a786fed90a67ea89> |
| Worktree | `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-754-year-simulation-execution` |
| Branch | `tsk-754-year-simulation-execution` |
| HEAD | `024b55c5fe2bdd3e83263b40b97268d015de54f6` |
| Upstream | `origin/tsk-754-year-simulation-execution`, same SHA |
| Tracked state before this handoff | Clean |
| Database | `rentalcommand-tsk754-db`, running on local port `5754` |
| API | Stopped |
| Web | Stopped |
| Engine | Stopped |
| Browser automation | No TSK-754 Playwright or Chrome session running |
| Preferred mobile target | Physical Samsung `SM_S906U` over wireless ADB |
| Azure emulator | Fallback and checkpoint/final-environment verification only |

This worktree is intentionally retained because TSK-754 is still active and unmerged. The
separate TSK-749 planning worktree is also not owned by this continuation and must not be
deleted.

## Read these first

1. `/Users/blackcolours/.codex/AGENTS.md`
2. Repository `AGENTS.md`
3. Repository `CLAUDE.md`
4. This handoff
5. `Docs/Testing/YearSimulation2027/README.md`
6. `Docs/Testing/YearSimulation2027/schedule.csv`
7. `Docs/Testing/YearSimulation2027/financial-oracle.csv`
8. `Docs/Testing/YearSimulation2027/journal.csv`
9. `output/qa/tsk-754-execution-ledger.csv`
10. `output/qa/tsk-754-bug-ledger.csv`

The committed planner and financial oracle are the expected-state authority. The two ignored
QA ledgers and evidence folder are the execution-state authority. Database readback is the
authoritative product-state check.

## Preserved artifacts

- The planner covers 30 properties, 40 units, 45 leases, every role, web and mobile, scan and
  manual entry, notifications, messaging, maintenance, CRUD, and financial reconciliation.
- There are **953 rendered uploadable scan files** under
  `output/pdf/tsk-749-year-simulation-scan-corpus/documents/`.
- There are currently **416 evidence files** under `output/qa/tsk754-evidence/`.
- The execution ledger has **104 rows, 89 unique run IDs, and 10 duplicated run IDs**.
  Deduplicate by run ID and evidence before final control totals.
- The bug ledger has **164 rows**:
  - 81 statuses beginning with `Fixed`
  - 78 `Open`
  - 1 `Open intermittent`
  - 1 `Reopened`
  - 2 `Fix in progress`
  - 1 `Planner correction required`
- Fourteen rows still say `Fixed locally and live verified; awaiting commit`. Reconcile every
  such row against Git history before claiming it is committed; the ledger wording may be
  stale.

Do not add the generated corpus, QA ledgers, screenshots, tokens, build outputs, or other
execution artifacts to Git. They are intentionally ignored.

## Mobile targets

### Physical phone — preferred now

The user confirmed wireless debugging is connected and wants the phone used instead of the
emulator.

```bash
/Users/blackcolours/Library/Android/sdk/platform-tools/adb devices -l
```

Expected device:

```text
adb-RFCT60SMJNN-9BQpeO._adb-tls-connect._tcp
model:SM_S906U
```

Installed development package:

```text
com.rentalcommand.rental_command.dev
```

Before using the local API, verify rather than assume the reverse:

```bash
/Users/blackcolours/Library/Android/sdk/platform-tools/adb reverse --list
/Users/blackcolours/Library/Android/sdk/platform-tools/adb reverse tcp:5666 tcp:5666
```

Run the Flutter app in debug mode against this device so UI changes can use hot reload. Do not
leave a stale Flutter process or ADB log stream running when handing the lane off again.

### Azure emulator — fallback/checkpoint

- Host: `azureuser@100.126.201.65`
- SSH key: `~/.ssh/rental-build-runner-01-key.pem`
- Remote ADB: `/opt/runner-tools/android-sdk/platform-tools/adb`
- Emulator: `emulator-5554`
- Package: `com.rentalcommand.rental_command.dev`

Use the `$remote-verification-handoff` runbook for Azure work. Preserve the reusable
`rc-preview-rental` database, volumes, credentials, uploads, and data-protection keys. Do not
improvise transfers, locks, deployment, or cleanup.

## Restarting the local isolated stack

The database is already running. Start only the services needed for the next test.

**Critical:** the API must start with `Simulation__Enabled=true`. A previous restart omitted
that flag and made `/api/v1/dev/clock` return 404. Verify the clock endpoint before doing any
product work. Use `Auth__ExposeDevTokens=true` only in this isolated QA environment.

Load the isolated container's owner password without printing it. Use that credential only for
the one-shot migration process. The long-running API and Engine must use their direct restricted
development logins; current startup code rejects `MigratorConnection` in a long-running process.

```bash
export TSK754_DB_OWNER_PASSWORD="$(
  docker inspect rentalcommand-tsk754-db \
    --format '{{range .Config.Env}}{{println .}}{{end}}' |
  sed -n 's/^POSTGRES_PASSWORD=//p'
)"
```

Apply migrations once after rebuilding:

```bash
env \
  ASPNETCORE_ENVIRONMENT=Development \
  DOTNET_ENVIRONMENT=Development \
  Simulation__Enabled=true \
  ConnectionStrings__MigratorConnection="Host=localhost;Port=5754;Database=rentalcommand_tsk754;Username=postgres;Password=${TSK754_DB_OWNER_PASSWORD}" \
  ConnectionStrings__DefaultConnection='Host=localhost;Port=5754;Database=rentalcommand_tsk754;Username=rentalcommand_api;Password=rentalcommand_api_dev' \
  ConnectionStrings__EngineConnection='Host=localhost;Port=5754;Database=rentalcommand_tsk754;Username=rentalcommand_engine;Password=rentalcommand_engine_dev' \
  Jwt__SecretKey='dev_only_super_secret_signing_key_at_least_64_chars_long_0123456789' \
  Jwt__Issuer=RentalCommand \
  Jwt__Audience=RentalCommandWeb \
  Seed__Enabled=false \
  dotnet run --no-build --project RentalCommand.Api -- --migrate-only
```

API:

```bash
tmux new-session -d -s tsk754-api \
  -c '/Users/blackcolours/dev/work/worktrees/rental-management/tsk-754-year-simulation-execution/RentalCommand.Api' \
  "env ASPNETCORE_ENVIRONMENT=Development DOTNET_ENVIRONMENT=Development Simulation__Enabled=true Auth__ExposeDevTokens=true ASPNETCORE_Kestrel__Certificates__Default__Path='/Users/blackcolours/dev/work/worktrees/rental-management/tsk-754-year-simulation-execution/web/.cert/api-cert.pem' ASPNETCORE_Kestrel__Certificates__Default__KeyPath='/Users/blackcolours/dev/work/worktrees/rental-management/tsk-754-year-simulation-execution/web/.cert/api-key.pem' ConnectionStrings__DefaultConnection='Host=localhost;Port=5754;Database=rentalcommand_tsk754;Username=rentalcommand_api;Password=rentalcommand_api_dev' Jwt__SecretKey='dev_only_super_secret_signing_key_at_least_64_chars_long_0123456789' Jwt__Issuer=RentalCommand Jwt__Audience=RentalCommandWeb Seed__Enabled=false dotnet run --no-build --urls 'https://localhost:5666;http://localhost:5665'"
```

Web:

```bash
tmux new-session -d -s tsk754-web \
  -c '/Users/blackcolours/dev/work/worktrees/rental-management/tsk-754-year-simulation-execution/web' \
  "env NODE_EXTRA_CA_CERTS='/Users/blackcolours/Library/Application Support/mkcert/rootCA.pem' pnpm dev"
```

Vite currently binds Rental Command to `https://localhost:5667`. Confirm the printed URL after
startup instead of assuming its preferred port was available.

Engine, only when a scheduled worker is required:

```bash
tmux new-session -d -s tsk754-engine \
  -c '/Users/blackcolours/dev/work/worktrees/rental-management/tsk-754-year-simulation-execution/RentalCommand.Engine' \
  "env DOTNET_ENVIRONMENT=Development Simulation__Enabled=true ConnectionStrings__DefaultConnection='Host=localhost;Port=5754;Database=rentalcommand_tsk754;Username=rentalcommand_engine;Password=rentalcommand_engine_dev' dotnet run --no-build"
```

Keep the Engine stopped during manual backfill and inspection unless a scenario explicitly
needs a worker. This prevents unplanned background mutation while the clock moves.

Immediately after API startup:

1. Confirm `/api/v1/dev/clock` exists.
2. Confirm it reports the current authoritative frozen checkpoint. As of 2026-07-28 that is
   `2027-01-22T05:00:00Z`.
3. Log in fresh. Never reuse bearer tokens from `/tmp/tsk754-*`.
4. Confirm the phone's ADB reverse.
5. Start the web or Flutter debug client.

The clock provider starts in an in-memory Real default and refreshes from PostgreSQL
asynchronously. A first HTTP 200 can therefore briefly say Real even when the preserved database
row is Frozen. Poll until both `mode=Frozen` and the exact expected instant are returned; do not
mutate product data after a merely reachable but not-yet-refreshed clock response.

Synthetic QA credentials previously used include:

- Administrator: `qa.tsk754.azure.mobile@example.local` / `Admin123!`
- Verified administrator: `qa.tsk754.azure.verified@example.local` / `Admin123!`
- Property manager: `qa.tsk754.property.manager@example.local` / `Admin123!`
- Tenant 008 was reset to `Tenant123!`
- Default sandbox admin: `admin@rentalcommand.local` / `Admin123!`

Freshly verify access context and portfolio after login. Do not rely on old tokens or cached
role state.

## What has been completed

The test reached January 21 through real web and Android workflows while recording every
observed defect. It has exercised, among other areas:

- Scan-first lease, expense, insurance, receipt, mortgage, work-order, and loan-statement
  flows using exact rendered source files.
- Manual leases without an application, including the restored rent-charge start choices:
  current date, lease-start backfill, and custom date.
- Property/unit/tenant/lease setup and edit flows.
- Team creation, scoped assignments, assignment replacement/end state, suspension, and
  reactivation.
- Tenant portal navigation, payments/messages/maintenance links, validation, role isolation,
  and maintenance creation.
- Property-manager and leasing-agent role routes.
- Forgot password, reset password, change password, session revocation, and negative
  validation on web and Android.
- Simulation clock movement and time-dependent authorization.
- File type validation, rejection, retry, idempotency, scan confirmation, and audit search.
- Manual and scanned expenses with PostgreSQL allocation reconciliation.
- Notifications, message targets, appointments, vendors, work orders, audit activity, and
  destructive-action cancellation paths.

The latest completed chronological run is:

```text
2027-01-21 RUN-20270121-04
Manual web Create lease without an application
```

It created the Casey ManualJan lease on Dogwood Duplex Unit B and live-proved all three
rent-charge start choices. Do not repeat it unless a regression specifically requires it.

## Important committed fixes

The branch contains many fixes. The latest verified sequence is:

| Commit | Fix |
| --- | --- |
| `024b55c5` | Password reset/change now revoke prior sessions atomically; simulation clock no longer intercepts the Security submit button |
| `4cca0db8` | Team selected-property scope replacement no longer 500s; ended assignments render and behave as ended |
| `16c3886d` | Scan-confirmed expenses persist the required allocation |
| `f2f5283d` | Global scan authorization uses the correct security-time boundary under the simulation clock |
| `81824d17` | Failed scan retry is idempotent server-side |
| `a1de76d5` | Scan retries send an idempotency key |
| `3d349ccb` | Mobile quick actions no longer overlap content; actions use modal sheets |
| `dd7841e9` | Failed scans expose recovery actions again |

Other important fixed areas in the bug ledger include:

- Lease-scan possession date and guided-lease square footage.
- Simulation gates surviving correct restarts.
- Scan claim, timeout, and LLM process handling.
- Scan-created work-order images.
- Date pickers and vendor/work-order timestamps using simulation time.
- Outbox claiming and captured email delivery.
- Tenant appointments and conversation read state.
- Historical loan-period generation.
- Late-fee automation controls.
- Lease templates, native e-sign, RLS, rollback, and proration.
- Check-number account scoping.
- Mobile/global scan overlays and message-recipient targeting.
- Audit filters and search.
- Manual expense recovery, dates, and field exposure.
- Atomic work assignment.
- Future-token rejection, analytics zeros, and navigation overlays.

Use `output/qa/tsk-754-bug-ledger.csv` for the complete evidence-linked list. Do not rely on
this summary as the bug authority.

## High-impact open bugs

The most important known open defects include:

- `YS-109` — mobile scan context missing.
- `YS-110` — stale mobile property/owner cache.
- `YS-111` — state selector clears itself.
- `YS-112` — mobile property pagination is incorrect.
- `YS-113` — imported in-term executed leases can appear Upcoming/unoccupied without
  possession.
- `YS-114` — a confirmed scan can reopen as editable/create action.
- `YS-115` — January 15 control counts are polluted by demo/seed data.
- `YS-117` — move-out notice lease misclassification.
- `YS-118` — wrong scan is rejected.
- `YS-119` — incomplete expense can confirm.
- `YS-120`, `YS-122` — update/confirmed expense field and allocation loss.
- `YS-123` through `YS-126` — manual expense scope and field defects.
- `YS-128` — scan-created work order can return an unrelated tenant.
- `YS-133` — duplicate Created audit entries.
- `YS-147` — Android DocumentsUI returns to Money without uploading.
- `YS-154` — no supported web/mobile/API workflow to post a scheduled loan payment.
- `YS-156` — loan-payment statements are treated as new loans, not matched payments.
- `YS-157` — tenant-created work-order timestamp uses real time.
- `YS-158` — Property Manager startup calls Getting Started and receives 403.
- `YS-159` — Leasing Agent Today endpoint returns 403.
- `YS-160` — selected scan property is not bound to the loan destination.

Record every newly reproduced defect before dispatching a fixer. A fixer owns one clear bug or
one tightly related batch and must return the fix commit plus focused proof.

## Property and mortgage backfill checkpoint

P010 Juniper is complete:

- Exact mortgage scan `SCN-0053` was uploaded on Android.
- `ScanDraft 113` confirmed as `Loan 15`.
- The real web form completed every loan section.
- Active balance is `171,500.00`, monthly principal and interest `1,270.00`, escrow `318.00`.
- Deed `SCN-0052`, ownership, and property basis are attached/completed.
- Execution row `RUN-20270108-10` was appended.

The following property basis and ownership edits succeeded, despite later confusing 401
output:

| Planner property | Database property | Basis/ownership state | Deed | Mortgage |
| --- | --- | --- | --- | --- |
| P017 Quarry | 17 | Complete | Missing | Missing |
| P019 Summit | 19 | Complete | Missing | Missing |
| P020 Terrace | 20 | Complete | Missing | Missing |
| P022 Valley | 23 | Complete | Missing | Missing |
| P023 Walnut | 24 | Complete | Missing | Missing |

These remain incomplete:

| Planner property | Database property | Missing |
| --- | --- | --- |
| P025 Zenith | 26 | Basis, ownership, deed, mortgage |
| P026 Ash | 27 | Basis, ownership, deed, mortgage |
| P028 Chestnut | 29 | Basis, ownership, deed, mortgage |
| P029 Dogwood | 30 | Basis, deed, mortgage; ownership already exists |

Exact assets:

| Property | Deed | Mortgage |
| --- | --- | --- |
| P017 | `SCN-0064` Jan 9 | `SCN-0065` Jan 14 PDF |
| P019 | `SCN-0067` Jan 9 | `SCN-0068` Jan 14 PDF |
| P020 | `SCN-0069` Jan 9 | `SCN-0070` Jan 14 JPEG |
| P022 | `SCN-0072` Jan 10 | `SCN-0073` Jan 14 JPEG |
| P023 | `SCN-0074` Jan 10 | `SCN-0075` Jan 14 PDF |
| P025 | `SCN-0077` Jan 10 | `SCN-0078` Jan 15 PDF |
| P026 | `SCN-0079` Jan 10 | `SCN-0080` Jan 15 JPEG |
| P028 | `SCN-0082` Jan 10 | `SCN-0083` Jan 15 JPEG |
| P029 | `SCN-0084` Jan 10 | `SCN-0085` Jan 15 PDF |

A supported `POST /api/v1/documents` attempt returned 404 saying the referenced record was not
found in the portfolio. The later API restart accidentally omitted simulation support, so this
is only a **candidate authorization defect**. Reproduce it under the correctly configured
stack before assigning a new bug ID. If it persists, inspect document target authorization and
simulation/security time; keep all filtering and authorization DB-side.

## Critical financial checkpoint

January is **not reconciled**.

The database currently contains 11 active loans. Among the ten backfill target properties,
only P010 has its loan. Nine mortgages remain to be created.

January scheduled payment rows 53 through 56 are:

| Payment | Loan | Principal | Interest | Escrow | Total | Balance after | State |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| 53 | 5 | 464.24 | 661.76 | 318.00 | 1,444.00 | 140,585.76 | Scheduled, unpaid |
| 54 | 6 | 475.39 | 674.61 | 318.00 | 1,468.00 | 145,649.61 | Scheduled, unpaid |
| 55 | 7 | 497.37 | 700.63 | 318.00 | 1,516.00 | 155,777.63 | Scheduled, unpaid |
| 56 | 8 | 508.03 | 713.97 | 318.00 | 1,540.00 | 160,841.97 | Scheduled, unpaid |

The scheduler incorrectly reduced `Loan.CurrentBalance` while merely creating these unpaid
rows. That must be repaired as part of `YS-154`.

Required `YS-154` behavior:

1. Add a supported web, mobile, and API action to post a scheduled loan payment.
2. Wrap the entire posting command in one explicit database transaction.
3. Make it idempotent with a business/idempotency key.
4. Change the live loan balance, cash/journal state, paid date, and payment status only when
   the payment becomes paid.
5. Keep scheduled rows as projections with no cash or balance mutation.
6. Keep all lookup, authorization, filtering, and reconciliation DB-side.
7. Add PostgreSQL failure-injection proof for all-or-nothing behavior.
8. Repair/recompute Loans 5-8 and Payments 53-56.
9. Live-prove the action on web and the physical phone.

`YS-156` must then allow each exact statement scan to match the correct existing loan and
scheduled payment rather than offering `Add Loan`.

The financial oracle's monthly `Amount Due` is principal and interest; the product
`TotalAmount` includes escrow. Reconciliation must explicitly compare the correct components
instead of reporting escrow as a variance.

## Exact continuation sequence

1. Confirm TSK-754 is still `Doing`; do not create a replacement task.
2. Confirm the database container and physical phone connection.
3. Start the API with simulation enabled and prove the frozen January 14 clock.
4. Start web and Flutter debug only; leave Engine stopped.
5. Reproduce the property-document 404 under the correctly configured stack.
6. Complete deeds and exact mortgage scans for P017, P019, P020, P022, and P023, alternating
   web/manual and phone/scan surfaces as the planner requires.
7. Move to January 15 and complete every field, ownership, deed, and mortgage for P025, P026,
   P028, and P029.
8. Append or correct the original planner run IDs in the execution ledger. Do not invent
   substitute runs and do not add duplicate rows.
9. Fix and prove `YS-154`, including the existing data repair.
10. Return to January 20, scan/match all 20 loan statements, test duplicate idempotency, and
    reconcile principal, interest, escrow, total cash, and loan balances to the oracle.
11. Deduplicate the execution ledger using evidence.
12. Resume after `RUN-20270121-04`; do not redo completed January 21 work without a specific
    regression reason.
13. Continue the remaining year in date order, alternating web/mobile and scan/manual paths,
    recording every bug and all financial effects.

## Atomic-layer replacement contract

The whole-layer reliability replacement retains the `TSK-781` identity, but the user explicitly
made its wholesale completion a blocking prerequisite for continuing `TSK-754`. It is being
performed in this worktree and session against the year simulation's characterization baseline;
do not resume the calendar on the retired Atomic implementation and do not turn the replacement
into an incremental or dual-path migration.

Current closed-world checkpoint on 2026-07-29:

- `scripts/qa/generate_tsk781_atomic_cutover_manifest.py` completes with zero failures and zero
  registration-contract attention rows.
- The live manifest has 2,726 classified rows: 202 API/Engine registrations, 342 caller rows,
  304 command/result rows, 170 replay-policy rows, 146 receipt-codec rows, 830 database-contract
  rows, 191 persistence-context escape rows, 439 fingerprint rows, and 84 test-fixture rows.
- Thirty-eight registrations currently have a static caller count of zero. They remain explicitly
  classified as registrations to retain until the coordinated cutover, but each must be resolved to
  one real caller or deletion before the final V1 removal sweep; a zero count is not cutover proof.
- Consecutive V2 metadata generation runs are byte-stable:
  - `AtomicRuntimeGeneratedCommandManifest.g.cs`:
    `93f538043358d1a9e13e4a0a26251039483ea57ace42aa3835793a77aaa434ae`
  - `AtomicRuntimeGeneratedAdmissionDependencyManifest.g.cs`:
    `e509ee2afb24fcd5554b51cc57f7e2723c6cb5ee0fee00e68fa6e83efab71862`
  - `AtomicRuntimeGeneratedSimulationRegistry.g.cs`:
    `d249197ed633a0ca130a27dc9e5667be2db71740d3b34164f82dc941330908c8`
- Four unreachable commands and their handlers, registrations, and test references were deleted:
  `ChangeWorkspaceAssignmentScopeCommand`, `ChangeWorkspaceAssignmentEndCommand`,
  `GrantOwnerUserAccessCommand`, and `RevokeOwnerUserAccessCommand`.
- The former combined scheduled rent/late-fee command was split into exact rent and late-fee
  commands, results, handlers, registrations, and receipt contracts; the combined symbols have
  zero repository references.
- SOL medium independently passed the new live replay-authorization policies for accounting
  mapping confirmation/continuation and Plaid token-exchange preparation.
- The thin V2 foundation is still dark and unregistered. Its non-generated Core/Data runtime is
  987 logical lines and has no domain namespace dependency, command switch, service lookup, nested
  execution path, or second runtime registration.
- `RentalCommand.AtomicHost` is a separate compile-time provenance boundary: API issues an immutable
  access proof only from middleware-resolved `ActiveAccessContext`, Engine issues only its runtime
  proof, and Data can consume but cannot mint either proof.
- Validation now completes under the read-only database role before the receipt claim. Only after
  successful validation does the runner restore the exact writer role/RLS coordinates and insert the
  transaction-coupled receipt. Replay re-enters read-only, authorizes before reading the stored result,
  then restores the exact writer before commit.
- The focused Atomic runtime suite passes 62/62, including real PostgreSQL proof for validation-before-
  claim, rollback, exact replay/conflict, unauthorized replay denial, fresh retry attempts, restored
  role/RLS state, audit/outbox/receipt rollback, authoritative audit time, ACL/function surface, and
  commit-unknown proof. The Engine project also builds successfully with the host boundary.
- GPT-5.5 passed the corrected Simulation V2 slice. SOL medium found and fixed the original
  claim-before-validation ordering defect, then the same 62/62 PostgreSQL/static suite passed.
- Auth/authorization and operations/maintenance are now being ported by separate GPT-5.5 lanes;
  money/payments is being ported by a SOL-medium lane. These additions remain dark. Root owns the
  final registry, host cutover, repository-wide zero-reference proof, and immediate deletion of every
  V1 contract/registration/interceptor/grant/test path in the same coordinated change.

- Preserve the proven transaction kernel during the simulation: one explicit transaction,
  DB-side set operations, one stable command identity, exact committed-result replay,
  rollback-coupled audit/outbox, database authorization/session context, and no remote calls
  inside the transaction.
- Insert the durable outbox intent inside that same transaction. Only delivery or another
  external effect occurs after commit.
- Every distant service or repository save/raw SQL call must use the exact runner-owned
  `DbContext`, connection, database transaction, runtime role, and RLS/access coordinates.
  Interceptors must fail closed on any mismatch; a separately resolved context must never
  silently become a second transaction.
- The replacement must be wholesale. Write and prove the complete new command runner,
  execution context, narrow persistence ports, typed identities/time, audit writer, and
  capability manifest before cutover.
- Switch every consumer in one coordinated change and immediately delete the retired
  contracts, adapters, handlers, registrations, grants, tests, feature flags, fallbacks, and
  compatibility paths.
- There must never be two supported ways to perform the same command. Do not leave the old
  path temporarily for another domain slice to use.
- Diagnose later issues against the commit that removed the old implementation and git
  history. Do not keep dead runtime code or compatibility comments as documentation.
- Repository-wide reference and registration sweeps are required proof that the retired
  surface is gone.
- Keep the replacement kernel thin: it may own transaction execution, stable command identity
  and exact replay, database session/authorization context, required locks, and
  rollback-coupled audit/outbox coordination only.
- Domain scheduling, recovery decisions, notification construction, business validation, DTO
  mapping, provider calls, and domain authorization queries are prohibited from the kernel.
- Define measurable public-surface and dependency budgets plus automated architecture checks
  that reject domain dependencies, service-locator capabilities, alternate execution paths,
  and unbounded interface growth.
- Add no speculative extension point. Every abstraction must either remove demonstrated
  duplication or enforce a tested invariant, and completion evidence must show that the new
  surface is materially smaller than the one removed.
- Generate a cutover manifest before implementation that maps every command, caller, handler,
  API/Engine registration, receipt/result contract, replay authorizer, database grant/policy,
  and test fixture to exactly one replacement or deletion. The cutover cannot start with an
  unclassified manifest row.
- Preserve the existing `AtomicCommandReceipts` and `AtomicAuditLogs` schemas and historical
  rows as durable data contracts. The new generic runner becomes their only writer/reader;
  old handlers and runtime paths do not survive. Prove replay parity for every stored result
  contract before cutover.
- Treat these as hard kernel ceilings, enforced by CI architecture tests: at most four public
  kernel interfaces, at most twenty public members across them, exactly one public command
  execution method, zero domain persistence properties on the execution context, zero domain
  namespace/project dependencies, zero command-type switches, and no service lookup API.
  Non-generated production kernel code must remain below 1,000 logical lines.
- A temporary architecture exception requires one exact symbol, a named owner, a tracked
  removal task, and an expiration date in the CI allowlist. Wildcards and permanent exceptions
  are forbidden, and an exception cannot create a second execution path.
- Cutover proof must include exact-result replay parity, changed-payload conflict, unauthorized
  replay denial, audit/outbox failure rollback, commit-unknown replay, generated-SQL DB-side
  assertions, API/Engine registration parity, database grant/policy/function diff, and
  repository-wide zero references to every retired symbol.
- Database rollback support may restore the previous deployment as one versioned rollback
  operation. It must not leave a feature flag, fallback, compatibility adapter, old grant set,
  or simultaneously callable legacy runtime path in the deployed application.
- The staged compatibility and vertical-slice migration proposed in the original audit is
  explicitly rejected. It is not an alternative implementation plan for `TSK-781`.

## Faster operating model for the continuation

The previous run spent too much time serially switching from testing into coding, rebuilding,
restarting, redeploying, and then reconstructing the exact test context. Use this operating
model:

### Keep the tester moving

- The primary agent owns the calendar, exact scan selection, browser/phone sessions, execution
  ledger, bug ledger, and financial oracle.
- Use only one or two GPT-5.5 coding agents at a time for reproduced defects.
- Give each fixer an exact bug ID, reproduction, owned files, acceptance proof, atomicity and
  DB-side constraints, and an instruction not to revert other work.
- The tester continues with independent playbook runs while fixes are underway.
- Do not create a separate reviewer lane for every small fix. Use focused proof, then a
  checkpoint review for major lease, auth, money, or atomic-workflow changes.

### Keep the stack local and hot

- Local API, web, Engine-on-demand, and Flutter debug on the physical phone are the fastest
  feedback loop.
- Web uses Vite hot updates; Flutter debug uses hot reload.
- Batch related UI-only changes before one rebuild/restart.
- Keep critical auth, ledger, and transaction fixes isolated and prove each one before
  continuing.
- Use Azure only for a checkpoint, environment-specific behavior, or final verification.
  Rebuilding and moving every small change to Azure caused substantial avoidable delay.

### Prevent lost chronology

- Before advancing a simulation date, run a prerequisite check against `schedule.csv` and the
  database. The January 8-15 mortgage gap was discovered only after January 21 and forced this
  rollback.
- At the end of each simulated day, reconcile expected run IDs, scans, CRUD rows,
  notifications, and financial entries before moving the clock.
- Treat the Engine as opt-in. Start it for the exact worker run, wait for the expected
  idempotent result, then stop it.
- Take a recoverable database snapshot at month boundaries and before high-risk automation
  batches.

### Reduce repeated setup

- Add or use a small QA “doctor” command that verifies services, simulation flag and clock,
  database, fresh login, ADB device/reverse, scan corpus, and writable evidence paths.
- Keep stable role-specific QA accounts instead of repeatedly resetting passwords just to
  enter a surface.
- Pre-stage that day's exact scan files in a uniquely named phone folder so Android
  DocumentsUI does not require repeated ambiguous searches.
- Use API and PostgreSQL readback as verification after a real UI action, not as a substitute
  for the required UI action.
- Upsert ledger rows by `run_id` or check for an existing run ID before append. The 10 duplicate
  IDs now create avoidable reconciliation work.

### Build and resource discipline

- Never run more than two heavy builds/tests concurrently.
- Prefer focused PostgreSQL and UI tests; reuse build output.
- Set `MSBUILDDISABLENODEREUSE=1` and shut down the .NET build server after heavy batches.
- The agent that opens a browser owns closing its entire named process tree immediately.
- A fixer that creates a worktree owns removing it immediately after its branch is
  merged/pushed/abandoned; the primary agent verifies cleanup.

## Definition of done

TSK-754 is not complete until:

- Every planner run is executed through a real allowed role and surface.
- Every applicable screen and field has saved and read back on web and mobile.
- Scan and manual paths are both covered everywhere they apply.
- Every generated scan asset required by the planner is actually uploaded where specified.
- Every observed bug is recorded, with fixed bugs re-proved in the real browser/device.
- Notifications, reminders, messages, assignments, work orders, tenant portal, CRUD,
  lifecycle, and destructive/recovery paths are certified.
- Execution-ledger run IDs are unique and traceable to evidence.
- The product's year-end cash, receivables, deposits, income, expenses, debt service, loan
  balances, and journal/control totals reconcile to the external oracle with explained zero
  variance.
- The final checkpoint is run in the Azure verification environment and on a real mobile
  target.
- The branch is merged and TSK-754 is verified `Done`.

Until then, preserve the database, corpus, evidence, ignored ledgers, branch, and worktree.
