# TSK-803 / TSK-800 Accounting Backend and Money UI Handoff

Prepared 2026-07-31 for Fable to orchestrate the backend implementation and then own the web and
mobile experience. This document is the implementation contract for:

- `TSK-803`: balanced double-entry general ledger and chart of accounts;
- `TSK-800`: explainable lease ledgers across the existing Money surfaces; and
- the API/UI contract Fable needs before frontend work starts.

`TSK-801`, the rent-roll report, remains a later additive report. It must not replace the global
Money or Unit Money ledgers.

## Outcome

Rental Command will keep its simple property-management workflows while every applicable financial
action produces an immutable, balanced journal entry underneath. A landlord should not need to
understand debits and credits to record rent, a payment, a bill, a mortgage payment, or an owner
contribution. An accountant must still be able to trace every amount from source document to
operational record, journal entry, account ledger, financial statement, audit, and outbox row.

The existing Atomic kernel is complete and remains the only transaction mechanism. Do not build a
second accounting transaction host, event-sourcing runtime, message bus, compatibility layer, or
versioned Atomic path. Extend the existing scoped `RentalCommandDbContext` write path so the
business mutation, balanced journal, receipt, required audits, and required outbox messages commit
or roll back together.

## Locked product decisions

1. The books are double-entry. Every posted journal entry balances debits and credits by currency.
2. Operational subledgers remain authoritative for property-management workflows:
   tenant receivables, security deposits, expenses, loans, banking, assets, and owner activity.
   The general ledger is the accounting consequence of those sources, not a competing workflow.
3. Posted financial history is append-only. Corrections use reversals and replacement entries;
   posted transactions and journal lines are never edited or deleted.
4. Expense `ScheduleECategory` remains a tax classification. It is not the chart of accounts.
5. Tenant charges, payments, credits, and allocations remain distinct concepts in the UI and API.
6. Security deposits remain liabilities and never become rental income merely because cash moved.
7. Property and unit are journal dimensions, not separate duplicated charts of accounts.
8. Routine transactions post automatically from configured system mappings. Manual general-journal
   entry is not part of this first implementation.
9. One portfolio has one base currency in this version. Store currency on every journal entry, but
   do not add foreign-exchange remeasurement.
10. Cash-basis tax reports and accrual management reports derive from the same source links:
    management statements use journal effective dates; cash-basis Schedule E uses settled receipt
    allocations and paid-expense evidence.
11. The ordinary Money UI uses plain language. Debit/credit details appear on row drill-in and in
    accounting reports, not as required inputs for normal landlord workflows.
12. All filtering, joining, grouping, aggregation, sorting, running balances, and paging execute
    DB-side in one translated SQL statement or a database view/function. No load-then-loop, N+1,
    lazy loading, or client-side financial aggregation is allowed.

## Accounting mental model

A charge and a payment are not the same accounting event:

- Posting monthly rent: debit Tenant Accounts Receivable; credit Rental Income. The tenant now
  owes more, but no cash has arrived.
- Receiving the tenant's payment: debit Cash or Undeposited Funds; credit Tenant Accounts
  Receivable. Cash increased and the tenant owes less; income was not counted a second time.
- Depositing provider funds: debit the destination Bank Cash account; credit Undeposited Funds.

The tenant-facing ledger describes the same sequence without accounting jargon:

| Date | What happened | Charges | Payments/credits | Running amount owed |
| --- | --- | ---: | ---: | ---: |
| Feb 1 | February rent | 1,050.00 | — | 1,050.00 |
| Feb 6 | Late fee | 52.50 | — | 1,102.50 |
| Feb 8 | ACH payment | — | 1,102.50 | 0.00 |

## Minimal persisted model

Add only four accounting concepts. Use the names below unless an existing repository convention
requires a mechanical variation.

### `LedgerAccount`

- `Id`, `PortfolioId`, unique `Code`, `Name`.
- `AccountType`: `Asset`, `Liability`, `Equity`, `Income`, or `Expense`.
- `NormalBalance`: `Debit` or `Credit`; derived and validated from `AccountType`.
- nullable `ParentAccountId` for presentation hierarchy only.
- nullable stable `SystemKey` for required posting roles.
- nullable `ScheduleECategory` mapping for tax reporting.
- `IsSystem`, `IsActive`, timestamps.
- System accounts can be relabeled but not deleted, retyped, or assigned a different system key.
- User-created accounts can be added, renamed, and deactivated. An account with posted lines cannot
  be deleted or retyped.

### `JournalEntry`

- `Id`, public GUID, `PortfolioId`, `EffectiveOn`, `PostedAtUtc`, `Currency`, description.
- `SourceType` plus a stable source identifier and source business key.
- required idempotency digest and posting-rule version.
- nullable `ReversesJournalEntryId`; reversals link to the original entry.
- actor/session/access context and Atomic receipt identity needed for audit tracing.
- immutable after posting.

Do not add draft/approved/posted workflow states in this version. A journal entry is created only
when its source action commits. A failed source action leaves no journal entry.

### `JournalLine`

- `Id`, `JournalEntryId`, `LedgerAccountId`, `DebitAmount`, `CreditAmount`, memo.
- nullable dimensions: `PropertyId`, `UnitId`, `TenantAccountId`, `OwnerEntityId`.
- optional source-line discriminator for an expense line, payment allocation, loan component, or
  other source detail.
- exactly one of debit or credit is positive; neither may be negative.
- immutable after posting.

Enforce balance twice: the posting service rejects an unbalanced entry before save, and a deferred
PostgreSQL constraint trigger rejects a transaction whose journal entry lines do not balance by
entry and currency at commit. Database immutability triggers reject update/delete of posted entries
and lines. This is required because application validation alone cannot protect imports, migrations,
or future write paths.

### `RecurringTenantCharge`

- portfolio, tenant account, governing lease agreement, display name, amount, currency.
- income `LedgerAccountId`, effective start/end dates, monthly due day, next run date, active flag.
- optional property/unit dimensions inherited and validated from the tenant account.
- stable occurrence business key unique by schedule and billing period.
- each occurrence posts one separate tenant charge and one balanced journal entry.

This first version supports monthly lease obligations. One-time charges use the existing manual
charge command. Do not add arbitrary cron expressions.

## Default chart of accounts

Seed one chart per portfolio idempotently. Codes are stable defaults; users may add subaccounts.

| Code | Account | Type | System purpose |
| --- | --- | --- | --- |
| 1000 | Operating Cash | Asset | default manual receipt/payment cash |
| 1010 | Undeposited Funds | Asset | provider receipts awaiting settlement |
| 1020 | Security Deposit Trust Cash | Asset | deposit cash held separately |
| 1100 | Tenant Accounts Receivable | Asset | unpaid tenant charges |
| 1200 | Mortgage Escrow Asset | Asset | lender-held escrow |
| 1500 | Buildings and Improvements | Asset | depreciable property basis |
| 1510 | Land | Asset | non-depreciable property basis |
| 1590 | Accumulated Depreciation | Asset | contra-asset |
| 2000 | Accounts Payable | Liability | unpaid bills |
| 2100 | Tenant Security Deposits Payable | Liability | amount owed back to tenants |
| 2200 | Mortgage Payable | Liability | outstanding principal |
| 3000 | Owner Contributions | Equity | capital contributed by owners |
| 3100 | Owner Distributions | Equity | contra-equity distributions |
| 3200 | Retained Earnings | Equity | accumulated closed-period earnings |
| 4000 | Rental Income | Income | base rent |
| 4010 | Pet Income | Income | pet rent and pet fees |
| 4020 | Parking Income | Income | parking obligations |
| 4030 | Late Fee Income | Income | late fees |
| 4040 | Utility Reimbursement Income | Income | tenant utility recovery |
| 4090 | Other Rental Income | Income | configured other lease charges |
| 5000 | Repairs and Maintenance | Expense | operating repairs |
| 5010 | Utilities | Expense | property utilities |
| 5020 | Insurance | Expense | insurance expense |
| 5030 | Property Taxes | Expense | property taxes |
| 5040 | Management Fees | Expense | management fees |
| 5050 | Mortgage Interest | Expense | debt-service interest |
| 5060 | Depreciation Expense | Expense | periodic depreciation |
| 5090 | Other Operating Expense | Expense | mapped fallback requiring review |

Do not create one account per property. Reports group the property dimension DB-side.

## Posting rules

All rules run inside the source command's existing explicit Atomic transaction. Every source type
has a unique posting key so exact replay returns the already committed result.

| Source action | Debit | Credit |
| --- | --- | --- |
| Rent, pet, parking, late fee, utility, or other tenant charge | Tenant A/R | mapped income account |
| Manual tenant charge | Tenant A/R | selected/default income account |
| Tenant receipt deposited immediately | Operating Cash | Tenant A/R |
| Provider receipt before settlement | Undeposited Funds | Tenant A/R |
| Provider settlement | destination Bank Cash | Undeposited Funds |
| Tenant concession or charge reduction | mapped income/contra-income | Tenant A/R |
| Receivable write-off | mapped bad-debt expense | Tenant A/R |
| Security deposit receipt | Security Deposit Trust Cash | Security Deposits Payable |
| Security deposit refund | Security Deposits Payable | Security Deposit Trust Cash |
| Deposit applied to an authorized tenant charge | Security Deposits Payable | Tenant A/R |
| Expense paid immediately | mapped expense or asset | selected Cash account |
| Bill incurred but unpaid | mapped expense or asset | Accounts Payable |
| Bill payment | Accounts Payable | selected Cash account |
| Bank transfer | destination Cash | source Cash |
| Mortgage payment | Mortgage Payable principal + Mortgage Interest + Escrow Asset | Cash for total payment |
| Capital purchase | Buildings/Improvements, Land, or selected asset | Cash or Accounts Payable |
| Depreciation | Depreciation Expense | Accumulated Depreciation |
| Owner contribution | Cash | owner-specific contribution equity dimension |
| Owner distribution | owner-specific distribution equity dimension | Cash |

Reversal creates the exact opposite lines and links to the original journal entry. A corrected
replacement is a separate source action with its own balanced journal entry. Never update original
lines.

Payment allocations continue to say which charges a receipt settled. They do not create income
again. Oldest-open-charge-first remains the default, while an explicit target charge wins when the
existing command supplies one. The UI must show allocations.

## Backend implementation sequence

### 1. Foundation and invariants

- Add enums, the four entities, EF mappings, indexes, RLS policies/grants, migration, seed service,
  deferred balance enforcement, and immutability enforcement.
- Add a narrow `AccountingPostingService` that accepts a complete proposed entry, validates account
  scope/activity, balances it, deduplicates by source business key, and attaches it to the current
  scoped context. It must not open its own transaction or call external services.
- Add failure-injection tests proving a business mutation cannot commit without its required journal
  and a journal cannot commit without its business mutation, receipt, audits, and outbox.

### 2. Source posting integration

Integrate in this order so the highest-risk tenant and cash paths become balanced first:

1. tenant charges, receipts, allocations, credits, refunds, reversals, and opening balances;
2. security-deposit funding, deductions, applications, refunds, and reversals;
3. expenses, bills, recurring expenses, bank matching/transfers, and provider settlements;
4. loan payment components and corrections;
5. capital assets/depreciation and owner contributions/distributions.

Do not place posting logic in controllers. Source command handlers supply facts to the posting
service while still owning authorization and business invariants.

### 3. Existing-data conversion and reconciliation

- Seed each existing portfolio's chart.
- Generate journal entries deterministically from existing immutable source records, using one
  unique `(PortfolioId, SourceType, SourceId, PostingRuleVersion)` key.
- Run conversion in bounded server-side batches; never load an entire portfolio into memory.
- Do not rewrite operational source rows.
- Produce a reconciliation table/report containing source totals, posted totals, missing mappings,
  unsupported sources, and imbalance. A portfolio cannot be declared converted until differences
  are zero or represented by an explicitly approved opening-balance journal.
- Never silently use a suspense or conversion-equity account to make totals green. If historical
  source detail is insufficient, surface the exact discrepancy for review.

### 4. DB-side read models

Create translated queries or database views/functions for:

- tenant ledger rows with running receivable balance using a window function;
- monthly opening balance, charges, payments, credits/adjustments, and closing balance;
- 3/6/9/12-month totals and receivable aging;
- account ledger with correct opening and running account balance across pages;
- trial balance, balance sheet, income statement, and cash-basis Schedule E bridge;
- source-to-journal drill-down and journal-to-source drill-down.

Add covering indexes for the actual filters and orderings. Inspect generated SQL. A normal 20-row
screen should use 1–3 database queries total.

### 5. API contract freeze

Freeze DTOs before Fable begins frontend implementation. Extend existing routes when they already
represent the resource; do not create parallel v2 controllers.

Required management endpoints:

- `GET /api/v1/accounting/chart-of-accounts`
- `POST /api/v1/accounting/chart-of-accounts`
- `PATCH /api/v1/accounting/chart-of-accounts/{id}`
- `GET /api/v1/accounting/general-ledger`
- `GET /api/v1/accounting/journal-entries/{publicId}`
- `GET /api/v1/accounting/trial-balance`
- `GET /api/v1/accounting/balance-sheet`
- `GET /api/v1/accounting/income-statement`
- tenant-account ledger/month-summary endpoints on the existing tenant-account route family;
- recurring-tenant-charge list/create/edit/deactivate endpoints on the existing tenant-account or
  lease route family.

Every list accepts bounded server paging plus server filters. General-ledger filters include account,
property, unit, source type, effective range, and search. Tenant ledger filters include period,
transaction type, and open/settled state.

Required tenant-ledger row fields:

- stable row/public IDs and source/detail link;
- effective date and wall-clock posted date;
- type and plain-language description;
- separate `chargeAmount`, `paymentAmount`, and `creditAmount` fields;
- running amount owed after the row;
- due date, open amount, status, payment method/reference when relevant;
- account/category label and recurring-schedule/source-document context;
- allocations to or from exact charge rows;
- reversal/correction lineage;
- journal-entry public ID for authorized staff drill-in.

Required journal detail fields:

- entry description, effective/posted dates, source link, actor, idempotency identity;
- debit and credit lines with account code/name and dimensions;
- total debits/credits and explicit balanced result;
- reversal/replacement lineage, audit link, documents, and bank-reconciliation evidence.

The global mixed Money grid must not invent one meaningless running balance across unrelated cash,
receivable, expense, and loan records. Show the tenant running balance on tenant-account rows and
the account running balance only when an account is selected. Unit and tenant ledgers always show
their receivable running balance.

## Fable frontend contract

Fable owns layout, interaction design, responsive behavior, and styling after the API DTOs freeze.
The following behavior is required; visual treatment is intentionally left to Fable.

### Web management surfaces

1. **Global Money (`/accounting`)**
   - Preserve the current operational grid location.
   - Replace the misleading flat category behavior with working account/category filters for both
     income and expense rows.
   - Distinguish charge, payment received, credit, expense/bill, bank movement, loan payment, owner
     activity, and reversal.
   - Show effective date separately from entered/posted date.
   - Each row names what happened and its source; no raw business-key descriptions.
   - Clicking a row opens full source, allocation, running-balance, bank-match, document, audit, and
     journal-impact details.

2. **Unit Command Center Money**
   - Keep the ledger in the existing Unit Money/Rent experience.
   - Group by month with opening balance, itemized charges, payments, credits/adjustments, and closing
     balance.
   - Show running balance after every row and 3/6/9/12-month switches.
   - Add `Post charge` and `Recurring charge` actions for authorized roles.
   - Recurring base rent, pet, parking, utilities, storage, concessions, and other obligations appear
     as separate named lines, never collapsed into one rent amount.

3. **Tenant portal Money**
   - Present the same authoritative lease ledger in tenant language.
   - Allow row drill-in, payment receipt details, allocations, documents, and a print/download
     statement.
   - Never expose management-only account configuration or other tenants.

4. **Reports**
   - Rename/remove the current pseudo-General Ledger label once the real report is available.
   - Add Chart of Accounts, General Ledger, Trial Balance, Balance Sheet, and Income Statement.
   - Keep Schedule E clearly labeled cash-basis/tax-oriented where applicable.
   - Leave Rent Roll as the later `TSK-801` report; do not move operational ledgers into Reports.

5. **Expense, payment, loan, asset, deposit, and owner detail**
   - Show the mapped account and a compact `Accounting impact` block.
   - Drill-in reveals the balanced lines and source evidence.
   - Normal forms use sensible defaults. Account selection appears only where the source facts cannot
     determine it or the user chooses an advanced override.

6. **Chart of Accounts administration**
   - Web management only for this version.
   - Add/rename/deactivate user accounts, view system accounts, and map Schedule E categories.
   - Prevent destructive changes to required system accounts and accounts with history.

### Mobile surfaces

1. Preserve the global **Money bottom tab** and add the same corrected types, dates, filters, and row
   drill-in as web.
2. Preserve **Units > Money** with monthly groups, running balances, row detail, manual charge, and
   recurring charge access for authorized staff.
3. Preserve tenant **Money/account history** with the same server-calculated values as web.
4. Expense, receipt, loan, deposit, and owner-money details show the plain-language accounting impact.
5. Full COA editing is not required on mobile in this version; account labels and read-only journal
   drill-in are required.

No 3/6/9/12 totals, running balances, aging, or financial statement totals may be calculated in
Svelte or Dart. Clients render server-returned values.

## Suggested Fable delegation

Use two backend SOL lanes, with one schema/API contract controlled by Fable:

- **Backend lane A — foundation owner:** entities, migration, RLS, seeded COA, posting service,
  balance/immutability enforcement, conversion framework, and invariant tests.
- **Backend lane B — integration/read owner:** source posting adapters, DB-side ledger/report read
  models, DTOs/controllers, generated-SQL tests, and source-to-journal reconciliation.

Lane B may inspect and prepare against the locked model, but it must not invent competing entity or
DTO shapes. Merge the foundation contract before both lanes run build/test-heavy verification.
Never run more than two heavy builds/tests concurrently; serialize real-PostgreSQL suites.

Fable then owns web and mobile work against the frozen DTOs. Backend agents should not style or
restructure frontend screens beyond minimal contract fixtures.

Create the implementation branch/worktree from the current
`tsk-754-year-simulation-execution` HEAD that contains this handoff. Commit `5c7a1c60` is the
immutable implementation checkpoint immediately before the documentation handoff, not the branch
point to use by itself. Create the worktree under:

```text
/Users/blackcolours/dev/work/worktrees/rental-management/tsk-800-803-accounting-ledgers
```

Use a branch containing both task keys, for example `tsk-800-803-accounting-ledgers`. Preserve the
TSK-754 simulation worktree and database. Remove the new worktree immediately after its branch is
merged or abandoned.

## Verification gates

Backend acceptance requires:

- seeded default COA is idempotent and portfolio scoped;
- every posting rule above has balanced debit/credit proof;
- DB commit rejects imbalance even if the application check is bypassed;
- journal entries and lines reject update/delete;
- exact idempotency replay creates no second source or journal entry;
- injected failure at business, journal, audit, receipt, and outbox stages rolls back everything;
- cross-portfolio account/source IDs are denied without existence leakage;
- existing-source conversion is repeatable and reconciles to zero unexplained difference;
- trial-balance debits equal credits by portfolio, period, and currency;
- tenant running balances, monthly totals, aging, account running balances, and statements are
  generated DB-side with inspected SQL and bounded query counts;
- existing YS-295 loan history remains append-only and its later correction posts balanced lines;
- focused API, Data, Core, Engine, and real-PostgreSQL suites pass without concurrent heavy runners.

Frontend acceptance requires live proof on both web and the physical Samsung for:

- global Money row clarity, filters, pagination, reload, and row drill-in;
- Unit Money monthly/running balance and manual/recurring charge workflows;
- tenant account history and statement parity;
- expense/payment/deposit/loan accounting-impact drill-ins;
- staff authorization and adjacent-role denial;
- exact parity with database ledger, journal, audit, outbox, and idempotency evidence.

## TSK-754 coordination and stop condition

The year simulation is deliberately paused after `RUN-20270215-05`. Do not create more financial
history while the accounting foundation and lease-ledger contract are changing. The first untouched
row is `RUN-20270215-06`; YS-309's mobile property-scope source repair is tested but still needs a
fresh physical-phone build and live proof before that run can be recorded.

Resume TSK-754 only after:

1. TSK-803 source posting and conversion reconcile the preserved simulation database;
2. TSK-800 APIs and all four staff Money surfaces are implemented and live verified;
3. YS-309 is rebuilt and proven on the physical Samsung;
4. the frozen `2027-02-15` database passes source-to-journal, trial-balance, tenant-ledger, audit,
   outbox, and duplicate-prevention gates; and
5. the TSK-754 handoff is refreshed with the merged commit, installed APK hash, device target, and
   exact next run.

`TSK-801` rent roll starts only after these operational ledgers are stable. It is a report over the
same DB-side facts, not a prerequisite for resuming the simulation.

## Explicit non-goals

- no replacement of the Atomic kernel;
- no new message broker or event-sourcing platform;
- no manual journal-entry UI in this version;
- no payroll, inventory, corporate consolidation, foreign exchange, or full accounts-payable vendor
  subledger;
- no automatic period closing or retained-earnings transfer workflow yet;
- no deletion or rewriting of historical financial facts;
- no rent-roll implementation inside TSK-800/803;
- no client-side financial aggregation.
