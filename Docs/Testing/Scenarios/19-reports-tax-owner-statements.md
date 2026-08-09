# Scenario 19 — Reports, owner statements, tax, year-end, and financial drill-downs

## Purpose

Confirm that every report tells the same financial story as the underlying ledger: owner reports/statements, rent ledger, profit and loss, balance sheet, trial balance, chart of accounts, tax/Schedule E, past-due, and year-end packets.

## Preconditions and login

Use read-mostly QA data from Scenario 18 or existing safe fixtures. Reports may be opened on preview; do not change categories, close a year, distribute funds, or edit another owner’s statement. Capture the selected dates, property, owner, and filters with each result.

## Read the implementation first

Before opening a browser, read these named source files and the relevant controller/service code they call:

- web/src/routes/(protected)/reports/+page.svelte and web/src/routes/(protected)/reports/[report]/+page.svelte
- web/src/routes/(protected)/owners-report/+page.svelte
- web/src/routes/(protected)/owner/statements/+page.svelte and web/src/routes/(protected)/owner/properties/+page.svelte
- web/src/routes/(protected)/tax/+page.svelte
- web/src/routes/(protected)/accounting/balance-sheet/+page.svelte, web/src/routes/(protected)/accounting/profit-and-loss/+page.svelte, web/src/routes/(protected)/accounting/trial-balance/+page.svelte, web/src/routes/(protected)/accounting/chart-of-accounts/+page.svelte, and web/src/routes/(protected)/accounting/year-end/+page.svelte
- web/src/lib/components/accounting/ReportsCatalog.svelte and web/src/lib/accounting/accounting-help.ts
- RentalCommand.Api/Controllers/ReportsController.cs, AccountingController.cs, AnalyticsController.cs, OwnerPortalController.cs, CapitalAssetsController.cs, LoanController.cs, and HistoricalRentRecoveryController.cs

## Rough exploration areas

Use these as exploration goals, not a step-by-step script:

- Open the report catalog and every report route; verify each title, help link, selected period/property/owner, table columns, totals, print/export affordance, and drill-down destination.
- Compare rent ledger, owner statement, profit and loss, balance sheet, trial balance, tax, and year-end values against known QA transactions, expenses, deposits, loans/capital assets, and distributions.
- Change date, property, owner, category, status, and pagination filters independently and together; confirm rows and totals change as one server-side result.
- Inspect empty, no-data, partial-data, closed-year, and provider-unavailable states, including an owner relationship view that should not expose staff-only ledger detail.
- Open a report drill-down to the exact tenant ledger/expense/transaction and return without losing filter or date context.

## Specific edge cases worth trying

- Start/end date inversion, leap day, timezone midnight, future year, custom period with no rows, very large totals, negative net income, and currency rounding.
- Two properties with same name, owner with no properties, inactive category, unbalanced trial balance, missing source row, and duplicate report request.
- Print/export with long labels, CSV/PDF download failure, browser Back/Forward, direct unknown report slug, and a cross-portfolio property/owner ID.

## What to verify visually

- Report hierarchy, section totals, sign conventions, date range, currency, filters, pagination, and drill-down links are easy to scan.
- Print layout removes navigation without clipping tables; mobile report cards expose labels and totals without requiring guesswork.
- No-data and error states explain whether the result is empty, blocked, or failed; loading placeholders do not look like zero.

## Data safety and evidence

Do not edit report-source records on preview. Any local/additive QA transaction used for reconciliation must be prefixed QA-YYYYMMDD and must be clearly listed in the report evidence.

Record actual-versus-expected behavior, URLs, response/status evidence, persistence after reload, and console errors. Report confirmed bugs separately from potential issues and observations using the BUG-N format from the exploratory tester definition.
