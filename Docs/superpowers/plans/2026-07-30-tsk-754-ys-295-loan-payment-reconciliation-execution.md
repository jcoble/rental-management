# TSK-754 / YS-295 Append-Only Loan-Payment Reconciliation Contract

**Goal:** Preserve Payments 67/68 while correcting their effective February
loan-payment state through the existing Atomic transaction and one DB-side
projection.
**Source brief:** `Docs/superpowers/plans/2026-07-30-tsk-754-ys-295-loan-payment-reconciliation-discovery.md`
**Active goal:** TSK-754 / YS-295
**Active step:** Step 1 — implement and prove the append-only correction boundary
**Plan state:** Ready for fresh roadmap-contract review; not approved until
`PLAN CONTRACT PASS`.
**Planning retry:** 1; no numeric planning or execution retry limit is recorded.

## Preserved authority

- Work only in
  `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-754-year-simulation-execution`
  at starting HEAD `8f0b3c5e1d0c0893161fccd85526133ec00f45a1`.
- Preserve all 711 starting dirty entries and unrelated WIP. Do not reset,
  stash, clean, switch branches, create/remove a worktree, commit, or push.
- Payments 67 and 68 are immutable historical facts.
- The existing thin Atomic runtime, scoped `RentalCommandDbContext`, explicit
  transaction, and shared SaveChanges boundary remain the only write path.
- The physical mobile target is Samsung `SM_S906U`; do not substitute the Azure
  emulator.

## Numbered acceptance checks

1. **YS295-01 — Source configuration:** Loan 10 persists due day 20. Loan 3
   persists due day 20 and monthly escrow 318. Each existing Atomic PATCH has
   one canonical receipt, one required Loan audit, and its required outbox
   result per idempotency key; replay makes no new row or state change.
2. **YS295-02 — Immutable history:** Every original column of LoanPayments 67
   and 68 is byte-for-byte unchanged. Payment 67 retains
   `48d0d36c116ee5d78453265bdd8d3489`; Payment 68 retains
   `3d0d038769f3c39390f2f95674adc3c6`.
3. **YS295-03 — Append-only/idempotent correction:** Each distinct confirmed
   February statement appends exactly one full effective correction snapshot
   linked to its original payment. The command locks the original payment;
   replay returns the canonical result without a second correction.
4. **YS295-04 — Effective Arbor result:** Payment 68 is effectively due
   February 20 with principal 431, interest 615, escrow 318, total 1,364, and
   resulting Loan 10 balance 124,963.
5. **YS295-05 — Effective Briar result:** Payment 67 is effectively due
   February 20 with principal 442, interest 628, escrow 318, total 1,388, and
   resulting Loan 3 balance 130,016.
6. **YS295-06 — Period totals and timing:** Before February 20 both effective
   rows remain Scheduled and cause no cash movement. After confirmation both
   are Paid once; February cash is 2,752, principal is 873, interest is 1,243,
   and escrow is 636.
7. **YS295-07 — Database-side reads:** Latest-correction selection plus every
   filter, join, group, aggregate, sort, and page used by loan schedule,
   accounting, Schedule E, reports, banking, and Engine consumers is one
   translated SQL statement or a database view. Generated SQL and command
   counts prove there is no materialize-then-shape, load-loop, N+1, per-row
   query, lazy loading, or client evaluation.
8. **YS295-08 — Atomic rollback:** A deterministic failure immediately after
   correction insertion rolls back the correction, mutable Loan balance,
   Atomic receipt, required correction/Loan audits, and required
   LoanPayment/Loan data-update outbox messages together.
9. **YS295-09 — Engine duplicate prevention:** A second February 20
   debt-service run leaves one `2027-02` payment per loan, unchanged correction
   counts, unchanged balances, and no duplicate ledger/audit/outbox movement.
10. **YS295-10 — Real web flow:** At
    `https://localhost:5667/properties/1?area=property-finances` and
    `/properties/2?area=property-finances`, an authenticated Workspace
    Administrator uses Property finances → Mortgage / Loans, edits the source
    fields, scans/matches the assigned statement, reloads, and sees the exact
    effective schedule, split, status, and balance at 1440 × 900 CSS pixels.
11. **YS295-11 — Real physical-phone flow:** On connected Samsung `SM_S906U`,
    the same administrator uses normal Property finances navigation, edits the
    other loan, scans/matches the other statement, reopens both amortization
    schedules, and sees the same split and balances as web. Static/widget proof
    and the Azure emulator do not satisfy this check.
12. **YS295-12 — Authorization and live readback:** Adjacent unauthorized and
    stale-scope attempts create no correction, Loan mutation, receipt, audit,
    or outbox. One DB-side live readback reconciles the effective payments,
    mutable balances, receipt, audits, outbox, and original hashes. The final
    handoff names every resulting identifier and evidence path.

## Exact allowed files and acceptance map

Create only:

| File | Acceptance |
|---|---|
| `RentalCommand.Core/Entities/LoanPaymentCorrection.cs` | YS295-02, 03, 08 |
| `RentalCommand.Data/LoanPaymentEffectiveQuery.cs` | YS295-04, 05, 06, 07, 09 |
| `RentalCommand.Data/Migrations/20260730010000_AddLoanPaymentCorrections.cs` | YS295-03, 07, 08 |
| `RentalCommand.Data/Migrations/20260730010000_AddLoanPaymentCorrections.Designer.cs` | YS295-03, 07, 08 |

Modify only:

| File | Acceptance |
|---|---|
| `RentalCommand.Core/Entities/LoanPayment.cs` | YS295-02, 03 |
| `RentalCommand.Data/RentalCommandDbContext.cs` | YS295-03, 07, 08, 12 |
| `RentalCommand.Data/Migrations/RentalCommandDbContextModelSnapshot.cs` | YS295-03, 07 |
| `RentalCommand.Data/FoundationBaselinePostgreSql.cs` | YS295-03, 07, 12 |
| `RentalCommand.Data/Scanning/ProductionScanConfirmationTargetWriter.cs` | YS295-02, 03, 04, 05, 06, 08, 12 |
| `RentalCommand.Data/Automation/AtomicScheduledFinancePersistence.cs` | YS295-06, 09 |
| `RentalCommand.Api/Services/Domain/AtomicMoneyMutation.cs` | YS295-01, 02, 03, 04, 05, 06, 08, 12 |
| `RentalCommand.Api/Services/Domain/LoanService.cs` | YS295-04, 05, 07, 10, 11 |
| `RentalCommand.Api/Services/Domain/AccountingService.cs` | YS295-06, 07, 10, 11 |
| `RentalCommand.Api/Services/Domain/ScheduleEService.cs` | YS295-06, 07 |
| `RentalCommand.Api/Services/Domain/ReportsService.cs` | YS295-06, 07 |
| `RentalCommand.Api/Services/Domain/BankingService.cs` | YS295-06, 07 |
| `RentalCommand.IntegrationTests/ProductionScanConfirmationTargetWriterTests.cs` | YS295-02, 03, 04, 05, 06, 08, 12 |
| `RentalCommand.IntegrationTests/WorkspaceAuthorizationKernelTests.cs` | YS295-01, 03, 08, 12 |
| `RentalCommand.IntegrationTests/ScheduledFinanceAtomicCommandTests.cs` | YS295-06, 08, 09 |
| `RentalCommand.Api.Tests/Domain/LoanServiceTests.cs` | YS295-04, 05, 07 |
| `RentalCommand.Api.Tests/Domain/AccountingServiceTests.cs` | YS295-06, 07 |
| `RentalCommand.Api.Tests/Domain/ScheduleEServiceTests.cs` | YS295-06, 07 |
| `RentalCommand.Api.Tests/Domain/ReportsServiceTests.cs` | YS295-06, 07 |
| `RentalCommand.Data.Tests/FoundationBaselinePostgreSqlTests.cs` | YS295-03, 07, 12 |

Exercise unchanged:

- `RentalCommand.Data/Atomic/AtomicTransactionRunner.cs` for YS295-01, 03, 08,
  and 12.
- `RentalCommand.Data/Automation/ScheduledFinanceCommandHandlers.cs` for
  YS295-09.
- `mobile/test/property_loans_mobile_test.dart` for supporting static evidence
  for YS295-11. It is not an allowed modification.

No web or mobile production file may change. No other source, test, migration,
configuration, planner, ledger, handoff, or runtime file may be created or
modified by the implementer.

## Exact persistent proof files and acceptance map

Acceptance verification may create only:

| Proof file | Acceptance |
|---|---|
| `output/qa/tsk754-evidence/YS-295-acceptance-proof.txt` | YS295-01–09, YS295-12; command results, hashes, generated SQL/counts, rollback, replay, Engine rerun, identifiers, one-statement readback, and YS295-10/11 flow narrative |
| `output/playwright/YS-295-web-arbor-effective-schedule.png` | YS295-04, YS295-06, YS295-10 |
| `output/playwright/YS-295-web-briar-effective-schedule.png` | YS295-05, YS295-06, YS295-10 |
| `output/playwright/YS-295-web-adjacent-unauthorized.png` | YS295-12 |
| `output/qa/tsk754-evidence/YS-295-mobile-arbor-effective-schedule.png` | YS295-04, YS295-06, YS295-11 |
| `output/qa/tsk754-evidence/YS-295-mobile-briar-effective-schedule.png` | YS295-05, YS295-06, YS295-11 |
| `output/qa/tsk754-evidence/YS-295-mobile-stale-scope-denied.png` | YS295-12 |

This is the complete persistent proof set. Do not create another screenshot,
trace, video, log, SQL dump, or report. The exact real-browser viewport for
YS295-10 is 1440 × 900 CSS pixels.

## Step 1 — Implement and prove the append-only correction boundary

**Status:** Active after `PLAN CONTRACT PASS`
**Acceptance:** YS295-01 through YS295-12

### 1.1 Preserve the starting truth

1. Record starting HEAD/status without changing the index or worktree.
2. In one DB-side statement, record every original column and the two exact
   hashes for Payments 67/68 in
   `output/qa/tsk754-evidence/YS-295-acceptance-proof.txt`.
3. Record the current Loan 3/10 due days, escrow, balances, occurrence counts,
   correction count, receipt/audit/outbox counts, and cash movement.
4. If either starting hash differs from YS295-02, stop without implementation.

### 1.2 Add one append-only effective model

1. Add the full effective correction snapshot and relationship to the immutable
   payment.
2. Add its schema, migration, model snapshot, baseline permissions, and
   constraints. API has SELECT/INSERT only; Engine has SELECT only.
3. Implement `LoanPaymentEffectiveQuery` as the single composable query owner.
   The latest correction, authorization scope, filtering, joining, grouping,
   aggregation, sorting, and paging must remain server-side in the generated
   SQL.
4. Route only the named existing loan, accounting, Schedule E, reports,
   banking, scheduled-finance, posting, and statement-confirmation owners
   through that projection. Do not add a second route, service, compatibility
   path, or fallback.

### 1.3 Keep correction, audit, outbox, and balance atomic

1. Existing loan PATCH remains the source configuration path for YS295-01.
2. Posting and statement confirmation lock the original payment, append the
   correction, update the mutable Loan balance once, bind the required
   correction and Loan audits, stage the required LoanPayment and Loan
   data-update outbox messages, and commit through the unchanged Atomic runner.
3. Every `SaveChanges` participates in the same explicit transaction.
4. Required receipt, audit, or outbox failure fails the command. External
   notification/broadcast work occurs only after commit.
5. Replay uses the original idempotency key and returns its canonical result
   without a new correction, balance movement, audit, outbox, or occurrence.
6. Inject failure immediately after correction insertion and prove the complete
   rollback in `output/qa/tsk754-evidence/YS-295-acceptance-proof.txt`.

### 1.4 Run focused verification serially

Run one command at a time from the worktree root unless the command changes
directory:

1. YS295-02, 03, 04, 05, 06, 08, and 12:

   ```bash
   MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.IntegrationTests/RentalCommand.IntegrationTests.csproj --filter "FullyQualifiedName~ProductionScanConfirmationTargetWriterTests.LoanScanConfirmation"
   ```

2. YS295-01, 03, 08, 09, and 12:

   ```bash
   MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.IntegrationTests/RentalCommand.IntegrationTests.csproj --filter "FullyQualifiedName~WorkspaceAuthorizationKernelTests.AtomicMoneyMutation_PostLoanPayment|FullyQualifiedName~ScheduledFinanceAtomicCommandTests.DebtService"
   ```

3. YS295-04, 05, 06, and 07:

   ```bash
   MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~LoanServiceTests|FullyQualifiedName~AccountingServiceTests|FullyQualifiedName~ScheduleEServiceTests|FullyQualifiedName~ReportsServiceTests"
   ```

4. YS295-03, 07, and 12:

   ```bash
   MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Data.Tests/RentalCommand.Data.Tests.csproj --filter "FullyQualifiedName~FoundationBaselinePostgreSqlTests"
   ```

5. Supporting static check for YS295-10; it does not satisfy real-browser
   proof:

   ```bash
   pnpm --dir web check
   ```

6. Supporting static check for YS295-11; the test file remains unchanged and
   this does not satisfy physical-device proof:

   ```bash
   cd mobile && flutter test test/property_loans_mobile_test.dart
   ```

7. Verify the required physical target and API reverse for YS295-11:

   ```bash
   /Users/blackcolours/Library/Android/sdk/platform-tools/adb devices -l
   /Users/blackcolours/Library/Android/sdk/platform-tools/adb reverse tcp:5666 tcp:5666
   ```

8. After the serialized .NET batch:

   ```bash
   dotnet build-server shutdown
   ```

All commands must pass with the mapped assertions present. Generated SQL and
reader-command counts go only in
`output/qa/tsk754-evidence/YS-295-acceptance-proof.txt`. No build or test may
run in parallel with another heavy build/test.

### 1.5 Execute the supported web and physical-device flow

Do not execute this section until a fresh reviewer returns
`PLAN CONTRACT PASS`. Resize the real browser to exactly 1440 × 900 CSS pixels
before capturing YS295-10.

1. Open a uniquely named web session `tsk754-ys295-web`. As Workspace
   Administrator, use Property 1 finances to edit Arbor Loan 10 to due day 20.
   Capture its final effective schedule only at
   `output/playwright/YS-295-web-arbor-effective-schedule.png`.
2. On Samsung `SM_S906U`, use normal Property finances navigation for Property
   2 and edit Briar Loan 3 to due day 20 and escrow 318. Capture its final
   effective schedule only at
   `output/qa/tsk754-evidence/YS-295-mobile-briar-effective-schedule.png`.
3. Cross-reload both loans on the opposite client. Before February 20, prove
   both effective payments are Scheduled, the original hashes are unchanged,
   and cash and post-January live balances have not moved. Capture the web
   Briar state only at
   `output/playwright/YS-295-web-briar-effective-schedule.png` and the physical
   phone Arbor state only at
   `output/qa/tsk754-evidence/YS-295-mobile-arbor-effective-schedule.png`.
4. Keep the official February 20 statement-match portion closed under
   `Deferred future-step revision gate`. The selected field semantics are
   opening unpaid principal immediately before the reviewed payment, not ending
   balance. SCN-0478 must carry opening 125,394, principal 431, interest 615,
   escrow 318, and total 1,364, with derived ending 124,963. SCN-0479 must
   carry opening 130,458, principal 442, interest 628, escrow 318, and total
   1,388, with derived ending 130,016. FIN-01121/FIN-01122 and their journal
   splits remain unchanged; the product matcher remains unchanged.
5. Open that gate only after fresh normal execution discovery names the exact
   implementation/test/proof files and commands, its executable contract
   passes independent review, scoped implementation produces corrected
   SCN-0478/0479 PDFs, and selected-PDF text extraction plus rendered-page
   inspection prove the opening values and that the field is no longer called
   ending balance. Then use web Scan with corrected SCN-0478, select existing
   Payment 68, review the exact YS295-04 split, and confirm once. On Samsung,
   use Scan with corrected SCN-0479, select existing Payment 67, review the
   exact YS295-05 split, and confirm once.
6. Replay both confirmations with their original idempotency keys. Reopen both
   amortization schedules on both clients and prove YS295-04 through YS295-06.
7. Run the February 20 debt-service path a second time and prove YS295-09.
   Stop Engine immediately after the exact run.
8. Attempt the same mutation through an adjacent unauthorized role and stale
   property scope on the real clients; prove YS295-12 at
   `output/playwright/YS-295-web-adjacent-unauthorized.png` and
   `output/qa/tsk754-evidence/YS-295-mobile-stale-scope-denied.png`.
9. Re-read the database in one statement. Record Loans, immutable payments,
   corrections, canonical receipts, audits, outbox, ledger/cash totals, and
   occurrence counts only in
   `output/qa/tsk754-evidence/YS-295-acceptance-proof.txt`.
10. Recompute every original payment column and both hashes. Record the exact
    YS295-02 result only in
    `output/qa/tsk754-evidence/YS-295-acceptance-proof.txt`.

Do not append or claim the overall `RUN-20270220-01` schedule row beyond this
YS-295 proof. The fresh future-step revision owns the planner/fixture contract;
all unrelated February 20 loans and later simulation work remain deferred.

### 1.6 Independent gates and cleanup

1. A fresh `relevance-reviewer` must return `RELEVANCE PASS` for this one step,
   its exact allowed files, all command results, and YS295-01 through YS295-12.
2. A fresh `acceptance-verifier` must inspect every numbered check, the real web
   flow, the physical Samsung flow, the generated SQL, database readback,
   rollback, idempotency, hashes, and proof paths.
3. Browser opener closes `tsk754-ys295-web` and verifies its Playwright daemon
   and complete Chrome helper tree exited.
4. Preserve the user's wireless-debugging connection. Remove only a reverse
   mapping created solely by this lane; do not disconnect or reset the phone.
5. Stop Engine after its exact run, execute `dotnet build-server shutdown`, and
   verify no lane-owned build/test process remains.
6. Worktree cleanup remains prohibited because this is active dirty WIP.

## Transaction, idempotency, audit, and outbox invariants

- Loan source PATCH: Loan mutation + receipt + required Loan audit + required
  outbox commit together.
- Payment correction: payment lock + correction insert + Loan balance update +
  receipt + required correction/Loan audits + required LoanPayment/Loan
  data-update outbox rows commit together.
- Failure after any required write rolls back every row in that command.
- Duplicate idempotency replay returns the prior canonical result and performs
  no new write or balance movement.
- Original Payments 67/68 are never updated or deleted.
- No side effect may publish from uncommitted state.

## Explicit non-goals

- No Payment 67/68 deletion/update, alternate Atomic runtime, compatibility
  bridge, handler factory, generalized financial architecture, unrelated
  migration, broad hardening, or speculative test.
- No web/mobile production change, Owner lifecycle `RUN-20270212-07`,
  unrelated bug-ledger work, Azure emulator proof, or later simulation work.
- No planner/fixture correction or decision about whether the disputed field
  is opening or ending balance.
- No worktree creation/removal, cleanup of the 711 dirty entries, branch
  change, commit, push, or unrelated handoff edit.

## Retry and stop rules

- Current planning retry count is 1. No numeric planning or execution retry
  limit was supplied; do not invent one.
- `PLAN CONTRACT PASS` routes to `scope-implementer`.
- On `PLAN CONTRACT FAIL`, stop and return the reviewer-authored planning fixer
  task to the controller for retry authority; do not self-amend.
- On a reproduced acceptance failure, return exactly one narrow fixer task.
  Because no execution retry limit is configured, the controller must stop for
  retry authority before dispatching it.
- Stop immediately for changed original hashes, inability to keep the original
  payments immutable, partial transaction evidence, client-side query shaping,
  missing required physical-phone proof, unexplained financial variance, or an
  unresolved future-step gate before the February 20 statement match.
- Static checks, tests, emulator proof, or screenshots from memory cannot waive
  a browser/device/DB acceptance failure.

## Handoff evidence

The role return and
`output/qa/tsk754-evidence/YS-295-acceptance-proof.txt` must report:

- starting and ending HEAD plus exact dirty-entry count;
- every changed/created file mapped to a numbered acceptance check;
- every focused command and result, run serially;
- before/after original Payment 67/68 hashes and original-column equality;
- Loan 3/10 source fields, effective splits, balances, totals, statuses, and
  `2027-02` occurrence/correction counts;
- receipt, audit, and outbox identifiers for both source edits and corrections;
- generated-SQL/command-count proof and the one-statement live readback;
- rollback injection and duplicate Engine/replay results;
- real web URLs and physical `SM_S906U` navigation/assertions;
- all exact proof paths;
- future revision status and whether the official statement-match gate was
  opened;
- `Browser cleanup`, `Device cleanup`, `Build cleanup`, `Engine cleanup`, and
  `Worktree cleanup: not removed` lines.

## Deferred future-step revision gate

- **Originating terminal checkpoint:** `Docs/Testing/YearSimulation2027/TSK-754-FRESH-AGENT-HANDOFF.md`,
  `Authoritative continuation checkpoint — 2026-07-30, February 12 after RUN-06`.
- **Accepted-step evidence reference:** `RUN-20270212-06` and its recorded
  evidence remain accepted and unchanged.
- **Affected unstarted boundary and planning path:** Only the YS-295 portion of
  `RUN-20270220-01` for SCN-0478/FIN-01121 and SCN-0479/FIN-01122, represented
  by steps 4–5 of the pre-match gate above and this
  `Deferred future-step revision gate`.
- **Reason:** The renderer reused immutable portfolio-opening balances
  125,825/130,900 instead of the period-opening unpaid principal immediately
  before the reviewed February payment. The supported product matcher is
  correct: it requires that opening value and derives ending unpaid principal
  after principal.
- **Before/after relationship:** Before, SCN-0478/0479 carried the immutable
  portfolio-opening balances and called the matched field ending principal.
  After, the field is opening unpaid principal immediately before the reviewed
  payment:
  1. SCN-0478 carries opening 125,394, principal 431, interest 615, escrow 318,
     and total 1,364; ending 124,963 is derived after principal.
  2. SCN-0479 carries opening 130,458, principal 442, interest 628, escrow 318,
     and total 1,388; ending 130,016 is derived after principal.
  3. FIN-01121/FIN-01122 and their journal splits remain unchanged.
  4. Corrected SCN-0478/0479 PDFs visibly and textually carry those opening
     values and no longer claim that the field is ending balance.
  5. The supported product matcher remains unchanged.
- **Allowed planning revision:** Modify only this gate, its corresponding
  pre-match steps 4–5, and the paired `Deferred future-step revision log` in
  `Docs/superpowers/plans/2026-07-30-tsk-754-ys-295-loan-payment-reconciliation-discovery.md`.
  Create no planning file. This revision authorizes no product file, product
  test, proof-file, command, fixture, manifest, oracle, journal, PDF, matcher,
  task-state, runtime, git, or worktree change.
- **Observable future acceptance gate:**
  1. Fresh normal execution discovery names exact allowed implementation,
     test, proof files, and commands for only SCN-0478/0479.
  2. That executable contract passes independent contract review before
     implementation.
  3. Scoped implementation corrects only the two selected PDFs and their
     source rendering inputs without changing the matcher, FIN-01121/FIN-01122,
     or journal splits.
  4. Selected-PDF text extraction and rendered-page inspection prove the
     opening values, splits, totals, derived endings, and removal of the ending
     balance claim.
  5. Only after items 1–4 pass may the pre-match flow continue to the existing
     web SCN-0478 and Samsung SCN-0479 confirmation steps.
- **Supported flow:** Correct the two selected statement PDFs through a fresh,
  independently reviewed execution contract; prove their text and rendered
  pages; then resume only the existing YS-295 statement-match flow.
- **Non-goals:** SCN-0480–0497, other loans, the later schedule,
  browser/mobile proof, corpus cleanup, Payments 67/68, live-database mutation,
  cleanup or alteration of the 711-entry dirty WIP, terminally accepted past
  steps, and any matcher change remain deferred and unchanged.
- **Planning retry:** Current planning retry count is 1; no numeric planning or
  execution retry limit is configured.
- **Proposed review state:** Planning revision written; not reviewed. A fresh
  `roadmap-contract-reviewer` supplies the verdict and revision-log review
  state.

## Completion gate

- [x] Fresh discovery supplied exact proof paths, their YS295 acceptance
      mappings, and the exact browser viewport.
- [x] The contract writer incorporated only those fresh facts.
- [ ] Fresh roadmap contract reviewer returns `PLAN CONTRACT PASS`.
- [ ] Every changed source/test file and every proof artifact is explicitly
      allowed and mapped above.
- [ ] YS295-01 through YS295-12 have fresh independent evidence.
- [ ] Original Payment 67/68 hashes and columns are unchanged.
- [ ] Generated SQL proves all shaping remains database-side.
- [ ] Rollback and replay/Engine duplicate checks pass.
- [ ] Real web and physical Samsung proof pass.
- [ ] The future-step revision gate is reviewed before the official statement
      match; this contract did not decide it.
- [ ] No deferred or non-goal work was implemented.
- [ ] Browser, device, Engine, and build cleanup are recorded.
- [ ] Final role handoff reports
      `Worktree cleanup: not removed — active dirty WIP`.
