# Exploratory Test Report: Accounting, transactions, tenant ledgers, charges, deposits, and banking
Date: 2026-08-27
Tester: e2e-s18
Duration: ~75 minutes (Phase 1 code reading + Phase 2 browser session)

## Scenario

Follow the landlord's money surfaces end to end — accounting tabs, portfolio ledger, tenant-account
ledger, one-time/recurring charges, receipts and allocations, past-due, corrections/reversals,
security-deposit lifecycle, expenses, and the Banking/Plaid boundary — checking that every amount
reconciles server-side and reads the same on every page.

## Summary

I exercised the Money hub (Rent & payments / Activity tabs), the Who's-behind list, one tenant
account end to end (charge → receipt → allocation → reversal), the security-deposit detail and list,
the expense create flow, the recurring-charge form, and the Banking/Plaid entry points, all against
the seeded SANDBOX portfolio. The core money engine reconciles well: past-due count/total, deposit
held/received/deducted, tenant balance after each mutation, and every list filter I tried are
computed server-side and agree with the database.

Six confirmed defects. Two are money-visibility problems: an expense saved with no property is
accepted and then disappears from every accounting surface, and "Kept this month" counts *allocated*
money rather than *received* money so an overpayment is silently missing from the cash figure. The
other four are consistency/UX defects on the primary money screens: the same charge is shown with
opposite signs on two tabs of the same page, every month of every tenant ledger carries a red
"Needs review" warning, the accounting ledger's row links land on the wrong unit tab, and the
"Fix this charge" dialog stays open with no confirmation after it has already posted the reversal.

Tooling note: `playwright-cli` is not installed on this machine (`command not found`, not on PATH,
not in any global npm/pnpm root). I drove a real headless Chromium through the repo's own
`@playwright/test` 1.60.0 install with a single long-lived session process isolated to this task
(scratch dir `.../scratchpad/e2e/drv`), viewport pinned and verified at 1710x990
(`window.innerWidth + 'x' + window.innerHeight` → `"1710x990"`). No JS workarounds, no DOM
manipulation, no direct API calls were used to reach a result; the database was read SELECT-only for
proof.

## Bugs Found

### BUG-1: Stop the accounting ledger rows from linking to a unit tab that does not exist

**Severity:** Medium
**Location:** Money → "Rent & payments" tab (`/accounting`), every ledger row; lands on `/units/{id}?tab=rent`
**Expected:** Clicking a money row in the portfolio ledger opens that unit's rent ledger — the same
place the unit's own next-best-action link goes (`/units/19?tab=money&ledger=rent`).
**Actual:** The row links to `/units/{unitId}?tab=rent`. `rent` is not a member of `UNIT_TABS`, so the
resolver falls back to `summary` and the landlord lands on the unit overview with no ledger in sight.
Verified by navigating to `https://localhost:5667/units/19?tab=rent` — the Summary tab renders
selected (`e27 <button> "Summary" [testid=tab-summary] SELECTED`).
**Evidence:** snapshot of `/units/19?tab=rent` shows `tab-summary` SELECTED; the same URL shape is
emitted for all 24 rows on `/accounting` (e.g. `[testid=portfolio-ledger-row-59] href=/units/3?tab=rent`).
**Code Reference:** `web/src/lib/components/accounting/PortfolioLeaseLedgerPanel.svelte:73` builds
`/units/${row.unitId}?tab=rent`; `web/src/lib/components/unit/unit-tabs.ts:1` defines the valid tabs
(`summary, leasing, tenant-lease, money, maintenance, documents-history`) and `:117` silently falls
back to `summary` for anything else. A contract test asserts the wrong shape:
`web/src/lib/components/accounting/portfolio-lease-ledger.test.ts:10`.
**Suggested Fix:** Change the href in `PortfolioLeaseLedgerPanel.svelte:73` to
`/units/${row.unitId}?tab=money&view=tenant-account` and update the assertion in
`portfolio-lease-ledger.test.ts:10` to match.
**Why This Matters:** The portfolio ledger is the landlord's main drill-down. Every row is a dead end
that dumps them on a page without the number they clicked.

### BUG-2: Reject (or surface) an expense saved with no property — it currently vanishes from the books

**Severity:** High
**Location:** Money → Activity → "Add expense" (`/accounting?tab=activity`)
**Expected:** Either the form requires a property, or an expense saved without one still appears in
the Activity list and in expense totals. Money the landlord records must be findable again.
**Actual:** The property picker defaults to "No property" and the save succeeds with no warning. The
row is then invisible everywhere in the UI: the Activity list count stayed at `1–20 of 35` before and
after the save, and searching the rendered list for `QA-20260827` returns nothing. The row exists in
the database.
**Evidence:**
`psql … "SELECT \"Id\",\"Amount\",\"PropertyId\",\"Status\",\"Description\" FROM \"Expenses\" WHERE \"Description\" LIKE 'QA-20260827-s18%'"`
→ `36 | 12.34 | (null) | 0 | QA-20260827-s18 expense test`.
Activity list after the save: `1–20 of 35`, `Page 1 of 2`, no QA row. Other *Pending* expenses do
render (e.g. "Inspect and repair exterior outlets (code) … Pending"), so status is not the reason —
the null property is.
**Code Reference:** `RentalCommand.Api/Services/Domain/AccountingService.Transactions.cs:255` —
`AND expense."PropertyId" IS NOT NULL` excludes the row from the transactions grid. The same
predicate excludes it from every total: `AccountingService.cs:723` (expense total),
`:612` (per-property P&L), `:681`/`:692` (1099 vendor totals), `:874` (report ledger).
**Suggested Fix:** Make the property required in the expense form
(`web/src/routes/(protected)/accounting/+page.svelte`, `[testid=expense-property-input-trigger]`) and
validate it server-side in `ExpenseController` so a property-less expense can no longer be created.
**Why This Matters:** The landlord types in a real cost, the app says it saved, and the money is gone
from the ledger, the year-end packet, and Schedule E. Silent under-reporting of deductible expenses.

### BUG-3: Make the charge/payment sign the same on the Rent & payments tab and the Activity tab

**Severity:** High
**Location:** `/accounting` — "Rent & payments" tab vs "Activity" tab (same page, two tabs)
**Expected:** One sign convention across the Money hub, as the scenario's visual contract requires.
**Actual:** They are exactly inverted. On **Rent & payments**, a charge is positive and a payment is
negative: `Aug 1, 2026 · Rent for August 2026 · Darius Clark · $1,075.00` and
`Aug 13, 2026 · Rent payment for August 2026 · Jordan Smith · −$925.00`. On **Activity**, the same
kind of rows read the other way: `8/1/2026 · Rent for August 2026 · Darius Clark · -$1,075.00` and
`8/13/2026 · Rent payment for August 2026 · Jordan Smith · $925.00`. My own QA rows show it on one
screen each: charge `-$25.00` on Activity, receipt `$1,200.00` on Activity.
**Evidence:** `~/Workbox/screenshots/e2e-s18-rent-payments-signs.png` and
`~/Workbox/screenshots/e2e-s18-activity-sign-inversion.png`.
**Code Reference:** `web/src/lib/components/accounting/PortfolioLeaseLedgerPanel.svelte:74` renders
the sign from direction (`row.direction === 'Debit' ? '' : '−'`), while
`web/src/routes/(protected)/accounting/+page.svelte:676-682` renders the server's already-signed
`amount` column verbatim (the server negates debits/expenses, e.g. `AccountingService.cs:880`
`Amount = -e.Amount`).
**Suggested Fix:** Drop the local sign logic at `PortfolioLeaseLedgerPanel.svelte:74` and render the
server-signed amount the way the Activity grid does, so "money out is negative" holds everywhere.
**Why This Matters:** Two tabs of the same screen tell the landlord opposite stories about whether a
line is money in or money out. That is exactly the confusion the "simple on the surface" design rule
exists to prevent, and it makes any hand-reconciliation wrong.

### BUG-4: Stop showing "Needs review: month totals need attention" on every month of every tenant ledger

**Severity:** Medium
**Location:** Unit → Money → Rent & payments (`/units/{id}?tab=money&view=tenant-account`), every month header
**Expected:** The red reconciliation warning marks a month whose ledger movement disagrees with its
journal — an actual problem worth the landlord's attention.
**Actual:** It shows on all 8 month blocks for the tenant I opened, including months that reconcile
perfectly (July 2026: Opening $0.00, Charges $1,075.00, Payments $1,075.00, Credits $0.00,
Closing $0.00). The cause is the `JournalCount = 0` arm of the predicate: the sandbox has no journal
entries at all, so every historical month trips it.
**Evidence:** `~/Workbox/screenshots/e2e-s18-tenant-ledger-needs-review.png`; 8 occurrences of the
banner in one page snapshot. Before I posted anything:
`psql … "SELECT \"SourceType\", count(*) FROM \"JournalEntries\" GROUP BY 1"` → `(0 rows)`. After I
posted one QA charge through the UI: `SELECT count(*) FROM "JournalEntries"` → `1`, i.e. the write
path *does* journal correctly and only the pre-existing/seeded history is unjournalled.
**Code Reference:** `RentalCommand.Api/Services/Domain/AccountingLedgerReadModelService.cs:1330` —
`(running."JournalCount" = 0 OR ABS(running."LedgerMovement" - running."JournalMovement") > 0.005) AS "NeedsReview"`.
Rendered at `web/src/lib/components/accounting/TenantLedgerMonth.svelte:78-86`.
**Suggested Fix:** Backfill journal entries for seeded/imported tenant ledger history (the sandbox
seeder is the concrete gap), or drop the `JournalCount = 0` arm so "no journal at all" is treated as
"nothing to compare", not as a mismatch.
**Why This Matters:** A permanent red error on every month of the money screen trains the landlord to
ignore the one warning that is supposed to mean "your books do not balance."

### BUG-5: Count money actually received, not only money allocated, in "Kept this month"

**Severity:** High
**Location:** `/accounting` header KPI "Kept this month" (money snapshot); same source feeds the
dashboard collection rollup
**Expected:** A $1,200.00 receipt increases the month's collected/kept cash by $1,200.00.
**Actual:** It increased by $1,100.00 — exactly the allocated portion. The remaining $100.00 landed
in "Unapplied credit" on the tenant account and is counted nowhere in the portfolio cash figure.
Reversing a $25.00 charge later *reduced* "Kept this month" by another $25.00 even though no money
left the landlord's hands.
**Evidence:** Header before the receipt: `Kept this month $15,046.25`. After a $1,200.00 receipt
(tenant ledger entry 315, `psql` confirms `PaymentReceipt | Credit | 1200.00`):
`Kept this month $16,146.25` (+$1,100.00), with the tenant card reading
`Balance due -$100.00` / `Unapplied credit $100.00`. After reversing the $25.00 QA charge:
`Kept this month $16,121.25`.
**Code Reference:** `RentalCommand.Api/Services/Domain/AccountingService.cs:1169-1215` —
`TenantIncomeQuery` is built `from allocation in _db.TenantLedgerAllocations join receipt in _db.TenantLedgerEntries …`,
so unallocated receipt amounts never enter the sum; consumed by the snapshot/rollup at `:184`, `:197`
and by the reports total at `:716-717`.
**Suggested Fix:** Source the collected-cash figure from `PaymentReceipt` ledger entries (net of
refunds/reversals) rather than from `TenantLedgerAllocations`, keeping allocations for the
receivables/aging view only.
**Why This Matters:** This is the headline number on the money screen. An overpaying tenant, a
prepayment, or a receipt landing before its charge all make the landlord's reported cash lower than
the cash in the bank — and reversing a charge appears to destroy money that was really collected.

### BUG-6: Close the "Fix this charge" dialog and confirm after the reversal has posted

**Severity:** Medium
**Location:** Unit → Money → row actions → "Reverse charge" → "Reverse the posted charge" → Continue
**Expected:** On success the dialog closes and a "Charge reversed." confirmation appears — that is
what the mutation's `onSuccess` is written to do.
**Actual:** The reversal posts, the ledger behind the dialog refreshes, but the dialog stays open on
the same three choices with no toast. A landlord's natural response is to press Continue again. I
did: no second entry was written (server-side duplicate protection held) and still no feedback of any
kind, success or error.
**Evidence:** After the first Continue,
`psql … "SELECT \"Id\",\"EntryType\",\"Amount\",\"ReversesEntryId\" FROM \"TenantLedgerEntries\" WHERE \"Id\">317"`
→ `318 | Reversal | 25.00 | 314`. Page state at that moment:
`{"dialogs":["Fix this charge\nAug 27, 2026 · QA-20260827-s18 utility reimb"],"toast":false}`.
After the second Continue the same query still returns exactly one row.
Screenshot: `~/Workbox/screenshots/e2e-s18-fix-charge-dialog-stuck.png`. Balance did move correctly
(`Balance due -$125.00`, `Unapplied credit $125.00`).
**Code Reference:** `web/src/lib/components/accounting/TenantLedgerPanel.svelte:310-313` calls
`closeFixCharge()` from `onSuccess`, but `closeFixCharge()` at `:221-226` is a no-op while
`reverseMutation.isPending` is still true — which it is at the moment `onSuccess` runs, so the dialog
is never dismissed.
**Suggested Fix:** In `onSuccess`, clear the dialog state directly (`fixTarget = null; fixChoice = null;`)
instead of routing through the `isPending`-guarded `closeFixCharge()`.
**Why This Matters:** The landlord cannot tell whether a correction to a real charge was applied, and
the obvious recovery (press it again) is a double-post attempt against money.

## Potential Issues (need investigation)

- **A typed minus sign is silently discarded in the charge amount.** Typing `-50` into the one-time
  charge amount leaves `50` in the field with no message; the live preview then read
  `New balance $1,125.00` (a $50 *increase*). A landlord entering a negative to mean "credit" gets the
  opposite. The currency mask at
  `web/src/lib/components/accounting/OneTimeChargeSheet.svelte:208` (`mask="currency"`) strips the
  sign. Whether this should be an inline error ("charges must be positive — use Give credit") or is
  acceptable sanitisation is a product call, so I have not filed it as a bug.
- **"Review payment allocation" omits the unapplied remainder.** For my $1,200.00 receipt the dialog
  listed allocation #155 `$1,075.00` and #156 `$25.00` and nothing else — no receipt total and no
  "$100.00 not yet applied" line. Component:
  `web/src/lib/components/accounting/TenantPaymentAllocationReview.svelte` (opened from
  `TenantLedgerPanel.svelte:514-522`). It is a real gap for a screen whose whole job is explaining
  where a payment went, but it may be intentionally scoped to allocations only.
- **Refunding a payment is unreachable from the tenant ledger.** The API has a full refund state
  machine (`RentalCommand.Data/Payments/TenantMoneyRules.cs:1212-1305`, including
  `AlreadyRefunded` duplicate protection), and `tenantMoney.refundPayment` exists in
  `web/src/lib/api/endpoints/tenant-money.ts`, but the payment row's action menu offers only
  "View detail" and "Review payment allocation". I could not exercise the duplicate-refund path
  through the UI at all. Possibly deliberate for this phase.
- **"Connect bank" gives no visible feedback for ~2.5s.** Clicking it produced no dialog, toast, or
  navigation in the page snapshot. Inspecting the DOM afterwards showed Plaid Link *had* opened
  (`iframe src=https://cdn.plaid.com/link/v2/stable/link.html?…token=link-`, `typeof window.Plaid === "object"`),
  so the feature works — the modal simply is not represented in the accessible page text. Flagging it
  only because a landlord on a slow link gets no "Connecting…" affordance from the page itself. Per
  the brief I stopped here and connected nothing. Note for the environment owner: Plaid **is**
  configured in dev user secrets (`Plaid:Environment = sandbox`), so this button reaches the real
  Plaid sandbox API — the banking page's "Bank connections are ready" state is accurate here.

## Observations

- **`Balance due -$125.00` reads badly for the primary user.** When a tenant is in credit the card
  shows a negative "Balance due" next to `Unapplied credit $125.00`, which looks like the same money
  counted twice with a sign error. "Credit balance $125.00" (or `$0.00` due, with the credit called
  out) would match how a landlord thinks. `TenantLedgerPanel.svelte:527-529`.
- **Currency precision is inconsistent between money screens.** "Who's behind" renders whole dollars
  (`$1,050 · 26 days late`, `5 rentals behind, owing $5,175`) while the accounting header and every
  ledger render cents (`$5,175.00`). Same number, two formats, one hop apart.
- **Returned deposits lose the tenant's name.** Three rows on `/deposits` read `Tenant not listed`
  with a real received amount ($1,100.00 / $850.00 / $1,075.00). For a record whose purpose is
  proving what was returned to whom, the "whom" is the part that went missing.
- **The Activity grid's "Status" column shows `Debit` / `Credit` for tenant-ledger rows.** Those are
  directions, not statuses, and they sit in the same column as real statuses (`Paid`, `Pending`).
- **The portfolio ledger has no result count.** `/accounting` → Rent & payments pages with bare
  Previous/Next and no `1–20 of N`, unlike the Activity grid (`1–20 of 353, Page 1 of 18`) and the
  deposits grid (`1–20 of 20`), so the landlord cannot tell how deep the list goes.
- **Server-side aggregation held up everywhere I probed.** Past-due count/total, deposit balances,
  the transactions grid, and the deposits grid all recompute their `totalCount` when filtered
  (Activity `kind=Expense` → `1–20 of 35`; deposits `status=Returned` → `1–3 of 3`), and the
  past-due list, KPI, and header all agree. No in-memory-aggregation smell surfaced in this run.
- **No console errors or 4xx/5xx responses were recorded during the entire session** (the driver
  logged console errors, page errors, dialogs, and every HTTP response ≥ 400; nothing fired).

## What Was Tested

Login as `admin@rentalcommand.local` (Fill dev login → Sign In), SANDBOX portfolio, viewport
1710x990. Every record I created is prefixed `QA-20260827-s18`.

1. `/accounting` — Rent & payments tab: read the portfolio ledger, checked row deep-links
   (→ BUG-1), noted the sign convention and the missing result count.
2. `/accounting/past-due` — reconciled `5 rentals behind, owing $5,175` against the header KPI
   `Past due $5,175.00 (5)` and against the five listed amounts (1050+1100+975+975+1075). ✔
3. `/units/19?tab=rent` — confirmed the invalid tab falls back to Summary (BUG-1).
4. `/units/19?tab=money&view=tenant-account` (Darius Clark, tenant account 17) — read the ledger,
   counted 8 "Needs review" banners on months that reconcile (BUG-4), cross-checked
   `JournalEntries` in the database before and after posting.
5. **Add charge** edge values: `-50` → silently becomes `50` (potential issue); `0` → preview stays at
   the current balance; `12345678901234.567` → masked to `.56`; missing income category → blocked with
   "Choose an income category." Then posted the real QA charge: $25.00, Utility Reimbursement Income,
   description `QA-20260827-s18 utility reimbursement` → ledger entry 314, balance $1,075 → $1,100.
6. `/tenant-accounts/17/entries/314` — entry detail persisted with amount, date, description,
   property/unit/tenant, posted timestamp, and the accounting-impact block.
7. **Record payment** $1,200.00, method Check, reference `QA-20260827-s18-REF1`, apply to oldest →
   ledger entry 315. Balance `-$100.00`, `Unapplied credit $100.00`; past-due dropped to
   `4 rentals behind, owing $4,100`; header `Kept this month` rose only $1,100 (BUG-5).
8. **Review payment allocation** on entry 315 — allocations #155 $1,075.00 and #156 $25.00, no
   unapplied line (potential issue).
9. **Reverse charge** on entry 314 → reversal entry 318 posted, balance `-$125.00` /
   `Unapplied credit $125.00`, dialog stuck open with no confirmation (BUG-6); a second Continue
   created no duplicate.
10. `/deposits/17` — over-deduction $2,000.00 against $1,075.00 held was blocked client-side
    ("The deduction cannot exceed the held balance."); a real $50.00 QA deduction reconciled to
    Held $1,025.00 / Received $1,075.00 / Deductions $50.00, matching the `/deposits` list row.
11. `/deposits` — paging `1–20 of 20`; status filter `Returned` → `1–3 of 3` with
    `?sort=-createdAtUtc&status=Returned`.
12. `/banking` — empty-state counters, "Connect bank" (Plaid Link opened in an iframe; stopped there,
    nothing connected); `/plaid/auth` returned safely to `/banking` with no error.
13. `/accounting?tab=activity` — paging `1–20 of 353` / 18 pages; type filter `kind=Expense` →
    `1–20 of 35`; observed the inverted signs (BUG-3).
14. **Add expense**: `0` blocked ("Amount must be greater than zero"); `12.34` with the default
    "No property" saved successfully and then appeared nowhere (BUG-2) — database row 36 proves it
    exists.
15. **Recurring charge**: start 09/01/2026 with end 08/01/2026 was correctly blocked with
    "End date must be on or after the start date."; cancelled without saving.

**Records left behind (all additive, all prefixed):** tenant ledger entries 314 (reversed), 315,
316, 317, 318 on tenant account 17; security deposit deduction $50.00 on deposit 17; expense 36
(`QA-20260827-s18 expense test`, $12.34, no property — kept deliberately as BUG-2 evidence). No
existing row was edited or deleted, no bank or payment provider was connected, and no money moved.

Browser cleanup: stopped e2e-s18 (session process + Chromium tree)
