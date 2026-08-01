# TSK-754 / YS-295 Loan-Payment Reconciliation Discovery Brief

**Original discovery verdict:** ROADMAP DISCOVERY READY
**Supplemental discovery verdict:** ROADMAP DISCOVERY READY — exact proof paths,
acceptance mappings, and browser viewport recovered.
**Goal:** Correct the premature February loan-payment state through one append-only
Atomic path while preserving LoanPayments 67 and 68 byte-for-byte.
**Planning retry:** 1; no numeric planning or execution retry limit is recorded.

## Authority and user outcome

The current TSK-754 handoff and YS-295 bug evidence establish seven required
outcomes:

1. Reconcile Loans 3 and 10 to the February 20 due date stated by their
   authoritative statements.
2. Add the statement-required 318.00 monthly escrow configuration to Loan 3.
3. Preserve LoanPayments 67 and 68 as historical facts without deletion or
   update.
4. Apply supported append-only correction snapshots rather than rewriting the
   original occurrences.
5. Prove the corrected balances, principal, interest, and escrow.
6. Prevent debt-service automation from creating another `2027-02` occurrence
   or moving either balance again.
7. Re-run the February 20 verification with database, ledger, audit, outbox,
   web, and physical-phone evidence.

The physical mobile target is the user-connected Samsung `SM_S906U`. The Azure
emulator is not an acceptance substitute.

## Observable acceptance

1. Loan 10 persists due day 20; Loan 3 persists due day 20 and escrow 318
   through the existing Atomic PATCH, with one receipt, Loan audit, and outbox
   result per idempotency key.
2. Original Payment 67 retains hash
   `48d0d36c116ee5d78453265bdd8d3489`; original Payment 68 retains hash
   `3d0d038769f3c39390f2f95674adc3c6`; every original column is unchanged.
3. Each confirmed February statement appends exactly one full effective
   correction snapshot linked to its original payment. Replay returns the
   canonical result and appends nothing.
4. Effective Payment 68 is due February 20 with principal 431, interest 615,
   escrow 318, total 1,364, and resulting Loan 10 balance 124,963.
5. Effective Payment 67 is due February 20 with principal 442, interest 628,
   escrow 318, total 1,388, and resulting Loan 3 balance 130,016.
6. Before February 20 both effective rows remain Scheduled and cause no cash
   movement. After confirmation both are Paid exactly once; February cash is
   2,752, principal is 873, interest is 1,243, and escrow is 636.
7. Latest-correction selection and every filter, join, group, aggregate, sort,
   and page remain in one translated SQL statement or a database view. There is
   no materialize-then-shape, load-loop, N+1, per-row follow-up, lazy loading,
   or client-side evaluation.
8. Failure immediately after correction insertion rolls back the correction,
   Loan balance, receipt, required audits, and required outbox rows together.
9. A second February 20 Engine run leaves exactly one `2027-02` payment per
   loan, unchanged correction counts, and unchanged balances.
10. Real web proof at
    `https://localhost:5667/properties/1?area=property-finances` and
    `https://localhost:5667/properties/2?area=property-finances` shows the
    corrected source fields and effective schedules after reload at
    1440 × 900 CSS pixels.
11. Real-device proof on Samsung `SM_S906U` uses normal Property finances
    navigation to edit, scan/match, confirm, reopen the amortization schedules,
    and obtain the same split and balances as web.
12. Adjacent unauthorized and stale-scope attempts create no correction, Loan
    mutation, receipt, audit, or outbox row.

## Exact discovered implementation map

Create:

- `RentalCommand.Core/Entities/LoanPaymentCorrection.cs`
- `RentalCommand.Data/LoanPaymentEffectiveQuery.cs`
- `RentalCommand.Data/Migrations/20260730010000_AddLoanPaymentCorrections.cs`
- `RentalCommand.Data/Migrations/20260730010000_AddLoanPaymentCorrections.Designer.cs`

Modify:

- `RentalCommand.Core/Entities/LoanPayment.cs`
- `RentalCommand.Data/RentalCommandDbContext.cs`
- `RentalCommand.Data/Migrations/RentalCommandDbContextModelSnapshot.cs`
- `RentalCommand.Data/FoundationBaselinePostgreSql.cs`
- `RentalCommand.Data/Scanning/ProductionScanConfirmationTargetWriter.cs`
- `RentalCommand.Data/Automation/AtomicScheduledFinancePersistence.cs`
- `RentalCommand.Api/Services/Domain/AtomicMoneyMutation.cs`
- `RentalCommand.Api/Services/Domain/LoanService.cs`
- `RentalCommand.Api/Services/Domain/AccountingService.cs`
- `RentalCommand.Api/Services/Domain/ScheduleEService.cs`
- `RentalCommand.Api/Services/Domain/ReportsService.cs`
- `RentalCommand.Api/Services/Domain/BankingService.cs`
- `RentalCommand.IntegrationTests/ProductionScanConfirmationTargetWriterTests.cs`
- `RentalCommand.IntegrationTests/WorkspaceAuthorizationKernelTests.cs`
- `RentalCommand.IntegrationTests/ScheduledFinanceAtomicCommandTests.cs`
- `RentalCommand.Api.Tests/Domain/LoanServiceTests.cs`
- `RentalCommand.Api.Tests/Domain/AccountingServiceTests.cs`
- `RentalCommand.Api.Tests/Domain/ScheduleEServiceTests.cs`
- `RentalCommand.Api.Tests/Domain/ReportsServiceTests.cs`
- `RentalCommand.Data.Tests/FoundationBaselinePostgreSqlTests.cs`

No web or mobile production file requires modification. The existing loan edit,
scan, schedule, post, and read contracts remain the supported UI path.

Exact persistent proof files and acceptance mapping:

- `output/qa/tsk754-evidence/YS-295-acceptance-proof.txt` → YS295-01 through
  YS295-09 and YS295-12; it also records the YS295-10/11 web/device flow
  narrative.
- `output/playwright/YS-295-web-arbor-effective-schedule.png` → YS295-04,
  YS295-06, YS295-10.
- `output/playwright/YS-295-web-briar-effective-schedule.png` → YS295-05,
  YS295-06, YS295-10.
- `output/playwright/YS-295-web-adjacent-unauthorized.png` → YS295-12.
- `output/qa/tsk754-evidence/YS-295-mobile-arbor-effective-schedule.png` →
  YS295-04, YS295-06, YS295-11.
- `output/qa/tsk754-evidence/YS-295-mobile-briar-effective-schedule.png` →
  YS295-05, YS295-06, YS295-11.
- `output/qa/tsk754-evidence/YS-295-mobile-stale-scope-denied.png` → YS295-12.

The exact YS295-10 real-browser viewport is 1440 × 900 CSS pixels. TSK-754 owns
web evidence under `output/playwright/` and physical-device evidence under
`output/qa/tsk754-evidence/`.

## Discovered transaction and query boundary

- `LoanPaymentCorrection` is an append-only full effective snapshot linked to
  its original `LoanPayment`.
- Existing `PostPayment` and scan confirmation acquire the payment lock and
  append rather than update.
- The correction, mutable Loan balance, Atomic receipt, required correction and
  Loan audits, and LoanPayment/Loan data-update outbox messages share the
  existing explicit Atomic transaction and commit or roll back together.
- The existing Atomic runner remains unchanged.
- API access to the correction store is SELECT/INSERT only; Engine access is
  SELECT only.
- `LoanPaymentEffectiveQuery` is the one translated projection used by the
  existing loan, schedule, accounting, Schedule E, reports, banking, and Engine
  consumers.

## Focused commands

Run serially:

```bash
MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.IntegrationTests/RentalCommand.IntegrationTests.csproj --filter "FullyQualifiedName~ProductionScanConfirmationTargetWriterTests.LoanScanConfirmation"
MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.IntegrationTests/RentalCommand.IntegrationTests.csproj --filter "FullyQualifiedName~WorkspaceAuthorizationKernelTests.AtomicMoneyMutation_PostLoanPayment|FullyQualifiedName~ScheduledFinanceAtomicCommandTests.DebtService"
MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~LoanServiceTests|FullyQualifiedName~AccountingServiceTests|FullyQualifiedName~ScheduleEServiceTests|FullyQualifiedName~ReportsServiceTests"
MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Data.Tests/RentalCommand.Data.Tests.csproj --filter "FullyQualifiedName~FoundationBaselinePostgreSqlTests"
pnpm --dir web check
cd mobile && flutter test test/property_loans_mobile_test.dart
/Users/blackcolours/Library/Android/sdk/platform-tools/adb devices -l
/Users/blackcolours/Library/Android/sdk/platform-tools/adb reverse tcp:5666 tcp:5666
dotnet build-server shutdown
```

Static web/mobile checks support but never replace real browser/device proof.
The real browser must be resized to 1440 × 900 CSS pixels before YS295-10 proof.

## Evidence that selected this path

- `Docs/Testing/YearSimulation2027/TSK-754-FRESH-AGENT-HANDOFF.md:102-109`
  prohibits deletion/rewrite and requires append-only reconciliation.
- `output/qa/tsk-754-bug-ledger.csv:287` records the premature occurrences,
  amounts, receipt, and audits.
- Live PostgreSQL evidence found Loan 3 / Payment 67 at due day 12, escrow 0,
  balance 130,458 and Loan 10 / Payment 68 at due day 12, escrow 318, balance
  125,394.
- `RentalCommand.Core/Entities/LoanPayment.cs:6-47` owns the immutable
  occurrence contract; `RentalCommand.Data/RentalCommandDbContext.cs:1999-2023`
  owns the `(LoanId, PeriodKey)` occurrence fence.
- `RentalCommand.Api/Services/Domain/AtomicMoneyMutation.cs:405-531` owns the
  existing Atomic post/update flow.
- `RentalCommand.Data/Scanning/ProductionScanConfirmationTargetWriter.cs:1019-1176`
  currently rewrites the payment.
- `RentalCommand.Data/Atomic/AtomicTransactionRunner.cs:64-233` is the unchanged
  canonical receipt/business/audit/outbox transaction.
- `RentalCommand.Data/Automation/ScheduledFinanceCommandHandlers.cs:35-132`
  and `AtomicScheduledFinancePersistence.cs:158-173` own schedule generation
  and its Atomic tail.
- No correction entity, service, route, or append-only effective projection
  currently exists.

## Explicit non-goals

- No deletion or update of Payments 67/68.
- No alternate Atomic runtime, compatibility bridge, handler factory,
  generalized financial architecture, unrelated migration, broad hardening,
  or unrelated bug-ledger work.
- No Owner lifecycle `RUN-20270212-07`, later simulation execution, emulator
  proof, worktree creation/removal, dirty-WIP cleanup, branch change, commit, or
  push.
- No web or mobile production change.

## Stop condition

Discovery is complete because the exact source, test, command, transaction,
query, persistent proof, acceptance mapping, and 1440 × 900 browser viewport
are known. Route the amended contract to a fresh
`roadmap-contract-reviewer`. Preserve the future-step gate below unchanged.

## Deferred future-step revision log

- **Originating terminal checkpoint:** `Docs/Testing/YearSimulation2027/TSK-754-FRESH-AGENT-HANDOFF.md`,
  `Authoritative continuation checkpoint — 2026-07-30, February 12 after RUN-06`.
- **Accepted-step evidence reference:** `RUN-20270212-06` and its evidence paths
  recorded in that checkpoint; this future impact does not revise accepted
  work.
- **Affected unstarted boundary and planning paths:** Only the YS-295 portion of
  `RUN-20270220-01` for SCN-0478/FIN-01121 and SCN-0479/FIN-01122, represented
  by this `Deferred future-step revision log` and by the corresponding
  pre-match gate plus `Deferred future-step revision gate` in
  `Docs/superpowers/plans/2026-07-30-tsk-754-ys-295-loan-payment-reconciliation-execution.md`.
- **Reason:** The renderer reused immutable portfolio-opening balances
  125,825/130,900 instead of the period-opening unpaid principal immediately
  before the reviewed February payment. The supported product matcher is
  correct: it requires that opening value and derives ending unpaid principal
  after principal.
- **Before/after relationship:** Before, SCN-0478/0479 carried the immutable
  portfolio-opening balances and described the matched field as ending
  principal. After, that field means **opening unpaid principal immediately
  before the reviewed payment**:
  - SCN-0478 carries opening 125,394, principal 431, interest 615, escrow 318,
    and total 1,364; its derived ending unpaid principal is 124,963.
  - SCN-0479 carries opening 130,458, principal 442, interest 628, escrow 318,
    and total 1,388; its derived ending unpaid principal is 130,016.
  - FIN-01121/FIN-01122 and their journal splits remain unchanged.
  - The corrected PDFs must visibly and textually carry the opening values and
    must no longer claim that the matched field is ending balance.
  - The supported product matcher remains unchanged.
- **Future gate:** This planning-only revision authorizes no product file,
  product test, proof-file, command, fixture, manifest, oracle, journal, PDF,
  matcher, task-state, runtime, git, or worktree change. The official YS-295
  statement-match gate remains closed until fresh normal execution discovery
  names the exact implementation/test/proof files and commands, its executable
  contract passes independent review, the scoped implementation is completed,
  and selected SCN-0478/0479 PDF text extraction and rendered-page inspection
  prove the values and label above.
- **Non-goals:** SCN-0480–0497, other loans, the later schedule, browser/mobile
  proof, corpus cleanup, Payments 67/68, live-database mutation, cleanup or
  alteration of the 711-entry dirty WIP, and terminally accepted past steps
  remain deferred and unchanged.
- **Planning retry:** Current planning retry count is 1; no numeric planning or
  execution retry limit is configured.
- **Proposed review state:** Planning revision written; not reviewed. A fresh
  `roadmap-contract-reviewer` supplies the verdict and audit-log review state.
