# Act 1 — Onboarding (T0 = 2025-01-01, clock FROZEN)

Dana comes in cold, adopts the app, and reconstructs the business as of **2025-01-01**: company +
owners + team, 13 properties / 21 units, 19 active leases (via the flagship scan→draft→confirm), 11
loans, recurring-expense templates, security deposits, and 2 years of history — then verifies the T0
state. The clock stays **frozen at 2025-01-01** for all of Act 1.

> **Authoritative lease set at T0 (verified against `events.csv`):** the **19 active leases** are
> **L01–L07, L09–L15, L17, L18, L19, L21, L22**. Units **P05·3 (L08)** and **P09·4 (L16)** are
> **vacant at T0** and lease up live in Act 2; **P04·U1 (L20)** is the July turnover tenant. (L21/L22
> are the *existing* P13 duplex tenants, rent back-filled to 2023 — not lease-ups. The design §3.2
> prose / calendar call the P05·3 lease-up "L21"; that is a stale code — trust `scenario.json`.)

**Global preconditions for Act 1:** a fresh, migrated database with `Simulation:Enabled=true` (API) and
`PUBLIC_SIMULATION_ENABLED=true` (web); the master clock **set to 2025-01-01, tz America/New_York,
mode=frozen** (`POST /api/v1/dev/clock/set {"date":"2025-01-01","timeZoneId":"America/New_York","mode":"frozen"}`).
Run `ONB-01 → ONB-09` in order.

Legend, actor credentials, clock/worker conventions, and the **execution-surface tags**
(`[UI]`/`[DEV-CLOCK]`/`[WORKER]`/`[SEED]` + the authoritative action→screen map + product-gap findings)
are in `README.md`. **Every step below is `[UI]` unless tagged otherwise** — the browser agent drives the
real screen; assertions are read primarily from the UI, with GET endpoints as secondary confirmation.

---

## ONB-01 — Registration & email confirmation
- **Goal / exercises:** the cold-start user journey — self-registration, email confirmation, first login
  as the owner-operator. Establishes Dana's admin session for the rest of onboarding.
- **Preconditions:** fresh DB; clock frozen at 2025-01-01. No prior scenario.
- **Actor:** Dana (registers → becomes `Admin`).
- **Surfaces:** `[UI]` — `/register`, `/verify-email`, `/login`.
- **Steps:**
  1. Navigate to the app root; choose **Register**.
  2. Register **Dana R. Okafor** — email `dana@okaforpg.com`, password `Okafor!123` (convention; adjust to
     the app's password policy — the seeded policy accepts `Admin123!`-style).
  3. Complete **email confirmation** via the dev confirm path (dev mail log / auto-confirm link).
     `PENDING running-app detail — confirm the exact dev mail/confirm surface in the app.`
  4. Log in as Dana.
- **Expected results / assertions:**
  - Registration succeeds; a confirmation is required and then satisfied; login lands on the
    authenticated home/dashboard (empty portfolio at this point).
  - Dana's role is **Admin** (she can reach admin-only areas; the nav shows owner/admin surfaces).
  - `GET /auth/me` returns Dana with role Admin.
- **Worker fires:** none.
- **Idempotency/cleanup:** email is unique — re-running requires a fresh DB (or a different email). If
  registration friction blocks the run, the seeded dev admin `admin@rentalcommand.local` / `Admin123!`
  may stand in as Dana for ONB-02+ (note the substitution).

---

## ONB-02 — Company/portfolio, owner entities, team users, worker settings
- **Goal / exercises:** portfolio creation; the 4 `OwnerEntity` records (with the per-entity fee %); the 3
  additional team users with correct roles; and the **one-time worker enablement** that everything else
  depends on.
- **Preconditions:** `ONB-01` done; Dana logged in; clock 2025-01-01.
- **Actor:** Dana.
- **Surfaces:** `[UI]` throughout — onboarding wizard (`/onboarding`/`/get-started`), `/owners`,
  `/(admin)/admin/users`, `/settings`.
- **Steps:**
  1. **`[UI]`** Create the portfolio / management company: **Okafor Property Group**, market
     **Springfield, Clark County, OH**, timezone **America/New_York**.
  2. Create the **4 owner entities** (the app labels these "owners"):
     | Code | Name | Type | Primary | Mgmt fee % |
     |---|---|---|---|---|
     | OE-LLC | Okafor Rentals LLC | LLC | **yes** | 0 |
     | OE-BELL | Bell-Okafor Holdings LLC | LLC | no | **8** |
     | OE-TRUST | Okafor Family Trust | Trust | no | **8** |
     | OE-PERS | Dana R. Okafor | Person | no | 0 |
  3. Invite/create the **3 team users**: Priya Nair (`priya@okaforpg.com`, **Manager**), Tomás Reyes
     (`tomas@okaforpg.com`, **Agent**), Marcus Bell (`marcus@okaforpg.com`, **Owner**). Password
     `Okafor!123` (convention).
  4. **`[UI]` Enable workers** — open **Settings** (`/settings`) and toggle **ON**: **Rent reminders**
     (`enableRentCharges`), **Late fees** (`enableLateFees`), and **Notify tenants** (`notifyTenants`);
     leave lease-expiry reminders on. Save. *(The action is the Settings screen — the page performs the
     GET→mutate→PUT full-object overwrite for you; do NOT PUT the API directly.)*
- **Expected results / assertions:**
  - **`[UI]`** Portfolio exists; owners list (`/owners`) shows **4 entities** with exactly one `IsPrimary`
    (OE-LLC) and fee % = 0/8/8/0 respectively.
  - **`[UI]`** Users list shows **4 people** (Dana Admin, Priya Manager, Tomás Agent, Marcus Owner); each
    can log in and sees role-appropriate nav (Marcus sees only portal/owner surfaces).
  - **`[UI]`** Settings shows Rent reminders / Late fees / Notify tenants toggled **on**. *(Secondary:
    `GET /notifications/settings` returns the three flags true.)*
- **Worker fires:** none.
- **Idempotency/cleanup:** owner codes/fee % are load-bearing for owner statements (Act 3) — get them
  exact. Re-running on a dirty DB risks duplicate owners; prefer a fresh DB.

---

## ONB-03 — Properties & units (13 properties / 21 units) with owner attribution, escrow, depreciation seed
- **Goal / exercises:** the full portfolio structure with the **harness invariants** set explicitly:
  `ownerEntityId` on every property, per-**property** `ManagementFeePercent`, escrow-cover flags, and the
  purchase/land/in-service basis that drives depreciation. Also seeds `AccumulatedDepreciation` so CY2025
  is a normal (non-first) depreciation year.
- **Preconditions:** `ONB-02` done (owners exist); clock 2025-01-01.
- **Actor:** Dana (or Priya).
- **Surfaces:** `[UI]` — `/properties` + `/properties/[id]` (property create/edit, owner, escrow, basis,
  fee%), `/units` (units). Assertions read from the property detail / dashboard.
- **Steps:** **`[UI]`** create all **13 properties** and their **21 units** from `scenario/scenario.json` (property
  table below). For **each** property set: address, type, **owner entity** (explicit — never leave blank),
  acquired/in-service date, purchase price, land value, **feePercent** (per-property), annual property tax,
  annual insurance, and the unit(s) with market rent. **Seed** `AccumulatedDepreciation` through 2024 =
  `ledger/depreciation.csv → accumulated_through_2024` and leave `ManualAnnualDepreciation` null.

  | Prop | Owner | fee% | Type | Purchase/Land | In-service | Units × market rent | Escrow covers | AccumDepr thru 2024 |
  |---|---|---|---|---|---|---|---|---|
  | P01 | OE-LLC | 0 | SingleFamily | 128k/22k | 2016-04-01 | U1×1250 | T+I | 33566.70 |
  | P02 | OE-LLC | 0 | MultiFamily | 175k/28k | 2017-08-01 | A×950, B×975 | T+I | 39422.70 |
  | P03 | OE-TRUST | **8** | SingleFamily | 110k/20k | 2015-06-01 | U1×1150 | **none (paid off)** | 31227.30 |
  | P04 | OE-LLC | 0 | SingleFamily | 158k/30k | 2019-03-01 | U1×1450 | T+I | 26957.60 |
  | P05 | OE-BELL | **8** | MultiFamily | 245k/35k | 2018-11-01 | 1×850, 2×850, 3×850 | T+I | 46772.71 |
  | P06 | OE-LLC | 0 | SingleFamily | 172k/32k | 2020-07-01 | U1×1395 | T+I | 22696.97 |
  | P07 | OE-BELL | **8** | MultiFamily | 210k/34k | 2021-05-01 | A×1100, B×1150 | T+I | 23200.00 |
  | P08 | OE-PERS | 0 | SingleFamily | 99k/18k | 2015-09-01 | U1×1050 | **none (paid off)** | 27368.14 |
  | P09 | OE-LLC | 0 | MultiFamily | 340k/48k | 2022-02-01 | 1–4 × 825 | T+I | 30527.27 |
  | P10 | OE-LLC | 0 | SingleFamily | 185k/33k | 2023-06-01 | U1×1525 | T+I | 8521.21 |
  | P11 | OE-LLC | 0 | Condo | 95k/**0** | 2019-10-01 | U1×1100 | **T only** (HO-6 direct) | 17992.45 |
  | P12 | OE-LLC | 0 | SingleFamily | 198k/36k | 2024-01-01 | U1×1600 | T+I | 5645.45 |
  | P13 | OE-LLC | 0 | MultiFamily | 165k/27k | 2018-03-01 | A×925, B×940 | T+I | 34081.81 |

- **Expected results / assertions:**
  - **13 properties, 21 units** exist. Portfolio dashboard property count = 13; unit count = 21.
  - Each property's **owner** matches the table (OE-BELL owns P05+P07; OE-TRUST owns P03; OE-PERS owns
    P08; OE-LLC owns the other 9). No property fell back to the primary owner silently.
  - Per-property `ManagementFeePercent`: 8 on P03/P05/P07, 0 elsewhere.
  - Escrow flags: P03 & P08 = no loan/no escrow; P11 = escrow covers **tax only**; all other loaned
    properties = escrow covers **taxes + insurance**.
  - Depreciation basis (verify on a property detail / Schedule-E preview): building basis =
    purchase − land, e.g. **P01 = 106000**, **P11 = 95000** (land 0), matching
    `ledger/depreciation.csv → building_basis`.
- **Worker fires:** none.
- **Idempotency/cleanup:** `ownerEntityId` and per-property fee % are the two silent-failure traps — a
  wrong owner or fee here mis-attributes income and only surfaces in Act 3 owner statements. Verify each.

---

## ONB-04 — Confirm 19 active agreements via scan→draft→confirm — FLAGSHIP
- **Goal / exercises:** the flagship *"the computer types for you"* path — upload a lease document, let the
  LLM extract fields into a **draft**, review the proposed agreement, and confirm it against explicit
  LeaseManagement, Agreement, and TenantAccount targets. Historical account entries are loaded separately
  in ONB-08; agreement confirmation never selects a rent-generation mode. Also exercises the **multi-photo
  stitch** path on 3 leases.
- **Preconditions:** `ONB-03` done (properties/units exist to map onto); clock 2025-01-01.
- **Actor:** Priya (does the scanning/confirming), with Dana available to approve.
- **Surfaces:** `[UI]` — the flagship scan→draft→confirm via **`/scan/new-rental`** (`LeaseFirstImport`),
  which links to the existing property/unit from ONB-03 and confirms the reviewed agreement facts.
- **Steps:**
  1. **`[UI]`** For each of the **19 active leases** (L01–L07, L09–L15, L17, L18, L19, L21, L22), upload the
     born-digital lease PDF from `generated/leases/` and run scan→draft→confirm **through `/scan/new-rental`**:
     `generated/leases/L01_P01U1.pdf`, `L02_P02A.pdf`, `L03_P02B.pdf`, `L04_P03U1.pdf`, `L05_P04U1.pdf`,
     `L06_P051.pdf`, `L07_P052.pdf`, `L09_P06U1.pdf`, `L10_P07A.pdf`, `L11_P07B.pdf`, `L12_P08U1.pdf`,
     `L13_P091.pdf`, `L14_P092.pdf`, `L15_P093.pdf`, `L17_P10U1.pdf`, `L18_P11U1.pdf`, `L19_P12U1.pdf`,
     `L21_P13A.pdf`, `L22_P13B.pdf`.
  2. **`[UI]`** On each new-rental review screen: verify the extracted **tenant, property/unit, monthly
     rent, deposit, term dates, due-day** against `scenario/scenario.json → leases`, correct any
     low-confidence field, **link to the existing property + unit** (the duplicate-guard "link existing"
     choice — do NOT create a duplicate of the ONB-03 records), review the Agreement and TenantAccount
     targets, then confirm. Add co-tenants where present (L04 Mary Abernathy, L11 Minh Pham,
     L19 Dana Malloy).
  3. **`[UI]` Stitch path (3 leases):** for **L01, L11, L14** instead upload the per-page phone photos to
     exercise client-side multi-photo stitch → ONE draft: `generated/leases/photos/L01_p1.jpg`+`L01_p2.jpg`;
     `L11_p1.jpg`+`L11_p2.jpg`; `L14_p1.jpg`+`L14_p2.jpg`. Confirm as above (same `/scan/new-rental` review).
- **Expected results / assertions:**
  - **19 leases** created, all `Active`, each mapped to the right property/unit with the exact rent/deposit/
    due-day from `scenario.json` (spot-check: **L01** rent 1250 due-day 1; **L07** rent 850 **due-day 5**;
    **L10** rent 1100 **due-day 15**; **L17** rent 1525; **L19** rent 1600 co-tenant Dana Malloy).
  - Each confirmation creates one relationship, one reviewed agreement version, and the intended tenant
    account context; historical rent entries are absent until the bounded ONB-08 seed posts them.
  - Stitch: L01/L11/L14 each produced exactly **one** lease from **two** photos (not two drafts).
  - Occupancy now 19/21 (P05·3, P09·4 still vacant).
- **Worker fires:** none. Historical account entries are posted in `ONB-08`.
- **Idempotency/cleanup:** re-confirming the same lease double-creates — confirm each draft once. If a draft
  mis-maps the unit, fix on the draft before confirming (post-confirm requires deleting the lease).

---

## ONB-05 — Confirm 11 loans via mortgage-statement scan + debt-service back-fill
- **Goal / exercises:** loan ingestion by scanning a mortgage statement → `ConfirmAsLoan`, then firing the
  debt-service worker to back-generate the full amortization chain (interest/principal/escrow) from loan
  start to T0.
- **Preconditions:** `ONB-03` done; clock 2025-01-01. (P03 & P08 are paid-off → **no loan**.)
- **Actor:** Priya (scan/confirm), Engine (back-fill fire).
- **Surfaces:** `[UI]` mortgage-statement scan → `ConfirmAsLoan` (`/scan/[draftId]` isLoan; loans live on
  `/properties/[id]` → `PropertyLoansSection`) **+** `[WORKER]` debt-service back-fill. **PG-2:** there is
  no user "generate amortization history" button — the back-fill is worker-only, so firing it is legitimate
  scaffolding (no UI to convert).
- **Steps:**
  1. **`[UI]`** For each of the **11 loans**, upload the mortgage-statement JPEG and `ConfirmAsLoan`, mapping
     to the property and verifying lender / loan number / original amount / rate / term / P&I / escrow /
     escrow-cover flags against `ledger/loans.csv`:
     `generated/mortgage/P01_PSB-4471-0412.jpg`, `P02_PSB-4471-0418.jpg`, `P04_CCCU-2031-1203.jpg`,
     `P05_PSB-4471-0305.jpg`, `P06_CCCU-2031-0088.jpg`, `P07_FTB-8890-0210.jpg`, `P09_FTB-8890-1450.jpg`,
     `P10_CCCU-2031-0063.jpg`, `P11_PSB-4471-0009.jpg`, `P12_FTB-8890-1155.jpg`, `P13_CCCU-2031-0330.jpg`.
  2. **`[WORKER]`** fire `POST /api/v1/dev/workers/debt-service/run-once` to back-fill all `LoanPayment`
     rows from each loan's `start_date` to T0.
- **Expected results / assertions:**
  - **11 loans** exist mapped to P01,P02,P04,P05,P06,P07,P09,P10,P11,P12,P13; P03 & P08 have none.
  - Loan terms match `ledger/loans.csv` — spot-check **P01** (Prairie State Bank, 96000 @ 4.25%, esc 310,
    covers T+I), **P09** (Fifth Third, 272000 @ 5.50%, esc 720), **P11** (76000 @ 4.00%, esc 180, covers
    **T only**), **P12** (158400 @ 6.75%).
  - After the debt-service fire, each loan's amortization schedule (`/loans/{id}/payments`) shows the
    chained balance. Spot-check the **2025-01 opening balance**: P01 = **89405.08**, P09 = **260725.27**,
    P12 = **156711.85** (`loans.csv → balance_2025_01_open`). CY2025 interest/principal totals are asserted
    in Act 3 (`REC-LOAN-SCHED`).
- **Worker fires:** `debt-service` (run-once).
- **Idempotency/cleanup:** debt-service is idempotent on `(LoanId, PeriodKey)` — safe to re-fire. Reports
  ignore `LoanPayment.Status`, so no mark-paid step is needed for loans.

---

## ONB-06 — Recurring-expense templates + direct tax/insurance setup
- **Goal / exercises:** the recurring-expense engine — configure the operating-expense templates once and
  let the worker back-fill the historical rows; establishes the monthly cadence Act 2 continues.
- **Preconditions:** `ONB-03` done; clock 2025-01-01.
- **Actor:** Priya (create templates), Engine (back-fill fire).
- **Surfaces:** `[UI]` recurring-expense template create on each **`/properties/[id]`**
  (`PropertyRecurringExpensesSection`) **+** `[WORKER]` recurring-expense back-fill (same PG-2 — no user
  "generate history" button; worker-only).
- **Steps:**
  1. **`[UI]`** Create the **4 recurring-expense templates** (StartDate 2023-01-01 so history back-fills),
     from `scenario/scenario.json → recurring`:
     - **REC-HOA-P11** — P11, category **Other**, "Condo HOA dues", **$185**, Monthly (all months).
     - **REC-TRASH** — P02, P05, P07, P09, P13, category **Utilities**, "Trash collection", **$45**,
       Monthly (all months).
     - **REC-LAWN** — P04, P06, P10, P12, category **CleaningMaintenance**, "Lawn care", **$120**, Monthly
       **Apr–Oct only** (P01 excluded — tenant maintains lawn per lease).
     - **REC-PEST** — P02, P05, P07, P09, P13, category **CleaningMaintenance**, "Pest control", **$90**,
       **Quarterly** (Jan/Apr/Jul/Oct).
  2. **`[WORKER]`** fire `POST /api/v1/dev/workers/recurring-expense/run-once` to back-fill history (≤36
     periods from StartDate through T0).
  3. Note: **escrow-property** taxes/insurance and **P03/P08 direct** taxes/insurance are **hand-entered on
     disbursement dates in Act 2** (Jan insurance, Feb/Jul property tax), NOT recurring templates.
- **Expected results / assertions:**
  - 4 active templates exist with the exact properties/amounts/cadence above.
  - After the fire, back-filled `Expense` rows exist for 2023–2024 (Pending status is fine — Schedule E
    buckets by `IncurredAt` with no status filter). Spot-check a P11 HOA row per month at $185 and a
    quarterly pest row (Jan/Apr/Jul/Oct) at $90.
  - **CY2025 totals these templates will produce** (asserted in Act 3, here just sanity): HOA 12×185 =
    **2220**; trash 5 props × 12 × 45 = **2700**; lawn 4 props × 7 mo × 120 = **3360**; pest 5 props × 4 ×
    90 = **1800** (cf. `expected/cy2025-portfolio.json → vendor1099`).
- **Worker fires:** `recurring-expense` (run-once).
- **Idempotency/cleanup:** idempotent per `(template, period)`. Re-firing is safe. Getting the **lawn month
  set** (Apr–Oct) and **pest quarters** right matters — a wrong cadence changes CleaningMaintenance totals.

---

## ONB-07 — Security deposits (19 held at T0)
- **Goal / exercises:** the security-deposit register — one `SecurityDepositHolding` per active lease.
- **Preconditions:** `ONB-04` done (19 leases active); clock 2025-01-01.
- **Actor:** Priya.
- **Surfaces:** `[UI]` — `/deposits` (new holding per lease).
- **Steps:** **`[UI]`** enter a **held** security deposit for each of the 19 active leases, amount = the lease's
  deposit (= one month's rent), from `expected/cy2025-portfolio.json → deposits.byLease`: L01 1250, L02 950,
  L03 975, L04 1150, L05 1450, L06 850, L07 850, L09 1395, L10 1100, L11 1150, L12 1050, L13 825, L14 825,
  L15 825, L17 1525, L18 1100, L19 1600, L21 925, L22 940. (L08/L16/L20 deposits are entered when those
  leases start in Act 2.)
- **Expected results / assertions:**
  - Security-deposit register shows **19 holdings**, total **held = $20,735.00**, deductions 0, returned 0,
    balance $20,735.00. (Cross-check: this is the T0 subset of the year-end `deposits.totals.held` 23905,
    which adds L08 850 + L16 825 + L20 1495 during Act 2.)
- **Worker fires:** none.
- **Idempotency/cleanup:** one holding per lease — don't double-enter. Opening balances: **none** in this
  corpus (every tenant is current at T0; `running_balance` = 0.00 throughout `events.csv`). If exercising
  the `OpeningBalance` feature, note it folds only into the per-lease ledger, never into P&L/Schedule E
  (design §8.9).

---

## ONB-08 — Import CY2023–24 tenant-account history  ·  [PENDING GAP1]
- **Goal / exercises:** bring **2 full tax years** of transactions into the books so 2023/2024 reports are
  reconstructable. Each tenancy receives historical rent charges plus the corresponding append-only
  receipts, dated in the year the money was received.
- **Preconditions:** `ONB-04/05/06` done (leases, loans, recurring templates back-filled); clock
  2025-01-01.
- **Actor:** Priya (spine path) / Dana (import).
- **Surfaces:** `[SEED]` for the bulk 2-year posting (justified — clicking ~500 rows through the UI is
  impractical; the real path is GAP1 bulk import) **paired with a required representative `[UI]` sample** so
  the manual pay path is genuinely tested at least once (README PG-5).
- **Steps — a required UI sample + two bulk paths:**
  - **`[UI]` (required representative sample):** on the unit **Rent** view, record a handful of **2024**
    receipts by hand — e.g. L01's 2024 receipts — so the real receipt path is exercised; open each exact
    tenant-account entry detail and confirm the date, amount, method, and reference.
  - **`[PENDING GAP1]` Intended (bulk import):** use the **bulk transaction import** to load the CY2023–24
    rent payments and one-off expenses in one pass (CSV/spreadsheet keyed by lease/property, amount, date,
    method, category). `PENDING GAP1 — confirm exact import UI / file format / field mapping when the
    feature lands (Notion 390394b0689d8145a6b7db2fd605f004).`
  - **`[SEED]` (bulk, current app):** for the remaining ~500 rows, post each historical receipt to its
    tenant account with `POST /api/v1/tenant-accounts/{tenantAccountId}/receipts`, a unique idempotency key,
    its historical effective date (≈ due+2, cf. `events.csv`), and the lease's method. Recurring and
    one-off expenses count by `IncurredAt`.
- **Expected results / assertions (spot-check only — history is held-flat, not cent-exact):**
  - **CY2023 Schedule E:** income **221995.00**, total expenses **171276.20**, net **50718.80**
    (`expected/spotcheck-2023-2024.json → spotcheck.2023.scheduleE`).
  - **CY2024 Schedule E:** income **248820.00**, total expenses **197646.47**, net **51173.53**
    (`…2024.scheduleE`).
  - The 2023/2024 charges and receipts appear as distinct entries in each tenant-account ledger, dated
    in the correct year, with balances matching the expected history.
- **Worker fires:** none (receipt posting / import only).
- **Idempotency/cleanup:** reuse the same operation key for retries so a receipt is never double-posted.
  **GAP1 note:** when bulk import lands, this scenario's spine path is
  retired in favor of the import; the CY2023–24 corpus figures may be regenerated (`scenario.json` re-run)
  — a corpus-revision trigger, not a spine bug.

---

## ONB-09 — T0 onboarding verification (dashboard / rent-roll / occupancy / ledger back-fill)
- **Goal / exercises:** a single consolidated checkpoint that the T0 state is correct before the clock
  advances — the "books are open and correct on 2025-01-01" gate.
- **Preconditions:** `ONB-01…ONB-08` done; clock still frozen at 2025-01-01.
- **Actor:** Dana.
- **Surfaces:** `[UI]` (read-only) — portfolio dashboard, rent roll, occupancy, `/deposits`, `/leases/[id]`
  ledger. All figures are read from the screens.
- **Steps:** **`[UI]`** open the portfolio **dashboard**, the **rent roll**, the **occupancy** report, the
  **security-deposit register**, and **L01's tenant ledger**; read the figures.
- **Expected results / assertions:**
  - **Portfolio dashboard:** 13 properties, 21 units, **19 active leases**, **19/21 occupied** (2 vacant:
    P05·3, P09·4), 11 loans.
  - **Rent roll:** Σ monthly rent over the 19 active leases = **$20,735.00/mo**.
  - **Occupancy:** 19/21 = **90.5%**; vacant = P05·3 and P09·4.
  - **Security-deposit register:** held **$20,735.00** across 19 leases (from `ONB-07`).
  - **Ledger back-fill:** **L01** (James Whitfield, P01) tenant ledger shows rent history back-filled from
    lease start (2024-04) through 2024-12, all **Paid** after `ONB-08`, running balance **0.00**
    (`events.csv` L01 rows E00001…; balance 0.00). A short-history lease (**L12** Carla Reynolds, start
    2025-01-01) shows only the Jan 2025 charge.
  - **Loans:** 11 schedules present with 2025-01 opening balances per `ONB-05`.
  - **Workers enabled:** `notifications/settings` shows RentCharges/LateFees/NotifyTenants = true.
- **Worker fires:** none.
- **Idempotency/cleanup:** read-only. A mismatch here (wrong active-lease count, wrong owner attribution,
  Scheduled-not-Paid history) is a **blocking** onboarding bug — fix before advancing the clock into Act 2.

---

### Act 1 → Act 2 handoff
At the end of Act 1: clock frozen at 2025-01-01; 13 properties / 21 units / 19 active leases (2 vacant) /
11 loans / 4 owners / 4 recurring templates / 19 deposits; 2023–24 history Paid; workers enabled. Act 2
un-freezes/advances the clock month by month.
