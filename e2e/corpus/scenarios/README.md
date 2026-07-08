# Tester Scenario Catalog — Rental Command E2E

A **tester-facing playbook**: discrete, labeled, browser-agent-executable scenarios that replay the
Okafor Property Group business (design: `Docs/superpowers/specs/2026-06-30-e2e-scenario-corpus-design.md`)
through the **real web app** under the master simulation clock, then reconcile the app's own reports
against the independently-kept ground-truth ledger.

Each scenario tells a browser agent **exactly what to do** (WHAT + the exact corpus DATA values + which
artifact to upload) and **exactly what "pass" looks like** (records created, list/dashboard/report
figures, ledger balances — cross-referenced to `expected/*.json` and specific `events.csv` rows). A
tester agent can pick up any scenario in isolation, satisfy its preconditions, execute, and verify.

> This catalog **consumes** the frozen corpus already on disk (`scenario/scenario.json`,
> `ledger/events.csv`, `expected/*.json`). It does **not** regenerate any data. Ground truth is an
> **event log**, not pre-summed totals — every report figure is a derived view with its own predicate
> (design §8.3), so assertions cite the exact `expected/*.json` key or `events.csv` row.

> **Authority (per team-lead, 2026-07-01):** this catalog is the **authoritative tester + replay
> instruction set.** Where it conflicts with `Docs/superpowers/specs/2026-06-30-e2e-operations-calendar-CY2025.md`
> or the design doc §3.2 (both drifted on lease numbering), **the catalog wins** — the harness and the
> future replay driver key off this catalog, not the calendar. **Authoritative lease map:** L01–L07,
> L09–L15, L17–L19, **L21, L22** are active at T0; **L08 (P05·3)** and **L16 (P09·4)** are the CY2025
> lease-ups; **L20** is the P04 turnover (replaces L05). (The two source docs carry an errata block
> pointing here; full prose reconciliation happens in the post-features corpus revision.)

---

## Files in this folder

| File | Role |
|---|---|
| `README.md` | This index + how to consume + id scheme + legend + conventions. |
| `act1-onboarding.md` | **Act 1 — Onboarding** (clock frozen at T0 = 2025-01-01): register → set up company/owners/users → 13 properties/21 units/4 owners → confirm 19 leases + 11 loans by scan → enable workers → seed deposits/history → verify T0 state. |
| `act2-operations.md` | **Act 2 — Operations** (CY2025, clock advancing): the monthly rhythm as reusable templates (`TMPL-B1/B2/B3`) + every story beat (delinquency arc, turnover, lease-ups, adverse action, NSF, inspections, maintenance intake, owner statements, renewals) + the 7 gap-feature spotlights. |
| `act3-reconciliation.md` | **Act 3 — Reconciliation** (clock at 2025-12-31): one scenario per app report, cent-exact vs `expected/*.json`, pre-asserting the 4 deliberate divergences. |
| `scenarios.json` | Machine-readable flat index the harness iterates: `{id, act, title, preconditions, sim_date, actor, artifacts, expectedRefs, workerFires, pendingFeature?}`, sortable by `sim_date`. |

---

## How the tester harness consumes this catalog

1. **Load `scenarios.json`.** It is the authoritative iterable. Sort by `sim_date` (ties broken by the
   `order` field) for a faithful chronological replay, or filter by `act` / `id` to run one slice.
2. **For each scenario:** read its full spec in the matching `actN-*.md` (the JSON is an index; the
   markdown is the source of truth for steps + assertions). Satisfy `preconditions` first — either by
   running the listed prior scenarios or by asserting the seeded state exists.
3. **Set the clock** to the scenario's `sim_date` before acting (see *Clock & worker conventions*).
4. **Authenticate** as the scenario's `actor` (see *Actors & credentials*).
5. **Execute the steps** at intent level — the agent drives Chrome; steps give the WHAT + exact values,
   not pixel coordinates.
6. **Fire workers** listed in `workerFires` (Engine scenarios), in the documented order.
7. **Assert every expected result.** Each assertion is checkable in the running app (a list count, a
   dashboard KPI, a report figure, a ledger balance). Cite the `expectedRefs` to confirm the number.
8. **Record pass/fail.** A mismatch on a spine (non-`PENDING`) scenario is a **bug**. A mismatch on a
   `PENDING GAP<N>` scenario before that feature lands is **expected** (the feature isn't built yet).

**Two-pass posture.** Run the **spine pass** first (all non-`PENDING` scenarios) — it is cent-exact and
must fully reconcile in Act 3. Run the **gap pass** (`PENDING GAP<N>` + `STRIPE-*`) as each feature lands;
those may shift `expected/*.json`, which is a corpus-revision trigger, not a spine bug (see *Gap features*).

---

## Execution surfaces (true-to-life mandate)

**Governing principle:** scenarios must exercise the **real app the way a real landlord does** — the browser
agent drives the actual **screen** whenever one exists. Never POST to the API to skip work a user would do
on a page. Every step is classified by **execution surface** and tagged:

| Tag | Meaning | Rule |
|---|---|---|
| **`[UI]`** | The browser agent drives the actual screen (default for all user actions). | Create/edit records, upload+confirm scans, record payments, enter expenses, run screenings, approve/send notices, change settings, do inspections, **and read reports** — all `[UI]`. Assertions are **read primarily from the UI** (the on-screen list/dashboard/report value); GET endpoints are **secondary** confirmation only. |
| **`[DEV-CLOCK]`** | The dev time-travel surface: `POST /api/v1/dev/clock/set\|advance\|freeze\|unfreeze\|reset` (or the **SimClockPanel** UI). | Legitimate scaffolding — there is no user equivalent for "advance time." Keep as-is; tag so it's transparent it's not a user action. |
| **`[WORKER]`** | The dev worker-trigger: `POST /api/v1/dev/workers/{key}/run-once\|run-due`. | Legitimate scaffolding — firing a scheduled job IS the mechanism under test. No user UI exists (the superadmin **engine** page is monitoring-only, not a trigger). Keep as-is; tag it. |
| **`[SEED]`** | A bulk setup shortcut used **only** where doing it fully through the UI is impractical (e.g., ~500 historical rows). | Allowed but must be (a) **minimized**, (b) **explicitly justified**, (c) **replaced** by the real path when it lands (GAP1 bulk import), and (d) **paired with representative `[UI]` coverage** so the manual path is genuinely tested at least once. |

Each scenario carries a **`Surfaces:`** line (the set of tags it uses); non-obvious/converted steps are
tagged inline. `scenarios.json` carries a `surfaces` array per scenario. **Reports (Act 3) are `[UI]`
reads** of the report screens; `expected/*.json`/GET are the cross-check, not the action.

### Authoritative action → screen → surface map (verified in `web/src/routes` + `web/src/lib/components`)
Every action type below has a **real screen** — so its steps are `[UI]`. The harness uses this map to know
which screen an action drives.

| Action | Screen / component `[UI]` |
|---|---|
| Register / confirm email / login | `/register`, `/verify-email`, `/login` |
| Portfolio + owners + users + onboarding wizard | `/onboarding`, `/get-started`, `/owners`, `/owners/[id]`, `/(admin)/admin/users` |
| **Worker enablement** (Rent reminders / Late fees / Notify tenants toggles) | `/settings` (`settings/+page.svelte`) |
| Properties + units (owner, escrow, basis, fee%) | `/properties`, `/properties/[id]`, `/units`, `/units/[id]` |
| **Lease scan→draft→confirm + "Backfill from lease start"** | `/scan/new-rental` (`LeaseFirstImport.svelte` → `LeaseTermFields.svelte`; links to existing property/unit) |
| Lease detail / edit / non-renewal auto-action | `/leases/[id]` (`LeaseDetail.svelte`) |
| Loans (create + mortgage-statement `ConfirmAsLoan`) | `/properties/[id]` (`PropertyLoansSection.svelte`); scan → `/scan/[draftId]` (isLoan) |
| Recurring-expense templates | `/properties/[id]` (`PropertyRecurringExpensesSection.svelte`) |
| Security deposits (hold / **deduction / refund**) | `/deposits`, `/deposits/[id]` (add-deduction / refund dialog) |
| **Record rent Paid / mark-paid** | `/leases/[id]` (`LeaseDetail` "Mark paid") + `/accounting/past-due` (one-tap for behind leases) |
| Scanned rent-check → `ConfirmAsPayment` | `/scan/[draftId]` (isPayment) |
| Expenses (insurance, tax, repairs) + scanned receipt `ConfirmAsExpense` | `/accounting`, unit `ExpensesTab.svelte`, `ExpenseDetail.svelte`; scan → `/scan/[draftId]` (isExpense) |
| Public rental application (no-login) | `/apply/[token]` |
| Screening → approve/decline + **AdverseActionNotice** | `/applications`, `/applications/[id]` (`ApplicationDetail.svelte`) |
| Inspections (MoveIn/MoveOut/AnnualSafety) | `/maintenance`, `/maintenance/inspections/[id]`, unit `TurnoverTab.svelte` |
| Work orders + maintenance intake (photo/typed/voice) | `/maintenance`, `/maintenance/[id]`; tenant `/portal/maintenance` |
| Notices approve & send (Renewal/M2M/LateRent/MoveOut) | `/notices` (Draft→Approved, portal/email/sms channels, Send) |
| Owner statements | `/owners-report` |
| Reports (Schedule E/P&L/NOI/delinquency/deposits/rent-roll/1099/GL/year-end) | `/reports/[report]`, `/tax`, `/accounting`, `/accounting/year-end`, `/deposits`, portfolio dashboard |
| Tenant portal (pay / message / maintenance) | `/portal`, `/portal/payments`, `/portal/messages`, `/portal/maintenance` |
| Advance sim time / fire scheduled workers | **no user UI** → `[DEV-CLOCK]` / `[WORKER]` (SimClockPanel is the clock UI) |

### Product-gap findings (surfaced during this pass — a user action with missing/partial UI; do NOT API around them silently)
- **PG-1 (rent-tracking control missing in the generic scan-draft confirm).** `RentTrackingStartMode`
  ("Backfill from lease start") is surfaced in **`/scan/new-rental`** (`LeaseFirstImport`) and the manual
  lease form (`LeaseTermFields`), but **not** in the generic `/scan/[draftId]` lease-draft confirm. So a
  lease confirmed via the generic draft path **cannot set back-fill from the UI**. **Resolution:** ONB-04
  routes lease confirms through **`/scan/new-rental`** (which links to the existing property/unit from
  ONB-03 and exposes the control). **Fix candidate:** add the rent-tracking control to `/scan/[draftId]`
  lease confirms. (DoD "backend supports it, one UI path doesn't surface it.")
- **PG-2 (no historical/loan/recurring "generate now" user action).** Rent back-fill runs synchronously on
  lease create (via the `/scan/new-rental` control, `[UI]`), but **loan** amortization back-fill
  (DebtService) and **recurring-expense** back-fill have **no create-time trigger and no user "generate
  history" button** — only the scheduled worker. The harness fires them via `[WORKER]`; there is no `[UI]`
  equivalent (inherent to the design, not a regression, but noted).
- **PG-3 (owner-distribution record).** `/owners-report` shows the **computed** statement, but there is no
  UI to record an **actual distribution** — this is exactly the GAP2 feature (`PENDING GAP2`).
- **PG-4 (mark-paid method/date capture — confirm in-app).** The lease-detail "Mark paid" is one-tap; it
  stamps the payment Paid at the **current sim date** (correct when the clock is set to the pay date), but
  may **not capture `Method`/`PayerName`** on non-scanned payments. If so, method variety is a display-only
  fidelity limitation (method is a lightly-used free string); scanned checks carry payer/check#/method via
  `/scan`. Confirm the mark-paid dialog's fields in-app; flag if richer capture is needed.
- **PG-5 (bulk historical mark-paid).** No bulk UI to mark ~500 historical rows Paid → ONB-08 uses `[SEED]`
  (justified; the real path is GAP1 bulk import) paired with a representative `[UI]` mark-paid.

## Scenario id scheme (STABLE — never renumber)

| Prefix | Meaning | Examples |
|---|---|---|
| `ONB-NN` | Act 1 onboarding step | `ONB-01` … `ONB-09` |
| `TMPL-B1/B2/B3` | Act 2 reusable monthly-batch **templates** (parameterized by month) | `TMPL-B1` month-open Engine batch; `TMPL-B2` record-rent; `TMPL-B3` rent-reminders + outbox |
| `OPS-<MON>-B1/B2/B3` | A **concrete monthly instance** of a template (12 months × 3) | `OPS-JAN-B1`, `OPS-JUL-B2`, `OPS-DEC-B3` |
| `OPS-<MON>-<SLUG>` | An Act 2 **story beat** on a specific date | `OPS-APR-L14-PARTIAL`, `OPS-MAY-L05-MOVEOUT` |
| `GAP<N>-<SLUG>` | A **gap-feature spotlight** (feature is the star); `<N>` = 1..7 | `GAP2-OWNERDIST`, `GAP6-SALE`, `GAP7-EVICTION` |
| `STRIPE-<SLUG>` | Real Stripe **test-mode** path, off the reconciliation ledger | `STRIPE-AUTOPAY-L09` |
| `REC-<SLUG>` | Act 3 reconciliation, one per app report | `REC-SCHED-E`, `REC-NOI`, `REC-DELINQ` |

`<MON>` = `JAN`…`DEC`. Codes `P01`–`P13` (properties), `U#`/`A`/`B`/`1`–`4` (units), `L01`–`L22`
(leases), `OE-LLC|BELL|TRUST|PERS` (owner entities) are the frozen scenario ids from
`scenario/scenario.json` — do not renumber.

---

## Legend

### Actors & credentials
The harness logs in as the named actor for each scenario. Credentials are a **convention** the harness
may adjust to match the running app; the roles and person↔role mapping are fixed.

| Actor | Role | Convention login | Function |
|---|---|---|---|
| **Dana** (Dana R. Okafor) | `Admin` | `dana@okaforpg.com` / `Okafor!123` (created in `ONB-01`; or use the seeded dev admin `admin@rentalcommand.local` / `Admin123!`) | Owner-operator; approves drafts; sees everything; runs onboarding + year-end. |
| **Priya** (Priya Nair) | `Manager` | `priya@okaforpg.com` / `Okafor!123` (created `ONB-02`) | Day-to-day ops: scanning/confirming, expense entry, inspections, move-outs. |
| **Tomás** (Tomás Reyes) | `Agent` | `tomas@okaforpg.com` / `Okafor!123` (created `ONB-02`) | Leasing: listings, applications, screening, move-ins. |
| **Marcus** (Marcus Bell) | `Owner` | `marcus@okaforpg.com` / `Okafor!123` (created `ONB-02`) | Silent co-owner (OE-BELL); portal/owner-statement recipient only. |
| **Tenant:\<name\>** | `Tenant` | `<slug>@tenant.test` / `Tenant!123` (created with each lease) | Portal: pays rent, files maintenance, messages. `<slug>` = lowercased tenant name, e.g. `l.turner@tenant.test`. |
| **Engine** | — (dev API) | Admin token (Dana) | Not a UI login: set the master clock to `sim_date` and fire the named dev worker(s). |

Email confirmation in dev: follow the dev confirm path (dev mail log / auto-confirm link). The exact dev
mail surface is a running-app detail; `ONB-01` flags it. Portal tenants are invited/enabled from the
tenant record; confirm the exact invite UI in-app.

### Assertion conventions
- **`expected/<file>.json → <keypath> = <value>`** — the authoritative cent-exact figure. E.g.
  `expected/cy2025-by-property.json → byProperty.P01.scheduleE.income = 15337.50`.
- **`events.csv <event_id>`** — a specific ground-truth row (a rent payment, late fee, deposit, expense).
  E.g. `events.csv E01118` = L16 prorated first-month rent 452.42.
- **`ledger/loans.csv <loan_code>`** / **`ledger/depreciation.csv <property_code>`** — loan-schedule and
  depreciation lines.
- **Money is asserted to the cent.** "≈" appears only where the app value is not part of the cent-exact
  spine (async Stripe timing, gap features not yet landed).
- **List/dashboard assertions** name the count and any per-row values a browser agent can read on screen.

### Clock & worker conventions (the dev surface — final TSK-615 contracts)
All under `/api/v1/dev/*`, mapped **only** when `Simulation:Enabled && non-prod`; web dev panel needs
`PUBLIC_SIMULATION_ENABLED=true`.

- **Set the clock:** `POST /api/v1/dev/clock/set { "date": "2025-03-01", "timeZoneId": "America/New_York", "mode": "frozen" }`
  (or `"instantUtc"`; instantUtc wins). `POST /api/v1/dev/clock/advance { "days": 5 }` to step within a
  scenario (e.g. past due+grace before a late fee). `GET /api/v1/dev/clock` → `{simNowUtc, mode, timeZoneId, offsetSeconds}`.
  `POST /api/v1/dev/clock/{freeze|unfreeze|reset}`.
- **Fire a worker:** `POST /api/v1/dev/workers/{key}/run-once` (enqueue + long-poll → result). Keys:
  `rent-charge, notice-draft, lease-expiry-reminder, late-fee, autopay, debt-service, recurring-expense,
  recurring-maintenance, daily-briefing`.
- **Fire all due (dependency order):** `POST /api/v1/dev/workers/run-due` →
  `rent-charge → notice-draft → lease-expiry-reminder → late-fee → autopay → debt-service →
  recurring-expense → recurring-maintenance` (daily-briefing excluded), returns `{created: <sum>}`.
- **Per-simulated-day fire order (design §5.3)** when firing individually: `rent-charge → notice-draft
  (reminders) → advance clock past due+grace → late-fee + notice-draft (late) → autopay → debt-service /
  recurring-expense / recurring-maintenance → outbox drain`.
- **Idempotency:** all workers key on `(LeaseId/LoanId, Type, PeriodKey)` — re-firing the same sim-day is
  safe. Re-run reset = clear `Lease.ExpiryReminderSentAt` + the daily-briefing outbox date key.
- **Config at T0 (once):** `PUT /api/v1/notifications/settings` full-object overwrite (GET → mutate →
  PUT) with `EnableRentCharges=true`, `EnableLateFees=true`, `NotifyTenants=true`. Omitting fields reverts
  them to DTO defaults — always PUT the complete object.

### Harness invariants (design §9 item 13; carry into every scenario that creates data)
- **Always set `ownerEntityId` explicitly** on every property create/update (omitting silently
  misattributes income to the primary self-owner and still "passes").
- **`ManagementFeePercent` is per-property** — replicate the owner's % across each of its properties
  (OE-BELL P05/P07 = 8%, OE-TRUST P03 = 8%, all others 0%).
- **Rent back-fill** needs `RentTrackingStartMode=BackfillFromLeaseStart` on lease-create (default
  `ForwardOnly` gives ~1 month). Back-fill runs synchronously inside `POST /leases`.
- **Scan-month rent-check "replace":** in the 5 scan months (Jan/Apr/Jul/Oct/Dec) a confirmed scanned
  check **replaces** the worker's Scheduled rent row for that lease-month (waive/delete the duplicate) so
  each lease-month has exactly one Paid rent row (design §9 item 12).

### Gap features (the 7 flagged, `PENDING GAP<N>`)
Author to the **intended** behavior; tag `PENDING GAP<N> — confirm exact UI/endpoint when the feature
lands`. Map to the Notion tasks in `Docs/e2e/ORCHESTRATION-LEDGER.md` ("Notion task URLs"):

| Tag | Feature | Locked decision (SA-STATE §82-108) | Notion page |
|---|---|---|---|
| `GAP1` | Bulk transaction import | Import the CY2023–24 history via the real bulk-import path (replaces worker-backfill + per-payment mark-paid). | `390394b0689d8145a6b7db2fd605f004` |
| `GAP2` | Owner-distribution record | A real `OwnerDistribution` record (NOT memo, NOT Expense); reconcile cash-distributed vs computed net. | `390394b0689d8149b906d099889ed32f` |
| `GAP3` | Rent proration | App computes partial-month rent on move-in/out (L16 half-March; L05/L20 boundaries). | `390394b0689d81afa611c9ecd6289db8` |
| `GAP4` | Capital-improvement depreciation | A capital improvement depreciates on its own schedule (NOT expensed as Repairs). | `390394b0689d81a4a91cedfd9a77a0af` |
| `GAP5` | Application/screening-fee income | Real application fees as income before any lease exists. | `390394b0689d81b4a5cde08d2e894ce6` |
| `GAP6` | Property sale / disposition | Sell one property mid/late CY2025: sale-year depreciation + gain/loss + §1250 recapture. | `390394b0689d81b3ba3fc75459f29b7c` |
| `GAP7` | First-class eviction | Escalate the P09/L14 delinquency into a real eviction path/record (reworks the cure arc). | `390394b0689d81d3a215e7d0bfbf667c` |

**Spine vs gap.** The cent-exact reconciliation spine (Acts 1–3, non-`PENDING`) is authored against the
frozen corpus as it stands: owner distributions = memo (`events.csv` E01146/E01295/E01446/E01573),
proration = hand-entered (`events.csv` E01118 = 452.42), autopay L09/L17 = marked Paid, L14 = **cures**
in September, no app-fee income, no property sale, no capital-improvement schedule. The gap scenarios
describe the **intended** behavior; where a gap **reworks** the spine (GAP7 eviction vs the L14 cure;
GAP4/GAP5/GAP6 which add/redirect money events), the scenario states that the expected figures shift and
a **corpus revision** (`scenario.json` + re-run the 4 builders) is required — it does **not** silently
edit `expected/*.json`.

### The 4 deliberate divergences (pre-assert as EXPECTED in Act 3, not bugs)
Per property (values in `expected/cy2025-by-property.json → divergenceChecks`):
- **DIV1** `propertyPnl.net − scheduleE.netIncome = mortgageInterest + depreciation + nonScheduleEIncome`.
  (Property-P&L neither deducts interest/depreciation nor drops `Other`-type income.)
- **DIV2** escrowed `Taxes + Insurance` are **excluded** from True-Cash-Flow/NOI operating expenses (escrow
  cash is already inside debt service), but **counted** by Schedule E / Cash-Flow-P&L / Property-P&L. Zero
  on paid-off P03/P08.
- **DIV3** `trueCashFlow.cashFlow − scheduleE.netIncome` (cash vs taxable net; = depreciation on
  no-debt/no-escrow properties).
- **4th — `Other`-type income:** the **$35 NSF fee** (P03, `events.csv` E01398) is counted by Property-P&L
  (`propertyPnl.income` P03 = 14042) but **not** Schedule E (`scheduleE.income` P03 = 14007); portfolio
  delta `propertyPnl.income 265853.72 − scheduleE.income 265818.72 = 35`. Folds into DIV1's
  `nonScheduleEIncome`.
- **Also note (design divergence c):** owner-statement income (Rent-only) < Schedule E income by the
  year's late fees — here the **$250** of L14 late fees (5 × $50, all cured/Paid): P09 owner-statement
  income 37123.67 vs Schedule E income 37373.67.

---

## Global reset / re-run

The corpus is a **fresh database** each full run. To re-run from scratch: reset the DB + re-seed (drop
& re-migrate, or restore a T0 snapshot), then run Act 1 in order. Individual worker re-fires within a run
are idempotent; the only one-shot state to reset for a mid-run replay is `Lease.ExpiryReminderSentAt` +
the daily-briefing outbox date key. Each scenario's **Idempotency/cleanup** note calls out anything extra.
