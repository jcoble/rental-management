# Act 2 — Operations (CY2025, clock ADVANCING)

The live-operations year: the monthly rent/expense/notice rhythm plus every story beat (delinquency arc,
turnover, lease-ups, adverse action, NSF, inspections, maintenance intake, quarterly owner statements,
renewals). The clock advances month by month; at each dated step the harness **sets the clock**, logs in
as the **actor**, executes, and **fires workers** in the documented order.

This act has three parts:
1. **Reference tables** — per-month batch parameters, the renewal/notice ladder, the scan-month matrix.
2. **Reusable monthly templates** — `TMPL-B1` (month-open Engine batch), `TMPL-B2` (record rent),
   `TMPL-B3` (rent-reminders + outbox), plus the **notice & renewal rhythm**. These instantiate as
   `OPS-<MON>-B1/B2/B3` and `OPS-<MON>-NOTICES` (enumerated in `scenarios.json`).
3. **Story beats** (chronological) and **gap-feature spotlights** (`GAP2…GAP7`, `STRIPE-*`; `GAP1` is in
   Act 1 / `ONB-08`).

Read `README.md` for actor credentials, the dev clock/worker endpoints, harness invariants, and the
`PENDING GAP<N>` policy. **Spine vs gap:** everything non-`PENDING` is authored against the frozen corpus
and reconciles cent-exact in Act 3; gap spotlights describe intended behavior and, where they add/redirect
money, flag a **corpus-revision** need (they do not silently edit `expected/*.json`).

**Execution surfaces (see README "Execution surfaces"):** in this act the surface follows the **actor** —
every **human/tenant** step (Priya/Tomás/Dana/Tenant) is **`[UI]`** (drive the real screen per the README
action→screen map; assertions read from the screen), and every **`actor: Engine`** step is
**`[DEV-CLOCK]`** (set/advance the clock) **+ `[WORKER]`** (fire the named worker). Scanned-artifact
confirms (checks/receipts/leases/apps) are `[UI]` via `/scan`. Worker-enablement was set once in ONB-02
(`[UI]` Settings) — it is **not** re-PUT here. The only `[SEED]` in the live year is none: all CY2025
operational payments are recorded through the UI (lease-detail "Mark paid" / `/accounting/past-due` /
`/scan` for scanned checks). Each scenario carries a `Surfaces:` line; `scenarios.json` has the
machine-readable `surfaces` array.

---

## 1. Reference tables

### 1a. Per-month batch parameters (drives `TMPL-B1` / `TMPL-B2`)
"Worker rent charges" = how many `Payment(Rent,Scheduled)` rows `rent-charge` should create that month.
"Hand-entered rent" = partial/turnover months the worker can't produce (GAP3). "Recurring $" = the
`recurring-expense` output that month. Debt-service = **11** `LoanPayment` rows every month.

| Month | sim day-1 | Worker rent charges | Hand-entered rent | Recurring expenses fired | Recurring $ | Scan month? |
|---|---|---|---|---|---|---|
| Jan | 2025-01-01 | 19 | — | HOA + trash×5 + **pest Q1×5** | 860 | **yes** |
| Feb | 2025-02-01 | 19 | — | HOA + trash×5 | 410 | no |
| Mar | 2025-03-01 | **20** (+L08) | L16 $452.42 (½-Mar) | HOA + trash×5 | 410 | no |
| Apr | 2025-04-01 | **21** (+L16) | — | HOA + trash×5 + **lawn×4 begins** + **pest Q2×5** | 1540 | **yes** |
| May | 2025-05-01 | 21 | — | HOA + trash×5 + lawn×4 | 890 | no |
| Jun | 2025-06-01 | **20** (L05 out; P04 vacant → 0) | — | HOA + trash×5 + lawn×4 | 890 | no |
| Jul | 2025-07-01 | 20 | L20 $1495 (first mo) | HOA + trash×5 + lawn×4 + **pest Q3×5** | 1540 | **yes** |
| Aug | 2025-08-01 | **21** (+L20) | — | HOA + trash×5 + lawn×4 | 890 | no |
| Sep | 2025-09-01 | 21 | — | HOA + trash×5 + lawn×4 | 890 | no |
| Oct | 2025-10-01 | 21 | — | HOA + trash×5 + **lawn×4 final** + **pest Q4×5** | 1540 | **yes** |
| Nov | 2025-11-01 | 21 | — | HOA + trash×5 | 410 | no |
| Dec | 2025-12-01 | 21 | — | HOA + trash×5 | 410 | **yes** |

Recurring unit amounts: HOA (P11) **$185**; trash (P02,P05,P07,P09,P13) **$45** each = 225; lawn
(P04,P06,P10,P12) **$120** each = 480; pest (P02,P05,P07,P09,P13) **$90** each = 450. CY2025 totals: HOA
**2220**, trash **2700**, lawn **3360**, pest **1800** (`expected/cy2025-portfolio.json → vendor1099`).

### 1b. Renewal / notice ladder (renewalDate → new rent; drives `OPS-<MON>-NOTICES` + `TMPL-B2` amounts)
The rent the worker charges **rises to the escalated amount from `renewalDate`** (+3%, `scenario.json`).
Notice-draft windows: Renewal/M2M ≤ 75 d, MoveOut ≤ 30 d, RentReminder ≤ 7 d; expiry reminder ≤ 60 d.

| Effective | Lease | Prop·Unit | Rent old → new | Outcome |
|---|---|---|---|---|
| 2025-03-01 | L11 | P07·B | 1150 → **1184.50** | Renewal |
| 2025-04-01 | L01 | P01 | 1250 → **1287.50** | Renewal |
| 2025-05-01 | L02 | P02·A | 950 → **950 (M2M)** | Month-to-month (no fixed end) |
| 2025-05-01 | L13 | P09·1 | 825 → **849.75** | Renewal |
| 2025-06-01 | L22 | P13·B | 940 → **968.20** | Renewal |
| 2025-07-01 | L04 | P03 | 1150 → **1184.50** | Renewal (NSF beat Aug) |
| 2025-07-01 | L17 | P10 | 1525 → **1570.75** | Renewal (autopay) |
| 2025-08-01 | L06 | P05·1 | 850 → **875.50** | Renewal |
| 2025-08-01 | L14 | P09·2 | 825 → **849.75** | Renewal (**held** until Sep cure) |
| 2025-09-01 | L09 | P06 | 1395 → **1436.85** | Renewal (autopay) |
| 2025-09-01 | L21 | P13·A | 925 → **952.75** | Renewal |
| 2025-10-01 | L03 | P02·B | 975 → **1004.25** | Renewal |
| 2025-10-01 | L15 | P09·3 | 825 → **849.75** | Renewal |
| 2025-11-01 | L07 | P05·2 | 850 → **875.50** | Renewal |
| 2025-11-01 | L18 | P11 | 1100 → **1133.00** | Renewal |
| 2025-12-01 | L10 | P07·A | 1100 → **1133.00** | Renewal |
| 2026-01-01 | L12 | P08 | 1050 → 1081.50 | Renewal (**out of spine** — approve, effective 2026) |
| 2026-01-01 | L19 | P12 | 1600 → 1648.00 | Renewal (**out of spine**) |
| — | L05 | P04 | — | **Non-renewal** → move-out 05-31 (see `OPS-MAY-L05-MOVEOUT`) |
| new | L08 | P05·3 | 850 | Lease-up (from 03-01) |
| new | L16 | P09·4 | 825 | Lease-up (from 03-15, prorated) |
| new | L20 | P04 | 1495 | Turnover (from 07-01) |

### 1c. Scan-month rent-check matrix (drives the `TMPL-B2` "replace" logic)
Scan months = **Jan, Apr, Jul, Oct, Dec**. In these months the scannable Check/MoneyOrder payers' rent is
recorded by **scanning the check** (`ConfirmAsPayment`), which **replaces** the worker's Scheduled row for
that lease-month (waive/delete the duplicate). All other payers/months use worker-charge + `mark-paid`.

| Scan month | Scannable payers to scan (files under `generated/checks/`) |
|---|---|
| Jan (2025-01) | L01, L04, L07, L11, L14, L21, L22 — `L01_2025-01.jpg` … `L22_2025-01.jpg` (7; L08 not yet active) |
| Apr (2025-04) | L01, L04, L07, L08, L11, L14, L21, L22 — `*_2025-04.jpg` (8; L14 is a **Partial** — see `OPS-APR-L14-PARTIAL`) |
| Jul (2025-07) | L01, L04, L07, L08, L11, L14, L21, L22 — `*_2025-07.jpg` (8) |
| Oct (2025-10) | L01, L04, L07, L08, L11, L14, L21, L22 — `*_2025-10.jpg` (8) |
| Dec (2025-12) | L01, L04, L07, L08, L11, L14, L21, L22 — `*_2025-12.jpg` (8) |

(Extra `*_2025-03.jpg` check images exist but March is **not** a scan month — leave them unused in the
spine.) Payer methods: L01/L04/L11/L21 = Check; L07/L22 = MoneyOrder; L08/L14 = Check.

---

## 2. Reusable monthly templates

### TMPL-B1 — Month-open Engine batch  ·  actor: Engine  ·  fires: rent-charge, debt-service, recurring-expense
- **Goal / exercises:** the first-of-month engine run that charges rent, posts debt service, and generates
  recurring expenses for all active leases/loans/templates.
- **Preconditions:** Act 1 complete; prior months' batches done; the month's active-lease set per §1a.
- **Surfaces:** `[DEV-CLOCK]` (set clock) + `[WORKER]` (rent-charge, debt-service, recurring-expense) — no
  user UI; firing scheduled jobs IS the mechanism under test (the superadmin engine page only monitors).
- **Steps (parameterized by month M, see §1a):**
  1. **`[DEV-CLOCK]`** `POST /api/v1/dev/clock/set {"date":"2025-<M>-01","timeZoneId":"America/New_York","mode":"frozen"}`.
  2. **`[WORKER]`** Fire `rent-charge` (`POST /api/v1/dev/workers/rent-charge/run-once`) → creates that month's
     `Payment(Rent,Scheduled)` for every active lease at its **current** rent (escalated per §1b from the
     renewal effective date). Count = §1a "Worker rent charges".
  3. Fire `debt-service` → **11** `LoanPayment` rows (one per loan) for the period.
  4. Fire `recurring-expense` → the templates due this month per §1a.
  5. (Autopay/late-fee/notice-draft are **not** part of B1 in the spine — autopay is a no-op without Stripe
     and is covered by `STRIPE-*`; late fees/notices fire in their beats. If using `run-due` for
     convenience, those steps no-op on a clean day-1.)
- **Assertions:**
  - Scheduled rent rows created = §1a count; each amount = current rent (spot-check the month's renewals,
    e.g. **Mar**: L11 charges **1184.50**; **Apr**: L01 charges **1287.50**; **Jun**: P04 has **no** rent
    row).
  - **11** new `LoanPayment` rows; the running balance chain advances (cf. `ledger/loans.csv`).
  - Recurring `Expense` rows match §1a (e.g. **Jan** = HOA 185 + 5×trash 45 + 5×pest 90; **Apr** adds
    4×lawn 120).
- **Idempotency:** all three workers key on `(Lease/Loan/template, PeriodKey/period)` — safe to re-fire.

### TMPL-B2 — Record rent Paid  ·  actor: Priya (+ Tenants)  ·  fires: ScanProcessing (scan months)
- **Goal / exercises:** turning the month's Scheduled rent into Paid receipts by method, including the
  scan-month check "replace" and portal self-pay.
- **Preconditions:** `OPS-<M>-B1` done (Scheduled rent exists); clock at ~day 3–6 of month M.
- **Surfaces:** `[UI]` — scanned checks via `/scan/[draftId]` (`ConfirmAsPayment`); all other rent via the
  **lease-detail "Mark paid"** control (`/leases/[id]`) or **`/accounting/past-due`** (one-tap). `[DEV-CLOCK]`
  only to advance the clock to the pay date. **No API mark-paid in the live year.** (PG-4: if the "Mark
  paid" dialog doesn't capture `Method`/`PayerName` on non-scanned rows, that's a display-only fidelity
  note — flag it, don't API around it.)
- **Steps (parameterized by month M):**
  1. **`[DEV-CLOCK]`** Advance clock to ~day 3–6 (`POST /api/v1/dev/clock/advance {"days":N}`) so PaidDate = sim date.
  2. **`[UI]` Scan months (§1c):** for each scannable payer, upload the check JPEG in **`/scan`** and
     `ConfirmAsPayment` (Rent, Paid, method per §1c, PayerName/CheckNumber from the check) → then
     **waive/delete** that lease-month's worker Scheduled row (on the lease detail) so the lease-month has
     exactly one Paid rent (design §9 item 12).
  3. **`[UI]` All other payers (and all payers in non-scan months):** on **`/leases/[id]`** click **"Mark
     paid"** on the month's Scheduled rent row (PaidDate = the sim date; set `Method` if the dialog
     supports it), or use **`/accounting/past-due`** one-tap for any behind lease. (**L09/L17** method =
     Autopay, marked Paid day 1 — deterministic spine; real Stripe is `STRIPE-*`.)
  4. **Hand-entered rent** (§1a): Mar → L16 prorated **$452.42** (`OPS-MAR-L16-MOVEIN`); Jul → L20 **$1495**
     (`OPS-JUL-L20-MOVEIN`).
  5. **Portal self-pay beats** (some months a tenant pays online — see `portalEvents`): Feb L03 (Zelle),
     Aug L12 (online) — recorded as that tenant's Paid rent.
- **Assertions:**
  - Every active lease-month has exactly **one** Paid rent row at the correct amount (no scan/worker
    double-count). Dashboard "collected this month" = Σ that month's rent (minus any delinquency, e.g.
    L14 Apr/May/Jun).
  - Scan-month scanned payers show a Paid rent sourced from the check (PayerName/CheckNumber populated).
- **Idempotency:** don't both mark-paid **and** scan the same lease-month. Re-running requires resetting
  that lease-month to one Paid row.

### TMPL-B3 — Rent-reminders + outbox drain  ·  actor: Engine  ·  fires: notice-draft, outbox
- **Goal / exercises:** end-of-month notice-draft run producing next-month `RentReminder` drafts, then
  draining the outbox (queued email/SMS).
- **Preconditions:** month M's rent recorded; clock at ~day 26–29 of M.
- **Surfaces:** `[DEV-CLOCK]` (advance clock) + `[WORKER]` (notice-draft, daily-briefing/outbox) — Engine only.
- **Steps:** **`[DEV-CLOCK]`** advance clock to ~day 28; **`[WORKER]`** fire `notice-draft` (creates
  `RentReminder` drafts for leases with rent due ≤7 d, i.e. next month's day-1 leases); fire
  `daily-briefing`/outbox drain if exercising email. *(Read the resulting drafts in `/notices` `[UI]`.)*
- **Assertions:** `RentReminder` drafts exist for the appropriate leases; with `NotifyTenants=true` +
  active template they auto-send and the outbox drains (queued notifications marked sent). No duplicate
  reminders on re-fire (idempotent).
- **Idempotency:** re-run reset = the daily-briefing outbox date key.

### Notice & renewal rhythm  ·  `OPS-<MON>-NOTICES`  ·  actor: Engine (draft) + Dana (approve/send)
- **Goal / exercises:** the monthly lease-expiry-reminder + renewal/M2M/move-out draft generation, and
  Dana approving & sending them per the §1b ladder.
- **Surfaces:** `[WORKER]` (lease-expiry-reminder, notice-draft) generates the drafts **+ `[UI]`** Dana
  approves & sends on **`/notices`** (Draft→Approved, portal/email/sms channels, Send); the non-renewal /
  renewal outcome is verified on the lease (`/leases/[id]`).
- **Mechanism (each month, ~day 10–17):** set clock; fire `lease-expiry-reminder` (leases with EndDate ≤ 60
  d, one-shot via `ExpiryReminderSentAt`) and `notice-draft` (Renewal/M2M ≤ 75 d, MoveOut ≤ 30 d). Then Dana
  opens `/notices`, reviews each draft, and **approves & sends** the renewals/M2M whose effective date is
  next month (per §1b). Held items (L14 renewal) wait for the cure beat.
- **Per-month draft/send (from the calendar + §1b):**
  | Month | Expiry reminders (≤60d) | Renewal/M2M drafts created | Dana approves & sends |
  |---|---|---|---|
  | Jan | L11(02-28), L01(03-31) | Renewal L11, L01 | (sent Feb) |
  | Feb | — | M2M L02(04-30), Renewal L13(04-30) | L11, L01, **L02 M2M**, L13 |
  | Mar | L02, L13 | Renewal L22(P13·B 05-31) | L22 renewal; set **L05 NonRenewal** (`OPS-MAR-L05-NONRENEW`) |
  | Apr | L05(05-31), L22(05-31) | Renewal L04(06-30), L17(06-30) | L04, L17 |
  | May | L04, L17 | **L05 MoveOutReminder** (≤30d), Renewal L06(07-31), **L14(07-31)** | L06; **L05 move-out**; **hold L14** |
  | Jun | L06, L14 | Renewal L09(08-31), L21(P13·A 08-31) | L09, L21 |
  | Jul | L09, L21 | Renewal L03(09-30), L15(09-30) | L03, L15 |
  | Aug | L03, L15 | Renewal L07(10-31), L18(10-31) | L07, L18 |
  | Sep | L07, L18 | Renewal L10(11-30) | L10; **send held L14 renewal** (`OPS-SEP-L14-RENEW`) |
  | Oct | L10 | Renewal L12(12-31), L19(12-31) | L12, L19 (**effective 2026**) |
  | Nov | L12, L19 | — | — |
  | Dec | — | — | — |
- **Assertions:** drafts of the right **type** appear for the right leases (Renewal vs MonthToMonth vs
  MoveOutReminder); after send, the renewed lease shows the new `EndDate` (+12 mo) and escalated rent from
  §1b; L02 becomes month-to-month (no fixed end); expiry reminders are one-shot (no re-send).
- **Idempotency:** re-run reset = clear `Lease.ExpiryReminderSentAt`.

---

## 3. Story beats (chronological)

Each beat is a discrete scenario. Format: **id — title** · goal · preconditions (sim_date) · actor ·
steps · assertions · worker fires · idempotency. Routine months' B1/B2/B3/NOTICES are covered by the
templates above; the beats below are the keyed one-off events.

**Beat execution surfaces** (the per-act rule applies — human actor ⇒ `[UI]`, `actor: Engine` ⇒
`[DEV-CLOCK]`+`[WORKER]`; `scenarios.json` carries the per-beat `surfaces` array). By category:

| Beat category | Surface | Screen `[UI]` |
|---|---|---|
| Insurance / tax / repair / make-ready **expense entry** | `[UI]` | `/accounting` + unit `ExpensesTab`; scanned receipts → `/scan/[draftId]` (`ConfirmAsExpense`) |
| **Payment** beats (L14 partials, NSF re-pay, portal self-pay) | `[UI]` | `/leases/[id]` "Mark paid" / `/accounting/past-due`; scanned checks → `/scan`; tenant → `/portal/payments` |
| **Late-fee / late-notice** (Engine) | `[DEV-CLOCK]`+`[WORKER]` | (fire `late-fee`, `notice-draft`; read result in `/notices`, `/leases/[id]`) |
| **Leasing** (public application, screening, approve/decline, adverse action) | `[UI]` | `/apply/[token]` (public), `/applications/[id]` (`ApplicationDetail`) |
| **Inspections** (MoveIn/MoveOut/AnnualSafety) | `[UI]` | `/maintenance`, `/maintenance/inspections/[id]`, unit `TurnoverTab` |
| **Move-out deposit** deduction/return | `[UI]` | `/deposits/[id]` (add-deduction / refund dialog) |
| **Maintenance intake** (photo / typed / voice) | `[UI]` | tenant `/portal/maintenance`; staff `/maintenance` |
| **Notices** approve & send | `[UI]` | `/notices` |
| **Non-renewal** auto-action set | `[UI]` | `/leases/[id]` (`LeaseDetail`) |
| **Owner statement** generate/view | `[UI]` | `/owners-report` |
| **Owner distribution** record (GAP2) | *no UI yet* | `PENDING GAP2` (PG-3) |

### January
**OPS-JAN-INSURANCE — Annual hazard insurance (direct P03/P08/P11)**
- Goal: enter the direct-pay hazard-insurance expenses (paid-off + condo-HO6 properties). Escrow-covered
  insurance for the other 10 properties is entered in April (`OPS-APR-INSURANCE`) on its disbursement date.
- Preconditions: Act 1 done; sim_date 2025-01-15. Actor: Priya.
- Steps: enter `Insurance` expenses, `escrow_covered=false`, IncurredAt 2025-01-15, vendor **Buckeye Mutual
  Insurance** — **P03 $780** (scan `generated/receipts/2025-01-15_P03_58211.jpg`), **P08 $680** (scan
  `2025-01-15_P08_58214.jpg`), **P11 $320** (hand-entered, HO-6).
- Assertions: 3 Insurance expense rows dated 2025-01-15; they feed Schedule E `Insurance` for P03/P08/P11
  (`expected/cy2025-by-property.json → byProperty.{P03,P08,P11}.scheduleE.deductibleByCategory.Insurance`
  = 780/680/320). Because P03/P08/P11 insurance is **direct** (not escrowed), it counts in NOI opex too
  (no DIV2 exclusion).
- Worker fires: none. Idempotency: one row per property.

**OPS-JAN-MAINT — Tenant portal maintenance request (L14 leaky faucet)**
- Goal: tenant self-service maintenance intake via the portal with a **photo** → scan→WorkOrder path.
- Preconditions: sim_date 2025-01-12; L14 tenant portal enabled. Actor: **Tenant: Lena Turner (L14)**.
- Steps: log in to the tenant portal; submit a maintenance request "leaky kitchen faucet" attaching the
  photo `generated/maintenance/P092_2025-01-12.jpg`. Fire `ScanProcessing` (auto).
- Assertions: a maintenance request / `ScanDraft` appears for P09·2 (L14) with the photo; visible to Priya
  in the work-queue. Sets up `OPS-JAN-REPAIR`.
- Worker fires: ScanProcessing (async). Idempotency: one request.

**OPS-JAN-REPAIR — WorkOrder → plumber → receipt scan → Expense (P09 Repairs $182.33)**
- Goal: convert the request to a WorkOrder, dispatch a vendor, and capture the invoice by receipt scan →
  `ConfirmAsExpense`.
- Preconditions: `OPS-JAN-MAINT` done; sim_date 2025-01-14. Actor: Priya.
- Steps: create a WorkOrder from the L14 request; dispatch vendor **Rooter Brothers Plumbing**; scan
  `generated/receipts/2025-01-14_P09_58201.jpg` and `ConfirmAsExpense` → **Repairs, P09·2, $182.33**,
  IncurredAt 2025-01-14.
- Assertions: WorkOrder linked to P09·2; Expense row `events.csv E01051` (Repairs, P09, 182.33, Rooter
  Brothers Plumbing). Contributes to P09 Schedule E `Repairs` (year total **427.33** = 182.33 + 245.00).
- Worker fires: ScanProcessing. Idempotency: confirm the draft once.

### February
**OPS-FEB-TAX-H1 — H1 property tax (all 13; escrow-disbursed + P03/P08 direct)**
- Goal: enter the first-half county property-tax disbursement for every property, with the escrow-covered
  flag set correctly (the DIV2 driver).
- Preconditions: sim_date 2025-02-05. Actor: Priya.
- Steps: enter `Taxes` expenses IncurredAt 2025-02-05, vendor **Clark County Treasurer**, = ½ annual tax
  per property (`events.csv` E01091–E01098+): P01 980, P02 1200, **P03 800 (direct, `escrow_covered=false`,
  scan `2025-02-05_P03_58209.jpg`)**, P04 1075, P05 1550, P06 1150, P07 1325, **P08 700 (direct, scan
  `2025-02-05_P08_58212.jpg`)**, P09 2100, P10 1225, P11 675, P12 1275, P13 1125. Escrow props →
  `escrow_covered=true`.
- Assertions: 13 Taxes rows for H1; combined with H2 (`OPS-JUL-TAX-H2`) they total each property's annual
  tax and portfolio **$30,360** (`vendor1099 → Clark County Treasurer`). Escrow props' taxes are excluded
  from NOI opex (DIV2) but counted by Schedule E/P&L.
- Worker fires: ScanProcessing (P03/P08 scans). Idempotency: one row per property per half.

**OPS-FEB-LEASEUP-APPS — List vacant P05·3 & P09·4; take 3 applications**  ·  weaves `GAP5`
- Goal: the leasing funnel intake — list two vacant units and accept public (no-login) rental
  applications, including the one that will be **declined**.
- Preconditions: sim_date 2025-02-01; P05·3 (L08 unit) and P09·4 (L16 unit) vacant. Actor: Tomás.
- Steps: list P05·3 and P09·4 as available; take applications via the public form / scan:
  **P05·3 → Ravi Anand** (`generated/applications/Ravi-Anand_P053.pdf`) and **Marcus Webb**
  (`Marcus-Webb_P053.pdf`, the to-be-declined applicant); **P09·4 → Sofia Marin**
  (`Sofia-Marin_P094.pdf`). Fire `ScanProcessing`.
- Assertions: 3 applications captured against the right units; each in a reviewable state for screening.
- **`GAP5` weave:** each applicant pays a **screening fee** (income before any lease). `PENDING GAP5 —
  application/screening-fee income; confirm exact fee-collection UI/endpoint (Notion
  390394b0689d81b4a5cde08d2e894ce6).` See `GAP5-APPFEE`. Additive → corpus revision.
- Worker fires: ScanProcessing. Idempotency: one application per applicant.

**OPS-FEB-SCREEN-L08 — Screen → approve Ravi Anand → Lease L08 (P05·3); decline Marcus Webb → adverse action**
- Goal: screening → approve creates Tenant + Lease; the declined applicant gets an FCRA
  `AdverseActionNotice`. (This is the P05·3 lease-up = **L08**, not "L21" — the design prose is stale.)
- Preconditions: `OPS-FEB-LEASEUP-APPS` done; sim_date 2025-02-10. Actor: Tomás (screen), Dana (approve).
- Steps: run `ScreeningResult` on both P05·3 applicants. **Approve Ravi Anand** → create Tenant + **Lease
  L08** (P05·3, rent **$850**, deposit $850, term 2025-03-01 → 2026-02-28, method Check, move-in 03-01).
  **Decline Marcus Webb** → generate & send an **AdverseActionNotice**.
- Assertions: Lease L08 exists (Active, future-dated move-in 2025-03-01); Ravi Anand is a Tenant; Marcus
  Webb is **not** a tenant and has a recorded AdverseActionNotice. P05·3 shows a pending move-in.
- Worker fires: none. Idempotency: approve once (creates the lease).

**OPS-FEB-SCREEN-L16 — Screen → approve Sofia Marin → Lease L16 (P09·4, prorated move-in)**  ·  weaves `GAP3`
- Goal: approve the P09·4 applicant → Lease L16, whose move-in (03-15) triggers a **prorated** first month.
- Preconditions: `OPS-FEB-LEASEUP-APPS` done; sim_date 2025-02-20. Actor: Tomás (screen), Dana (approve).
- Steps: screen Sofia Marin → approve → create Tenant + **Lease L16** (P09·4, rent **$825**, deposit $825,
  term 2025-03-15 → 2026-03-14, method ACH, move-in **2025-03-15**, first month prorated).
- Assertions: Lease L16 exists with move-in 2025-03-15; first-month proration is exercised in
  `OPS-MAR-L16-MOVEIN` / `GAP3-PRORATION`.
- Worker fires: none. Idempotency: approve once.

### March
**OPS-MAR-L08-MOVEIN — L08 move-in inspection (P05·3)**
- Goal: move-in inspection + occupancy flip + deposit for the first lease-up.
- Preconditions: `OPS-FEB-SCREEN-L08`; sim_date 2025-03-01. Actor: Tomás/Priya.
- Steps: perform a **MoveIn** inspection on P05·3; set Unit → Occupied; enter SDHolding **$850** (L08).
- Assertions: P05·3 Occupied; L08 active with a $850 deposit (register held rises by 850; `events.csv`
  E01134). L08's March rent is its first (worker-charged $850, `events.csv` E01112) — recorded in
  `OPS-MAR-B2`. Occupancy now 20/21.
- Worker fires: none. Idempotency: one inspection/deposit.

**OPS-MAR-L16-MOVEIN — L16 move-in inspection + prorated ½-March rent (P09·4)**  ·  `GAP3` prorated value
- Goal: mid-month move-in with a **prorated first-month rent** the worker cannot produce (GAP3).
- Preconditions: `OPS-FEB-SCREEN-L16`; sim_date 2025-03-15. Actor: Tomás/Priya.
- Steps: **MoveIn** inspection on P09·4; Unit → Occupied; SDHolding **$825** (L16); record the prorated
  first month rent = **$452.42** (17/31 days × $825), PeriodKey null.
- Assertions: L16 prorated rent Paid = **$452.42** (`events.csv E01118`); P09·4 Occupied; deposit register
  +825 (`E01145`). Occupancy 21/21 after 03-15. L16's April onward = full $825 (worker-charged, `E01159`).
- **`GAP3` weave:** the $452.42 is **hand-entered** in the spine. Intended: the app's proration computes
  it. `PENDING GAP3 — confirm proration UI/endpoint & rounding (Notion 390394b0689d81afa611c9ecd6289db8).`
  See `GAP3-PRORATION`.
- Worker fires: none. Idempotency: one prorated payment; don't also let the worker charge L16 a full March.

**OPS-MAR-L05-NONRENEW — Set L05 NonRenewal + send non-renewal notice (P04)**
- Goal: landlord-initiated non-renewal that sets up the May move-out + turnover.
- Preconditions: sim_date 2025-03-18. Actor: Dana.
- Steps: set **L05 `LeaseEndAutoAction=NonRenewal`**; send the non-renewal notice (EndDate 2025-05-31).
- Assertions: L05 flagged NonRenewal; a non-renewal notice is on file; the notice rhythm will produce a
  MoveOutReminder in May (`OPS-<MON>-NOTICES`). No renewal draft for L05.
- Worker fires: none. Idempotency: idempotent flag set.

**OPS-MAR-INSPECT-P07 — AnnualSafety inspection (P07 duplex)**
- Goal: routine annual-safety inspection (hand-entry).
- Preconditions: sim_date 2025-03-20. Actor: Priya.
- Steps: record an **AnnualSafety** inspection for P07 (both units).
- Assertions: an AnnualSafety inspection exists for P07 dated 2025-03-20. (One of 4 annual-safety
  inspections in CY2025: P07 Mar, P09+P05 Jul, P01 Dec.)
- Worker fires: RecurringMaintenance (optional). Idempotency: one record.

**OPS-MAR-OWNERSTMT-Q1 — Q1 owner statement → Marcus Bell (OE-BELL)**  ·  weaves `GAP2`
- Goal: generate the quarterly owner statement for the 8%-fee co-owned entity and (intended) record the
  actual distribution.
- Preconditions: Q1 rent/expenses recorded; sim_date 2025-03-31. Actor: Dana.
- Steps: generate the **OE-BELL** owner statement for Q1 (properties P05 + P07); review income − expenses −
  8% mgmt fee = net; deliver to Marcus (Owner portal).
- Assertions: the statement lists P05 + P07 with an 8% management fee line; the Q1 net is the computed
  distribution. (Year-end OE-BELL: income **56456.50**, expenses **10180**, mgmtFee **4516.52**, net
  **41759.98** — `expected/cy2025-portfolio.json → ownerStatements.OE-BELL`; Q1 is a slice.)
- **`GAP2` weave:** in the spine the actual cash-to-Marcus is **memo only** (`events.csv E01146`). Intended:
  record a real `OwnerDistribution`. `PENDING GAP2` — see `GAP2-OWNERDIST`. Idempotency: read/generate.
- Worker fires: none.

### April
**OPS-APR-INSURANCE — Escrow-disbursed hazard insurance (10 properties)**
- Goal: enter the escrow-disbursed annual insurance on its servicer-disbursement date, with
  `escrow_covered=true` (a DIV2 driver on the insurance side).
- Preconditions: sim_date 2025-04-15. Actor: Priya.
- Steps: enter `Insurance` expenses IncurredAt 2025-04-15, vendor **Buckeye Mutual Insurance**,
  `escrow_covered=true`, for the 10 escrow-covers-insurance properties (annualInsurance each): P01 820,
  P02 1050, P04 980, P05 1450, P06 1010, P07 1180, P09 2050, P10 1080, P12 1120, P13 1000.
- Assertions: 10 Insurance rows dated 2025-04-15; total insurance (with direct P03/P08/P11 from
  `OPS-JAN-INSURANCE`) = **$13,520** (`vendor1099 → Buckeye Mutual Insurance`). These 10 are **excluded**
  from NOI opex (DIV2) but counted by Schedule E/P&L.
- Worker fires: none. Idempotency: one row per property.

**OPS-APR-L14-PARTIAL — L14 April partial + first late fee + LateRentNotice (FIRST)**  ·  delinquency arc 1/4
- Goal: begin the L14 delinquency — a **Partial** rent payment, the flat late fee, and the first escalating
  late notice.
- Preconditions: `OPS-APR-B1` created L14's April Scheduled rent ($825); sim_date 2025-04-05 → advance to
  04-08. Actor: Priya (record partial), Engine (fees/notices).
- Steps:
  1. 2025-04-05: record L14 April rent as **Partial $400** of $825 (scanned check `L14_2025-04.jpg`) →
     `Payment(Rent, Partial, AmountPaid=400)`.
  2. Advance clock past due+grace (to 2025-04-08); fire `late-fee` → L14 April rent → **Late**, add
     `Payment(LateFee, $50)`; fire `notice-draft` → **LateRentNotice (FIRST, ≤7d late)**.
- Assertions: L14 April rent status **Partial** (owes 825−400 = **425**); a **$50** LateFee exists; a
  LateRentNotice (first) draft/sent. Delinquency aging shows L14 with $425 + $50 owing in the current
  bucket. (In the spine these all later **cure** — `events.csv` E01157/E01192 show them Paid on
  2025-09-05.)
- Worker fires: late-fee, notice-draft. Idempotency: late-fee keyed `(L14,LateFee,2025-04)`.

### May
**OPS-MAY-L14-MISS — L14 misses May + second late fee + LateRentNotice (SECOND)**  ·  delinquency arc 2/4
- Goal: escalate — a fully missed month, second late fee, second notice.
- Preconditions: `OPS-MAY-B1` created L14 May rent; sim_date 2025-05-03 (no payment) → 05-08. Actor: Priya
  (holds), Engine.
- Steps: do **not** record L14 May rent. Advance to 2025-05-08; fire `late-fee` (L14 May → Late, +$50) and
  `notice-draft` (**LateRentNotice SECOND, ≤20d**).
- Assertions: L14 May rent unpaid/Late; a second $50 LateFee; second LateRentNotice. Delinquency aging: L14
  now spans two months (April partial remainder + May full + 2 late fees). Cf. spine cure `events.csv`
  E01215/E01245.
- Worker fires: late-fee, notice-draft. Idempotency: keyed per period.

**OPS-MAY-L05-MOVEOUT — L05 move-out inspection + deposit deduction/return (P04 turnover 1/3)**
- Goal: the non-renewal move-out — move-out inspection finds damage → deposit deduction + partial refund →
  unit vacant.
- Preconditions: `OPS-MAR-L05-NONRENEW`; L05 May rent Paid (full $1450, move-out is last day); sim_date
  2025-05-31. Actor: Priya.
- Steps: perform a **MoveOut** inspection on P04 (finds carpet damage beyond wear); record **SDDeduction
  $450** (carpet) and **SDReturn $1000** (= $1450 held − $450); set Unit → Vacant.
- Assertions: L05 ended; P04 Unit Vacant; deposit register for L05 shows held 1450, deductions **450**,
  returned **1000**, balance **0** (`expected/cy2025-portfolio.json → deposits.byLease.L05`; `events.csv`
  E01248/E01249). May rent for L05 = full **$1450** (no proration — occupied through 05-31).
- Worker fires: none. Idempotency: one deduction + one return.

### June
**OPS-JUN-P04-MAKEREADY — P04 make-ready work orders + vendor receipts (turnover 2/3)**
- Goal: turnover make-ready — paint + carpet work orders with scanned vendor receipts; June is a **vacant**
  (zero-rent) month for P04.
- Preconditions: `OPS-MAY-L05-MOVEOUT`; sim_date 2025-06-02 (WOs) → 2025-06-10 (receipts). Actor: Priya.
- Steps: create make-ready WorkOrders (paint, carpet); dispatch vendors; on 2025-06-10 scan the two
  receipts and `ConfirmAsExpense`: **Freshcoat Painters — Repairs, P04, $680** ("interior repaint",
  `generated/receipts/2025-06-10_P04_58207.jpg`) and **Valley Carpet — CleaningMaintenance, P04, $520**
  ("carpet replacement", `2025-06-10_P04_58208.jpg`).
- Assertions: 2 P04 expenses dated 2025-06-10 ($680 Repairs + $520 CleaningMaintenance); P04 has **no rent**
  in June (worker charged 20, not 21). P04 Schedule E: `Repairs` includes 680, `CleaningMaintenance`
  includes 520 (plus lawn) — `expected/cy2025-by-property.json → byProperty.P04` (Repairs 680,
  CleaningMaintenance 1360 = 520 + 7×120 lawn).
- Worker fires: ScanProcessing. Idempotency: confirm each receipt once.

**OPS-JUN-L14-PARTIAL — L14 June partial + third late fee + LateRentNotice (FINAL)**  ·  delinquency arc 3/4
- Preconditions: `OPS-JUN-B1` created L14 June rent; sim_date 2025-06-06 (partial) → 06-09. Actor: Priya,
  Engine.
- Steps: 2025-06-06 record L14 June rent **Partial $400** (scanned check `L14_2025-06.jpg`). Advance to
  2025-06-09; fire `late-fee` (+$50) and `notice-draft` (**LateRentNotice FINAL, >20d**).
- Assertions: L14 June Partial (owes 425); third $50 LateFee; FINAL LateRentNotice. Aging spans Apr–Jun +
  3 late fees. Cf. spine cure `events.csv` E01259/E01289.
- Worker fires: late-fee, notice-draft. Idempotency: per period.

**OPS-JUN-L15-MAINT — Tenant portal maintenance (L15 AC not cooling — voice intake)**  ·  maintenance intake (voice)
- Goal: exercise **voice/typed** maintenance intake (vs the photo path) — one of the "both" intake methods.
- Preconditions: sim_date 2025-06-12. Actor: **Tenant: Grace Iverson (L15)**.
- Steps: submit a maintenance request "AC not cooling" via **voice** intake (typed transcription) — no
  photo — for P09·3. (`scenario.json → typedVoiceWorkOrders`: P09·3, voice, "AC not cooling".)
- Assertions: a maintenance request exists for P09·3 from voice/typed intake (no attached image); routes to
  Priya. Sets up `OPS-JUN-L15-REPAIR`.
- Worker fires: none (no scan). Idempotency: one request.

**OPS-JUN-L15-REPAIR — WorkOrder → HVAC vendor → receipt → Expense (P09 Repairs $245)**
- Preconditions: `OPS-JUN-L15-MAINT`; sim_date 2025-06-14. Actor: Priya.
- Steps: WorkOrder → vendor **CoolFlow HVAC**; scan `generated/receipts/2025-06-14_P09_58202.jpg` →
  `ConfirmAsExpense` **Repairs, P09·3, $245.00**.
- Assertions: Expense `events.csv E01292` (Repairs, P09, 245.00, CoolFlow HVAC); P09 `Repairs` year total
  **427.33** (182.33 + 245.00). Worker fires: ScanProcessing.

**OPS-JUN-P04-APP — New applicant for P04 → screen → Lease L20 (turnover 3/3)**  ·  weaves `GAP5`
- Goal: re-lease the turned-over unit — application → screening → approve → new lease.
- Preconditions: P04 vacant/make-ready; sim_date 2025-06-15 (list/app) → 2025-06-20 (approve). Actor: Tomás.
- Steps: list P04; take **Derek Cole**'s application (`generated/applications/Derek-Cole_P04U1.pdf`); screen
  → approve → create Tenant + **Lease L20** (P04, rent **$1495**, deposit $1495, term 2025-07-01 →
  2026-06-30, method Zelle, move-in 07-01).
- Assertions: Lease L20 exists (move-in 2025-07-01, replaces L05). **`GAP5` weave:** Derek pays a screening
  fee (income) — `PENDING GAP5`, see `GAP5-APPFEE`.
- Worker fires: ScanProcessing. Idempotency: approve once.

**OPS-JUN-OWNERSTMT-Q2 — Q2 owner statement → Marcus Bell (OE-BELL)**  ·  weaves `GAP2`
- Same shape as `OPS-MAR-OWNERSTMT-Q1`, sim_date 2025-06-30. `events.csv E01295` memo. `PENDING GAP2`.

### July
**OPS-JUL-L20-MOVEIN — L20 move-in inspection + first-month rent (P04)**
- Goal: turnover complete — move-in inspection, occupancy, deposit, first month rent (full month, **not**
  prorated — move-in is 07-01).
- Preconditions: `OPS-JUN-P04-APP`; sim_date 2025-07-01. Actor: Priya/Tomás.
- Steps: **MoveIn** inspection P04; Unit → Occupied; SDHolding **$1495** (L20); record L20 July rent
  **$1495** (hand-entered because the worker resumes P04 in Aug), method Zelle.
- Assertions: P04 Occupied; L20 July rent Paid **$1495** (`events.csv E01310`, full month — **no
  proration**); deposit register +1495 (`E01324`). P04 occupancy restored; worker resumes L20 in August
  (`E01372`). Note for `GAP3`: 07-01 move-in = full month, so proration must **not** apply here.
- Worker fires: none. Idempotency: one payment; don't double-charge July.

**OPS-JUL-TAX-H2 — H2 property tax (all 13; escrow + P03/P08 direct)**
- Same shape as `OPS-FEB-TAX-H1`, sim_date 2025-07-10, second-half amounts (equal to H1 halves). P03/P08
  scans `2025-07-10_P03_58210.jpg`, `2025-07-10_P08_58213.jpg`. Combined H1+H2 = **$30,360** portfolio.

**OPS-JUL-L14-LATE — L14 July late fee + notice (still behind)**  ·  delinquency arc (continues)
- Preconditions: `OPS-JUL-B1` created L14 July rent (renewal held → still $825 base until cure); sim_date
  2025-07-08. Actor: Engine.
- Steps: L14 July rent unpaid; fire `late-fee` (+$50) and `notice-draft` (FINAL). (July L14 scanned check
  `L14_2025-07.jpg` reflects still-partial/behind.)
- Assertions: 4th $50 LateFee (`events.csv` E01342); L14 aging spans Apr–Jul. Worker fires: late-fee,
  notice-draft.

**OPS-JUL-INSPECT — AnnualSafety inspections (P09 fourplex + P05 triplex)**
- Preconditions: sim_date 2025-07-15. Actor: Priya. Steps: record AnnualSafety inspections for P09 and P05.
- Assertions: AnnualSafety inspections exist for P09 and P05 dated 2025-07-15. Worker fires:
  RecurringMaintenance (opt).

### August
**OPS-AUG-NSF-L04 — L04 August check returns NSF → Failed → re-pay + NSF fee (P03)**
- Goal: the NSF/bounced-check beat (a minor path, no gap) — a Paid rent flips to **Failed**, the tenant
  re-pays, and a **$35 NSF fee** (`Other`-type income) is charged — the 4th-divergence driver.
- Preconditions: `OPS-AUG-B1`/`B2` (L04 Aug rent recorded Paid, renewed rent **$1184.50**); sim_date
  2025-08-04 → 08-06. Actor: Priya.
- Steps: L04's August rent check returns NSF → set that Payment **Paid → Failed**; tenant re-pays by
  **Check $1184.50** (`events.csv E01361`); add an **NSF fee $35** as `Payment(Other, Paid)` on 2025-08-06
  (`events.csv E01398`).
- Assertions: L04 August rent ends **Paid $1184.50** (via the re-payment); a separate **$35 Other** income
  row exists (P03). **Divergence:** the $35 is counted by **Property-P&L** (P03 `propertyPnl.income` =
  **14042**) but **not Schedule E** (P03 `scheduleE.income` = **14007**) — `expected/cy2025-by-property.json
  → divergenceChecks[P03].DIV1.nonScheduleEIncome = 35`. This is the 4th deliberate divergence.
- Worker fires: none. Idempotency: one NSF fee; the rent nets to one Paid.

**OPS-AUG-P02-ROOF — P02 roof leak → WorkOrder → roofer → receipt → Expense (Repairs $640)**
- Preconditions: sim_date 2025-08-15. Actor: Priya. Steps: WorkOrder for P02·A roof leak; vendor **Summit
  Roofing**; scan `generated/receipts/2025-08-15_P02_58203.jpg` + roof photo
  `generated/maintenance/P02A_2025-08-15.jpg` → `ConfirmAsExpense` **Repairs, P02, $640** (`events.csv
  E01402`).
- Assertions: P02 `Repairs` = **640** (`expected/cy2025-by-property.json → byProperty.P02`). Note: this is a
  **repair** (not a capital improvement); the capital-improvement path is the separate `GAP4-CAPIMPROVE`.
- Worker fires: ScanProcessing.

**OPS-AUG-L13-MAINT — Tenant portal maintenance (L13 disposal jammed — typed intake)**  ·  maintenance intake (typed)
- Preconditions: sim_date 2025-08-20. Actor: **Tenant: Anthony Brooks (L13)**. Steps: submit "Garbage
  disposal jammed" via **typed** intake (photo optional `generated/maintenance/P091_2025-08-20.jpg`) for
  P09·1; vendor slated **ApplianceCare** (`scenario.json → typedVoiceWorkOrders`).
- Assertions: a typed-intake maintenance request for P09·1. (No cent-exact expense required — a light beat
  demonstrating typed intake.) Worker fires: ScanProcessing if a photo is attached.

### September
**OPS-SEP-L14-CURE — L14 lump catch-up clears all arrears + late fees (delinquency arc 4/4, CURE)**  ·  spine terminal
- Goal: the delinquency **cure** — a lump portal payment clears April–August arrears + all late fees; L14
  returns to current. **This is the cent-exact spine terminal** (the eviction alternate is `GAP7-EVICTION`).
- Preconditions: arcs 1–3 done; sim_date 2025-09-05. Actor: **Tenant: Lena Turner (L14)** (portal).
- Steps: L14 makes a lump catch-up covering Apr partial remainder + May + Jun partial remainder + Jul + Aug
  rent + all 5 late fees; every affected `Partial`/`Late` row → **Paid** with PaidDate 2025-09-05.
- Assertions: L14's Apr–Aug rents and the **5 × $50 late fees** all show **Paid** dated 2025-09-05
  (`events.csv` E01157/E01192/E01215/E01245/E01259/E01289/E01305/E01342/E01367/E01399); L14 delinquency →
  **$0** as of 2025-09-05. Year-round the 5 late fees (**$250**) are the owner-statement-vs-Schedule-E
  delta for P09 (owner-stmt income 37123.67 vs Schedule E 37373.67). L14's **only** year-end delinquency is
  the **fresh December miss** ($899.75 — see `OPS-DEC-B1`/`REC-DELINQ`).
- Worker fires: none. Idempotency: mark the set Paid once at 2025-09-05.

**OPS-SEP-L14-RENEW — Send held L14 renewal (cured; term from 08-01)**
- Preconditions: `OPS-SEP-L14-CURE`; sim_date 2025-09-06. Actor: Dana. Steps: approve & send the **held**
  L14 renewal (rent → **$849.75**, term from 2025-08-01). Assertions: L14 renewed; Aug+ rent = $849.75
  (`events.csv` E01367 = 849.75). Worker fires: none.

**OPS-SEP-P06-REPAIR — P06 dishwasher repair → receipt → Expense (Repairs $210)**
- Preconditions: sim_date 2025-09-18. Actor: Priya. Steps: WorkOrder; vendor **ApplianceCare**; scan
  `generated/receipts/2025-09-18_P06_58204.jpg` → **Repairs, P06, $210** (`events.csv E01445`).
- Assertions: P06 `Repairs` = **210** (`expected/cy2025-by-property.json → byProperty.P06`).

**OPS-SEP-OWNERSTMT-Q3 — Q3 owner statement → Marcus Bell (OE-BELL)**  ·  weaves `GAP2`
- Same shape as Q1/Q2, sim_date 2025-09-30. `events.csv E01446` memo. `PENDING GAP2`.

### October
**OPS-OCT-B2 note — scan month (8 payers)**; renewals L12/L19 drafted (effective 2026) via
`OPS-OCT-NOTICES`. No unique one-off beats beyond the templates in October.

### November
**OPS-NOV-P13-REPAIR — P13 water-heater replacement → receipt → Expense (Repairs $985)**
- Preconditions: sim_date 2025-11-10. Actor: Priya. Steps: WorkOrder P13·A; vendor **Rooter Brothers
  Plumbing**; scan `generated/receipts/2025-11-10_P13_58205.jpg` → **Repairs, P13, $985** (`events.csv
  E01530`).
- Assertions: P13 `Repairs` = **985** (`expected/cy2025-by-property.json → byProperty.P13`). Note: treated as
  a **repair** in the spine; a like-kind big-ticket item is the candidate for `GAP4-CAPIMPROVE` (capital
  improvement depreciating on its own schedule).

**OPS-NOV-P08-CLEAN — P08 gutter cleaning → receipt → Expense (CleaningMaintenance $140)**
- Preconditions: sim_date 2025-11-20. Actor: Priya. Steps: vendor **Handy Hank LLC**; scan
  `generated/receipts/2025-11-20_P08_58206.jpg` → **CleaningMaintenance, P08, $140** (`events.csv E01533`).
- Assertions: P08 `CleaningMaintenance` = **140** (`expected/cy2025-by-property.json → byProperty.P08`).

### December
**OPS-DEC-INSPECT — AnnualSafety inspections (SFH P01/P06/P10/P12)**
- Preconditions: sim_date 2025-12-10. Actor: Priya. Steps: AnnualSafety inspections for P01, P06, P10, P12.
- Assertions: 4 AnnualSafety inspections dated 2025-12-10. Worker fires: RecurringMaintenance (opt).

**OPS-DEC-L14-MISS — L14 December miss → late fee (year-end delinquency)**  ·  spine year-end state
- Goal: the single fresh delinquency carried into year-end — L14 misses December and takes one late fee,
  leaving exactly **$899.75** owing at 2025-12-31.
- Preconditions: `OPS-DEC-B1` created L14 Dec rent ($849.75); sim_date 2025-12-08. Actor: Engine.
- Steps: L14 December rent left unpaid; fire `late-fee` (+$50, `events.csv E01570`, **Late/unpaid at
  year-end**). Do **not** cure.
- Assertions: L14 year-end owed = **$899.75** = Dec rent 849.75 + late fee 50 (`expected/cy2025-portfolio.json
  → delinquencyAtYearEnd`: L14 total 899.75, all **current** bucket). This is the **only** year-end
  delinquency. Worker fires: late-fee, notice-draft.

**OPS-DEC-OWNERSTMT-Q4 — Q4 owner statement → Marcus Bell (OE-BELL)**  ·  weaves `GAP2`
- sim_date 2025-12-31. `events.csv E01573` memo. `PENDING GAP2`. Year-end OE-BELL net **41759.98**.

### Act 2 → Act 3 handoff
By 2025-12-31: all 12 months of rent/expense/loan/notice cycles run; L14 cured in Sep then a fresh Dec
miss; P04 turned over (L05 out, L20 in); L08/L16 leased up; 4 quarterly OE-BELL statements; all one-off
repairs and taxes/insurance booked. Leave the clock **frozen at 2025-12-31** and proceed to Act 3.

---

## 4. Gap-feature spotlights

These make a gap feature the **star**. Author to intended behavior; run in the **gap pass** (as each
feature lands). `GAP1` (bulk import) is in Act 1 (`ONB-08`).

**Surfaces (all gap spotlights):** each is an intended **`[UI]`** action whose exact screen/control **lands
with the feature** — so today the surface is *"no UI yet"* (`PENDING GAP<N>`, the DoD "build the UI +
wire it" clause). Do **not** API around a missing feature UI; the scenario waits for the screen. Exceptions:
**GAP2** owner-distribution has no record UI today (PG-3); **STRIPE-AUTOPAY** is `[UI]` (autopay enrollment +
Stripe test-mode entry) **+ `[WORKER]`** (fire `autopay`) and is off-ledger. Confirm each control's exact
label in the running app when its feature ships.

### GAP2-OWNERDIST — Owner-distribution records (real `OwnerDistribution`)  ·  `PENDING GAP2`
- Goal / exercises: recording an **actual cash distribution** to an owner as a first-class
  `OwnerDistribution` record (not a memo, not an Expense), and reconciling cash-distributed vs computed net.
- Preconditions: the 4 quarterly OE-BELL statements (`OPS-*-OWNERSTMT-Q*`). Actor: Dana. sim_dates
  2025-03-31 / 06-30 / 09-30 / 12-31.
- Steps: after each OE-BELL quarterly statement, record an `OwnerDistribution` to **Bell-Okafor Holdings
  LLC** = that quarter's computed net (Dana wires Marcus). `PENDING GAP2 — confirm the OwnerDistribution
  create UI/endpoint + which report surfaces it (Notion 390394b0689d8149b906d099889ed32f).`
- Assertions (intended): 4 `OwnerDistribution` records for OE-BELL summing to the year net **$41,759.98**
  (`expected/cy2025-portfolio.json → ownerStatements.OE-BELL.netToOwner`); an **Owner Distributions
  report** shows cash-distributed = computed net (0 variance), and the distributions do **not** appear as
  P&L expenses (P05/P07 expense totals unchanged: 5450 / 4730). Spine keeps these as memo (`events.csv`
  E01146/E01295/E01446/E01573).
- Corpus impact: **additive** (new records; existing P&L/Schedule E unchanged). No cent-exact spine change;
  a corpus revision adds the distribution records + the owner-distributions reconciliation.

### GAP3-PRORATION — App-computed move-in/out proration  ·  `PENDING GAP3`
- Goal / exercises: the app computing partial-month rent on move-in/out instead of hand-entry.
- Preconditions: L16 (P09·4) move-in 2025-03-15; L05 (P04) move-out 2025-05-31; L20 (P04) move-in
  2025-07-01. Actor: Priya. sim_dates as above.
- Steps: at L16 move-in, let the app **prorate** the first month (03-15 → 03-31). Verify it does **not**
  prorate the full-month boundaries L05 (occupied through 05-31) and L20 (move-in 07-01).
- Assertions (intended): L16 first-month rent = **$452.42** (17/31 × $825, app-computed = `events.csv
  E01118` hand-entered value); L05 May = full **$1450**; L20 July = full **$1495**. `PENDING GAP3 — confirm
  proration rounding/day-count (inclusive 17 days) + UI (Notion 390394b0689d81afa611c9ecd6289db8).`
- Corpus impact: **value-preserving** for L16 (the app should reproduce the hand-entered $452.42); this is
  the one true proration in the corpus. If the app's day-count/rounding differs, that's a corpus-revision
  decision, not a spine bug.

### GAP4-CAPIMPROVE — Capital-improvement depreciation  ·  `PENDING GAP4` · off-spine (net-new)
- Goal / exercises: a capital improvement that **depreciates on its own schedule** instead of being expensed
  as Repairs (the year-end packet currently warns "may be a capital improvement").
- Preconditions: an eligible property; a big-ticket improvement. Actor: Dana/Priya. sim_date (proposed)
  2025-08-01.
- Steps (intended): add a **capital improvement** — e.g. a **$9,900 roof replacement on P09** placed in
  service 2025-08-01 — recorded as a depreciating asset (27.5-yr residential improvement, mid-month
  convention), **not** a Repairs expense. `PENDING GAP4 — confirm the capital-improvement entry UI, class
  life, and convention (Notion 390394b0689d81a4a91cedfd9a77a0af).`
- Assertions (intended, TBD-formalized): a new depreciation line for the improvement; 2025 partial-year
  depreciation ≈ **$135** (9900/27.5 = 360/yr × 4.5/12 mid-month for an Aug in-service); the $9,900 does
  **not** appear in Schedule E `Repairs`; year-end total depreciation rises by the partial-year amount.
- Corpus impact: **net-new** — not in the current `expected/*.json`. Off the cent-exact spine; a corpus
  revision formalizes the exact asset/amount/date and the new depreciation figures.

### GAP5-APPFEE — Application / screening-fee income  ·  `PENDING GAP5` · additive
- Goal / exercises: recording **application/screening-fee income before any lease exists** (today a Payment
  requires a LeaseId, so applicant fees are unrepresentable).
- Preconditions: the applicants from the leasing funnel — **Ravi Anand** (L08), **Sofia Marin** (L16),
  **Derek Cole** (L20), and the **declined Marcus Webb**. Actor: Tomás. sim_dates 2025-02-01 / 06-15.
- Steps (intended): collect a **screening fee** (e.g. **$45**) from each applicant at application time as
  income tied to the applicant/unit (not a lease); the **declined** applicant's fee is still income
  (non-refundable screening). `PENDING GAP5 — confirm fee amount + the applicant-fee income UI/endpoint +
  which report bucket (Notion 390394b0689d81b4a5cde08d2e894ce6).`
- Assertions (intended): 4 application-fee income entries (~$45 each = ~$180) attributed to P05·3 / P09·4 /
  P04; they appear in the appropriate income report **without** a lease. Marcus Webb's fee is retained
  despite the decline.
- Corpus impact: **additive** income → corpus revision (adds to Schedule E / income totals; the exact fee
  and bucket are confirmed when the feature lands).

### GAP6-SALE — Property sale / disposition with gain-loss + §1250 recapture  ·  `PENDING GAP6` · off-spine (net-new)
- Goal / exercises: selling a property mid/late CY2025 — sale-year depreciation, gain/loss, and §1250
  depreciation recapture in the year-end reports (year-end explicitly doesn't compute these today).
- Preconditions: a property to dispose. **Proposed: sell P08** (540 Sycamore, OE-PERS, **paid-off** → no
  mortgage payoff at closing; tenant L12 lease ends 2025-12-31). Actor: Dana. sim_date (proposed)
  2025-10-31.
- Steps (intended): list P08 for sale; **close** at a sale price (proposed **$135,000**) on 2025-10-31;
  the app computes: sale-year (partial) depreciation to the disposition date, **adjusted basis** = purchase
  99,000 − accumulated depreciation, **gain** = sale price − adjusted basis − selling costs, and **§1250
  recapture** on the depreciation taken. Rent stops after the sale; L12 is closed/transferred. `PENDING
  GAP6 — confirm the disposition UI/endpoint, selling-cost handling, and recapture computation (Notion
  390394b0689d81b3ba3fc75459f29b7c).`
- Assertions (intended, TBD-formalized): P08 shows a disposition on 2025-10-31; year-end reports show
  sale-year depreciation (mid-month to Oct), a computed gain, and §1250 recapture; P08 rent ends after Oct;
  occupancy/rent-roll drop P08.
- Corpus impact: **net-new + spine-disrupting** for P08 (removes Nov/Dec rent, changes depreciation,
  occupancy). Off the cent-exact spine; requires a corpus revision (choose the property/price/date and
  regenerate `expected/*.json`). Until then, the spine keeps P08 held all year (income 12600, no sale).

### GAP7-EVICTION — First-class eviction (L14 escalation)  ·  `PENDING GAP7` · alternate to the spine cure
- Goal / exercises: escalating the P09·2 / L14 delinquency into a **real eviction** path/record (delinquency
  → notices → eviction filing → resolution), instead of the spine's September cure.
- Preconditions: L14 delinquency arcs 1–3 (`OPS-APR/MAY/JUN-L14-*`). Actor: Dana/Priya. sim_dates from
  ~2025-07 onward.
- Steps (intended): rather than curing on 2025-09-05, escalate — file an **eviction** case/record against
  L14, track it through filing → hearing → resolution (judgment / move-out / settlement). `PENDING GAP7 —
  confirm the eviction entity, its states, and which reports surface it (Notion 390394b0689d81d3a215e7d0bfbf667c).`
- Assertions (intended): an eviction record exists for L14 with a state timeline; the tenant ledger and
  delinquency aging reflect the unresolved arrears (no September cure); year-end delinquency for L14 is
  **much larger** than the spine's $899.75 (it accrues Apr–Dec unpaid).
- Corpus impact: **reworks the spine.** This **supersedes** `OPS-SEP-L14-CURE` / `OPS-SEP-L14-RENEW` and
  changes year-end delinquency, income, and possibly a move-out/turnover. Requires a corpus revision (the
  frozen `scenario.json` currently curing L14 must be re-authored to the eviction arc). Do not run both the
  cure and the eviction against the same DB.

### STRIPE-AUTOPAY-L09 / STRIPE-AUTOPAY-L17 — Real Stripe test-mode autopay  ·  off the reconciliation ledger
- Goal / exercises: the **real Stripe test-mode** autopay path for the 2 enrolled leases — enrollment →
  `AutopayChargeWorker` → Stripe test charge → webhook → Payment Paid — with **async settlement timing**.
- Preconditions: Stripe **test mode** wired (`Stripe:Enabled=true`, test keys) and Dana logged into Stripe
  via Chrome (per the integrations decision). Actor: Dana (setup) + Engine (autopay fire) +
  Tenant:B.Kowalski (L09) / Tenant:N.Adeyemi (L17). sim_date any month day-1.
- Steps: enroll **L09** (P06, $1,395 / renewed $1,436.85) and **L17** (P10, $1,525 / renewed $1,570.75) in
  autopay (`AutopayEnrollment`) with a **Stripe test card** (e.g. `4242 4242 4242 4242`); on month day-1
  fire `autopay` (`POST /api/v1/dev/workers/autopay/run-once`) → a real Stripe **test-mode** charge →
  webhook → the lease's rent `Payment` → **Paid**.
- Assertions: the autopay charge appears in the **Stripe test dashboard** with the matching amount; after
  the webhook settles, the app shows L09/L17 rent **Paid** via autopay for that month. Reconcile against
  **Stripe's recorded test amount**, allowing for **webhook/settlement latency** (not instant) — do **not**
  assert cent-exact instantaneous like the spine.
- Corpus impact: **off-ledger** — the cent-exact spine keeps L09/L17 marked Paid deterministically
  (`scenario.json → storyBeats.autopayStripeScenario`). This scenario is **path coverage**, run
  independently; it is not part of Act 3 reconciliation.
- `PENDING` note: this depends on the integrations test-env task (Notion 390394b0689d818ab71cc6d9d74b1b63)
  being wired. Confirm the exact enrollment UI + test-card entry surface in the running app.
