# Exploratory Test Report: Reports, owner statements, tax, year-end, and financial drill-downs
Date: 2026-08-27
Tester: e2e-s19
Duration: roughly 60 minutes

## Scenario
Verify that reports, owner statements, tax outputs, year-end packets, and accounting drill-downs tell the same financial story as the underlying ledger.

## Summary
The cash-basis reports, Schedule E, owner statements, year-end on-screen property filter, true cash flow, and journal drill-downs were exercised against the seeded SANDBOX. Cash-basis totals reconciled at the final observation to $124,966.25 of income, $9,325.00 of paid expenses, and $115,641.25 net; owner statements were correctly $116.25 lower because they are rent-only. Four defects were confirmed: the advanced financial statements omit most of the platform ledger, inverted dates are silently swapped, a filtered year-end export ignores the property filter, and expense categories render as numeric enum values.

No records were created by this session. The shared SANDBOX changed during the run when another tester added a property and a $1,050 payment; final totals are identified as final observations rather than attributed to e2e-s19.

## Bugs Found

### BUG-1: Include tenant-ledger and paid-expense history in advanced statements
**Severity:** High
**Location:** Advanced accounting `/accounting/profit-and-loss`, `/accounting/balance-sheet`, `/accounting/trial-balance`, and `/accounting?tab=general-ledger`
**Expected:** The advanced statements and their general ledger should represent the platform's financial history for the selected period and reconcile to the cash-basis reports, excluding security deposits from income. The accounting help explicitly describes the general ledger as the complete history behind cash, income, expenses, debts, and tenant balances, and says financial statements summarize dated general-ledger records.
**Actual:** For `2026-01-01` through `2026-08-27`, the generic cash-flow/Schedule E reports showed $124,966.25 income and $9,325.00 expenses, while the advanced Profit & Loss showed `Utility Reimbursement Income $0.00`, total income `$0.00`, `Repairs and Maintenance $12.34`, total expense `$12.34`, and net income `-$12.34`. The advanced general ledger contained only the small set of journal rows; the final read-only database check found 319 tenant-ledger entries and 31 paid expenses but only 6 journal entries and 12 journal lines. The balance sheet and trial balance likewise reflected only that journal subset, although they remained balanced on the incomplete subset.
**Evidence:** API/UI request `GET /api/v1/accounting/income-statement?from=2026-01-01&to=2026-08-27` returned the `$0.00` income / `$12.34` expense statement. The matching cash report returned `$124,966.25` / `$9,325.00` / `$115,641.25`. Screenshot: `/home/blackcolours/Workbox/screenshots/e2e-s19-accounting-profit-and-loss.png`. Read-only database counts: `journal_entries=6`, `journal_lines=12`, `tenant_ledger_entries=319`, `paid_expenses=31`.
**Code Reference:** `RentalCommand.Api/Services/Domain/AccountingLedgerReadModelService.cs:1548-1563` filters statement input exclusively to `_db.JournalLines`; `RentalCommand.Api/Services/Domain/AccountingLedgerReadModelService.cs:1574-1601` groups those lines into statement rows; `RentalCommand.Api/Services/Domain/AccountingLedgerReadModelService.cs:651-667` builds Profit & Loss from that query. By contrast, `RentalCommand.Api/Services/Domain/ReportsService.cs:1253-1325` builds the cash report from the authorized financial report projections. The user-facing contract is in `web/src/lib/accounting/accounting-help.ts:23-26,53-56`.
**Suggested Fix:** Backfill the existing tenant-ledger and paid-expense facts into balanced `JournalEntries`/`JournalLines` before exposing these statements as complete, and keep new writes on that same journal path; add a reconciliation test that compares the statements with the documented cash-basis source totals.
**Why This Matters:** A landlord can hand an accountant a Profit & Loss showing no rental income when the platform's own reports show more than $124,000 received. A balanced but incomplete trial balance gives false confidence and can lead to incorrect tax, cash, and business decisions.

### BUG-2: Reject inverted report date ranges before generating a report
**Severity:** Medium
**Location:** Generic report viewer, for example `/reports/cash-flow`
**Expected:** If the user selects `From Aug 31, 2026` and `To Aug 1, 2026`, the report should show a validation message and prevent generation, or return a clear validation error. The displayed date controls and the server's reporting period must never describe different ranges.
**Actual:** The date control accepted the inverted range with no warning and left the Update action enabled. The browser sent `GET /api/v1/reports/cash-flow?from=2026-08-31&to=2026-08-01` and received HTTP 200. The response normalized the range to Aug 1–Aug 31, while the control continued to display Aug 31–Aug 1; the report heading therefore showed a period different from the selected input.
**Evidence:** Screenshot: `/home/blackcolours/Workbox/screenshots/e2e-s19-date-inversion.png`. The captured response was HTTP 200 with normalized `from=2026-08-01T23:59:59.9999999Z` and `to=2026-08-31T00:00:00Z`; the page heading read `Aug 1, 2026 – Aug 31, 2026` while the date picker read `Aug 31, 2026 – Aug 1, 2026`.
**Code Reference:** `web/src/routes/(protected)/reports/[report]/+page.svelte:137-157` collects and submits both dates without an order check. `RentalCommand.Api/Services/Domain/ReportsService.cs:3012-3025` silently swaps the bounds when `to < from`.
**Suggested Fix:** Make `ResolveRange` reject an explicitly inverted range with a validation error and surface that error in the generic report viewer instead of swapping the dates.
**Why This Matters:** A non-technical landlord can believe they reviewed a short or future period while the server silently substituted another period, which is dangerous for rent, expense, and tax decisions.

### BUG-3: Scope year-end packet exports to the selected property
**Severity:** Medium
**Location:** `/accounting/year-end`, Property filter and `Export packet`
**Expected:** After selecting `Maple Ridge Duplex`, the packet export should use the same selected property, or the control should clearly say that it exports the full portfolio regardless of the visible filter.
**Actual:** The on-screen year-end query honored `propertyId=1` and showed only Maple Ridge: income `$5,250.00`, expenses `$915.00`, and net `$4,335.00`. Clicking the visible Export packet button downloaded `year-end-2026.pdf`, but the PDF was a four-page `Default Portfolio` packet containing all eight properties, portfolio totals of `$124,966.25` income and `$9,325.00` expenses, and the full 16-lease rent roll.
**Evidence:** Filtered-page screenshot: `/home/blackcolours/Workbox/screenshots/e2e-s19-year-end-export-filter.png`. Rendered packet evidence: `/home/blackcolours/Workbox/screenshots/e2e-s19-year-end-packet-page-2.png` shows the portfolio Schedule E and multiple properties; `/home/blackcolours/Workbox/screenshots/e2e-s19-year-end-packet-page-4.png` shows the full rent roll. The downloaded file was `year-end-2026.pdf`; text extraction found `Default Portfolio`, all eight property names, and 16 leases.
**Code Reference:** `web/src/routes/(protected)/accounting/year-end/+page.svelte:18-21` sends `selectedPropertyId` to the on-screen query, but `web/src/routes/(protected)/accounting/year-end/+page.svelte:46-56` calls `downloadYearEndPacket(year)` without it. `web/src/lib/api/endpoints/accounting.ts:147-158` and `RentalCommand.Api/Controllers/AccountingController.cs:330-339` expose only the year for packet download.
**Suggested Fix:** Add the selected `propertyId` to the year-end packet download flow and pass it through the client endpoint, controller, and packet service so the exported sections use the same scope as the page.
**Why This Matters:** A landlord may send an apparently filtered property packet to an accountant or owner while it contains other properties' income, expenses, and tenants.

### BUG-4: Render expense categories by name in cash-and-operating-activity
**Severity:** Low
**Location:** `/reports/cash-and-operating-activity`, Category column and CSV export
**Expected:** Expense categories should be readable labels such as `Repairs & maintenance`, `Cleaning & maintenance`, and `Utilities`. The report's own category formatter supports those enum names.
**Actual:** Expense rows rendered raw numeric values such as `8`, `2`, and `11` in the Category column. The underlying amounts and totals were correct, but the user cannot tell which tax or operating category each expense belongs to without knowing the enum ordinals.
**Evidence:** Screenshot: `/home/blackcolours/Workbox/screenshots/e2e-s19-cash-operating-category.png`. The visible rows include category values `8`, `2`, and `11`; the same report footer reconciled to `$124,966.25` income, `$9,325.00` expense, and `$115,641.25` net. `ScheduleECategory` defines `Repairs=8`, `CleaningMaintenance=2`, and `Utilities=11`.
**Code Reference:** `RentalCommand.Api/Services/Domain/ReportsService.cs:2131-2141` projects PostgreSQL's integer enum column with `expense."Category"::text`. `web/src/routes/(protected)/reports/[report]/+page.svelte:713-720` calls the label formatter, while `web/src/lib/accounting/money-display.ts:25-55` maps named enum values but has no ordinal mapping. The enum ordinals are in `RentalCommand.Core/Enums/ScheduleECategory.cs:6-21`.
**Suggested Fix:** Return the category's enum name from the report query instead of the stored numeric ordinal so the existing `formatMoneyCategoryLabel` mapping produces the human-readable label in both the table and CSV.
**Why This Matters:** A landlord or accountant may misread or be unable to classify an expense during review even though the money total is numerically right.

## Potential Issues (need investigation)

- A custom future range (`2030-01-01` through `2030-12-31`) returned 12 zero-valued month rows and HTTP 200 rather than the generic viewer's `Nothing to show for this period` state. This may be intentional calendar scaffolding, but the UI does not distinguish an empty result from real zero activity. Relevant code: `web/src/routes/(protected)/reports/[report]/+page.svelte:236-241`.
- The owner-report email action passes only `ownerId` and `year` even when the screen is set to a monthly or quarterly period. It was not clicked because it sends an external email. Relevant call site: `web/src/routes/(protected)/owners-report/+page.svelte:403-463`, especially the email action around line 408.
- Generic CSV export is client-side from the currently loaded rows. A paged report could therefore export only the current page rather than the complete filtered result; the seeded security-deposit register had exactly 20 rows, so this was not reproduced. Relevant code: `web/src/routes/(protected)/reports/[report]/+page.svelte:243-309`.

## Observations

- The Reports Catalog showed 22 reports: 3 everyday reports and 19 accountant reports. Titles, help links, and deep links were present; unknown `/reports/unknown-report` produced a clear not-found state without an HTTP or console error.
- Generic cash flow, Schedule E, and the year-end all-properties view agreed at the final observation: income `$124,966.25`, paid expense `$9,325.00`, net `$115,641.25`. The property filter changed the year-end on-screen rows correctly.
- Owner statements behaved as documented. For 2026 year-to-date, owner statement income totaled `$124,850.00` across the two owners, exactly `$116.25` below the cash/Schedule E income because owner statements are rent-only and late-fee income is included in the other reports. Expenses totaled `$9,325.00`.
- True cash flow was opened through Advanced accounting. With no loan or distribution source rows in the seeded data, the current-month result was `$17,171.25` money in, `$0.00` operating expense, and `$17,171.25` net cash flow. It excluded deposit activity as intended.
- The general-ledger account filter and journal detail drawer worked. Selecting Operating Cash and opening the Aug 27 rent-payment row showed a balanced `$1,050.00` debit to Operating Cash and credit to Tenant AR; closing the drawer preserved the tab, account filter, and URL state.
- Schedule E category totals, vendor 1099 threshold/W-9 flags, 2022 no-data state, tax-year switching, rent-ledger totals, aged receivables, occupancy, lease expirations, and security-deposit register were readable and did not produce confirmed mismatches. No loan, capital-asset, owner-distribution, or application-fee rows existed, so those branches were not altered or truth-tested.
- Generic report print media was checked at the required 1710x990 viewport. In print media, the sidebar, header, report actions, and scrolling containers were hidden or made visible as intended; screenshot: `/home/blackcolours/Workbox/screenshots/e2e-s19-cash-flow-print.png`.
- The generated year-end PDF was text-extracted with `pypdf` and rendered with Poppler for page-level inspection. The filtered-export scope defect is visible in the rendered packet; no clipping or unreadable table layout was observed at the tested desktop size.

## What Was Tested

- Logged in at `/login` with the seeded admin account using the persistent `e2e-s19` profile. All browser scripts used headless Chromium with a `1710x990` viewport and closed their context in `finally` blocks.
- Opened the report catalog and accountant section; visited generic report routes for cash flow, rent ledger, cash/operating activity, property P&L, aged receivables, occupancy, lease expirations, security deposits, vendor 1099, and unknown-report handling. Used date, property, year, status/category, and pagination-related controls where available; captured a CSV download.
- Tested inverted dates and a future no-data range on generic cash flow; verified API status and response period without mutating data.
- Opened `/owners-report`, changed monthly/YTD period and as-of date, switched owners, compared property rows and totals, and checked empty distributions/contributions states. Staff access to `/owner/statements` and `/owner/properties` redirected to the dashboard, consistent with the seeded staff role gate.
- Opened `/tax`, changed from 2026 to 2022, reviewed Schedule E and vendor 1099 outputs, and verified the no-data response. Opened `/accounting/year-end`, changed tax year and property, compared all-property versus Maple Ridge values, and downloaded/rendered the packet.
- Opened Advanced accounting cash flow, balance sheet, Profit & Loss, trial balance, chart of accounts, and general ledger. Filtered the ledger to Operating Cash, opened the journal detail drawer, verified balanced lines, and returned to the filtered list.
- Used only read-only database queries for reconciliation. No report-source record was edited or deleted.

Browser cleanup: stopped e2e-s19
