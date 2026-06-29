# Exploratory Test Report: Money & Leases (lease lifecycle + payments + accounting)
Date: 2026-06-28
Tester: tester1
Duration: ~1h45m (deep code read + live browser session)

## Scenario
Create/advance a lease, record rent payments against it, and confirm every number
reconciles across the Unit, Lease, Payment, and Accounting views.

## Summary
I created an isolated slice (tenant 264 "QA T1 213440", property 134 "QA-T1-213440",
unit 222 "#101", **Active lease 237 "T1-213440"**) and drove rent payments through it as
a real landlord. A clean **Paid** payment reconciles perfectly across all four views. The
moment a **Partial** payment is involved, money stops reconciling: the **Unit Rent tab
overstates the outstanding balance by the amount already collected** (it counts a partial at
its full amount), disagreeing with the Lease ledger Balance and the Accounting Outstanding
KPI — both of which correctly show only the remainder. The Lease ledger's line items also
fail to foot to their own Balance for a partial. Separately, the Unit's quick "Post payment"
form offers a **Partial** status it can't actually submit, and the **`/leases/{id}/signature-queue`
endpoint returns HTTP 500 for every lease that hasn't been sent for e-signature** (most leases).
All input validations (date range, $0/negative, partial-paid invariant) correctly return clean
400s — those are solid.

Severity tally: **2 High, 2 Medium, 1 Low.**

## Bugs Found

### BUG-1: Unit "Rent" tab overstates the outstanding balance for a partial payment (money doesn't reconcile across views)
**Severity:** High
**Location:** Unit detail → Rent tab ("Outstanding balance"), plus the header "Due · $X" and "Collect $X" CTA. `https://localhost:5667/units/222?tab=rent`
**Expected:** A partial payment only still owes its *remainder* (`Amount − AmountPaid`). With a $1,000 rent charge that has $700 collected, the unit should show **$300** outstanding — matching the Lease ledger Balance and the Accounting Outstanding KPI.
**Actual:** The Unit Rent tab shows **Outstanding balance $1,000.00** — it counts the partial at its *full* `Amount`, ignoring the collected `AmountPaid`. The discrepancy equals the collected amount and grows as more is collected:
- Lease 237 has: rent $1,000 **Paid**, plus rent $1,000 **Partial** with `AmountPaid=$700` (remainder owed = $300).
- **Lease ledger** (`/leases/237/ledger`): Charged $2,000 · Paid $1,700 · **Balance $300** ✓
- **Accounting summary** Outstanding rose by exactly **+$600** when the partial was first added at $400 collected (\$4,536.25 → \$5,136.25) ✓ (uses the remainder)
- **Accounting reports** property 134: income $1,400, overdue $0 ✓
- **Unit Rent tab** `outstandingRentBalance`: **$1,000** ✗ (was $1,000 at \$400 collected and still $1,000 at \$700 collected — completely ignores collection)
**Evidence:** Screenshot `output/playwright/tester1-unit-rent-outstanding-1000.png` (Outstanding $1,000.00, "Due · $1,000.00", "Collect $1,000.00"). API cross-check: unit dashboard `outstandingRentBalance:1000.0` while `/leases/237/ledger` returns `balance:300.0` and `/accounting/summary` Outstanding moved only by the remainder.
**Code Reference:** `RentalCommand.Api/Services/Domain/UnitDashboardService.cs:129-131` — `Outstanding = g.Sum(p => (Scheduled||Partial||Late) ? p.Amount : 0m)` sums the **full `p.Amount`** for a Partial. Compare the correct treatment in `RentalCommand.Api/Services/Domain/AccountingService.cs:104-107` (`Partial ? p.Amount - (p.AmountPaid ?? 0m)`) and the lease ledger in `LeaseService.cs:687-692`.
**Suggested Fix:** In `UnitDashboardService` change the Partial branch to owe only the remainder, mirroring AccountingService: `Outstanding = g.Sum(p => p.Status == Scheduled || p.Status == Late ? p.Amount : p.Status == Partial ? p.Amount - (p.AmountPaid ?? 0m) : 0m)`.
**Why This Matters:** The Unit Rent tab is the primary per-unit money surface for a non-technical landlord. It tells them to "Collect $1,000" from a tenant who actually owes $300, and contradicts the lease ledger and the books. Acting on it (e.g. demanding the wrong amount, or a late-notice) is a real, money-wrong, tenant-facing error.

### BUG-2: `GET /api/v1/leases/{id}/signature-queue` returns HTTP 500 for every lease that has not been sent for e-signature
**Severity:** High
**Location:** API `GET /api/v1/leases/{id}/signature-queue`; surfaced on the Lease detail "Agreement & Signing" surface and the Unit Lease/Rent tabs (repeated red console 500s).
**Expected:** A lease with no e-sign envelope yet should return `{ leaseId, items: [] }` (200) — exactly like a sent lease with an empty queue.
**Actual:** Returns `{"title":"Server error","status":500,"detail":"An unexpected database error occurred."}` for any lease whose `EsignEnvelopeId` is null. Reproduced deterministically: leases **2, 4, 5, 19, and my new 237 → 500**; leases **1 and 3 (esign "Sent", envelope present) → 200**. Brand-new leases are the common case, so most leases 500. Three console errors fire per affected page load:
```
[ERROR] Failed to load resource: the server responded with a status of 500 ()
  @ https://localhost:5667/api/v1/leases/237/signature-queue:0   (x3)
```
**Evidence:** `console-2026-06-29T01-37-49-604Z.log` (and 01-41, 01-45). Out-of-band curl: `…/leases/237/signature-queue → HTTP 500`; `…/leases/1/signature-queue → {"leaseId":1,"items":[]}` (200). The discriminator is `signature-status.envelopeId`: null → 500, present → 200.
**Code Reference:** `RentalCommand.Api/Services/Domain/LeaseEsignService.cs:566` — the Npgsql raw query interpolates a C# `null` into `AND ({signatureRequestIdText} IS NULL OR …)`. When `signatureRequestIdText` is null (no envelope → `currentSignatureRequestId` stays null at line 224-232), Npgsql binds a typeless NULL parameter and Postgres throws *"could not determine data type of parameter"*. When an envelope exists the parameter is a real string, so it's typed and the query succeeds.
**Suggested Fix:** Don't pass an untyped null into SQL. Simplest: when `signatureRequestId` is null, build the query without that clause (omit the `AND (… IS NULL OR …)` line); or cast the parameter so Postgres can type it, e.g. `AND ({signatureRequestIdText}::text IS NULL OR "Payload" ->> 'signatureRequestId' = {signatureRequestIdText})`.
**Why This Matters:** It's a hard server 500 on a core lease endpoint for the majority of leases, firing on every lease/unit detail view. It spams the server error log, breaks the signature-queue UI for un-sent leases, and is the kind of noise that masks real incidents.

### BUG-3: Unit "Rent" quick-post form offers a "Partial" status it cannot submit (silent failure, no visible error)
**Severity:** Medium
**Location:** Unit detail → Rent tab → "Post payment" inline form. `https://localhost:5667/units/222?tab=rent`
**Expected:** If "Partial" is offered as a status, the form must expose an "Amount paid" field (the server + zod schema require `0 < amountPaid < amount` for a Partial), or it should surface a visible error explaining what's missing.
**Actual:** Selecting status **Partial** renders **no "Amount paid" field** (the quick form only has Amount/Date/Type/Status/Method/Reference/Notes). Clicking "Post payment" does nothing visible — no toast, no inline error, the form stays open and **no payment is created** (verified: lease 237 payment count stayed 0). The blocking zod issue is keyed to `amountPaid`, a field the quick form never renders, so the error is invisible.
**Evidence:** Snapshot after submitting Partial showed the form still open with the same "Post payment" button and no error node; API confirmed `payment count: 0` for lease 237 immediately after. (The full New Payment dialog on the Money page *does* correctly show "Amount paid (so far)" for Partial — `accounting/+page.svelte:1247-1257` — so the gap is specific to the Rent tab quick form.)
**Code Reference:** `web/src/lib/components/unit/tabs/RentTab.svelte:48` lists `'Partial'` in `PAYMENT_STATUSES`, but `emptyCreate()` (lines 104-112) has no `amountPaid` and the template (lines 196-251) renders no such field; `submitCreate` (lines 151-164) calls `parseForm(paymentSchema, …)`, and `paymentSchema.superRefine` (`web/src/lib/schemas/index.ts:226-245`) raises a hidden `amountPaid` error.
**Suggested Fix:** Either drop `'Partial'` from the Rent quick form's `PAYMENT_STATUSES` (keep it to the Money-page dialog that has the field), or add a conditional "Amount paid" input shown when `createForm.status === 'Partial'` (mirroring `accounting/+page.svelte:1247`).
**Why This Matters:** A landlord recording a tenant's partial rent payment from the unit screen clicks "Post payment" and nothing happens, with no explanation. They can't tell whether it saved; the natural next step is to click again or assume the app is broken. Silent dead-ends erode trust in the "simple on the surface" promise.

### BUG-4: Lease ledger line items don't foot to the Balance for a partial payment (collected amount is invisible)
**Severity:** Medium
**Location:** Lease detail → Ledger tab ("Account History"). `https://localhost:5667/leases/237?tab=ledger`
**Expected:** The visible ledger entries should reconcile with the headline Charged/Paid/Balance. For a partial, the $X already collected should appear somewhere (a payment/credit line, or a net charge line), so a landlord auditing line-by-line can see where the money went.
**Actual:** A Partial charge renders as a single **"Charge · Partial · −$1,000.00"** line at its *full* amount, and there is **no line for the collected portion**. The collected amount lands only in the "Paid" total. So the visible entries don't add up to the Balance:
- Headline: Charged **$2,000** · Paid **$1,700** · Balance **$300** ("QA T1 213440 still owes $300.00")
- Visible entries: "Rent · Charge · Partial · **−$1,000.00**" + "Rent · Payment · Paid · **+$1,000.00**" = **$0**, not −$300/−$600.
- The partial's explanation even says "(partially paid, balance still owed)", so the data is known — it's just not shown as a line.
**Evidence:** Screenshot `output/playwright/tester1-lease-ledger-partial.png`. API `/leases/237/ledger` → `entries:[{type:Charge,status:Partial,amount:-1000}, {type:Payment,status:Paid,amount:1000}]` with `totalPaid:1700, balance:300`.
**Code Reference:** `RentalCommand.Api/Services/Domain/LeaseService.cs:640-641` builds each ledger entry as `isCollected = (Status==Paid); signedAmount = isCollected ? Amount : -Amount` — a Partial is treated wholly as a charge (`-Amount`) with no companion credit, while the totals at `LeaseService.cs:687-692` correctly add the partial's `AmountPaid` to Paid. Rendered at `web/src/lib/components/records/LeaseDetail.svelte:1331`.
**Suggested Fix:** For a Partial entry, either show the charge net of collection (`-(Amount − AmountPaid)`) or emit two lines (a full `-Amount` charge plus a `+AmountPaid` payment) so the entries sum to the Balance.
**Why This Matters:** The ledger is the landlord's plain-English account history. When the lines don't foot to the balance it prints, the one tool meant to explain "why does this tenant owe $300" actively undermines confidence in the number.

### BUG-5: "Total Collected" KPI tooltip claims it includes deposits, but security-deposit payments are excluded
**Severity:** Low
**Location:** Money page → KPI strip → "Total Collected" card tooltip. `https://localhost:5667/accounting`
**Expected:** The tooltip text and the computed figure should agree.
**Actual:** The tooltip reads **"All payments received — rent, deposits, and fees"**, but the Collected figure **excludes** `PaymentType.SecurityDeposit`. Recording a $500 **SecurityDeposit Paid** payment on lease 237 left Total Collected unchanged at **$1,275,903.79** (before and after).
**Evidence:** API `/accounting/summary` `payments.collected` = `1275903.79` before and after creating payment 3542 (SecurityDeposit, Paid, $500).
**Code Reference:** Tooltip at `web/src/routes/(protected)/accounting/+page.svelte:834`; the aggregation excludes deposits at `RentalCommand.Api/Services/Domain/AccountingService.cs:81-82` (`p.PaymentType != PaymentType.SecurityDeposit`).
**Suggested Fix:** Excluding deposits from collected income is defensible (deposits are liabilities, tracked separately as holdings) — so fix the copy: change the tooltip to "Rent and fees received (security deposits tracked separately under Deposits)."
**Why This Matters:** Minor, but a landlord who reads the tooltip and then can't find a deposit they collected in "Total Collected" will distrust the number or think the deposit wasn't recorded.

## Potential Issues (need investigation)
- **Unit outstanding ignores lease currency/recency.** `UnitDashboardService.cs:52-56` builds `leaseIds` from **all** leases on the unit (any status) and `:129-134` sums owed rows across all of them with **no** `ForCurrentLeaseAttention(now)` filter — unlike AccountingService, which scopes receivables to current leases. A unit whose prior lease ended (Expired/Terminated) while still carrying Scheduled/Late charges would have those leftover charges counted in the unit's "Outstanding balance," diverging from the Accounting KPI. Not reproduced here (my unit has only one Active lease), but it's the same surface as BUG-1 and worth a combined fix.
- **KPI cards are all-time and don't respond to the ledger date filter.** The Money page's From/To date filters re-query the transactions grid (DB view, correctly server-side) but the four KPI cards (Collected/Outstanding/Overdue/Expenses) come from `/accounting/summary`, which takes no date range. Filtering the ledger to "July" leaves the KPIs showing all-time totals. This is by design (no in-memory aggregation smell found — `/summary` and the ledger view both aggregate DB-side), but a landlord may read the filtered grid and the unfiltered KPIs as one period and misread them.

## Observations
- **Clean Paid payment reconciles perfectly.** Posting a $1,000 Paid rent payment showed Unit Outstanding $0.00, Lease ledger Charged $1,000 / Paid $1,000 / Balance $0.00 (single Payment line), and the Money ledger row "Rent − QA T1 213440 · $1,000.00". No discrepancy.
- **List updates without a manual refresh.** The posted payment appeared in the Rent tab list immediately (optimistic prepend + query invalidation; I did not isolate a pure SignalR push).
- **Input validation is solid.** Clean 400s for: lease end-before-start ("The lease start date must be before its end date."), payment `amount` 0 and −50 ("must be between 0.01 and 99999999"), and Partial `amountPaid ≥ amount` ("requires an amount paid greater than 0 and less than the full amount"). The lease picker is portfolio-scoped (no cross-tenant leases offered).
- **Currency formatting is consistent** ($1,234.56) across the Unit, Lease, Payment, and Money views.

## What Was Tested
1. Logged in as `admin@rentalcommand.local` (dev login). Portfolio 1 is in "example data" / sandbox mode.
2. Created scaffolding via API (non-money setup): tenant 264 "QA T1 213440", property 134 "QA-T1-213440", unit 222 "#101" (Vacant).
3. **Created lease 237 "T1-213440" via the UI** (New Lease dialog): property/unit/tenant pickers, dates 2026-06-01 → 2027-06-01, rent $1,000, deposit $1,000, due day 1, status **Active**, rent-tracking "Start from today". Verified: unit flipped to Occupied, lease is the unit's current lease, 0 auto-charges (ForwardOnly + due-day-1 → next due in the future), Rent tab usable.
4. **Rent tab quick-post → Partial (BUG-3):** set status Partial, amount $1,000, clicked Post payment → no field, no error, no payment created (count stayed 0).
5. **Posted a $1,000 Paid rent payment** via the Rent tab; confirmed it appears immediately and reconciles (Unit $0 / ledger Balance $0).
6. **Created a $1,000 Partial payment (AmountPaid $400)** via the Money page New Payment dialog; compared Unit ($1,000) vs Lease ledger ($600) vs Accounting Outstanding (+$600) → **BUG-1**; inspected ledger line items → **BUG-4**.
7. **Edited the partial's Amount paid $400 → $700** via the Payment detail page; confirmed it persisted (`amountPaid:700`) and propagated to the ledger (Balance $300) — while the Unit still showed $1,000 (BUG-1 worsens with collection).
8. **Validation edges** (API probes on my own records): lease end<start, payment $0, payment −50, Partial paid≥amount → all clean 400s.
9. **Deposit exclusion (BUG-5):** created a $500 SecurityDeposit Paid payment; Total Collected unchanged.
10. **signature-queue 500 (BUG-2):** observed in console on unit/lease pages; reproduced via curl across leases 1–5, 19, 237 and correlated with `signature-status.envelopeId`.

### Test data left in portfolio 1 (sandbox)
Tenant 264, Property 134 (QA-T1-213440), Unit 222, Lease 237 (T1-213440), Payments 3540 (Rent Paid $1,000), 3541 (Rent Partial $1,000 / $700), 3542 (SecurityDeposit Paid $500). All marked `QA-T1-213440`. No seed data was modified or deleted.
