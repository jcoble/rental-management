# Exploratory Test Report: Security Deposits, Owners Report, Tax/Year-end
Date: 2026-06-28
Tester: tester3
Duration: ~75 min (deep code read + browser-driven verification)

## Scenario
Test the money flows beside rent — holding/disbursing **security deposits**, the **owner
statement/report**, and **tax / year-end (Schedule E + 1099)** — for deposit math integrity,
owner-statement reconciliation, and server-side filtered tax totals.

## Summary
I read the full chain (controllers → services → entities → web routes) and then drove the browser
as a real landlord against isolated data I created (owner 65, props 139/140, leases 245/246,
holdings 181/182, a cross-year vendor 67 — all marked `QA-T3-233207`). **The deposit-return status
logic is inverted** (HIGH): a partial refund is labelled "Returned" and a fully-withheld deposit is
labelled "Partially Returned" — confirmed in the deposit detail, the deposits grid, the Security
Deposit Register report, and the persisted DB row. The **Tax page 1099 checklist sums vendor
payments across all years and ignores the year selector** (Medium), falsely flagging a 1099 for a
vendor under the $600/year threshold. The owner statement reconciles exactly (no double-count/
omission) and tax/owner year filters run server-side; the only owner-statement defect is a
**whole-dollar rounding display** that makes the on-screen Income column not sum to its total (Low).
Two accrual-vs-cash divergences between Schedule E and the owner statement are flagged as likely-by-
design. **No in-memory aggregation hard-rule violations were found** — report/statement totals are
EF-translated SQL (GroupBy/Sum or correlated subqueries).

## Bugs Found

### BUG-1: Security-deposit return status is inverted (partial refund → "Returned", full withhold → "Partially Returned")
**Severity:** High
**Location:** `/deposits/{id}` detail badge, `/deposits` grid Status column, Security Deposit Register report (`/reports/security-deposits`), and the persisted holding row.
**Expected:** Status should reflect how much of the deposit went back to the tenant:
- no deductions, full amount refunded → **Returned**
- some deductions, part refunded (0 < net < amount) → **Partially Returned**
- deductions consume the whole deposit, $0 refunded → **Returned/fully withheld** (definitely *not* "Partially Returned").

**Actual:** The two terminal states are swapped for every deposit that has deductions:
- Holding 181 (lease QA-T3-A): held **$1,000**, deduction **$250**, refunded **$750** → status **"Returned"** (green/success). The $250 we kept is invisible in the badge.
- Holding 182 (lease QA-T3-B): held **$600**, deduction **$600**, refunded **$0** → status **"Partially Returned"** (amber). Nothing went back, yet it reads as a partial refund.

Persisted (read back via API): `181 … returnedAmount 750 STATUS=Returned`; `182 … returnedAmount 0 STATUS=PartiallyReturned`. The Security Deposit Register report shows the same swapped `statusName` for both rows.
**Evidence:** `output/playwright/tester3-01-partial-return-mislabeled-returned.png` (181 → "Returned" with $250 deductions), `tester3-02-full-withhold-mislabeled-partial.png` (182 → "Partially Returned" with $0 returned), `tester3-06-deposits-grid-inverted-status.png` (both rows side-by-side). API confirm: register report `QA-T3-A … returned 750 STATUS=Returned` / `QA-T3-B … returned 0 STATUS=Partially Returned`.
**Code Reference:** `RentalCommand.Api/Services/Domain/SecurityDepositService.cs:211`
```csharp
entity.Status = net > 0m ? SecurityDepositStatus.Returned : SecurityDepositStatus.PartiallyReturned;
```
The condition keys off `net > 0` instead of off whether any deductions were taken. `net` is `Math.Max(0, Amount - DeductionsTotal)`, so a partial refund (net>0) wrongly maps to `Returned`, and a full withhold (net==0) wrongly maps to `PartiallyReturned`.
**Suggested Fix:** Base the terminal status on the deduction total, e.g.
`entity.Status = totalDeductions <= 0m ? SecurityDepositStatus.Returned : (net > 0m ? SecurityDepositStatus.PartiallyReturned : SecurityDepositStatus.Returned);`
(i.e. no deductions → Returned; partial → PartiallyReturned; fully consumed → Returned/withheld). If a distinct "fully withheld" state is wanted, add a `Withheld` enum value for the net==0 case.
**Why This Matters:** Deposits are the tenant's money held in trust and a frequent dispute/compliance point. Every partial deposit return now shows the reassuring green "Returned" while the landlord actually kept money, and every fully-withheld deposit shows "Partially Returned" implying the tenant got something back. The wrong status is also written verbatim into the deposit's audit log (`SecurityDepositService.cs:218-228`, `newValues.status = entity.Status.ToString()`), which the service comments call "the sole audit for legally-sensitive deposit lifecycle events" — so the legal record is wrong too.

### BUG-2: Tax-page 1099 checklist sums vendor payments across all years and ignores the year selector (false $600 threshold flags)
**Severity:** Medium
**Location:** `/tax` → "1099 checklist" card (the `Paid` column and "needs a W-9" / 1099-review flag). Source endpoint `GET /api/v1/accounting/reports`.
**Expected:** 1099 reporting is strictly per calendar year ($600/payee/year threshold). The Tax page's year selector should scope the checklist, and `Paid` should be that year's total — matching the dedicated Vendor 1099 report (`/reports/vendor-1099?year=`).
**Actual:** The checklist's `TotalPaid` is the vendor's **all-time** paid total with **no date filter**, and the card does not react to the year selector. Proven with vendor 67 (one $400 paid expense in 2024, one $300 in 2026):
- Dedicated report `/reports/vendor-1099?year=2026` → Paid **300**, needs1099 **False**; `year=2024` → Paid **400**, needs1099 **False** (neither year ≥ $600). ✓ correct
- Tax-page card (`/accounting/reports`) → Paid **700** (all-time), **needs1099Review = True** ✗
- Switching the Tax page year 2026 → 2025 leaves the checklist amounts unchanged ($347 / $152 in both), even though the Schedule E section correctly empties for 2025.
**Evidence:** API contrast above; `output/playwright/tester3-05-tax-1099-ignores-year.png` (2025 selected: Schedule E shows "No rental income or expense data for 2025" while the 1099 card still lists the same vendors/amounts).
**Code Reference:** `RentalCommand.Api/Services/Domain/AccountingService.cs:911-913` (TotalPaid summed with no year bound) and `:928` (`Needs1099Review = Is1099Eligible && TotalPaid >= 600`); web side `web/src/routes/(protected)/tax/+page.svelte:34-41` (`reportsQuery` has no `selectedYear` in its key and calls `accounting.reports()` with no year).
**Suggested Fix:** Make the Tax page's 1099 checklist use the year-scoped `GET /api/v1/reports/vendor-1099?year={selectedYear}` (which already filters `PaidAt` by year and is correct), or add a `year` parameter to `GetReportsAsync`'s vendor-1099 sum (`e.PaidAt >= yearStart && e.PaidAt < yearEnd`) and key the web query on `selectedYear`.
**Why This Matters:** A landlord using this checklist to decide who gets a 1099 will be told to issue 1099s to vendors who never crossed $600 in any single year (false positives), and the `Paid` figure shown is a multi-year sum that won't match the IRS form. Tax correctness in the most prominent, action-oriented card on the Tax page.

### BUG-3: Owner statement (and Tax) display money as whole dollars, so per-property columns don't sum to the shown totals
**Severity:** Low
**Location:** `/owners-report` statement (summary cards + property-breakdown table) and `/tax` summary cards/category amounts.
**Expected:** A money statement a landlord shares with owners should show cents and reconcile — property rows summing to the displayed total.
**Actual:** Both pages format money with `maximumFractionDigits: 0`, rounding every value independently to whole dollars, so the on-screen columns visibly fail to add up. With my owner (income 1000.50 + 100.50 = 1101.00 exact):
- Property Income rows display **$1,001** (PropA) + **$101** (PropB) = **$1,102**, but Total income displays **$1,101**.
- Net to owner displays $701 (700.95 rounded). The CSV export (`owner-statement/export`, `F2`) is exact, so only the on-screen summary is affected.
**Evidence:** `output/playwright/tester3-03-owner-statement-rounding-drift.png`. API truth: `totalIncome 1101.0`, property incomes `1000.5` and `100.5`.
**Code Reference:** `web/src/routes/(protected)/owners-report/+page.svelte:37-43` and `web/src/routes/(protected)/tax/+page.svelte:51-57` (`money()` with `maximumFractionDigits: 0`).
**Suggested Fix:** Use `minimumFractionDigits: 2, maximumFractionDigits: 2` for owner-statement and tax money so cents are shown and columns reconcile (the underlying data and CSV are already exact).
**Why This Matters:** This is the document a landlord forwards to a property owner. Income rows that add to $1,102 under a $1,101 total read as an arithmetic error and undermine trust, even though the stored/exported numbers are correct.

## Potential Issues (need investigation)

- **Schedule E (Tax) vs Owner statement use different accounting bases for the same property/year.** Schedule E counts expenses by `IncurredAt` with **any** status (`ScheduleEService.cs:62-67`), while the owner statement counts only `Status == Paid` dated by `PaidAt ?? IncurredAt` (`OwnerStatementService.cs:54-60`). My PropA shows **$300** expenses on the owner statement but **$800** of expenses (Repairs $300 + the *Pending, never-paid* Insurance $500) plus depreciation on the Tax page. Both are defensible (cash vs accrual), but a landlord comparing the two pages will see the same property with different expense totals and no explanation. Evidence: `tester3-04-tax-schedulee-propA-accrual.png`. Confirm this divergence is intended and consider a one-line note on each page.
- **Schedule E expense date is `IncurredAt` only (no `PaidAt` fallback)** (`ScheduleEService.cs:66`), whereas Cash Flow / Property P&L use `(PaidAt ?? IncurredAt)` (e.g. `ReportsService.cs:614, 1005`). An expense incurred in Dec 2025 but paid Jan 2026 lands in Schedule E 2025 but in Cash Flow/P&L 2026, so those reports won't cross-foot across a year boundary. Likely intentional (accrual) but worth confirming.
- **Owner-statement management-fee rounding is per-line vs rounded-after-sum.** Per-property `ManagementFee` is `Math.Round(income*pct,2)` (`OwnerStatementService.cs:86`) while `TotalManagementFee`/`TotalNetToOwner` round the *unrounded* grouped sum (`:104-111,119-122`). With incomes whose fee has sub-cent fractions, the sum of the displayed line fees can differ from the displayed total by a cent. I did **not** reproduce it (my data didn't trigger sub-cent fees, and the whole-dollar display of BUG-3 masks it), so flagging as latent — compute line and total from the same rounding to be safe.

## Observations
- **A deposit holding is auto-created when a lease is created** (holdings 181/182 appeared on lease creation with Notes "Created from lease security deposit"). Works as built; noted because deposits exist without an explicit "New Holding" action, and the New-Holding dialog is then idempotent (creating a second holding for a lease returns the existing one with a "created" toast — `SecurityDepositService.cs:106-112`).
- **Deposit returns are not gated on lease move-out.** `ReturnAsync` only blocks an already-returned holding; it doesn't reference `Lease.MoveOutDate`, so a return can be processed on an Active lease (I did, on Active leases 245/246). The code doesn't appear to intend a move-out gate, so this is an observation rather than a missing block.
- **Over-deducting beyond the held amount is allowed** (no cap; `AddDeductionAsync` doesn't check cumulative deductions ≤ Amount). Net refund is correctly clamped to ≥ 0 and the move-out PDF handles it well ("Balance owed by tenant", `MoveOutStatementPdfGenerator.cs:196-215`), so this is low-risk, but there's no UI warning when deductions exceed the deposit.
- **Positive findings:** Deduction amount is validated `[0.01, …]` (no negative/zero deductions, so you cannot inflate a refund above the amount held); you cannot refund twice or add a deduction after return (API + UI both block — buttons disable on non-`Held` status); owner statement income/expense/fee/net reconcile exactly (income 1101.00, expenses 300.00, mgmt 100.05, net 700.95; the unpaid $500 is correctly excluded — no double-count or omission); year filters on owner statement and Schedule E are applied server-side (2025 → zeros/empty). All totals reviewed are DB-side (EF GroupBy/Sum or correlated subqueries) — no in-memory aggregation hard-rule violations.

## What Was Tested
1. Read end-to-end: `SecurityDepositsController` + `SecurityDepositService` + `SecurityDepositHolding`/`SecurityDepositStatus` + `securityDeposits.ts` + deposits routes; `OwnerEntityController`/`AccountingController`/`ReportsController` + `OwnerStatementService`/`ReportsService`/`ScheduleEService`/`AccountingService` + owners-report & tax routes + `MoveOutStatementPdfGenerator`.
2. Created isolated fixtures via API (marker `QA-T3-233207`): owner 65, property 139 (mgmt 10%, basis for depreciation) + 140 (mgmt 0%), units 235/236, tenant 271, Active leases 245 (dep $1,000) / 246 (dep $600), a Paid rent payment on each ($1,000.50 / $100.50, PaidDate 2026), a Paid Repairs expense ($300) and a Pending Insurance expense ($500) on prop 139, and a 1099 vendor 67 with paid expenses in 2024 ($400) and 2026 ($300).
3. Deposit lifecycle in the browser: holding 181 — added a $250 deduction, processed the return ($750 net) → observed badge "Returned" (BUG-1). Holding 182 — added a $600 deduction, processed the return ($0 net) → observed badge "Partially Returned" (BUG-1). Verified buttons disable after return (no double-refund), and cross-checked persisted status + the Security Deposit Register report via API.
4. Owner report: selected my owner for 2026, reconciled the four summary cards and the property breakdown against the API; observed the whole-dollar column drift (BUG-3); switched to 2025 and confirmed list + detail zero out (server-side year filter).
5. Tax/Schedule E: 2026 — verified PropA categories (Insurance $500 + Repairs $300 + Depreciation $5,576) sum to the property Total expenses $6,376 and net −$5,375 (matches API), exposing the accrual/cash divergence; switched to 2025 and confirmed the empty state; isolated and proved the 1099 checklist all-time/year-insensitive flaw (BUG-2) by contrasting `/accounting/reports` against `/reports/vendor-1099?year=`.

Notes on hygiene: all created records are marked `QA-T3-233207` and left in place (shared DB; no seed records were mutated, no deposits on seed leases were refunded). Browser session `tester3` closed. Screenshots in `output/playwright/tester3-01..06`.
