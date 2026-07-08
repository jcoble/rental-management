# E2E Replay Driver — Design (Phase-2 step 4)

**Author:** Scenario Architect (SA) · **Date:** 2026-07-01 · **Status:** design (runs after TSK-615
dev clock/worker endpoints land — team-lead will ping "clock endpoints live").

The replay driver seeds the corpus into a live Rental Command instance, advances simulated time
through CY2025 firing scheduled jobs, and at year-end diffs the **app's own reports** against the
independently-derived `e2e/corpus/expected/*.json`. It is **web-app-facing / API-driven** and
**writes no git**. Companion docs: the scenario (`e2e/corpus/scenario/scenario.json`), the
operations calendar (`Docs/superpowers/specs/2026-06-30-e2e-operations-calendar-CY2025.md`), the
corpus design (`…/2026-06-30-e2e-scenario-corpus-design.md`), and the clock spec
(`…/2026-07-01-master-simulation-clock-design.md`, TSK-615).

## 0. Contract it targets (TSK-615, dev-only + admin-auth)
- **Clock:** `POST /api/v1/dev/clock/set {instantUtc|date, timeZoneId?, mode?="offset"|"frozen"}` ·
  `POST /api/v1/dev/clock/advance {days?,hours?,...}` · `GET /api/v1/dev/clock` · freeze/unfreeze/reset.
- **Workers:** `POST /api/v1/dev/workers/{key}/run-once` · `POST /api/v1/dev/workers/run-due`
  (fires all due in dependency order). Keys: `rent-charge, autopay, late-fee, recurring-expense,
  recurring-maintenance, debt-service, lease-expiry-reminder, notice-draft, daily-briefing`. Idempotent.
- **Settings:** `PUT /api/v1/notifications/settings` (full-object overwrite: GET → mutate → PUT).

## 1. Harness invariants (from source verification — non-negotiable)
1. **Always set `Property.OwnerEntityId`** on property create (omitting → silent fallback to the
   primary self-owner → misattributed income that still "passes"). Also set per-property
   `ManagementFeePercent` (fee is per-property, not per-owner).
2. **Leases created with `RentTrackingStartMode = BackfillFromLeaseStart`** → rent back-fills
   synchronously inside `POST /leases` (default `ForwardOnly` = ~1 month only).
3. **Mark rent Paid per-payment** with the row's historical `PaidDate` + `Method` (income buckets by
   `PaidDate` year; the bulk endpoint stamps one shared date). `PayerName/CheckNumber/BankName`
   only settable at `POST /payments` create.
4. **Enable workers at T0**: `EnableRentCharges/EnableLateFees/NotifyTenants = true`; simulated
   `timeZoneId = America/New_York` (business-day/grace arithmetic must match the ground truth).
5. **Rent-check scan months**: confirming a scanned check creates a *new* Payment (no `PeriodKey`);
   for the 5 scan months it **replaces** (waive/delete) the worker's Scheduled rent row for that
   lease-month so each lease-month has exactly one Paid rent row. All other months use worker-charge
   + `mark-paid`.
6. **Loans/recurring need a worker fire** to back-fill (no create-time trigger) — call
   `run-once{debt-service}` / `run-once{recurring-expense}` after the entities exist. Loans need no
   mark-paid (reports sum by `DueDate`, status-agnostic).

## 2. Data sources → API calls
| Corpus file | Feeds |
|---|---|
| `scenario/scenario.json` | portfolio, owners, properties+units, loans, leases, deposits, recurring templates, story beats |
| `ledger/loans.csv` | `Loan.MonthlyPrincipalInterest` (the emitted standard payment) + escrow terms |
| `ledger/depreciation.csv` | `Property.{PurchasePrice,LandValue,InServiceDate,AccumulatedDepreciation}` (seed = `accumulated_through_2024`) |
| `ledger/events.csv` | the exact rent/expense/deposit/late-fee events to record (dates, amounts, methods, statuses) |
| `expected/*.json` | year-end reconciliation targets |
| `generated/**` | scan uploads (lease PDFs, mortgage/receipt/check JPEGs, applications, maintenance photos) |

## 3. Phases

### Phase A — Onboarding at T0 (2025-01-01)
1. `clock/set {date:"2025-01-01", timeZoneId:"America/New_York", mode:"offset"}`; register/confirm the landlord; create the portfolio (live, not sandbox).
2. `PUT notifications/settings` (GET→mutate→PUT): enable rent-charges, late-fees, notify.
3. Create **4 owner entities** (`POST /owner-entities`), capture ids.
4. Create **13 properties** (`POST /properties`) with **explicit `ownerEntityId`**, `managementFeePercent`, and the depreciation basis fields (from `depreciation.csv`), then their **21 units**. (A subset via lease-scan bootstrap to exercise `/scan/new-rental`; the rest direct or CSV import.)
5. Create **11 loans** — scan the mortgage-statement JPEGs → `ConfirmAsLoan` (exercises the flagship path) with `MonthlyPrincipalInterest` from `loans.csv`; or direct `POST /loans`.
6. Create **tenants + 19 active leases** with `RentTrackingStartMode=BackfillFromLeaseStart` (synchronous rent back-fill). Lease docs via lease-scan confirm (born-digital + a stitch set) and direct.
7. Create **security deposits** (`/security-deposits`) and **autopay enrollments** for L09/L17.

### Phase B — History back-fill (CY2023–24)
8. `run-once{debt-service}` → all historical `LoanPayment` rows; `run-once{recurring-expense}` → recurring `Expense` rows. (Rent already back-filled in step 6.)
9. **Mark historical rent Paid** — iterate `events.csv` rent rows with `paid_date` in 2023–24, `POST /payments/{id}/mark-paid {paidDate, method}` each.
10. Enter historical **one-off expenses** (receipts scan-confirm or direct) and **direct tax/insurance** for P03/P08.

### Phase C — Live operations CY2025 (week-by-week per the calendar)
For each calendar row (companion calendar doc), in order:
- `clock/set`/`advance` to the `sim_date`.
- **Human actions**: record rent Paid (`mark-paid`, or scan-confirm a check in scan months), upload+confirm scans (receipts→Expense, maintenance photos/typed/voice→WorkOrder, applications→screening→approve→lease), enter inspections/deposits, approve notice drafts, hand-enter prorated move-in rent + the NSF beat.
- **Engine fires** (dependency order): `run-due` — or explicit `run-once` sequence `rent-charge → notice-draft → (advance past due+grace) → late-fee → autopay → debt-service/recurring-expense/recurring-maintenance → daily-briefing`. (Outbox drains async.)
- The L14 delinquency, L05→L20 turnover, and L08/L16 lease-ups follow their calendar dates; the ground-truth `events.csv` reflects the **year-end** state (L14 cured in Sep, fresh Dec miss).

### Phase D — Year-end reconciliation (2025-12-31)
Pull each report and diff vs `expected/*.json` (a `reconcile.mjs` fetches + compares; `derive-expected.mjs` remains the source of truth). Endpoint → expected mapping:

| App endpoint | Expected key |
|---|---|
| `GET /accounting/schedule-e?year=2025` (per property + portfolio) | `cy2025-by-property.scheduleE`, `cy2025-portfolio.scheduleE` |
| `GET /accounting/cash-flow` (True Cash Flow / NOI) | `…trueCashFlow` |
| `GET /reports/cash-flow` (P&L) | `…cashflowPnl` |
| `GET /reports/property-pnl` | `…propertyPnl` |
| `GET /accounting/owner-statement?ownerId&year=2025` | `cy2025-portfolio.ownerStatements[OE-*]` |
| `GET /reports/security-deposits` | `…deposits` |
| `GET /reports/delinquency` | `…delinquencyAtYearEnd` ($899.75, L14 current bucket) |
| `GET /reports/vendor-1099?year=2025` | `…vendor1099` |
| `GET /reports/rent-roll`, `/occupancy`, `/lease-expirations` | derive from scenario (active leases at as-of) |
| `GET /loans/{id}/payments` | `ledger/loan-payments.csv` (per period, balance chain) |
| `GET /accounting/year-end-packet?year=2025` | composite of the above |

**Assert the 4 deliberate divergences** (not bugs): DIV1 P&L−ScheduleE by interest+depr (+`Other`/NSF income); DIV2 escrowed tax/ins excluded from NOI opex; DIV3 cash-flow vs taxable net; and the Schedule-E-minus-Utility / owner-statement-minus-latefee deltas.

## 4. Pending confirmation when endpoints land
- Exact request/response shapes of `dev/clock/*` and `dev/workers/*` (field names, sync vs async, return payloads).
- Whether `run-due` alone suffices or explicit per-worker `run-once` ordering is needed each step.
- Whether `ScanService.ConfirmAsPayment` sets `PeriodKey` (decides whether the scan-month "replace" step is needed vs the dedupe index handling it).
- Auth: the dev endpoints' admin gate + the settings PUT scope.

## 5. Feature-dependent revision (when the 7 gaps land)
Owner-distribution-as-a-record (replaces the memo modeling + adds a reconcilable payout), proration
endpoints (replace hand-entered move-in rent), bulk transaction import (replaces the per-payment
`mark-paid` loop), application-fee income, and eviction each refine specific steps above. Architecture
(clock → seed → advance+fire → reconcile) is unchanged.
