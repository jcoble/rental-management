# TSK-803 Backend Lane B2 report

## Outcome

**verified.** Lane B2 conversion, database-side accounting read models, the approved API route and
DTO freeze, and the recurring-tenant-charge occurrence worker are present on
`jcoble/tsk-800-803-accounting-ledgers`. The closing route-contract correction is committed as
`273623adeaeabb9de4bb3f23939a9a60782c96c2` (`Fix receipt allocation route contract`); it makes the
receipt request's oldest-open-charge setting explicit, typed, and passed to the command.

**verified.** The full `RouteContract` filter passed 29 tests after the correction. The exact command
and output are recorded in the verification section below.

**verified.** This lane did not run conversion against a preserved simulation database and did not
touch the TSK-754 worktree or database. The only remaining commit after this source-fix commit is
the commit containing this report.

## Commits

The B2 implementation commits are (**verified** with `git log --oneline`):

1. `a6481a0b` — Implement historical accounting conversion.
2. `6e9f7808` — Add SQL-backed accounting read models.
3. `ac7c4c23` — Freeze accounting API routes and DTOs.
4. `38c36758` — Add recurring tenant charge occurrence worker.
5. `581098da` — Keep statement totals in PostgreSQL.
6. `273623ad` — Fix receipt allocation route contract (this task's source and test fix).

**verified.** The report commit is the commit that adds this file; its exact hash is included in
the final `git log` and worker handoff receipt because a report cannot contain its own hash without
changing that hash.

## Gate 1 — conversion approval and source map

**verified.** Gate 1 approval was obtained through the orchestration approval flow before the
conversion generators were implemented. The conversion service seeds the portfolio chart, uses a
caller-selected bounded batch, and keys every proposed entry by
`(PortfolioId, SourceType, SourceId, PostingRuleVersion)` at
`RentalCommand.Data/Accounting/AccountingConversionService.cs:68-110`.

**verified.** The source generator contract requires ordered, bounded source identities and SQL
source totals at `RentalCommand.Data/Accounting/AccountingConversionFramework.cs:8-29`; the
framework dispatches by source type at `:35-74`. The registered generator list is at
`RentalCommand.Data/Accounting/AccountingConversionService.cs:26-48`.

The approved map below records the source family, posting rule, SQL total approach, and historical
edge-case treatment. Every generator shares the same proposal rules used by its live adapter.

| Existing source family | Debit / credit rule | Source total and edge cases | Receipt |
| --- | --- | --- | --- |
| Tenant charge: rent, pet, parking, late fee, utility, other, and manual | Tenant A/R / configured income account | Sum posted charge amounts in SQL; skip drafts and deleted rows; preserve source effective date and dimensions. | **verified** `AccountingConversionService.cs:673-703`; generator and live rule share tenant-charge proposal code. |
| Tenant receipt and allocation | Operating Cash, Undeposited Funds, or Trust Cash / Tenant A/R, selected by receipt source and deposit context | Sum receipt amounts in SQL; an allocation settles charge rows and does not create income; one receipt owns one cash posting. | **verified** `AccountingConversionService.cs:673-871` and the live tenant receipt adapter in `RentalCommand.Data/Payments/TenantAccountingPosting.cs`. |
| Tenant concession, credit, adjustment, and opening balance | Configured contra-income or opening-balance account / Tenant A/R | Sum immutable posted source rows in SQL; do not convert drafts; corrections retain reversal lineage. | **verified** `AccountingConversionService.cs:673-703`. |
| Security-deposit funding | Security Deposit Trust Cash / Security Deposits Payable | Sum funding rows in SQL; never use an income account because cash moved. | **verified** `AccountingConversionService.cs:704-871`; the paired live rule is `TenantAccountingPosting.cs:172-216`. |
| Security-deposit application | Security Deposits Payable / Tenant A/R | Sum authorized applications in SQL; only authorized charge applications convert. | **verified** `AccountingConversionService.cs:704-871`. |
| Security-deposit refund | Security Deposits Payable / Security Deposit Trust Cash | Sum committed refunds in SQL; reversed refunds generate opposite lines with source lineage. | **verified** `AccountingConversionService.cs:704-871`. |
| Paid expense | Configured expense or asset account / selected cash account | Sum paid immutable expenses in SQL; exclude drafts, deleted rows, and expenses replaced by capitalization. | **verified** `AccountingConversionService.cs:271-355`; fixture assertion is `AccountingConversionPostgreSqlTests.cs:18-56`. |
| Bill incurred | Configured expense or asset account / Accounts Payable | Sum incurred bills in SQL; only committed bill facts are eligible. | **verified** `AccountingConversionService.cs:357-427`. |
| Bill payment | Accounts Payable / selected cash account | Sum committed bill payments in SQL; avoid a second expense posting. | **verified** `AccountingConversionService.cs:873-943`. |
| Provider receipt before settlement | Undeposited Funds / Tenant A/R | **Gate 1 correction, verified:** provider receipts debit Undeposited Funds until settlement; manual/direct receipts use operating cash and deposit-linked receipts use trust cash. The fixture keeps both settled and unsettled receipt cash in Undeposited Funds until the settlement journal exists. | **verified** `AccountingConversionPostgreSqlTests.cs:58-160`; live rule `TenantAccountingPosting.cs:57-85`. |
| Provider settlement | Destination bank cash / Undeposited Funds | Sum matched settlement transactions in SQL; a settled provider receipt and its settlement produce two journals, while an unsettled receipt remains undeposited. | **verified** `AccountingConversionService.cs:945-1035`; live rule `MoneyAccountingPosting.cs:826-855`. |
| Bank match, clear, and transfer | Match-specific expense/cash/payable lines; transfer destination cash / source cash | **Gate 1 correction, verified:** unmatched, ignored, and dismissed bank rows have no accounting effect, not an unsupported-source count. Same-ledger-account transfers report no accounting effect. Unsupported count is reserved for a source expected to produce a journal but lacking a usable mapping or immutable facts. | **verified** `AccountingConversionService.cs:1037-1092`; live reconciliation rules `MoneyAccountingPosting.cs:228-301,857-900`. |
| Loan payment | Mortgage Payable principal, Mortgage Interest, and Escrow Asset / selected cash account | Sum committed loan components in SQL; preserve component rows and correction lineage. | **verified** `AccountingConversionService.cs:1094-1186`; live rule `MoneyAccountingPosting.cs:473-574`. |
| Capital purchase and capitalization | Buildings, Land, or selected asset account / cash or Accounts Payable | Sum committed capital purchases in SQL; a capitalized paid expense suppresses its replaced expense journal and posts the capital purchase. | **verified** `AccountingConversionService.cs:1188-1252`; live rule `MoneyAccountingPosting.cs:607-666`. |
| Owner distribution | Owner-specific distribution equity account / cash | Sum approved distributions in SQL; bank evidence does not create a duplicate journal when approval already posted. | **verified** `AccountingConversionService.cs:1254-1320`; live rule `MoneyAccountingPosting.cs:574-605`. |
| Depreciation and owner contribution | Depreciation Expense / Accumulated Depreciation; Cash / Owner contribution equity | **assumed:** Lane B1 has no immutable source command/table for these historical actions. They are omitted and surfaced as unsupported/omitted reconciliation detail rather than posted to suspense; a future source-path decision is required. | **assumed** from the absence of a Lane B1 source path and the approved Gate 1 deferral. |

**verified.** Reconciliation source totals and posted debit/credit totals are calculated with SQL
`SumAsync` queries at `RentalCommand.Data/Accounting/AccountingConversionService.cs:113-162`.
`AccountingConversionReconciliation` rows are keyed by portfolio, source type, currency, and rule
version; no suspense posting is used.

**verified.** `AccountingConversionPostgreSqlTests` passed 2 tests. The paid-expense test proves a
second conversion creates no second journal and reconciles source, debit, credit, and imbalance to
125, 125, 125, and zero at `RentalCommand.Api.Tests/Domain/AccountingConversionPostgreSqlTests.cs:18-56`.
The provider-receipt test proves the corrected Undeposited Funds behavior and one settlement
journal at `:58-160`.

## Database-side read models

**verified.** General-ledger filters for account, property, unit, source type, effective range, and
search are composed on `JournalLines` and translated to SQL at
`RentalCommand.Api/Services/Domain/AccountingLedgerReadModelService.cs:97-124`. Count, deterministic
ordering, bounded paging, and the correlated running-balance calculation are issued through EF at
`:126-175`; a mixed-account page returns nullable `RunningBalance`, while an account-selected page
returns the account running balance.

**verified.** Tenant-ledger period/type/open/settled filters, allocation subtraction, running
receivable balance, open amount, and deterministic paging remain query expressions at
`AccountingLedgerReadModelService.cs:360-450` (the running sum is built from a correlated SQL
subquery at `:411-421`). Monthly summaries, period totals, and aging are implemented in the same
service at `:380-596`.

**verified.** Trial balance, balance sheet, income statement, and cash-basis statement totals use
database-side conditional aggregates. The statement totals SQL joins JournalLines, JournalEntries,
LedgerAccounts, and Portfolios and uses `SUM(CASE...)` at
`AccountingLedgerReadModelService.cs:765-804`; no rows are materialized to calculate statement
totals.

**verified.** Query-count and generated-SQL proof is in
`RentalCommand.Api.Tests/Domain/AccountingReadModelPostgreSqlTests.cs:77-99`. The interceptor
asserts no more than three SQL statements, and captured SQL contains `JournalLines` and `SUM`.
The focused read-model suite passed 3 tests, including account-vs-mixed running-balance behavior,
portfolio-scoped statements, and the SQL-count assertion.

## Gate 2 — frozen routes and DTOs

**verified.** Gate 2 corrected approval was obtained through the orchestration approval flow before
controller implementation. The following is the full frozen v1 route and field listing approved at
Gate 2.

### Gate 2 route listing

```text
GET   /api/v1/accounting/chart-of-accounts
POST  /api/v1/accounting/chart-of-accounts
PATCH /api/v1/accounting/chart-of-accounts/{id}
GET   /api/v1/accounting/general-ledger
GET   /api/v1/accounting/journal-entries/{publicId}
GET   /api/v1/accounting/trial-balance
GET   /api/v1/accounting/balance-sheet
GET   /api/v1/accounting/income-statement
GET   /api/v1/tenant-accounts/{tenantAccountId}/ledger
GET   /api/v1/tenant-accounts/{tenantAccountId}/month-summary
GET   /api/v1/tenant-accounts/{tenantAccountId}/ledger-summary?months=3|6|9|12
GET   /api/v1/tenant-accounts/{tenantAccountId}/recurring-charges
POST  /api/v1/tenant-accounts/{tenantAccountId}/recurring-charges
PATCH /api/v1/tenant-accounts/{tenantAccountId}/recurring-charges/{id}
POST  /api/v1/tenant-accounts/{tenantAccountId}/recurring-charges/{id}/deactivate
```

**verified.** General-ledger filters are account, property, unit, source type, effective range,
and search. Tenant-ledger filters are period, transaction type, and open/settled state. All list
routes use the bounded `{items,totalCount,skip,take}` envelope, and enums serialize as string names.
The route attributes are at `RentalCommand.Api/Controllers/AccountingController.cs:21,54-184` and
`RentalCommand.Api/Controllers/TenantAccountMoneyController.cs:16,47-212`; the frozen route tests
are at `RentalCommand.Api.Tests/AccountingApiContractFreezeTests.cs:12-50`.

### Gate 2 DTO listing

The shared `ListQuery` fields are `Skip:int`, `Take:int`, `Search:string?`, `Sort:string?`,
`From:DateTime?`, and `To:DateTime?`; `Take` is clamped to 200 by
`RentalCommand.Api/DTOs/ListQuery.cs:10-47`. `AccountingPage<T>` is the required bounded envelope.
The fields below are the frozen accounting DTO shapes.

```text
AccountingPage<T>
  items: IReadOnlyList<T>
  totalCount: int
  skip: int
  take: int

ChartOfAccountsQuery : ListQuery
  activeOnly: bool?

GeneralLedgerQuery : ListQuery
  accountId: int?
  propertyId: int?
  unitId: int?
  sourceType: JournalSourceType?
  effectiveFrom: DateOnly?
  effectiveTo: DateOnly?

TenantLedgerQuery : ListQuery
  entryType: TenantLedgerEntryType?
  effectiveFrom: DateOnly?
  effectiveTo: DateOnly?
  openOnly: bool?
  settledOnly: bool?

StatementQuery
  from: DateOnly?
  to: DateOnly?
  currency: string?
  propertyId: int?
  unitId: int?

TenantMonthSummaryQuery
  from: DateOnly?
  to: DateOnly?

TenantLedgerPeriodSummaryQuery
  months: int = 12

ChartOfAccountsRow
  id: int
  publicId: Guid
  code: string
  name: string
  accountType: AccountType
  normalBalance: NormalBalance
  parentAccountId: int?
  systemKey: string?
  scheduleECategory: ScheduleECategory?
  isSystem: bool
  isActive: bool
  hasPostedLines: bool

CreateChartOfAccountsRequest
  code: string
  name: string
  accountType: AccountType
  normalBalance: NormalBalance
  parentAccountId: int?
  systemKey: string?
  scheduleECategory: ScheduleECategory?
  isActive: bool = true

PatchChartOfAccountsRequest
  name: string?
  isActive: bool?
  scheduleECategory: ScheduleECategory?
  parentAccountId: int?

GeneralLedgerRow
  journalEntryPublicId: Guid
  lineId: int
  effectiveOn: DateOnly
  postedAtUtc: DateTime
  sourceType: JournalSourceType
  sourceId: long
  sourceBusinessKey: string
  description: string
  accountId: int
  accountCode: string
  accountName: string
  debitAmount: decimal
  creditAmount: decimal
  currency: string
  propertyId: int?
  unitId: int?
  tenantAccountId: int?
  ownerEntityId: int?
  runningBalance: decimal?

AllocationRef
  targetSourceId: long
  targetPublicId: Guid
  targetDescription: string
  amount: decimal
  effectiveOn: DateOnly

TenantLedgerRow
  tenantLedgerEntryId: long
  publicId: Guid
  sourceType: string
  sourceId: long
  sourcePublicId: Guid?
  effectiveOn: DateOnly
  postedAtUtc: DateTime
  type: TenantLedgerEntryType
  description: string
  chargeAmount: decimal
  paymentAmount: decimal
  creditAmount: decimal
  runningAmountOwed: decimal
  dueOn: DateOnly?
  openAmount: decimal
  status: string
  paymentMethod: string?
  reference: string?
  accountLabel: string?
  recurringScheduleContext: string?
  sourceDocumentContext: string?
  allocations: IReadOnlyList<AllocationRef>
  reversesEntryId: long?
  replacedByEntryId: long?
  journalEntryPublicId: Guid?
  currency: string

TenantMonthSummary
  year: int
  month: int
  currency: string
  openingBalance: decimal
  chargeAmount: decimal
  paymentAmount: decimal
  creditAmount: decimal
  closingBalance: decimal

TenantLedgerPeriodSummary
  periodMonths: int
  currency: string
  chargeAmount: decimal
  paymentAmount: decimal
  creditAmount: decimal
  endingBalance: decimal
  agingCurrent: decimal
  aging1To30: decimal
  aging31To60: decimal
  aging61To90: decimal
  aging90Plus: decimal

JournalDetail
  publicId: Guid
  description: string
  effectiveOn: DateOnly
  postedAtUtc: DateTime
  sourceType: JournalSourceType
  sourceId: long
  sourceBusinessKey: string
  actor: string?
  attemptId: Guid
  atomicReceiptId: Guid
  idempotencyDigest: string
  currency: string
  lines: IReadOnlyList<JournalDetailLine>
  totalDebits: decimal
  totalCredits: decimal
  isBalanced: bool
  reversesJournalEntryPublicId: Guid?
  reversalPublicIds: IReadOnlyList<Guid>
  auditLink: string?
  documentIds: IReadOnlyList<int>
  bankReconciliationEvidence: BankReconciliationEvidence?

JournalDetailLine
  id: int
  accountId: int
  accountCode: string
  accountName: string
  debitAmount: decimal
  creditAmount: decimal
  memo: string?
  propertyId: int?
  unitId: int?
  tenantAccountId: int?
  ownerEntityId: int?

BankReconciliationEvidence
  bankTransactionId: int?
  bankAccountLabel: string?
  matchedOn: DateOnly?
  status: string?

TrialBalanceRow
  accountId: int
  accountCode: string
  accountName: string
  accountType: AccountType
  debitBalance: decimal
  creditBalance: decimal
  currency: string

TrialBalanceResponse
  rows: IReadOnlyList<TrialBalanceRow>
  totalDebits: decimal
  totalCredits: decimal
  isBalanced: bool

FinancialStatementRow
  accountId: int
  accountCode: string
  accountName: string
  amount: decimal
  currency: string

StatementSection
  label: string
  rows: IReadOnlyList<FinancialStatementRow>
  subtotal: decimal

StatementTotals
  total: decimal
  netIncome: decimal?
  assets: decimal?
  liabilitiesAndEquity: decimal?

FinancialStatementResponse
  sections: IReadOnlyList<StatementSection>
  totals: StatementTotals

RecurringTenantChargeRow
  id: int
  publicId: Guid
  tenantAccountId: int
  leaseAgreementId: int?
  displayName: string
  amount: decimal
  currency: string
  ledgerAccountId: int
  effectiveStartOn: DateOnly
  effectiveEndOn: DateOnly?
  monthlyDueDay: int
  nextRunDate: DateOnly
  isActive: bool
  propertyId: int?
  unitId: int?

CreateRecurringTenantChargeRequest
  displayName: string
  amount: decimal
  ledgerAccountId: int
  leaseAgreementId: int?
  effectiveStartOn: DateOnly
  effectiveEndOn: DateOnly?
  monthlyDueDay: int
  nextRunDate: DateOnly?
  propertyId: int?
  unitId: int?
  // Currency is intentionally absent; the server derives portfolio base currency.

PatchRecurringTenantChargeRequest
  displayName: string?
  amount: decimal?
  ledgerAccountId: int?
  effectiveStartOn: DateOnly?
  effectiveEndOn: DateOnly?
  monthlyDueDay: int?
  nextRunDate: DateOnly?
  isActive: bool?
  propertyId: int?
  unitId: int?
```

**verified.** The DTO source is `RentalCommand.Api/DTOs/AccountingReadModelDtos.cs:7-360`.
`AccountingApiContractFreezeTests.cs:53-72` verifies the required typed running-balance fields,
typed allocations, currency and bank evidence, and the deliberate absence of client-supplied
recurring-charge currency.

**verified.** Gate 2 corrected freeze approval is binding with this clarification: nullable
running-balance belongs to `GeneralLedgerRow` only. `GeneralLedgerRow.runningBalance:decimal?` is
null unless exactly one `accountId` filter is selected. `TenantLedgerRow.runningAmountOwed:decimal`
is always present; tenant receivable running balance is not filter-dependent.

## Recurring-tenant-charge occurrence worker

**verified.** The Engine worker is `RentalCommand.Engine/Workers/RecurringTenantChargeWorker.cs:7-27`
and is hosted by `RentalCommand.Engine/EngineHostedServiceRegistration.cs:24-35`. It resolves the
atomic generation service, which runs the existing tenant-charge command path at
`RentalCommand.Engine/Services/RecurringTenantChargeGenerationService.cs:32-64`.

**verified.** Due active schedules are bounded and ordered in SQL at
`RentalCommand.Data/Payments/RecurringTenantChargeCommandHandler.cs:35-43`. Occurrences use the
stable business key `recurring-tenant-charge:{schedule.Id}:{runDate:yyyy-MM}` at `:138-163`, skip
already-created keys at `:49-60`, post each new charge and journal through the shared tenant-charge
posting rule at `:62-101`, and advance `NextRunDate` at `:103-121`. Inactive and ended schedules
are skipped by the active/end-date checks.

**verified.** The real-PostgreSQL month-boundary test at
`RentalCommand.Api.Tests/Domain/RecurringTenantChargeWorkerPostgreSqlTests.cs:29-103` passed 1
test: January and February each produced one charge and one journal, the February replay returned
`Replayed`, and the schedule advanced to March 31.

## Dependency-injection receipts

**verified.** API registration adds `AccountingPostingService`, `ChartOfAccountsSeedService`, all
source generators, conversion framework/service/reconciliation, and the read model at
`RentalCommand.Api/Program.cs:386-408`.

**verified.** Engine registration adds the posting, seed, generators, conversion framework/service,
and reconciliation service at `RentalCommand.Engine/Program.cs:99-120`. The recurring-charge worker
registration is at `RentalCommand.Engine/EngineHostedServiceRegistration.cs:31-35`.

## Closing route-contract fix

**verified.** The red-first command was:

```text
MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests --filter "FullyQualifiedName~TenantAccountMoneyRouteContractTests" --no-restore
```

It failed at `TenantAccountMoneyRouteContractTests.cs:113` because the stale test expected
`RecordTenantReceiptCommand.AllocateOldestCharges` to be absent while the command already exposed
the approved property.

**verified.** Commit `273623ad` changes
`RentalCommand.Api.Tests/TenantAccountMoneyRouteContractTests.cs:110-126` to assert that both
`RecordTenantReceiptCommand.AllocateOldestCharges` and
`RecordTenantReceiptRequest.AllocateOldestCharges` exist and are `bool`, while retaining both
`TargetChargeEntryId` assertions. The one-line test comment cites the oldest-open-charge contract
rule. `RentalCommand.Api/DTOs/TenantMoneyDtos.cs:5-18` now carries the request property with a
`true` default, and `RentalCommand.Api/Controllers/TenantAccountMoneyController.cs:215-234` passes
the request value into the command, preserving the approved default at the HTTP boundary.

## Verification receipts

**verified.** The focused tenant route class passed after the source fix:

```text
MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests --filter "FullyQualifiedName~TenantAccountMoneyRouteContractTests" --no-restore
Passed!  - Failed:     0, Passed:    13, Skipped:     0, Total:    13, Duration: 45 ms - RentalCommand.Api.Tests.dll (net10.0)
```

**verified.** The required full RouteContract filter passed after the source fix:

```text
MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests --filter "FullyQualifiedName~RouteContract" --no-restore
Passed!  - Failed:     0, Passed:    29, Skipped:     0, Total:    29, Duration: 1 s - RentalCommand.Api.Tests.dll (net10.0)
```

**verified.** B2 focused suites passed before this report was written:

```text
MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests --filter "FullyQualifiedName~AccountingConversionPostgreSqlTests" --no-restore
Passed!  - Failed: 0, Passed: 2, Skipped: 0, Total: 2

MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests --filter "FullyQualifiedName~AccountingReadModelPostgreSqlTests" --no-restore
Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3

MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests --filter "FullyQualifiedName~RecurringTenantChargeWorkerPostgreSqlTests" --no-restore
Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1

MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests --filter "FullyQualifiedName~AccountingApiContractFreezeTests" --no-restore
Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3

MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Engine.Tests --no-restore
Passed!  - Failed: 0, Passed: 94, Skipped: 0, Total: 94
```

**verified.** The required solution build passed before the report-only commit:

```text
MSBUILDDISABLENODEREUSE=1 dotnet build RentalCommand.sln --no-restore
Build succeeded.
48 Warning(s)
0 Error(s)
```

**verified.** `dotnet build-server shutdown` completed successfully after the heavy build/test
batch. The final post-report RouteContract run, `git diff --check`, and clean-tree output are
recorded in the closing worker handoff.

## Assumptions and deferred work

**assumed.** Depreciation and owner contribution remain omitted from historical conversion because
Lane B1 has no immutable source path for them; the reconciliation model must surface that omission
until a future source path is approved.

**assumed.** No preserved simulation database was used; all conversion and worker evidence comes
from the isolated `MigratedPostgreSqlFixture` suites.
