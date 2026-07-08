# E2E Operations Calendar — CY2025 (replay spine)

> ⚠️ **STALE lease numbering — defer to the authoritative catalog.** This calendar predates the frozen
> `scenario.json`. It calls the **P05·3** March lease-up "L21" and says "L20/L21/L22 = new leases created
> live" — that is **wrong** and, if executed literally, "create L21 for P05·3" would collide with the
> existing P13 tenant L21. **Authoritative:** **L08** (P05·3) and **L16** (P09·4) are the CY2025 lease-ups,
> **L20** is the P04 turnover (replaces L05), and **L21/L22** are *existing* P13 duplex tenants (rent back
> to 2023). The **Tester Scenario Catalog** (`e2e/corpus/scenarios/README.md` + `scenarios.json`) is now the
> authoritative tester + replay instruction set; **this calendar defers to it on any conflict.** Full prose
> reconciliation happens in the post-features corpus revision.

Companion to `2026-06-30-e2e-scenario-corpus-design.md` (§5). This is the full, deterministic
~104-row operations calendar the replay driver executes, ~2 events/week across CY2025, weaving in
every story beat from the design doc §3 (lease-expiry ladder, L05 non-renewal + P04 turnover, L14
delinquency arc, L21/L22 lease-ups + one declined applicant/adverse action, portal activity,
inspections, one-off repairs, NSF, quarterly owner statements).

**Read the design doc first** for the fixed scenario (§2 portfolio, §3.2 lease ladder, §8.7 worker
rules). Time control + worker triggering are specified in the sibling
`2026-07-01-master-simulation-clock-design.md` (TSK-615).

## Standing rules (legend — not calendar rows)

**Actor key:** Dana = Admin (owner-operator) · Priya = Manager · Tomás = Agent (leasing) ·
`Tenant:<name>` = portal user · **Engine** = set the master clock to `sim_date` and fire the named
worker(s) (via the TSK-615 dev worker-trigger command).

**Per-simulated-day worker fire order (design §5.3):** `RentChargeWorker → NoticeDraftWorker
(reminders) → advance clock past due+grace → LateFeeWorker + NoticeDraftWorker(late) →
AutopayChargeWorker → DebtServiceWorker / RecurringExpenseWorker / RecurringMaintenanceWorker →
OutboxDispatchWorker`. All idempotent on `(LeaseId/LoanId, Type, PeriodKey)`; re-run reset =
`Lease.ExpiryReminderSentAt` + daily-briefing outbox date key.

**Config set once at T0 (2025-01-01):** `PUT /api/v1/notifications/settings` (full-object
overwrite — GET, mutate, PUT) with `EnableRentCharges=ON`, `EnableLateFees=ON`, `NotifyTenants=ON`;
`EnableLeaseExpiryReminders` and notice-autopilot/recurring-maintenance are already on. Grace = 5
days; `LateFeeAmount` flat = $50; `LeaseExpiryReminderDays` = 60; NoticeDraft windows =
Renewal/M2M ≤ 75 d, MoveOut ≤ 30 d, RentReminder ≤ 7 d.

**Monthly baseline (3 rows shown every month; contents vary):** **B1** (day 1) Engine month-open
batch; **B2** (day ~3–6) record rent Paid by method; **B3** (day ~26–29) Engine RentReminders
(next month) + Outbox drain.

**Autopay leases (AutopayChargeWorker; needs Stripe + Active `AutopayEnrollment`):** **L09** (P06,
$1,395) and **L17** (P10, $1,525) auto-charge → Paid on day 1. The other 19 are recorded manually.

**Rent-due-day variety:** day 5 → L07, L12; day 15 → L10, L18; day 1 → all others (incl. L14).

**Recurring-expense templates (RecurringExpenseWorker):** HOA monthly (P11); trash monthly
(multifamily P02/P05/P07/P09/P13); lawn Apr–Oct (landlord-maintained SFH P01/P04/P06/P10/P12); pest
quarterly (Jan/Apr/Jul/Oct, multifamily); P03/P08 direct insurance (annual, Jan) + direct property
tax (semi-annual, Feb/Jul). Escrow-property Taxes/Insurance are **hand-entered** on
servicer-disbursement dates with `escrow_covered=true` (design §8.5).

**Rent-check scan policy:** the 7 scannable Check/MoneyOrder payers (L01, L04, L07·MO, L11, L14,
P13·A, P13·B·MO) are photographed & confirmed in **5 sample months (Jan, Apr, Jul, Oct, Dec)**;
other months those payers are recorded manually. **IMPORTANT execution constraint (design §9,
item 12):** confirming a scanned rent check creates a *new* Payment; if the rent-charge worker also
charged that lease-month, that double-counts. Resolution for the replay: in the 5 scan months, the
scanned-check `ConfirmAsPayment` **replaces** marking that month's worker charge Paid (waive/delete
the worker's Scheduled duplicate for those lease-months), so each lease-month has exactly one Paid
rent row. All other months use worker-charge + `mark-paid`.

**Active-lease count feeding RentChargeWorker:** Jan–Feb 19 → Mar 20 (+L21) → Apr–May 21 (+L22) →
Jun 20 (L05 out, P04 vacant) → Jul–Dec 21 (+L20). Proration hand-entered (worker only charges full
months, GAP-3): L22 half-March, L20 first month.

---

### January 2025
| sim_date | actor | action | input artifact(s) | worker dep. |
|---|---|---|---|---|
| 2025-01-01 | Dana | **T0 onboarding**: wizard → portfolio + 4 owner entities + 5 users; confirm 19 leases (lease-scan, **RentTrackingStartMode=BackfillFromLeaseStart**) + 11 loans (mortgage-stmt scan → ConfirmAsLoan); PUT notification settings; set each property's `ownerEntityId` explicitly | 19 lease PDFs, 11 mortgage-stmt JPEGs | lease-create back-fills 2023–24 rent synchronously; fire DebtService+RecurringExpense for loan/expense history |
| 2025-01-01 | Engine | **B1** — RentCharge: Jan Rent(Scheduled) ×19 (PeriodKey 2025-01) → Autopay L09+L17→Paid → DebtService ×11 → RecurringExpense: HOA(P11), trash×5, **pest Q1**×5 | — | RentCharge, Autopay, DebtService, RecurringExpense |
| 2025-01-02 | Priya | Enter annual hazard-insurance premiums, all 13 props (`/expenses`; escrow props `escrow_covered=true`; P03/P08 direct) | — | — |
| 2025-01-06 | Priya | **B2** — scan 7 Jan rent checks/MOs → ConfirmAsPayment Paid (replaces worker-charge for these lease-months); ACH/Cash/Zelle payers mark-paid | 7 rent-check/MO JPEGs | ScanProcessing |
| 2025-01-12 | Tenant:L. Turner (L14) | **Portal** — submit maintenance request: leaky kitchen faucet (photo) | maintenance photo | ScanProcessing |
| 2025-01-14 | Priya | WorkOrder from Turner request → dispatch plumber (Vendor); scan invoice → ConfirmAsExpense (Repairs, P09) | receipt JPEG | ScanProcessing |
| 2025-01-15 | Engine | LeaseExpiryReminder: L11(02-28)+L01(03-31) ≤60d; NoticeDraft: Renewal drafts L11(+3%→$1,185), L01(+3%→$1,288) | — | LeaseExpiryReminder, NoticeDraft |
| 2025-01-28 | Engine | **B3** — NoticeDraft RentReminders (Feb ≤7d); Outbox drain | — | NoticeDraft, Outbox |

### February 2025
| sim_date | actor | action | input artifact(s) | worker dep. |
|---|---|---|---|---|
| 2025-02-01 | Engine | **B1** — RentCharge ×19 → Autopay L09+L17 → DebtService ×11 → RecurringExpense: HOA, trash×5 | — | RentCharge, Autopay, DebtService, RecurringExpense |
| 2025-02-01 | Tomás | List vacant **P05·3** & **P09·4**; take public applications — P05·3 ×2 (A,B), P09·4 ×1 | 3 application PDFs | ScanProcessing |
| 2025-02-04 | Priya | **B2** — mark Feb rent Paid by method (no scan month) | — | — |
| 2025-02-05 | Priya | Enter **H1 property-tax** on county disbursement: escrow loans (`escrow_covered=true`) + P03/P08 direct (scan PropertyTax bills) | 2 property-tax bill JPEGs | ScanProcessing |
| 2025-02-10 | Tomás | Screening → **approve P05·3 applicant A** → Tenant + **Lease L21** (move-in 03-01); **decline B → AdverseActionNotice** | new lease PDF (L21) | — |
| 2025-02-10 | Tenant:S. Klein (L03) | **Portal** — pay Feb rent online (Zelle) → Paid | — | — |
| 2025-02-14 | Engine | NoticeDraft (75d): **M2M** draft L02(04-30); Renewal draft L13(04-30) | — | NoticeDraft |
| 2025-02-20 | Tomás | Screening P09·4 → approve → Tenant + **Lease L22** (move-in 03-15) | new lease PDF (L22) | — |
| 2025-02-25 | Dana | `/notices` → approve & send: L11 renewal, L01 renewal, **L02 M2M**, L13 renewal | — | (consumes drafts) |
| 2025-02-28 | Engine | **B3** — RentReminders (Mar) + Outbox | — | NoticeDraft, Outbox |

### March 2025
| sim_date | actor | action | input artifact(s) | worker dep. |
|---|---|---|---|---|
| 2025-03-01 | Engine | **B1** — RentCharge ×**20** (incl L21) → Autopay L09+L17 → DebtService ×11 → RecurringExpense: HOA, trash×5 | — | RentCharge, Autopay, DebtService, RecurringExpense |
| 2025-03-01 | Tomás/Priya | **L21 move-in inspection** (MoveIn, P05·3); Unit→Occupied; SDHolding entered | inspection (hand) | — |
| 2025-03-05 | Priya | **B2** — mark Mar rent Paid (no scan month) | — | — |
| 2025-03-10 | Engine | LeaseExpiryReminder: L02(04-30), L13(04-30) ≤60d; NoticeDraft: Renewal draft P13·B(05-31) | — | LeaseExpiryReminder, NoticeDraft |
| 2025-03-15 | Tomás/Priya | **L22 move-in inspection** (MoveIn, P09·4); **hand-enter prorated half-March rent** ≈$452 (17/31×$825, PeriodKey null); SDHolding | inspection (hand) | — (worker can't prorate) |
| 2025-03-18 | Dana | Set **L05 `LeaseEndAutoAction=NonRenewal`** + send non-renewal notice; approve P13·B renewal | — | — |
| 2025-03-20 | Priya | **AnnualSafety inspection #1** — P07 duplex | inspection (hand) | — |
| 2025-03-25 | Tenant:R. Delgado (L02) | **Portal** — Conversation message re: M2M terms | — | — |
| 2025-03-28 | Engine | **B3** — RentReminders (Apr) + Outbox | — | NoticeDraft, Outbox |
| 2025-03-31 | Dana | **Q1 owner statement** → Marcus Bell (OE-BELL, 8% fee) | — | — |

### April 2025
| sim_date | actor | action | input artifact(s) | worker dep. |
|---|---|---|---|---|
| 2025-04-01 | Engine | **B1** — RentCharge ×**21** (incl L22) → Autopay L09+L17 → DebtService ×11 → RecurringExpense: HOA, trash×5, **lawn×5 begins**, **pest Q2** | — | RentCharge, Autopay, DebtService, RecurringExpense |
| 2025-04-05 | Priya | **B2** — scan 7 Apr rent checks/MOs → confirm; **L14 April = PARTIAL** ($400/$825, scanned) → Payment(Partial) | 7 rent-check/MO JPEGs | ScanProcessing |
| 2025-04-08 | Engine | LateFee: **L14** ($50), April rent→Late; NoticeDraft: **LateRentNotice (FIRST, ≤7d)** | — | LateFee, NoticeDraft |
| 2025-04-10 | Tenant:E. Santos (L10) | **Portal** — Conversation message (yard question) | — | — |
| 2025-04-12 | Engine | LeaseExpiryReminder: L05(05-31), P13·B(05-31) ≤60d; NoticeDraft: Renewal drafts L04(06-30), L17(06-30) | — | LeaseExpiryReminder, NoticeDraft |
| 2025-04-16 | Dana | Approve & send L04 + L17 renewals (autopay continues) | — | (consumes drafts) |
| 2025-04-28 | Engine | **B3** — RentReminders (May) + Outbox | — | NoticeDraft, Outbox |

### May 2025
| sim_date | actor | action | input artifact(s) | worker dep. |
|---|---|---|---|---|
| 2025-05-01 | Engine | **B1** — RentCharge ×**21** (L02 now M2M; L13 renewed) → Autopay L09+L17 → DebtService ×11 → RecurringExpense: HOA, trash×5, lawn×5 | — | RentCharge, Autopay, DebtService, RecurringExpense |
| 2025-05-01 | Engine | NoticeDraft: **L05 MoveOutReminder** (≤30d); Renewal drafts L06(07-31), **L14**(07-31) | — | NoticeDraft |
| 2025-05-03 | Priya | **B2** — mark May rent (no scan); **L14 MISSES May** (no payment) | — | — |
| 2025-05-05 | Dana | Approve & send L06 renewal + L05 move-out; **hold L14 renewal** pending cure | — | (consumes drafts) |
| 2025-05-08 | Engine | LateFee: **L14 May**; NoticeDraft: **LateRentNotice (SECOND)** | — | LateFee, NoticeDraft |
| 2025-05-15 | Engine | LeaseExpiryReminder: L04(06-30), L17(06-30) ≤60d | — | LeaseExpiryReminder |
| 2025-05-28 | Engine | **B3** — RentReminders (Jun) + Outbox | — | NoticeDraft, Outbox |
| 2025-05-31 | Priya | **L05 move-out** (P04): **MoveOut inspection** finds carpet damage | inspection (hand) | — |
| 2025-05-31 | Priya | Security deposit: **SDDeduction** (carpet) + **SDReturn** (partial refund); Unit→Vacant | — | — |

### June 2025
| sim_date | actor | action | input artifact(s) | worker dep. |
|---|---|---|---|---|
| 2025-06-01 | Engine | **B1** — RentCharge ×**20** (**P04 vacant → zero rent**) → Autopay L09+L17 → DebtService ×11 → RecurringExpense: HOA, trash×5, lawn×5 | — | RentCharge, Autopay, DebtService, RecurringExpense |
| 2025-06-02 | Priya | **P04 make-ready** WorkOrders (paint, carpet); dispatch vendors | — | — |
| 2025-06-06 | Priya | **B2** — scan **L14 June PARTIAL** ($400/$825); others mark-paid | 1 rent-check JPEG (L14) | ScanProcessing |
| 2025-06-09 | Engine | LateFee: **L14 June**; NoticeDraft: **LateRentNotice (FINAL, >20d)** | — | LateFee, NoticeDraft |
| 2025-06-10 | Priya | Scan P04 make-ready receipts (paint, carpet) → ConfirmAsExpense (Repairs + CleaningMaintenance, P04) | 2 receipt JPEGs | ScanProcessing |
| 2025-06-12 | Tenant:G. Iverson (L15) | **Portal** — maintenance request: AC not cooling (photo) | maintenance photo | ScanProcessing |
| 2025-06-13 | Engine | LeaseExpiryReminder: L06(07-31), L14(07-31) ≤60d; NoticeDraft: Renewal drafts L09(08-31), P13·A(08-31) | — | LeaseExpiryReminder, NoticeDraft |
| 2025-06-14 | Priya | WorkOrder from Iverson → HVAC vendor; scan receipt → Expense (Repairs, P09) | receipt JPEG | ScanProcessing |
| 2025-06-15 | Tomás | List P04; take public application → scan | application PDF | ScanProcessing |
| 2025-06-18 | Dana | Approve & send L09 + P13·A renewals | — | (consumes drafts) |
| 2025-06-20 | Tomás | Screening P04 applicant → approve → Tenant + **Lease L20** (move-in 07-01) | new lease PDF (L20) | — |
| 2025-06-28 | Engine | **B3** — RentReminders (Jul) + Outbox | — | NoticeDraft, Outbox |
| 2025-06-30 | Dana | **Q2 owner statement** → Marcus Bell (OE-BELL) | — | — |

### July 2025
| sim_date | actor | action | input artifact(s) | worker dep. |
|---|---|---|---|---|
| 2025-07-01 | Engine | **B1** — RentCharge ×**20** (P04 occupied but L20 first month hand-entered) → Autopay L09+L17 → DebtService ×11 → RecurringExpense: HOA, trash×5, lawn×5, **pest Q3** | — | RentCharge, Autopay, DebtService, RecurringExpense |
| 2025-07-01 | Priya/Tomás | **L20 move-in inspection** (MoveIn, P04); Unit→Occupied; SDHolding; **hand-enter L20 July rent** $1,495 (worker resumes Aug) | inspection (hand) | — |
| 2025-07-05 | Priya | **B2** — scan 7 Jul rent checks/MOs (incl L14 still-partial) → confirm | 7 rent-check/MO JPEGs | ScanProcessing |
| 2025-07-08 | Engine | LateFee: **L14 Jul**; NoticeDraft: LateRentNotice (FINAL) | — | LateFee, NoticeDraft |
| 2025-07-10 | Priya | Enter **H2 property-tax** (escrow disbursement + P03/P08 direct) | 2 property-tax bill JPEGs | ScanProcessing |
| 2025-07-13 | Engine | LeaseExpiryReminder: L09(08-31), P13·A(08-31) ≤60d; NoticeDraft: Renewal drafts L03(09-30), L15(09-30) | — | LeaseExpiryReminder, NoticeDraft |
| 2025-07-15 | Priya | **AnnualSafety inspections #2** — P09 fourplex + P05 triplex units | inspections (hand) | RecurringMaintenance (opt) |
| 2025-07-17 | Dana | Approve & send L03 + L15 renewals | — | (consumes drafts) |
| 2025-07-28 | Engine | **B3** — RentReminders (Aug) + Outbox | — | NoticeDraft, Outbox |

### August 2025
| sim_date | actor | action | input artifact(s) | worker dep. |
|---|---|---|---|---|
| 2025-08-01 | Engine | **B1** — RentCharge ×**21** (L20 worker-charged; L06 renewed) → Autopay L09+L17 → DebtService ×11 → RecurringExpense: HOA, trash×5, lawn×5 | — | RentCharge, Autopay, DebtService, RecurringExpense |
| 2025-08-04 | Priya | **B2** — mark Aug rent; **L04 check returns NSF** → Paid→**Failed**, add NSF fee (Other); tenant re-pays by Check | — | — |
| 2025-08-08 | Engine | LateFee: **L14 Aug** (still behind); NoticeDraft: LateRentNotice (FINAL) | — | LateFee, NoticeDraft |
| 2025-08-10 | Tenant:C. Reynolds (L12) | **Portal** — pay Aug rent online → Paid | — | — |
| 2025-08-13 | Engine | LeaseExpiryReminder: L03(09-30), L15(09-30) ≤60d; NoticeDraft: Renewal drafts L07(10-31), L18(10-31) | — | LeaseExpiryReminder, NoticeDraft |
| 2025-08-15 | Priya | **P02 roof leak** → WorkOrder → roofer; scan receipt → Expense (Repairs, P02) | receipt JPEG + roof photo | ScanProcessing |
| 2025-08-17 | Dana | Approve & send L07 + L18 renewals | — | (consumes drafts) |
| 2025-08-20 | Tenant:A. Brooks (L13) | **Portal** — maintenance request: disposal jammed (photo) | maintenance photo | ScanProcessing |
| 2025-08-28 | Engine | **B3** — RentReminders (Sep) + Outbox | — | NoticeDraft, Outbox |

### September 2025
| sim_date | actor | action | input artifact(s) | worker dep. |
|---|---|---|---|---|
| 2025-09-01 | Engine | **B1** — RentCharge ×**21** (L09/P13·A renewed) → Autopay L09+L17 → DebtService ×11 → RecurringExpense: HOA, trash×5, lawn×5 | — | RentCharge, Autopay, DebtService, RecurringExpense |
| 2025-09-03 | Priya | **B2** — mark Sep rent (no scan month) | — | — |
| 2025-09-05 | Tenant:L. Turner (L14) | **Portal** — **LUMP catch-up** clears Apr–Aug arrears + late fees; Partial/Late → **Paid**; **delinquency cured** | — | — |
| 2025-09-06 | Dana | Approve & send **held L14 renewal** (cured; term from 08-01) | — | (consumes May draft) |
| 2025-09-13 | Engine | LeaseExpiryReminder: L07(10-31), L18(10-31) ≤60d; NoticeDraft: Renewal draft L10(11-30) | — | LeaseExpiryReminder, NoticeDraft |
| 2025-09-16 | Dana | Approve & send L10 renewal | — | (consumes draft) |
| 2025-09-18 | Priya | P06 dishwasher repair → WorkOrder → vendor; scan receipt → Expense (Repairs, P06) | receipt JPEG | ScanProcessing |
| 2025-09-28 | Engine | **B3** — RentReminders (Oct) + Outbox | — | NoticeDraft, Outbox |
| 2025-09-30 | Dana | **Q3 owner statement** → Marcus Bell (OE-BELL) | — | — |

### October 2025
| sim_date | actor | action | input artifact(s) | worker dep. |
|---|---|---|---|---|
| 2025-10-01 | Engine | **B1** — RentCharge ×**21** → Autopay L09+L17 → DebtService ×11 → RecurringExpense: HOA, trash×5, **lawn×5 final**, **pest Q4** | — | RentCharge, Autopay, DebtService, RecurringExpense |
| 2025-10-05 | Priya | **B2** — scan 7 Oct rent checks/MOs → confirm | 7 rent-check/MO JPEGs | ScanProcessing |
| 2025-10-10 | Tenant:W. Foster (L18) | **Portal** — Conversation message (renewal question) | — | — |
| 2025-10-13 | Engine | LeaseExpiryReminder: L10(11-30) ≤60d; NoticeDraft: Renewal drafts L12(12-31), L19(12-31) | — | LeaseExpiryReminder, NoticeDraft |
| 2025-10-17 | Dana | Approve & send L12 + L19 renewals | — | (consumes drafts) |
| 2025-10-28 | Engine | **B3** — RentReminders (Nov) + Outbox | — | NoticeDraft, Outbox |

### November 2025
| sim_date | actor | action | input artifact(s) | worker dep. |
|---|---|---|---|---|
| 2025-11-01 | Engine | **B1** — RentCharge ×**21** (L07/L18 renewed) → Autopay L09+L17 → DebtService ×11 → RecurringExpense: HOA, trash×5 | — | RentCharge, Autopay, DebtService, RecurringExpense |
| 2025-11-04 | Priya | **B2** — mark Nov rent (no scan month) | — | — |
| 2025-11-10 | Priya | **P13 water-heater** failure → WorkOrder → plumber; scan receipt → Expense (Repairs, P13) | receipt JPEG | ScanProcessing |
| 2025-11-13 | Engine | LeaseExpiryReminder: L12(12-31), L19(12-31) ≤60d | — | LeaseExpiryReminder |
| 2025-11-20 | Priya | P08 gutter cleaning → vendor; scan receipt → Expense (CleaningMaintenance, P08) | receipt JPEG | ScanProcessing |
| 2025-11-28 | Engine | **B3** — RentReminders (Dec) + Outbox | — | NoticeDraft, Outbox |

### December 2025
| sim_date | actor | action | input artifact(s) | worker dep. |
|---|---|---|---|---|
| 2025-12-01 | Engine | **B1** — RentCharge ×**21** (L10 renewed) → Autopay L09+L17 → DebtService ×11 → RecurringExpense: HOA, trash×5 | — | RentCharge, Autopay, DebtService, RecurringExpense |
| 2025-12-05 | Priya | **B2** — scan 7 Dec rent checks/MOs → confirm | 7 rent-check/MO JPEGs | ScanProcessing |
| 2025-12-10 | Priya | **AnnualSafety inspections #3** — SFH P01/P06/P10/P12 | inspections (hand) | RecurringMaintenance (opt) |
| 2025-12-28 | Engine | **B3** — RentReminders (Jan 2026) + Outbox | — | NoticeDraft, Outbox |
| 2025-12-31 | Dana | **Q4 owner statement** → Marcus Bell (OE-BELL) | — | — |
| 2025-12-31 | Dana | **YEAR-END reconciliation**: pull Schedule E, owner statements (all entities), rent roll, delinquency aging, cash-flow/NOI, general ledger, vendor-1099, occupancy, loan schedules, **year-end packet** → diff vs ground-truth ledger | — | — reconcile — |

---

## Artifacts consumed by month (feeds the generation manifest)

| Month | Rent-check/MO JPEGs | Receipt JPEGs | Application PDFs | Maintenance photos | New lease PDFs | Inspections (hand) |
|---|---|---|---|---|---|---|
| Jan | 7 | 1 | — | 1 | — | — |
| Feb | 0 | 2 | 3 | — | 2 | — |
| Mar | 0 | 0 | — | — | — | 3 |
| Apr | 7 | 0 | — | — | — | — |
| May | 0 | 0 | — | — | — | 1 |
| Jun | 1 | 3 | 1 | 1 | 1 | 1 |
| Jul | 7 | 2 | — | — | — | 2+ |
| Aug | 0 | 1 | — | 2 | — | — |
| Sep | 0 | 1 | — | — | — | — |
| Oct | 7 | 0 | — | — | — | — |
| Nov | 0 | 2 | — | — | — | — |
| Dec | 7 | 0 | — | — | — | 4 |
| **CY2025** | **~36** | **~12** | **4** | **~4** | **3** | **~18** |

(Plus T0 history artifacts not counted above: 19 lease PDFs + 11 mortgage-statement JPEGs, and the
2023–24 receipt/check history whose volume is set by the §4 import batch plan.)

**~104 calendar rows**, ~2/week. Every `actor = Engine` row is a worker fire (named in the last
column); everything else is a human/tenant action, with `ScanProcessing` noted where a scan upload
feeds an async draft.
