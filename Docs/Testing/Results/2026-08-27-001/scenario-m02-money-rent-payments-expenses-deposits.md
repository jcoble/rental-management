# Exploratory Test Report: Mobile M02 — Money: rent, payments, expenses, deposits
Date: 2026-08-27
Tester: 2026-08-27-001 / Mobile M02
Duration: ~35 minutes (09:05–09:40 UTC)

## Scenario
Use the Rental Command Android app as a landlord to review money owed, record a rent payment and expense, inspect a tenant ledger and deposit, and verify the figures reconcile.

## Summary
I exercised Money Insights, the overdue list, a tenant ledger, deposits, banking, the unified activity feed, and the expense form on the owner’s Galaxy S22+. Marcus Williams’s $1,050 Check receipt and a QA-prefixed $23.45 paid expense were saved and verified through the API; the rapid double-save produced one expense record. The main findings are a misleading negative rent-owed figure, stale dashboard/Today data after a receipt until manual refresh, and ungrouped thousands in deposit currency formatting.

All money values noted below were cross-checked against the API at or near the time they were read while web testers were concurrently changing sandbox data. The final API read at 09:40 UTC reported cash on hand $2,226.55, deposits held $20,125.00, cash after deposits -$17,898.45, cash received $2,250.00, cash paid $23.45, and 3 overdue tenants owing $3,050.00.

## Bugs Found

### BUG-1: Present rent still owed as a clear positive, reconciling amount
**Severity:** High
**Location:** Money → Insights → Overview
**Expected:** A card labelled “Rent still owed” should show a positive amount the landlord can understand, or clearly identify itself as a signed accounting balance. It should not contradict the canonical “Who’s behind” result, whose API contract describes one row per tenant currently behind and a matching total.
**Actual:** At the baseline API read around 09:07 UTC, “Who’s behind” showed 4 tenants owing $4,100.00, while the Money overview showed “Rent still owed -$1,250.00” (`m02-02-money-overview.png`). After Marcus’s receipt, the API at 09:32 UTC showed 3 tenants owing $3,050.00, while the overview showed “Rent still owed -$2,300.00” (`m02-49-money-overview-after-expense.png`, `m02-50-period-facts-after-expense.png`). The raw signed ledger number is being presented under a plain-language owed label with no explanation.
**Evidence:** `m02-02-money-overview.png`; `m02-04-overdue-from-rent-owed.png`; `m02-49-money-overview-after-expense.png`; API `GET /api/v1/accounting/money-position` at 09:40 UTC returned `rentStillOwed:-2300` and `pastDueAmount:3050`, while `GET /api/v1/accounting/past-due` returned `totalPastDueAmount:3050` and `totalCount:3`.
**Code Reference:** `mobile/lib/features/money/money_screen.dart:659-674` renders `moneyFmt(position.rentStillOwed)` under the “Rent still owed” label; `RentalCommand.Api/Services/Domain/AccountingLedgerReadModelService.cs:429-434` calculates the signed receivable balance; `RentalCommand.Api/Controllers/AccountingController.cs:254-257` defines the canonical past-due list and KPI.
**Suggested Fix:** Supply this user-facing card with a positive, canonical owed/past-due display value, and keep any signed ledger balance behind an explicitly accounting-oriented label.
**Why This Matters:** A non-accountant landlord can read a negative owed amount as a credit or as money they do not need to collect, leading to incorrect collection decisions.

### BUG-2: Refresh Money totals immediately after an overdue receipt
**Severity:** High
**Location:** Money → Insights → Overview after recording a receipt from “Who’s behind”
**Expected:** Once a receipt saves, the overdue list and Money overview should reflect the same committed transaction without requiring the landlord to leave the Money area or manually reload it.
**Actual:** Recording Marcus’s $1,050 receipt changed the overdue list immediately to 3 tenants owing $3,050.00 (`m02-11-receipt-saved.png`). Returning to the Money overview still displayed the old cash-on-hand `$1,200.00`, cash-after-deposits `-$18,925.00`, and rent-still-owed `-$1,250.00` (`m02-12-money-after-payment.png`). The API read at 09:12 UTC already reported cash-on-hand `$2,250.00`, cash-after-deposits `-$17,875.00`, and rent-still-owed `-$2,300.00`. Tapping the bottom Money tab later forced the correct values to appear (`m02-16-money-root-for-destinations.png`).
**Evidence:** `m02-11-receipt-saved.png`; `m02-12-money-after-payment.png`; `m02-16-money-root-for-destinations.png`; logcat showed the receipt request completed after an initial 401/retry, and the API ledger entry was settled with reference `QA-20260827-m02-Marcus`, target charge 43, and running amount owed 0.
**Code Reference:** `mobile/lib/features/money/overdue_screen.dart:37-59` invalidates only `moneySnapshotProvider` and refreshes `pastDueProvider` after success; the normal Money manual-entry path at `mobile/lib/features/money/money_screen.dart:258-263` also invalidates `moneyPositionProvider` and refreshes transactions.
**Suggested Fix:** Route overdue-receipt success through the same shared Money refresh path so `moneyPositionProvider`, the transaction feed, and the relevant snapshot/list providers are invalidated together.
**Why This Matters:** The landlord can record money successfully and then immediately make another decision using obsolete cash and rent figures.

### BUG-3: Refresh the Today overdue briefing after a receipt
**Severity:** High
**Location:** Today screen after recording a receipt from Money → Who’s behind
**Expected:** Today should stop warning about a tenant immediately after that tenant’s overdue charge is paid. The current API briefing and past-due list should agree with the screen without requiring a pull-to-refresh.
**Actual:** After the successful Marcus receipt, opening Today showed `Rent overdue — Marcus Williams ... $1,050 was due 26 days ago` (`m02-58-final-today.png` and `m02-61-today-top-for-refresh.png`). At the same time, the API at 09:36 UTC returned only Derek Johnson, Kevin Brown, and James Wilson in both `/accounting/past-due` and `/ai/briefing`. A manual pull-to-refresh took roughly 10 seconds and then removed Marcus (`m02-63-today-after-pull-refresh-complete.png`).
**Evidence:** `m02-58-final-today.png`; `m02-61-today-top-for-refresh.png`; `m02-63-today-after-pull-refresh-complete.png`; final API briefing at 09:40 UTC contained no Marcus bullet and had 3 rent-overdue bullets.
**Code Reference:** `mobile/lib/features/home/home_shell.dart:2212-2215` watches the cached `homeBriefingProvider`; `mobile/lib/features/home/home_shell.dart:2237-2244` refreshes that provider only through Today’s pull-to-refresh; `mobile/lib/features/money/overdue_screen.dart:56-59` does not invalidate it after the receipt succeeds; the provider is defined at `mobile/lib/features/home/home_access_providers.dart:10-14`.
**Suggested Fix:** Invalidate `homeBriefingProvider` on successful overdue-receipt completion, preferably through the same shared post-payment refresh used by Money.
**Why This Matters:** The landlord may contact or penalize a tenant who has just paid because Today still presents that tenant as overdue.

### BUG-4: Group thousands in deposit currency amounts
**Severity:** Low
**Location:** Money → Deposits list and deposit detail
**Expected:** Currency formatting should be consistent and easy to scan across Money. A $1,050 deposit should display as `$1,050.00`, matching the Overview, ledger, and expense screens.
**Actual:** The Deposits list and Marcus’s detail sheet displayed `$1050.00` without a thousands separator (`m02-18-deposits-loading-or-error.png`, `m02-19-deposit-detail-marcus.png`). The API value was `1050.00`, and the same amount appeared as `$1,050.00` elsewhere in Money.
**Evidence:** `m02-18-deposits-loading-or-error.png`; `m02-19-deposit-detail-marcus.png`; API `GET /api/v1/tenant-accounts/1/deposit` returned `heldBalance:1050.00`.
**Code Reference:** `mobile/lib/features/deposits/deposits_screen.dart:18-21` uses a local `toStringAsFixed(2)` formatter without grouping, while the shared Money formatter is already used by the other screens.
**Suggested Fix:** Replace the local deposit formatter with the shared `moneyFmt` formatter.
**Why This Matters:** Inconsistent formatting makes a landlord pause over whether a large deposit amount was read correctly and is harder to scan on a phone.

## Potential Issues (need investigation)

- The deposit list endpoint intermittently returned HTTP 500/499 while the web testers were concurrently loading the sandbox. The API log records `Timeout during reading attempt` at `TenantAccountQueryService.ListDepositsPageAsync` (`/tmp/rentalcommand-api.log:30353-30410`, response at `30431`, and a second timeout at `32133-32190`, response at `32211`). The same endpoint later returned 200 and the phone loaded all 20 records (`/tmp/rentalcommand-api.log:47651-47663`). This may be shared database contention rather than a deterministic mobile defect; investigate the query/connection behavior at `RentalCommand.Api/Services/Domain/TenantAccountQueryService.cs:81`.
- The first receipt and expense POSTs each logged a 401 before the authenticated retry completed. No duplicate or lost record was observed, but the token-expiry/retry path merits monitoring during longer sessions. The API log also records an expired token at `/tmp/rentalcommand-api.log:39905-39910`.

## Observations

- The receipt form prefilled the selected overdue amount, visibly required a payment method, kept the optional detail section understandable, and stored the `QA-20260827-m02-Marcus` reference. The settled ledger entry was visible in the unified feed as `+$1,050.00 Credit`.
- Tenant ledger detail was readable for Derek Johnson: Balance due and Past due were both `$1,100`, the August charge was open, and the API ledger summary agreed. The Advanced detail switch and the “How the tenant ledger works” help sheet were present and understandable (`m02-13-tenant-ledger.png`, `m02-14-tenant-ledger-advanced.png`, `m02-15-tenant-ledger-help.png`). The `3 / 6 / 9 / 12` period controls are ambiguous without the word “months.”
- Banking’s empty state matched the API exactly: 0 connections, 0 transactions, 0 unmatched, and 0 suggestions (`m02-26-banking.png`). It clearly directs the landlord to import or connect a read-only account on the web.
- The expense form fit the 1080×2340 screen with the keyboard open. Blank property, zero, negative, and comma-decimal values were blocked with visible validation. The rapid double-save produced one API expense (`id:37`, amount `$23.45`, status `Paid`, Maple Ridge Duplex), the activity feed showed `-$23.45`, and reopening the detail displayed the typed line item (`m02-47-expenses-list.png`, `m02-48-expense-detail.png`, `m02-51-money-activity.png`).
- The comma-decimal attempt displayed `$1,25` and “Enter a valid number” (`m02-55-expense-comma-validation.png`). This is expected for the US-locale build, but a locale-aware input hint or normalization could reduce confusion.
- Pull-to-refresh on Today eventually reconciled the briefing, but the refresh was slow enough to leave the spinner visible for several seconds (`m02-62-today-after-pull-refresh.png` before completion).

## What Was Tested

- Logged into the existing sample/sandbox portfolio on the Galaxy S22+ without tapping the “Example data — tap to start fresh” action.
- Read Money Overview and period facts, then compared cash, deposits held, cash movement, profit/loss, and overdue totals with `/accounting/money-position`, `/accounting/snapshot`, and `/accounting/past-due` at the time of each read.
- Opened the overdue list from the rent-owed card, recorded Marcus Williams’s full `$1,050.00` rent payment with method `Check`, Aug 27, 2026 receipt date, and reference `QA-20260827-m02-Marcus`; verified the overdue list, API ledger entry, allocation to charge 43, settled status, and transaction feed.
- Opened Derek Johnson’s tenant ledger, checked the 3/6/9/12 period controls, All/Open/Payments/Credits filters, Advanced detail, help text, balance, past-due amount, and deposit summary.
- Opened Deposits, inspected Marcus’s held `$1,050.00` deposit and detail totals, attempted blank and over-held deductions, verified visible validation, cancelled without mutation, and confirmed the deposit API remained at 1050 held / 0 deductions / 0 refunded.
- Opened Banking and compared its empty-state cards with `/banking/summary`, `/banking/review-queue`, and `/banking/transactions`.
- Created a paid Repairs expense for Maple Ridge Duplex: description `QA-20260827-m02 expense materials`, amount `$23.45`, Aug 27 incurred/paid dates, line item `QA-20260827-m02 materials`, quantity 1, unit price `$23.45`, and line amount `$23.45`; double-tapped save, verified one API record, reopened its detail, and checked the activity and period-facts totals.
- Tested blank required fields, zero, negative, and comma-decimal amount input, keyboard-open layouts, cancelling drafts, and rapid double-save. Future-date and very-large-number submissions were not saved.
- Pulled Today to refresh the briefing, verified the paid Marcus item disappeared, and left the app on Today while logged in.

Device cleanup: app left on Today, logged in
