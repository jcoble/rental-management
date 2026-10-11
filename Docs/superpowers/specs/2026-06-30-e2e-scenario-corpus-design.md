# E2E Scenario Corpus — Design (Phase 1)

**Author:** Scenario Architect (SA)
**Date:** 2026-06-30
**Status:** Phase 1 — design + grounding + proof set (NOT yet mass-generated)
**Scope:** Web app only (mobile ignored for the test path).

## Purpose

Design a fictional-but-fully-**reconcilable** rental-management business, its **2 years of
onboarding history**, and a **1-year live-operations story**, so we can replay the whole
thing through the real Rental Command web app under a **controllable master clock** and, at
year-end, diff the **app's own reports** against an **independently hand-kept ground-truth
ledger**. Everything here is representable in the app as it stands today; where it is not, it
is flagged as a **GAP**, never fabricated.

This document is the contract for Phase-2 mass generation. It contains:

1. Company profile
2. Portfolio (properties, owner entities, mortgages)
3. Tenants & leases
4. The 2-year onboarding import (what's scanned vs hand-entered vs worker-generated)
5. The 1-year operations calendar (keyed to simulated dates + worker dependencies)
6. Ground-truth ledger design + the exact app reports we reconcile against
7. Document/scan generation manifest (tooling, counts, formats, paths, footprint)
8. Grounding appendix — the app facts this all rests on (from source)
9. Gaps / feature-requests
10. Open questions + Phase-2 batch plan

---

## 0. Reconciliation model (read this first — it shapes everything)

The single most important grounding fact (from reading the money services): **there is no one
"income" number in the app.** At least **12 different report definitions of "income"** and
**7 of "expense"** coexist, each with its own PaymentType set, status filter, date field, and
scope (see §8.3). Consequences the corpus must respect:

- **Ground truth is an _event log_, not a set of pre-aggregated totals.** We record each money
  event **once**, with enough attributes (type, status, due date, paid date, method, category,
  property, lease, escrow-covered flag, amount, amount_paid) that we can **derive any of the 12
  income / 7 expense views** by filtering. Expected report figures are computed views over the
  log, matching each report's exact predicate — never a single "income" we hope all reports show.
- **Partial payments** are first-class: a `Partial` row contributes `AmountPaid` to cash reports
  and `Amount − AmountPaid` to receivable/aging reports. The corpus deliberately includes them.
- **Escrowed taxes/insurance** are the subtlest rule (§8.5). For a mortgaged property whose loan
  `EscrowCoversTaxes/Insurance`, we DO still record `Taxes`/`Insurance` expenses (dated on the
  servicer's disbursement) so **Schedule E can deduct them** — but we EXPECT the app's **True
  Cash Flow / NOI** report to **exclude** them from opex (escrow cash is already inside debt
  service). Same dollars, deliberately different across the two reports; ground truth encodes both.
- **Mortgage interest & depreciation are never entered as expenses.** Schedule E derives
  interest from `LoanPayment.InterestAmount` and depreciation from property basis; a manual
  `MortgageInterest`/`Depreciation` expense on a property with a loan is dropped by Schedule E.
  Ground truth computes both from formula (§8.4, §8.6).

### Anchor timeline (all simulated dates)

| Milestone | Sim date | Meaning |
|---|---|---|
| Business founded | 2015 | Owner has operated for ~10 yrs; oldest acquisition 2015-06. |
| **History window** | **2023-01-01 → 2024-12-31** | 2 full tax years brought in at onboarding. |
| **Onboarding / books-start (T0)** | **2025-01-01** | Landlord adopts the app; clock set here. |
| **Live operations year** | **2025-01-01 → 2025-12-31** | Replayed week-by-week through the app with worker fires. |
| **Year-end reconciliation** | **2025-12-31** | Diff app reports vs ground truth for CY2025 (primary), spot-check CY2023/24. |

Rationale for calendar-year alignment: Schedule E, owner statements, year-end packet, and
Vendor-1099 are all **per calendar year**, so a full-calendar sim year gives clean tax-year
reconciliation. CY2025 is the **reconciliation spine** (fully app-generated). CY2023–24 are
imported and spot-checked.

---

## 1. Company profile

- **Operating entity / brand:** **Okafor Property Group** (the management company name on the
  portfolio; `Portfolio.ManagementCompanyName`).
- **Owner-operator:** **Dana R. Okafor** — `UserRole.Admin`. Runs the business from a phone.
- **Team members** (map to `UserRole`; users are hand-created, not scannable):
  | Person | Role | Function in the story |
  |---|---|---|
  | Dana R. Okafor | `Admin` | Owner-operator; approves drafts, sees everything. |
  | Priya Nair | `Manager` | Day-to-day ops; does most scanning/confirming and expense entry. |
  | Tomás Reyes | `Agent` | Leasing: showings, applications, screenings, move-ins. |
  | Marcus Bell | `Owner` | Silent co-owner of the Bell-Okafor properties; portal/owner-statement recipient only. |
  | (each tenant) | `Tenant` | Portal login: pays rent, files maintenance, messages. |
- **Business age / market:** Founded 2015; market is **Springfield / Clark County, Ohio**
  (landlord-friendly, no rent control, mid-market rents $825–$1,600). Ohio → clean Schedule E,
  27.5-yr residential depreciation, county property tax, no local rent-control edge cases.
- **Portfolio timezone:** `America/New_York` (Ohio is Eastern) — matters for the daily-briefing
  worker's local-hour gate and rent-due-day arithmetic (§8.7).

---

## 2. Portfolio — 13 properties, 21 units, 11 mortgages, 4 owner entities

### 2.1 Owner entities (`OwnerEntity`, hand-entered)

| Code | Name | Type | Mgmt fee % Dana charges | Owns |
|---|---|---|---|---|
| OE-LLC | **Okafor Rentals LLC** | `LLC` (IsPrimary=true) | 0% (self-managed) | 9 properties |
| OE-BELL | **Bell-Okafor Holdings LLC** | `LLC` | **8%** | 2 properties (co-owned w/ Marcus) |
| OE-TRUST | **Okafor Family Trust** | `Trust` | **8%** | 1 property |
| OE-PERS | **Dana R. Okafor (personal)** | `Person` | 0% | 1 property (pre-LLC 2015 buy) |

The 8% fee on OE-BELL/OE-TRUST properties is what makes **owner statements** non-trivial:
`mgmtFee = round(rentIncomePaid × 8% , 2)`, `net = income − expenses − mgmtFee` (§8.4). On the
0%-fee properties the fee line is zero.

### 2.2 Properties (`Property`) + mortgages (`Loan`)

Depreciation basis = `PurchasePrice − LandValue`, straight-line 27.5 yr, mid-month first year
(§8.6). Loan P&I/escrow drive `LoanPayment` amortization (§8.4). Rates reflect the acquisition
year's environment (2015-era ~4.5%, 2020–21 lows ~3.25–3.75%, 2022–24 highs 5.5–6.75%) so
`MortgageInterest` deductions vary realistically across the portfolio.

| Code | Address | Type | Owner | Acq. | Price / Land | Units × rent | Loan (lender, orig, rate, P&I, escrow, covers) |
|---|---|---|---|---|---|---|---|
| P01 | 412 Maple St | SingleFamily | OE-LLC | 2016-04 | 128k / 22k | 1 × 1,250 | Prairie State Bank, 96k @ 4.25%, P&I 472.06, esc 310, T+I |
| P02 | 418-420 Vine St | MultiFamily(duplex) | OE-LLC | 2017-08 | 175k / 28k | 2 × (950, 975) | Prairie State Bank, 140k @ 4.50%, P&I 709.30, esc 360, T+I |
| P03 | 77 Birch Ave | SingleFamily | OE-TRUST | 2015-06 | 110k / 20k | 1 × 1,150 | **none (paid off)** → pays Taxes+Insurance directly |
| P04 | 1203 Oakwood Dr | SingleFamily | OE-LLC | 2019-03 | 158k / 30k | 1 × 1,450 | Clark County CU, 126k @ 3.75%, P&I 583.57, esc 395, T+I |
| P05 | 305 Elm Court | MultiFamily(triplex) | OE-BELL | 2018-11 | 245k / 35k | 3 × 850 | Prairie State Bank, 196k @ 4.75%, P&I 1,022.53, esc 520, T+I |
| P06 | 88 Cedar Ln | SingleFamily | OE-LLC | 2020-07 | 172k / 32k | 1 × 1,395 | Clark County CU, 137.6k @ 3.25%, P&I 598.86, esc 410, T+I |
| P07 | 210 Willow Way | MultiFamily(duplex) | OE-BELL | 2021-05 | 210k / 34k | 2 × (1,100, 1,150) | Fifth Third, 168k @ 3.50%, P&I 754.40, esc 470, T+I |
| P08 | 540 Sycamore St | SingleFamily | OE-PERS | 2015-09 | 99k / 18k | 1 × 1,050 | **none (paid off)** → pays Taxes+Insurance directly |
| P09 | 1450 Highland Blvd | MultiFamily(fourplex) | OE-LLC | 2022-02 | 340k / 48k | 4 × 825 | Fifth Third, 272k @ 5.50%, P&I 1,544.63, esc 720, T+I |
| P10 | 63 Chestnut St | SingleFamily | OE-LLC | 2023-06 | 185k / 33k | 1 × 1,525 | Clark County CU, 148k @ 6.50%, P&I 935.28, esc 430, T+I |
| P11 | 9 Aspen Circle | Condo | OE-LLC | 2019-10 | 95k / 0 | 1 × 1,100 | Prairie State Bank, 76k @ 4.00%, P&I 362.90, esc 180, **T only** (HOA sep.) |
| P12 | 1155 Riverside Dr | SingleFamily | OE-LLC | 2024-01 | 198k / 36k | 1 × 1,600 | Fifth Third, 158.4k @ 6.75%, P&I 1,027.19, esc 460, T+I |
| P13 | 330 Walnut St | MultiFamily(duplex) | OE-LLC | 2018-03 | 165k / 27k | 2 × (925, 940) | Clark County CU, 132k @ 4.50%, P&I 668.79, esc 350, T+I |

**Units total: 21.** Condo P11 has `LandValue = 0` (land is HOA common area → full price
depreciates; realistic condo case). P03 & P08 are **paid-off** → they pay property tax and
insurance as **direct expenses** (Schedule E `Taxes`/`Insurance`), the clean non-escrow case.
All escrowed loans set `EscrowCoversTaxes = EscrowCoversInsurance = true` except P11 (taxes
only; HOA + insurance billed separately).

> P&I figures above are illustrative and will be **recomputed exactly** in Phase 2 by
> `AmortizationCalculator` semantics (interest = balance×rate/12, principal = P&I−interest) so
> the amortization chain and `MortgageInterest` totals reconcile to the cent.

---

## 3. Tenants & leases

### 3.1 Design goals baked into the lease set

- **21 occupied units at T0** (2025-01-01), each with an active lease (`LeaseStatus.Active`).
- **Staggered `EndDate`s across CY2025** so `LeaseExpiryReminderWorker` (60-day default) and the
  notice-autopilot renewal/M2M/move-out drafts (75/75/30-day windows, §8.7) actually fire month
  after month through the sim year.
- **Rent-due-day variety:** most `RentDueDay = 1`; a few on the 5th/15th to exercise the mid-month
  due-date + late-fee-grace arithmetic.
- **Payment-method variety** (`Payment.Method` free string): Check, ACH, Cash, MoneyOrder, Zelle,
  plus 2 leases on **Stripe autopay** (`AutopayEnrollment`).
- **Security deposits** (`SecurityDepositHolding`, one per active lease, hand-entered), typically
  = one month's rent.
- **Co-tenants** (`LeaseTenant`) on 3 leases (roommates / married couple) — the primary tenant is
  on `Lease.TenantId`, the co-tenant via the join.

### 3.2 Lease end-date ladder (drives the 2025 notice/renewal calendar)

> ⚠️ **STALE lease numbering in this ladder — defer to the authoritative catalog.** The prose below (and
> the "Codes L20/L21/L22 = the new-tenant leases created live in 2025" line) drifted from the frozen
> `scenario.json`: the CY2025 lease-ups are **L08 (P05·3)** and **L16 (P09·4)**, **L20** is the P04
> turnover (replaces L05), and **L21/L22** are *existing* P13 duplex tenants active at T0 (rent back to
> 2023). Authoritative lease map + tester scenarios: `e2e/corpus/scenarios/README.md`.

| Lease | Property·Unit | Tenant | Term | EndDate | Outcome in CY2025 | Method |
|---|---|---|---|---|---|---|
| L01 | P01 | J. Whitfield | 12mo | 2025-03-31 | **Renewal** (+3% → 1,288) | Check (scannable) |
| L02 | P02·A | R. Delgado | 12mo | 2025-04-30 | **Month-to-month** conversion | ACH |
| L03 | P02·B | S. Klein | 12mo | 2025-09-30 | Renewal | Zelle |
| L04 | P03 | The Abernathys (co-tenant) | 12mo | 2025-06-30 | Renewal | Check |
| L05 | P04 | M. Osei | 12mo | 2025-05-31 | **Non-renewal** (landlord) → move-out 05-31, turnover, **L20** new tenant 07-01 | Cash |
| L06 | P05·1 | D. Ferraro | 12mo | 2025-07-31 | Renewal | ACH |
| L07 | P05·2 | T. Nakamura | 12mo | 2025-10-31 | Renewal | MoneyOrder (scannable) |
| L08 | P05·3 | (vacant at T0) | — | — | **Vacant → lease-up** (application→screening→L21 on 03-01) | — |
| L09 | P06 | B. Kowalski | 12mo | 2025-08-31 | Renewal | Autopay (Stripe) |
| L10 | P07·A | E. Santos | 12mo | 2025-11-30 | Renewal | ACH |
| L11 | P07·B | H. Pham (co-tenant) | 12mo | 2025-02-28 | Renewal (+3%) | Check (scannable) |
| L12 | P08 | C. Reynolds | 12mo | 2025-12-31 | Renewal | Zelle |
| L13 | P09·1 | A. Brooks | 12mo | 2025-04-30 | Renewal | Cash |
| L14 | P09·2 | L. Turner | 12mo | 2025-07-31 | **Delinquent** → partials, late fees, escalating late-rent notices; cures by 09 | Check |
| L15 | P09·3 | G. Iverson | 12mo | 2025-09-30 | Renewal | ACH |
| L16 | P09·4 | (vacant at T0) | — | — | **Vacant → lease-up** (03-15 move-in, L22) | — |
| L17 | P10 | N. Adeyemi | 12mo | 2025-06-30 | Renewal | Autopay (Stripe) |
| L18 | P11 | W. Foster | 12mo | 2025-10-31 | Renewal | Zelle |
| L19 | P12 | K. Malloy (co-tenant) | 12mo | 2025-12-31 | Renewal | ACH |
| — | P13·A | O. Ricci | 12mo | 2025-08-31 | Renewal | Check (scannable) |
| — | P13·B | V. Sokolov | 12mo | 2025-05-31 | Renewal | MoneyOrder |

That's **19 active leases at T0 + 2 vacant units (P05·3, P09·4)** leasing up in Q1 2025, plus
**2 new leases from the P04 turnover and the lease-ups** — landing at ~21–23 leases touched
during the year. (Codes L20/L21/L22 = the new-tenant leases created live in 2025.)

### 3.3 The deliberate "story" leases (exercise the hard paths)

- **L05 non-renewal + turnover (P04):** landlord declines renewal (`LeaseEndAutoAction.NonRenewal`)
  → tenant M. Osei moves out 2025-05-31 → **move-out inspection** (`InspectionType.MoveOut`) finds
  carpet damage → **security-deposit deduction** + partial refund → make-ready **work orders**
  (paint, carpet) with vendor **receipts** → **new application** (Tomás) → **screening** →
  approve → **new lease L20** → **move-in inspection** → tenant in 2025-07-01. One month vacancy
  (June) = zero rent on P04 in June (occupancy + cash-flow dip to reconcile).
- **L14 delinquency (P09·2):** L. Turner pays **partial** in Apr, **misses** May, pays partial
  Jun → triggers `LateFeeWorker` (flat `Lease.LateFeeAmount`) and escalating
  **LateRentNotice** drafts (first ≤7d, second ≤20d, final >20d) → cures with a lump
  catch-up in Sep. Exercises Partial arithmetic across delinquency aging, dashboard overdue,
  and Schedule E (late fees are income).
- **L08 / L16 lease-ups from vacancy:** public rental **application** (no-login) → `ScreeningResult`
  → `Approve` (creates Tenant) → lease → move-in inspection. Tests the full leasing funnel and
  the FCRA `AdverseActionNotice` on one **declined** applicant.
- **L02 month-to-month:** lease ends 04-30, `LeaseEndAutoAction.MonthToMonth` → conversion notice,
  rent keeps charging monthly with no fixed end.

---

## 4. The 2-year onboarding import (2023–2024) + establishing current state

**Critical grounding fact (GAP-1):** the app has **no bulk transaction import**. CSV import
(`/api/v1/import`) covers **Tenant / Property / Unit only** — not payments or expenses. There is
no "import 2 years of rent/expenses" button. So historical transaction volume enters through
**three** channels, and the corpus is explicit about which each record uses:

1. **SCAN → draft → confirm** (the flagship "computer types for you"): the 6 scan-ingestible doc
   types — **Lease, Receipt→Expense/Payment, Mortgage statement→Loan, Rental application,
   Maintenance photo→WorkOrder** (§8.8). This is where most *documents* come in.
2. **HAND-ENTRY:** everything with no scan path — Portfolio, Owner entities, Vendors (unless
   auto-created by a receipt scan), Security deposits, Opening balances, Inspections,
   Appointments, Autopay enrollment, Recurring-expense/maintenance templates, Notice templates,
   Users.
3. **WORKER/SEED-GENERATED (the key insight):** the landlord neither types nor scans the bulk ledger.
   Agreement confirmation targets explicit LeaseManagement, Agreement, and TenantAccount commands and
   never chooses a rent-generation mode. The bounded harness posts the 2023–24 tenant-account ledger
   entries separately, while loan and recurring-template history remains engine-generated.
   - **Marking rent Paid:** `POST /payments/{id}/mark-paid` accepts an **arbitrary historical
     `PaidDate` + `Method`** (no past-date validation), and income reports bucket by `PaidDate` year —
     so each of the ~500 historical rows needs its **own** `mark-paid` call to land in the right tax
     year. The bulk `POST /payments/leases/{leaseId}/past-due/mark-paid` stamps **one shared date** on
     all a lease's rows, so it's only usable when a single date is acceptable. **Constraint:**
     `PayerName/CheckNumber/BankName` are settable **only at `POST /payments` create**, not via
     mark-paid — see §9 item 12.
   - **Loans:** `DebtServiceService.GenerateAsync` back-generates **every monthly `LoanPayment`**
     (interest/principal/escrow chain) from loan `StartDate` to today once the `Loan` exists. Rows are
     created `Scheduled`, and **reports ignore `LoanPayment.Status`** (they sum by `DueDate`), so loans
     need **no mark-paid step**. But there is **no synchronous/on-demand trigger on loan-create** —
     only the hourly worker; the harness invokes `IDebtServiceService.GenerateAsync` directly (via the
     TSK-615 dev worker-trigger) or waits a tick.
   - **Recurring expenses:** `RecurringExpenseGenerationService` back-fills `Expense` rows (HOA, lawn,
     pest, trash, direct taxes/insurance) up to **36 periods** from the template `StartDate`; Schedule
     E buckets by `IncurredAt` with no status filter, so the `Pending` back-filled rows count. Same
     "hourly worker only, no create-time trigger" caveat as loans.

### 4.1 Import manifest — what arrives how

| Record set (2023–24 + current state) | Channel | Artifact / method | Reconciles into |
|---|---|---|---|
| Portfolio, 4 owner entities, 5 users | HAND-ENTRY | onboarding wizard forms | scope, owner statements |
| 13 Properties + 21 Units | SCAN side-effect (lease scan bootstraps) **or** CSV import | lease PDFs / `/import` | rent roll, occupancy, depreciation |
| 19 current Agreements (active at T0) | **SCAN** (confirm explicit management/agreement/account targets) | lease PDFs (born-digital) + **phone-photo lease sets to stitch** | rent roll, rent ledger, Schedule E income |
| 11 Loans/mortgages | **SCAN** | mortgage-statement images → `ConfirmAsLoan` (then fire DebtService) | debt service, Schedule E interest, NOI |
| Historical rent (2023–24, ~500 rows) | **BOUNDED LEDGER SEED** followed by explicit receipts | post dated tenant-account charges and receipts; **plus** a sample of rent checks scanned in 5 months (which replace the seeded charge for those account periods, §9 item 12) | rent ledger, cash-flow, owner statements |
| Historical loan payments (~250 rows) | **WORKER-GENERATED** | DebtServiceService back-fill | Schedule E interest, cash-flow debt service |
| Recurring expenses (HOA/lawn/pest/trash/direct tax+ins) | HAND-ENTRY template → **WORKER-GENERATED** rows | recurring-expense templates, StartDate 2023-01 | Schedule E, P&L, owner statements |
| One-off repair/maintenance expenses (2023–24, ~40) | **SCAN** (receipts) + some hand-entry | receipt phone-photos → `ConfirmAsExpense` | Schedule E Repairs/Cleaning, Vendor-1099 |
| Vendors (~10) | SCAN side-effect (receipt auto-creates) + hand-entry for W-9/1099 flags | receipt scans + edit | Vendor-1099 |
| Security deposits (per active lease) | HAND-ENTRY | `/security-deposits` | deposit register |
| Opening balances (a few carried tenant balances at 2025-01-01) | HAND-ENTRY | `/opening-balances` | per-lease ledger only (§8.9) |
| Prior-year owner statements (2023, 2024) | **regenerated by app** (reference docs on file) | none keyed; app computes from the above | owner-statement reconciliation |
| Insurance declarations / property-tax bills (direct-pay P03, P08) | **SCAN** (bill/PropertyTax receipts) | receipt images (document_kind=PropertyTax/Bill) | Schedule E Taxes/Insurance |

**Onboarding realism note:** a real landlord would NOT scan 500 old rent checks; they configure
current leases/loans and let the engine reconstruct the ledger (channel 3), while scanning the
*documents they actually have* (leases, mortgage statements, a shoebox of repair receipts, a few
rent checks). The corpus mirrors that: **bulk ledger = worker-generated; documents = scanned;
setup = hand-entered.** This also makes 2 years genuinely importable without the missing bulk-import
feature.

---

## 5. One-year operations calendar (CY2025)

> **The full ~104-row calendar is now authored** in the companion
> `2026-06-30-e2e-operations-calendar-CY2025.md` (all 12 months + a per-month artifact tally). The
> summary below stays here for context; the companion is the executable spine.

Format of every row: **`sim_date · actor · action-in-app · input artifact(s) · worker dependency`**.
"Actor" ∈ {Dana(Admin), Priya(Manager), Tomás(Agent), Tenant, **Engine**}. A worker action means:
set the clock to that date and fire that worker.

### 5.1 Recurring monthly cadence (repeats every month unless noted)

| Sim date (each month) | Actor | Action | Artifact | Worker dep. |
|---|---|---|---|---|
| Day 1 (or lease `RentDueDay`) | **Engine** | Rent charges created for all active leases | — | **RentChargeWorker** (needs `EnableRentCharges=true`) |
| ~Day 1–5 | Tenant / Priya | Rent paid: mark `Payment` Paid w/ method; some via **scanned check**, 2 via autopay | rent-check images (subset) | AutopayChargeWorker (the 2 enrolled) |
| Day of loan `DayOfMonthDue` | **Engine** | `LoanPayment` amortization row per loan | — | **DebtServiceWorker** |
| Template `NextRunDate` | **Engine** | Recurring expenses (HOA monthly, lawn Apr–Oct, trash monthly on P02/P05/P07/P09/P13) | — | **RecurringExpenseWorker** |
| Day 7 (grace+1 on day-1 leases) | **Engine** | Late fees on unpaid rent; source rent → `Late` | — | **LateFeeWorker** (needs `EnableLateFees=true`) |
| Rolling | **Engine** | Notice drafts: RentReminder (≤7d pre-due), LateRentNotice (overdue) | — | **NoticeDraftWorker** |
| Daily 08:00 local | **Engine** | Daily briefing enqueued | — | DailyBriefingDeliveryWorker (opt-in) |
| End of month | **Engine** | Outbox drains queued email/SMS | — | OutboxDispatchWorker |

### 5.2 Event spine — ~2 events/week (representative; full set in Phase 2)

Sample of the keyed one-off events (illustrative dates; the ladder in §3.2 fixes the rest):

| Sim date | Actor | Action | Artifact | Worker dep. |
|---|---|---|---|---|
| 2025-01-01 | Dana | Onboard: create portfolio/owners/users; import agreements+loans; enable rent charges & late fees; post bounded opening ledger facts | lease docs, mortgage stmts | seed account history then fire RentCharge+DebtService |
| 2025-01-06 | Priya | Scan Jan rent checks (check-payers) → confirm Payments | rent-check images | — |
| 2025-01-12 | Tenant (L14) | Portal: submit maintenance request (leaky faucet) | maintenance photo | ScanProcessingWorker |
| 2025-01-14 | Priya | WorkOrder → dispatch plumber (Vendor); scan invoice → Expense | receipt image | — |
| 2025-01-20 | **Engine** | P07·B (L11) expiry reminder fires (EndDate 02-28 ≤ 60d) | — | LeaseExpiryReminderWorker |
| 2025-02-01 | Tomás | List vacant P05·3; take application (public form) | application PDF | ScanProcessingWorker |
| 2025-02-10 | Tomás | Screening result → approve → create Tenant + Lease L21 (move-in 03-01) | — | — |
| 2025-02-25 | Dana | Approve L11 renewal notice draft; send | — | NoticeDraftWorker produced it |
| 2025-03-05 | **Engine** | L14 rent Partial (paid $400/825); remainder owed | — | (manual Partial) |
| 2025-03-08 | **Engine** | Late fee on L14 March rent; LateRentNotice (first) | — | LateFee + NoticeDraft |
| 2025-03-15 | Tomás | Move-in inspection P09·4 (L22) | inspection (hand-entry) | — |
| 2025-04-10 | **Engine** | L14 misses April entirely → LateRentNotice escalates (second) | — | NoticeDraft |
| 2025-05-31 | Priya | L05 move-out (non-renewal): move-out inspection, deposit deductions | inspection, deposit | — |
| 2025-06-02 | Priya | P04 make-ready work orders (paint, carpet) + vendor receipts | receipt images | — |
| 2025-06-20 | Tomás | New applicant for P04 → screening → approve → Lease L20 (07-01) | application | — |
| 2025-07-01 | Priya | L20 move-in inspection; P04 back to occupied | inspection | RentChargeWorker resumes P04 |
| 2025-09-05 | Tenant (L14) | Lump catch-up payment clears delinquency | — | — |
| 2025-06–12 | Dana | Approve monthly renewal/M2M drafts per the §3.2 ladder | — | NoticeDraftWorker |
| 2025-07-15 | Priya | Annual safety inspections batch (a few units) | inspections | RecurringMaintenance optional |
| 2025-12-31 | Dana | Year-end: pull Schedule E, owner statements, year-end packet | — | — reconcile — |

Roughly: 21 leases × 12 rent cycles + ~40 payments-of-interest events + ~30 maintenance/turnover
+ ~20 notice/renewal + ~12 monthly briefings ≈ **~110 calendar rows** to enumerate in Phase 2.

### 5.3 Worker-fire ordering (per simulated step)

From §8.7, fire in this order each simulated day that needs it, so producers precede consumers:
**RentChargeWorker → NoticeDraftWorker(reminders) → advance clock past due+grace →
LateFeeWorker + NoticeDraftWorker(late) → AutopayChargeWorker → DebtService/RecurringExpense/
RecurringMaintenance → OutboxDispatchWorker (drain).** All workers are **idempotent** (unique
`(LeaseId, PaymentType, PeriodKey)` etc.), so repeated fires on the same sim day are safe. The
only one-shot state to reset for a re-run is `Lease.ExpiryReminderSentAt` and the daily-briefing
outbox date key.

---

## 6. Ground-truth ledger design + reconciliation targets

### 6.1 Structure (the event log)

Ground truth lives under `e2e/corpus/ledger/` as **CSV** (human-diffable, compact) plus a small
JSON index. One **append-only event row per money event**, recorded independently of the app.

`ledger/events.csv` columns:

```
event_id, sim_entered_date, property_code, unit_code, lease_code, owner_entity,
record_kind,           # Payment | Expense | LoanPayment | SDHolding | SDDeduction | SDReturn | OpeningBalance
payment_type,          # Rent | LateFee | Utility | SecurityDeposit | Other      (Payment only)
status,                # Paid | Partial | Scheduled | Late | Waived | Failed | Refunded | Pending | Approved
method,                # Check | ACH | Cash | MoneyOrder | Zelle | Autopay        (Payment only)
sched_e_category,      # Repairs | CleaningMaintenance | Insurance | Taxes | Utilities | ManagementFees | ... (Expense only)
amount, amount_paid,   # amount_paid set only for Partial
due_date, paid_date, incurred_at,
vendor, billable_to_owner, escrow_covered,   # escrow_covered=true → expect exclusion from True-Cash-Flow opex
loan_interest, loan_principal, loan_escrow, loan_balance_after,   # LoanPayment only (the amortization chain)
period_key, note, source_artifact
```

Companion files:
- `ledger/loans.csv` — one row per loan with terms; the amortization chain is regenerated and its
  monthly rows land in `events.csv` (record_kind=LoanPayment) so `loan_balance_after` chains and
  the final row ties to `Loan.CurrentBalance`.
- `ledger/depreciation.csv` — per property per year: basis, in-service date, computed annual
  depreciation (formula §8.6). No app expense row; this is the expected Schedule E depreciation line.
- `ledger/deposits.csv` — per lease: held, deductions[], returned, expected register balance.
- `expected/<report>-<year>.json` — the derived expected figures per report (generated by a small
  script from `events.csv` using each report's exact predicate), diffed against the app's API.

### 6.2 Reports we reconcile (name + where each figure comes from)

Every figure below is a **derived view over `events.csv`** using the predicate documented in §8.
"Cash" reports filter on `paid_date`, "accrual" on `due_date`, tax on the year, etc.

| App report (endpoint) | Ground-truth derivation | Reconciliation grain |
|---|---|---|
| **Rent Roll** `/reports/rent-roll` | Σ `MonthlyRent`, Σ `SecurityDeposit` over active/notice leases w/ EndDate≥today | portfolio + per lease |
| **Rent Ledger** `/reports/rent-ledger` | per lease: Σ charges(due in range) − Σ paid(Paid, paid_date in range); running balance | per lease + entries |
| **Delinquency aging** `/reports/delinquency` | owed = {Scheduled,Partial,Late} ∧ (Late ∨ due<now); Partial owes `amount−amount_paid`; bucket by due_date vs now (0-30/31-60/61-90/90+) | per lease + totals |
| **Cash Flow P&L** `/reports/cash-flow` | income = Rent+LateFee Paid/Partial by paid_date; expense = all by paid_at; monthly | monthly + total |
| **True Cash Flow / NOI** `/accounting/cash-flow` | income (as above) − opex(**excl. escrowed Taxes/Ins**) − debt service(Σ LoanPayment.Total by due) | per property |
| **Property P&L** `/reports/property-pnl` | income = **all Paid** full Amount by paid_date; expense = all by paid_at | per property |
| **General Ledger** `/reports/general-ledger` | Paid payments (+) & expenses (−) by date, running net | entries + closing |
| **Schedule E** `/accounting/schedule-e` | income = Rent+LateFee+**Utility** Paid/Partial by paid_date(year); deductible = Σ expense by category by incurred_at (excl. manual interest/depr); +Σ LoanPayment.Interest; +computed depreciation | per property + category |
| **Owner Statement** `/accounting/owner-statement` | per owner: income = **Rent only Paid** by paid_date.year; expenses = Paid by (paid_at??incurred).year; mgmtFee = income×fee%; net | per owner entity |
| **Owner Distributions** `/reports/owner-distributions` | Σ(income − expenses − mgmtFee) **unrounded** per owner | per owner |
| **Security Deposit Register** `/reports/security-deposits` | held, deductions_total, returned, balance=max(0, held−ded−returned) | per lease + totals |
| **Vendor 1099** `/reports/vendor-1099` | Σ expense.amount by paid_at(year) per vendor; flag ≥ $600 & W-9 | per vendor |
| **Occupancy** `/reports/occupancy` | occupied units / total per property (Unit.Status) | per property |
| **Lease Expirations** `/reports/lease-expirations` | leases EndDate ≤ now+N | count + Σ rent |
| **Loan schedule** `/loans/{id}/payments` | the amortization chain from `loans.csv` | per period, balance chain |
| **Accounting Summary/Snapshot/Reports** `/accounting/*` | all-time & MTD collected/outstanding/overdue variants (§8.6) | portfolio KPIs |
| **Analytics / Dashboard** `/analytics/overview`, `/portfolios/{id}/dashboard` | occupancy, month collected vs scheduled, overdue | portfolio KPIs |
| **Year-End Packet** `/accounting/year-end-packet` | composite of the above for the year | PDF numbers |

**Deliberate expected divergences** the reconciliation must _expect_ (not treat as bugs), all
from §8.3–8.6: (a) Schedule E income > Cash-Flow-P&L income by the year's **Utility** payments;
(b) True-Cash-Flow opex < Property-P&L expense on escrow properties by the escrowed
**Taxes+Insurance**; (c) Owner-statement income (Rent-only) < Schedule E income by **late fees**;
(d) Owner-distributions (unrounded) may differ from the printed owner statement by **≤ 1¢**;
(e) `/accounting/summary` totals are **all-time**, not year-scoped. Each is asserted with its
expected delta.

---

## 7. Document / scan generation manifest

### 7.1 Tooling (detected on this Mac; what we'll use)

| Need | Tool present | Verdict |
|---|---|---|
| HTML → born-digital PDF (text path) | **Google Chrome** `--headless=new --print-to-pdf` | ✅ works (proof: 3-page lease PDF, 172 KB, `pdftotext` recovers every extracted field) |
| HTML → "phone photo" **JPEG** (vision path) | **Chrome** `--headless=new --screenshot=out.jpg --window-size=` + CSS skew/shadow/desk/vignette | ✅ works — Chrome writes **real JPEG** when the path ends in `.jpg` (proof: 57–80 KB each) |
| PDF page → raster (image-only "scanned" PDF) | **poppler** `pdftoppm -png/-jpeg -r150` | ✅ works |
| Merge single PDFs → multi-page | **poppler** `pdfunite` | ✅ present |
| Verify born-digital text extraction | **poppler** `pdftotext` | ✅ present |
| Convert / resize / recompress (footprint lever) | **`sips`** — macOS built-in (`/usr/bin/sips`) | ✅ zero-install: PNG 700 KB → JPEG 92 KB (q72) or 36 KB (q60, 1000px) |

**Not installed** (and not needed): LibreOffice/soffice, wkhtmltopdf, weasyprint, ImageMagick
(`magick`/`convert`), qpdf, ghostscript, pandoc, Pillow/reportlab/fpdf2. The **pure
Chrome + poppler + `sips`** pipeline covers every doc type with zero heavyweight installs.

Phone-photo realism is achieved in **CSS** (Chrome renders it): off-white paper card on a dark
desk gradient, `transform: rotate(-1.6deg)`, drop shadow, subtle paper-grain gradient, mild
vignette overlay, `filter: contrast(1.05) blur(0.3px)` — then Chrome's **JPEG encoder adds real
compression artifacts for free**, which is exactly what a phone camera produces (phones save JPEG,
not PNG). Heavier degradation (real lens blur, coffee stains, EXIF) would need Pillow/ImageMagick —
flagged optional; the vision model reads the CSS-styled JPEGs fine (proof images legible at
1200×1600).

### 7.2 Pipeline

```
templates/*.html  --(tools/generate.mjs: fill {{tokens}} from data manifest)-->  filled.html
   filled.html --Chrome --print-to-pdf-->     generated/leases/*.pdf      (born-digital, text path)
   filled.html --Chrome --screenshot=*.jpg--> generated/{receipts,checks,mortgage}/*.jpg  (phone photo JPEG, vision path)
   photo.jpg   --(wrap in <img> HTML)--Chrome --print-to-pdf--> *_scanned.pdf   (image-only PDF, vision path)
   page1.pdf page2.pdf --pdfunite--> lease_multipage.pdf          (born-digital multi-page)
   page1.jpg page2.jpg (separate)  --> app client stitchImagesToPdf --> ONE draft (stitch test)
   (optional) sips -Z / -s formatOptions  --> resize/recompress to hit a footprint budget
```

The generator (`tools/generate.mjs`, Node, no deps — just spawns Chrome/poppler) reads a data
manifest and emits every artifact deterministically, so the corpus is reproducible and
regenerable rather than a pile of committed binaries.

### 7.3 Artifact manifest (Phase-2 targets)

| # | Type | Count | Format | Feeds | Path |
|---|---|---|---|---|---|
| A | Lease agreements — born-digital | ~13 | PDF (text) | Lease scan (text path) | `generated/leases/*.pdf` |
| B | Lease agreements — phone-photo page sets (stitch) | ~7 × 3–5 pg | JPEG per page | Lease scan (stitch → 1 draft) | `generated/leases/photos/L##_p#.jpg` |
| C | Rent checks | ~35 | JPEG (phone photo) | Payment scan (RentCheck) | `generated/checks/*.jpg` |
| D | Vendor receipts / bills / utility / property-tax | ~55 | JPEG (phone photo); a few PDF | Expense scan | `generated/receipts/*.jpg` |
| E | Mortgage statements | 11 | JPEG (phone photo) or PDF | Loan scan | `generated/mortgage/*.jpg` |
| F | Rental applications | ~4 | PDF | Application scan | `generated/applications/*.pdf` |
| G | Maintenance photos | ~12 | JPEG (stylized/captioned) | WorkOrder scan | `generated/maintenance/*.jpg` |
| H | Insurance declarations / misc | ~4 | PDF/JPEG | Expense scan | `generated/misc/*` |
| I | One image-only "scanned" lease PDF | 1 | PDF (raster) | Lease scan (vision path) | `generated/leases/*_scanned.pdf` |
| J | One ≤100-file lease **batch** | ~10 | PDF | batch scan endpoint | reuse A subset |

**Estimated total artifacts:** ~145. **Measured footprint (JPEG photos):** born-digital PDFs
~80–172 KB, phone-photo JPEGs ~55–80 KB each (proof set: 5 artifacts = **456 KB**). Extrapolated:
~24 PDFs (~2.9 MB) + ~141 JPEGs (~9.9 MB) ≈ **~13 MB total** — trivial for disk. **Lever:** PNG
photos ran ~440–700 KB each (would be ~65 MB total); JPEG output (Chrome writes it directly to
`.jpg`) is the default and cuts that ~8×; `sips -Z 1000 -s formatOptions 60` cuts further to
~36 KB if a tighter budget is ever needed. **Recommendation:** commit `templates/`, `tools/`,
`ledger/`, `expected/` (all text); **git-ignore `e2e/corpus/generated/`** (regenerable binaries) —
an `e2e/corpus/.gitignore` with `generated/` is included. Repo-root `uploads/` is already ignored.

---

## 8. Grounding appendix (app facts this design rests on)

Condensed from source reads of `RentalCommand.Core/Entities|Enums`, `…Api/Services/Domain/*`,
`…Engine/Workers|Services/*`, `…Api/Scanning/*`, and the web onboarding routes.

### 8.1 Money primitives
A `Payment` row is simultaneously the **charge and the receipt** (no separate allocation table).
`Payment`: `PaymentType{Rent,SecurityDeposit,LateFee,Utility,Other}`,
`Status{Scheduled,Paid,Partial,Late,Waived,Failed,Refunded}`, `Amount`, `AmountPaid?`(Partial
only, 0<x<Amount), `DueDate`, `PaidDate?`, `Method`, `PeriodKey`("yyyy-MM"). `Expense`:
`Category(ScheduleECategory)`, `Status{Pending,Approved,Paid,Rejected,Draft}`, `Amount`,
`IncurredAt`, `PaidAt?`, `BillableToOwner`. `LoanPayment`: `Interest+Principal+Escrow=Total`,
`BalanceAfter` chains. `SecurityDepositHolding`: `Amount = ReturnedAmount + DeductionsTotal`.
`OpeningBalance`: signed, 1/lease, **display-only in the per-lease ledger** (§8.9).

### 8.2 Enums that constrain the corpus
`ScheduleECategory`: Advertising, AutoTravel, CleaningMaintenance, Commissions, Insurance,
LegalProfessional, ManagementFees, MortgageInterest, Repairs, Supplies, Taxes, Utilities,
Depreciation, Other. `LeaseEndAutoAction`: Draft, Renewal, MonthToMonth, NonRenewal.
`PropertyType`: SingleFamily, MultiFamily, Condo, Townhome, Commercial, MixedUse.
`OwnerEntityType`: Person, LLC, Trust. `UserRole`: Admin, Manager, Agent, Owner, Tenant.
`InspectionType`: MoveIn, MoveOut, Routine, AnnualSafety. `NotificationType`: RentCharge, LateFee,
LeaseExpiry, RentConfirmation, NoticeAutopilot, DailyBriefing. Notice types are **free strings**:
RentReminder, RenewalOffer, MonthToMonthConversion, MoveOutReminder, LateRentNotice.

### 8.3 The 12 income / 7 expense report definitions (reconciliation crux)
Each report defines income/expense differently — filter the event log to match:
- **Schedule E income**: Rent+LateFee+**Utility**, Paid/Partial (Paid→Amount, Partial→AmountPaid),
  dated `PaidDate`, per tax year.
- **Cash-Flow-P&L / True-Cash-Flow income**: Rent+LateFee (drops Utility), Paid/Partial, dated
  `PaidDate??DueDate`.
- **Property-P&L income**: **all** PaymentTypes incl. SecurityDeposit, **Paid only** (ignores
  Partial cash), full Amount.
- **Owner-Statement income**: **Rent only**, Paid only, full Amount, `PaidDate.Year`.
- **Accounting Summary/Reports**: all except SecurityDeposit, Paid/Partial, **all-time**, +
  unmatched bank deposits.
- **Analytics overdue**: uses **full Amount** even for Partial (over-counts) — expect it.
Expense sides diverge similarly; the load-bearing one is escrow (§8.5).

### 8.4 Owner statement + amortization
Owner statement per property: `income=Σ Rent Paid full Amount (year)`,
`expenses=Σ Expense Paid (year)`, `mgmtFee=round(income×ManagementFeePercent/100,2)`,
`net=income−expenses−mgmtFee`; totals = Σ of **rounded** per-line values.
Amortization per month: `interest=round(balance×(APR/100/12),2)`, `principal=round(P&I−interest,2)`,
`balanceAfter=balance−principal`, first balance=`OriginalAmount`. Schedule-E interest = Σ
`LoanPayment.InterestAmount`; principal never deductible; escrow not deductible as-is.

### 8.5 Escrow rule (subtlest)
For an Active loan with `EscrowCoversTaxes/Insurance=true`: **True-Cash-Flow opex EXCLUDES**
separately-entered `Taxes`/`Insurance` expenses (escrow cash already in debt service). But
**Schedule E, Cash-Flow-P&L, and Property-P&L still count them.** ⇒ For escrow properties we DO
enter Taxes/Insurance expenses (on servicer-disbursement dates) so the tax deduction is correct,
and the corpus expects True-Cash-Flow NOI opex to be lower by exactly those amounts. Paid-off P03
& P08 have no loan → their Taxes/Insurance count everywhere (clean case).

### 8.6 Depreciation
`buildingBasis = PurchasePrice − LandValue`; `annual = buildingBasis/27.5`; first in-service year
prorated mid-month: `months = (12 − InServiceMonth) + 0.5`, `= round(annual×months/12,2)`; capped
at remaining basis; `ManualAnnualDepreciation` overrides. Condo P11 basis = full price (land 0).

### 8.7 Scheduled workers (cadence · date rule · config · idempotency)
Timer loops (not cron); "today" = `DateTime.UtcNow` → business tz `America/New_York` (except
LeaseExpiryReminder uses raw UtcNow.Date, DailyBriefing uses each portfolio's tz). Firing =
invoking the backing service once.
- **RentChargeWorker** (1h): per active tenant-account obligation, one scheduled rent ledger entry
  per due period through the cutoff
  cutoff (today, extended to due date if within `RentChargeLeadDays`=5). **Back-fills all missed
  months in one fire.** Gate `EnableRentCharges` **default OFF** → must enable. Idempotent on
  `(LeaseId,Rent,PeriodKey)`.
- **LateFeeWorker** (6h): overdue rent past `DueDate+LateFeeGraceDays`(5); `Payment(LateFee,
  Amount=Lease.LateFeeAmount, capped by state)`, flips rent→Late. Gate `EnableLateFees` **default
  OFF**. Idempotent `(LeaseId,LateFee,PeriodKey)`.
- **DebtServiceWorker** (1h): per Active loan, monthly `LoanPayment` from StartDate to now,
  capped at TermMonths. **No gate.** Idempotent `(LoanId,PeriodKey)`.
- **RecurringExpenseWorker** (1h): templates `Active && NextRunDate≤today`, one Expense/period,
  catch-up ≤36. **No gate.**
- **RecurringMaintenanceWorker** (24h): one WorkOrder/fire, `IsActive && NextDueDate≤today`.
- **LeaseExpiryReminderWorker** (6h): Active, `ExpiryReminderSentAt=null`, `EndDate` within
  `LeaseExpiryReminderDays`(60). Gate **default ON**. One-shot via `ExpiryReminderSentAt`.
- **NoticeDraftWorker** (12h): per Active lease keyed on `daysToEnd`: Renewal/M2M ≤75, MoveOut
  ≤30; LateRentNotice on overdue (escalates ≤7 / ≤20 / >20 days late); RentReminder for scheduled
  rent due ≤7d. Auto-send only if `NotifyTenants` + type flag + active template.
- **AutopayChargeWorker** (1h): needs Stripe enabled + Active `AutopayEnrollment`; charges
  scheduled/late rent. Idempotent key `autopay-{PaymentId}-{period}`.
- Infra: OutboxDispatchWorker (10s), ScanProcessingWorker (2s), watchdog/heartbeat.
Config source: per-portfolio **`NotificationSettings`** DB row (not appsettings). New-portfolio
defaults: RentCharges OFF, LateFees OFF, ExpiryReminders ON, NotifyTenants OFF, briefing OFF.
**The harness must enable via `PUT /api/v1/notifications/settings`** (there is no
`NotificationSettingsController`; it's on `NotificationsController`). ⚠ That PUT is a **full-object
overwrite**, not a patch — GET, mutate, then PUT the complete object, or omitted fields revert to
DTO defaults. `NoticeAutopilot` and `RecurringMaintenance` are **always on** (no DB toggle);
`Autopay` gates on appsettings `Stripe:Enabled`. **Clock/worker-triggering is now specified in the
sibling `2026-07-01-master-simulation-clock-design.md` (TSK-615)** — an injected `TimeProvider`
(dev/test only) plus a dev worker-trigger command table (the Api can't call Engine services
directly, and only `POST /api/v1/notices/generate` fires a worker over HTTP today).

### 8.8 Scan ingestion (6 ingestible types) + onboarding
Pipeline: upload → `StoredFile` + thumbnail → `ScanDraft(Pending)` → ScanProcessingWorker LLM
extract → `Reviewing` → confirm-with-overrides → entity in one transaction. Ingestible confirm
targets: **Expense, Payment, WorkOrder, Lease, Application, Loan.** Formats: **PDF, JPG, PNG,
HEIC**, ≤**50 MB**, batch ≤**100** files. **Multi-page PDFs** read whole (PdfPig text or vision).
**Multi-photo stitching**: client-side `stitchImagesToPdf` (jsPDF) merges ≥2 photos → ONE A4 PDF
→ ONE draft (can't mix PDF+photos; HEIC won't stitch in-browser). Text path when PdfPig recovers
>40 chars, else vision; **photos default to the vision model** (Tesseract OCR off by default).
Extraction fields per type are in §8 of the ingestion survey (lease → tenant/property/unit/terms;
receipt → vendor/amounts/category/check fields; loan → lender/rate/term/P&I/escrow flags; etc.).
Onboarding wizard steps: Portfolio → Owner → Property+Units → Tenants → Lease (with photo
prefill) → email/SMS. Flagship `/scan/new-rental`: lease scan bootstraps property→unit→tenant→
lease in one confirm. CSV `/import`: **Tenant/Property/Unit only** (no payments/expenses).

### 8.9 Opening balances
`OpeningBalance` (signed, 1/lease) folds ONLY into the per-lease ledger
(`/leases/{id}/ledger`): `totalCharged += max(Amount,0)`, `totalPaid += max(−Amount,0)`. It does
**NOT** touch P&L, Schedule E, cash flow, NOI, owner statements, or accounting summary. So it
trues up a tenant's running balance only; pre-app income/expenses must be real Payment/Expense
rows to appear in financial reports (→ the worker-generation strategy in §4).

---

## 9. Gaps / feature-requests (things a landlord wants that the app can't represent)

Flagged, not fabricated. Ordered by impact on this corpus.

1. **No bulk transaction import** (GAP-1): CSV import is Tenant/Property/Unit only; there is no
   payment/expense bulk import. Mitigated by worker back-fill (§4) + scanning, but a real
   multi-year onboarding of transaction history is otherwise infeasible. *Biggest onboarding gap.*
2. **No owner-distribution / payout record**: "distribution" is a **computed net**
   (OwnerStatementService), not a recorded cash payout entity. We cannot record "Dana actually
   wired Marcus $X on date Y," so cash-distributed vs computed-net can't be reconciled. (Could be
   shoehorned as an `Expense` category `Other`, but that then pollutes P&L.)
3. **No rent proration**: RentChargeWorker always charges full `MonthlyRent`. Move-in/move-out
   partial-month rent (L05 out 05-31, L20 in 07-01) must be **hand-entered** as manual `Payment`
   rows (PeriodKey null). Corpus does this; flag that the engine can't.
4. **No capital-improvement depreciation**: only original building basis depreciates; a mid-life
   $8k roof can't start its own 27.5-yr schedule — it'd be expensed as `Repairs` (the year-end
   packet even warns about this). Limits multi-year realism; corpus keeps big-ticket items as
   Repairs and expects the app's own "may be capital improvement" caveat.
5. **No application/screening-fee income**: a `Payment` requires a `LeaseId`, but an applicant
   pays a screening fee **before** any lease. Application fee income is unrepresentable.
6. **No property disposition / sale**: year-end explicitly doesn't compute sale-year depreciation,
   gain/loss, or §1250 recapture. Corpus **avoids selling** a property (flag if a sale is wanted).
7. **No eviction workflow/entity**: a delinquent tenant heading to eviction is only representable
   via `LateRentNotice` strings + a `Lease.Terminated` status; no eviction case/filing record.
8. **Per-lease late fee is flat only** (`Lease.LateFeeAmount`): percentage-of-rent late fees exist
   only via the state-cap config, not as a per-lease %.
9. **Security-deposit interest** (some states) not modeled — irrelevant for Ohio <5-unit, noted
   for SaaS generality.
10. **NSF/bounced check** is representable (`Paid→Failed`, re-charge, NSF as `Other`) — included as
    a minor story beat, no gap.
11. **No fractional / co-ownership** (source-verified: no `PropertyOwner`/`OwnershipPercent`/`CoOwner`
    table): a property points to exactly one `OwnerEntityId`. Our "Bell-Okafor Holdings LLC co-owned
    by Marcus + Dana" is representable only as **one** `OwnerEntity`; the two humans behind the LLC
    (and any % split) are outside the app. Fine for the test (statements are per legal entity), but
    flag that owner-level equity splits aren't modeled.
12. **Rent-check scan double-counts a worker-charged month** (source-verified): `EnableRentCharges`
    is per-*portfolio*, so every active lease gets a Scheduled rent row each month; confirming a
    scanned rent check (`ConfirmAsPayment`) creates a **separate** Payment (no `PeriodKey`), so using
    both for the same lease-month double-counts income. The corpus resolves this by making a scanned
    check **replace** (waive/delete) the worker's Scheduled row for that lease-month; all other months
    use worker-charge + `mark-paid`. *(Phase-2 must confirm whether `ConfirmAsPayment` sets `PeriodKey`;
    if it does, the dedupe index handles it and no waive is needed.)*
13. **Silent owner fallback (harness invariant, not an app gap):** `PropertyService.CreateAsync`
    auto-links a property with no `ownerEntityId` to the portfolio's `IsPrimary` self-owner — so a
    forgotten `ownerEntityId` **silently misattributes** all that property's income and still "passes."
    The replay driver MUST set `ownerEntityId` on every property create/update. (Also: `ManagementFeePercent`
    is per-**property**, not per-owner — replicate the same % across an owner's properties. And the web
    calls `OwnerEntity` "owners"; the legacy C# `Owner` entity is vestigial — a naming foot-gun.)

---

## 10. Open questions + Phase-2 batch plan

### Open questions for the human
1. **History depth vs effort:** OK to make **CY2025 the full reconciliation spine** (100% app-
   generated, reconciled to the cent) while CY2023–24 are **worker-back-filled + spot-checked**
   (not every historical transaction hand-verified)? This is the honest consequence of GAP-1.
2. **Owner distributions (GAP-2):** leave "distribution" as the computed net (recommended), or
   also model actual payouts as `Expense/Other` (pollutes P&L) so cash-distributed reconciles?
3. **Autopay/Stripe:** run the 2 autopay leases through **real Stripe test mode**
   (AutopayChargeWorker + webhook), or simulate by marking those Payments Paid? Real test mode
   exercises more but needs Stripe test keys wired in the env.
4. **Maintenance photos (artifact G):** acceptable to use **stylized/captioned placeholder
   images** (we can't synthesize real photos of real damage), or should these WorkOrders be
   created from **typed notes** (hand-entry / voice intake) instead of photo scans?
5. **Commit policy:** confirm we **git-ignore `e2e/corpus/generated/`** (binaries, ~10–15 MB,
   regenerable) and commit only `templates/ tools/ ledger/ expected/`. Proposed `.gitignore`
   addition: `e2e/corpus/generated/`.
6. ~~Clock mechanism~~ **RESOLVED** by the sibling spec `2026-07-01-master-simulation-clock-design.md`
   (TSK-615): an injected `TimeProvider` (dev/test only, prod = `TimeProvider.System`) shared by API +
   Engine via a polled 1-row `SimulationClock` table, **not** moving the OS/Postgres clock — plus a
   dev-only worker-trigger command table (since the Api can't call Engine services directly and only
   `POST /api/v1/notices/generate` fires a worker over HTTP). The replay driver targets that surface.

### Phase-2 batch plan (mass generation), in order
1. **Freeze numbers:** finalize exact rents, deposits, loan terms; run `AmortizationCalculator`
   + `DepreciationCalculator` semantics in a script to emit `ledger/loans.csv`,
   `ledger/depreciation.csv`, and every monthly `LoanPayment` event → reconciles to the cent.
2. **Author `ledger/events.csv`** for CY2023–2025 deterministically from the scenario (rent cycles,
   the L14 delinquency, L05 turnover, recurring + one-off expenses, deposits). Generate
   `expected/*.json` via the derivation script (§6.2 predicates).
3. **Generate artifacts** (batch by type via `tools/generate.mjs`): leases (A/B/I), then receipts
   (D), checks (C), mortgage statements (E), applications (F), maintenance (G), misc (H). ~145
   files, ~10–15 MB. Non-build work — safe to parallelize across doc types; **no dotnet builds in
   these lanes** (RAM rule).
4. **Replay script:** the driver that sets the clock, seeds hand-entry + worker config via API,
   uploads scans + confirms drafts, fires workers in the §5.3 order, steps week-by-week through
   2025, and at 2025-12-31 pulls every report in §6.2 and **diffs against `expected/*.json`**.
5. **Reconcile & report:** produce a pass/fail matrix per report with expected-vs-actual deltas,
   pre-asserting the deliberate divergences in §6.2.

### Files created in Phase 1
- This design doc: `Docs/superpowers/specs/2026-06-30-e2e-scenario-corpus-design.md`
- Full CY2025 operations calendar: `Docs/superpowers/specs/2026-06-30-e2e-operations-calendar-CY2025.md`
- Proof set + pipeline under `e2e/corpus/` (see the companion `e2e/corpus/README.md`).
- Sibling (other track): `Docs/superpowers/specs/2026-07-01-master-simulation-clock-design.md` (TSK-615) — resolves the clock/worker-trigger open question.

### Post-design source verification (5 parallel agents)
Load-bearing assumptions were re-checked directly against source after the first draft:
- **Reconciliation predicates** — every P01 CY2025 figure CONFIRMED exact against
  `ScheduleEService`/`ReportsService`/`OwnerStatementService`; 2 seed preconditions recorded in
  `expected/P01-2025.reconciliation.sample.json`.
- **Import mechanism** — CORRECTED: agreement confirmation creates explicit management/agreement/account
  facts only; historical rent uses the bounded ledger seed with date-correct receipts. Loans/recurring
  back-fill still need a direct service call (§4).
- **Worker enablement** — `PUT /api/v1/notifications/settings` (full overwrite); clock/trigger via
  TSK-615 (§8.7).
- **Owner model** — the Owner-vs-OwnerEntity concern was a false alarm (onboarding creates an
  `OwnerEntity`); surfaced the silent-owner-fallback harness invariant (§9 item 13).
