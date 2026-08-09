# Scenario 18 — Accounting, transactions, tenant ledgers, charges, deposits, and banking

## Purpose

Follow the landlord’s money surfaces from transaction entry to tenant-account ledger, one-time/recurring charges, receipts, past-due handling, deposits, reconciliation, and provider boundaries. Every amount and relationship must reconcile server-side.

## Preconditions and login

Use a QA lease/tenant account and additive QA transactions. Run in local dev for mutations when possible; on preview, do not post, reverse, refund, or reconcile against existing records. Never connect a real bank or payment provider.

## Read the implementation first

Before opening a browser, read these named source files and the relevant controller/service code they call:

- web/src/routes/(protected)/accounting/+page.svelte and web/src/routes/(protected)/accounting/expenses/[id]/+page.svelte
- web/src/routes/(protected)/accounting/past-due/+page.svelte
- web/src/routes/(protected)/tenant-accounts/[tenantAccountId]/entries/[tenantLedgerEntryId]/+page.svelte
- web/src/routes/(protected)/deposits/+page.svelte and web/src/routes/(protected)/deposits/[id]/+page.svelte
- web/src/routes/(protected)/banking/+page.svelte and web/src/routes/(protected)/plaid/auth/+page.svelte
- web/src/lib/api/endpoints/accounting.ts, web/src/lib/api/endpoints/banking.ts, web/src/lib/api/endpoints/payments.ts, web/src/lib/api/endpoints/securityDeposits.ts, and web/src/lib/api/endpoints/tenant-accounts.ts
- RentalCommand.Api/Controllers/AccountingController.cs, RentalCommand.Api/Controllers/TenantAccountsController.cs, RentalCommand.Api/Controllers/TenantAccountMoneyController.cs, RentalCommand.Api/Controllers/ExpenseController.cs, RentalCommand.Api/Controllers/RecurringExpenseController.cs, RentalCommand.Api/Controllers/BankingController.cs, RentalCommand.Api/Controllers/OpeningSecurityDepositRecoveryController.cs, RentalCommand.Api/Controllers/RefundedTenantAllocationRecoveryController.cs, and RentalCommand.Api/Controllers/OwnerContributionController.cs

## Rough exploration areas

Use these as exploration goals, not a step-by-step script:

- Explore accounting tabs for transactions, general ledger, cash flow, expenses, and charges; create a marked expense/charge/receipt through the canonical UI and confirm the durable entry detail.
- Compare the Unit, Lease, Tenant Account, ledger entry, accounting summary, and dashboard representations of one QA money event; check dates, currency, method, references, allocations, and balance.
- Exercise one-time and recurring charges, past-due marking/receipt, corrections/reversals/refunds, and idempotent retry paths using only marked records.
- Run a security-deposit lifecycle on a QA lease: held, deduction, refundable amount, refund/disbursement boundary, and duplicate protection.
- Inspect Banking/Reconciliation and Plaid entry points for disconnected/provider-unavailable state, matching/review behavior, and safe return paths.
- For every list/filter/summary, verify filtering, sorting, grouping, totals, and paging are coherent and not changed by client-only materialization.

## Specific edge cases worth trying

- Zero, negative, maximum, high-precision, currency-boundary, duplicate external reference/operation key, and a date at month/year boundary.
- Charge with no tenant account, receipt larger than balance, deposit deduction larger than held, duplicate refund, invalid method, and stale record after another tab changes it.
- Recurring schedule with no next date, end before start, leap day, timezone boundary, and retry that could generate a duplicate.
- Disconnected bank, expired provider session, malformed import/match, cross-portfolio account/entry ID, and API timeout during a save.

## What to verify visually

- Debit/credit and balance signs, currency precision, status badges, date/timezone labels, and allocation context are consistent across pages.
- Totals update with the selected filters, pagination controls stay visible, and no loading state presents an editable stale value.
- Dialogs, field errors, confirmation/toast copy, print/account history links, and 390px tables remain legible.

## Data safety and evidence

Prefix all new descriptions, references, memo text, and external keys with QA-YYYYMMDD. Do not mutate existing ledger/deposit/bank rows; no real provider connection or money movement.

Record actual-versus-expected behavior, URLs, response/status evidence, persistence after reload, and console errors. Report confirmed bugs separately from potential issues and observations using the BUG-N format from the exploratory tester definition.
