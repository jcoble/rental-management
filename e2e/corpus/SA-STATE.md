# Scenario Architect (SA) — Durable State / Resume-From-Here

> **If this SA process is ever interrupted or restarted, START HERE.** This file is the
> authoritative, on-disk source of truth for the Scenario-Corpus lane of the E2E initiative.
> The orchestrator's own ledger is `Docs/e2e/ORCHESTRATION-LEDGER.md` (not mine — don't edit).
> **Durability rule (standing):** every number/scenario/ledger row is written to disk **as it is
> produced**, never held only in working memory. On-disk files are the real state.

- **Role:** Scenario Architect — long-lived agent for the whole initiative (NOT one-shot).
- **Last updated:** 2026-07-01
- **Status:** **AUTHORING the Tester Scenario Catalog** (new deliverable, in progress — see the
  "Tester Scenario Catalog" section below). Separately: **Phase-2 corpus-DATA generation stays
  PAUSED** (do NOT regenerate `events.csv`/`expected/*.json`/scans; those await the post-features
  "REVISE corpus" re-brief). The catalog is authoring work that *consumes* the frozen corpus + the
  cent-exact `expected/*.json` already on disk; it does not touch the data pipeline.
- **Phase-2 generation PAUSED context (unchanged):** Steps 1–3 were executed once (against the current
  app) and **kept as pipeline validation**. The 7 gaps are now being **implemented before testing**,
  which will change the corpus, `events.csv`, and reconciliation predicates → **do NOT generate
  further**; the eventual revision = edit `scenario.json` + the builders + re-run the proven pipeline.

### What's on disk (steps 1–3 outputs)
- **Finance (authoritative, from the app's own calculators):** `ledger/loans.csv` (11), `ledger/loan-payments.csv` (720 rows), `ledger/depreciation.csv` (13). Emitter `tools/emit-finance/` (ran once).
- **Ground-truth event log:** `ledger/events.csv` — **1,573 rows** CY2023–25 (686 rent, 379 loan, 473 expense, 6 late-fee, deposits, memo) with per-lease running balances. Builder `tools/build-ledger.mjs`.
- **Expected figures:** `expected/cy2025-by-property.json`, `expected/cy2025-portfolio.json`, `expected/spotcheck-2023-2024.json`. Deriver `tools/derive-expected.mjs` — **all 13 properties reconcile to the cent** (DIV1/DIV2/DIV3 pass). Deriver is the Phase-5 reconciliation authority.
- **110 scan artifacts, 9.5 MB** (git-ignored `generated/`): 22 lease PDFs + 6 photo-stitch JPEGs, 11 mortgage JPEGs, 48 rent-check JPEGs, 14 receipt JPEGs, 4 application PDFs, 5 captioned maintenance JPEGs. Manifest `tools/build-manifest.mjs` → `tools/data.full.json` → `tools/generate.mjs`.
- **4th divergence discovered + asserted:** `Other`-type payment income (e.g. the $35 NSF fee) is counted by Property-P&L but NOT Schedule E (app only counts Rent/LateFee/Utility). Encoded in the DIV1 self-check (`nonScheduleEIncome`).
- Portfolio CY2025 headline: Schedule E income $265,818.72 / net $65,717.15 (interest $72,830.15, depr $69,709.09); NOI $247,356.39; debt service $159,416.52; deposits held $23,905 / bal $22,455; year-end delinquency $899.75 (L14 fresh Dec miss).

### PIVOT — what changed and what it means (2026-07-01)
- User decided the **7 flagged gaps become real features** (bulk import, **owner-distribution as a real
  record**, proration, capital-improvement depreciation, app-fee income, sale/disposition, eviction).
- **Q2 answer SUPERSEDED:** owner distributions will be a **real tracked record**, not just computed net
  (the ledger's memo-column workaround is moot once the feature lands).
- The import strategy (§4) may change (Gap #1 bulk import) and proration/app-fee/eviction beats become
  first-class — so events.csv + expected must be authored AGAINST the improved app, after features land.
- **Feature-invariant (safe to keep/build now):** `scenario/scenario.json` (frozen business),
  amortization + base depreciation (emitter output). These do not change with the gaps.

## My responsibilities across the initiative
1. **Phase 2 — mass-generate the corpus:** all ~145 scan artifacts + the full CY2023–2025
   ground-truth ledger (`events.csv`) + per-report `expected/*.json`, written incrementally to disk.
2. **During test runs — verify execution:** confirm each scenario event is executed on the correct
   **simulated date**, and that worker-dependent events (rent charges, late fees, reminders, notice
   drafts, debt service) line up with the fires the calendar prescribes.
3. **Year-end — reconcile:** diff MY ground-truth ledger against the app's own reports; produce the
   pass/fail matrix, pre-asserting the deliberate divergences.

## Authoritative files (the durable state)
| File | Role |
|---|---|
| `Docs/superpowers/specs/2026-06-30-e2e-scenario-corpus-design.md` | Master design (portfolio, owners, import, reconciliation model, gaps, batch plan). **54 KB.** |
| `Docs/superpowers/specs/2026-06-30-e2e-operations-calendar-CY2025.md` | Full ~104-row CY2025 calendar + per-month artifact tally. |
| `Docs/superpowers/specs/2026-07-01-master-simulation-clock-design.md` | Sibling track (TSK-615): clock + worker-trigger surface I drive. |
| `Docs/superpowers/specs/2026-07-01-e2e-replay-driver-design.md` | Step-4 design: seed → advance+fire → reconcile against the TSK-615 contract. |
| `e2e/corpus/ledger/events.sample.csv` + `loans.sample.csv` + `depreciation.sample.csv` | **P01 CY2025 proof slice** (machine-verified exact). Phase-2 promotes these to the full `events.csv` etc. |
| `e2e/corpus/expected/P01-2025.reconciliation.sample.json` | P01 expected figures per report + seed preconditions + divergence identities. |
| `e2e/corpus/templates/*.html` + `tools/generate.mjs` + `tools/data.proof.json` | Artifact generation pipeline (Chrome+poppler; JPEG photos). |
| `e2e/corpus/README.md` | Pipeline how-to + footprint. |
| `e2e/corpus/generated/` | Rendered artifacts — **git-ignored, regenerable** (never the source of truth). |

## Fixed scenario identifiers (STABLE — do not renumber; downstream references depend on these)
- **Company:** Okafor Property Group, Springfield/Clark County OH. Timezone America/New_York.
- **Users/roles:** Dana Okafor (Admin), Priya Nair (Manager), Tomás Reyes (Agent), Marcus Bell (Owner), tenants (Tenant).
- **Owner entities:** OE-LLC (Okafor Rentals LLC, IsPrimary, 0% fee) · OE-BELL (Bell-Okafor Holdings LLC, 8%) · OE-TRUST (Okafor Family Trust, 8%) · OE-PERS (Dana personal, 0%).
- **Properties P01–P13 / 21 units / 11 loans:** see design §2.2 (frozen table). P03 & P08 paid-off (direct tax+ins). P11 condo (land 0, tax-only escrow).
- **Leases L01–L19 active at T0; L20 (P04 turnover), L21 (P05·3), L22 (P09·4) created live in CY2025.** End-date ladder in design §3.2.
- **Anchor timeline:** history CY2023–2024 · onboarding/books-start T0 = **2025-01-01** · live ops CY2025 · reconcile **2025-12-31**. CY2025 is the cent-exact reconciliation spine.

## Verified mechanism cheat-sheet (source-checked — the load-bearing facts for Phase 2)
- **Rent history:** confirm the Agreement against explicit LeaseManagement and TenantAccount targets, then post the bounded historical account-entry corpus separately. Scan confirmation never selects a rent-generation mode or silently creates history.
- **Loans:** `DebtServiceService.GenerateAsync` back-fills all `LoanPayment` rows (Scheduled; reports ignore status, sum by DueDate). **No create-time/HTTP trigger** — call the service via TSK-615 dev worker-trigger.
- **Recurring expenses:** back-fill ≤36 periods from template StartDate; Pending rows count (Schedule E buckets by IncurredAt). Same "no create-time trigger" caveat.
- **Enable workers:** `PUT /api/v1/notifications/settings` (on `NotificationsController`) — **full-object overwrite**, GET→mutate→PUT. Turn on `EnableRentCharges`, `EnableLateFees`, `NotifyTenants`. `NoticeAutopilot`+`RecurringMaintenance` always on.
- **HARNESS INVARIANT:** always set `Property.OwnerEntityId` explicitly (omitting → silent fallback to primary self-owner → misattributed income that still "passes").
- **Rent-check scan double-count:** in the 5 scan-months, a scanned check **replaces** (waive/delete) the worker's Scheduled rent row for that lease-month (verify whether `ConfirmAsPayment` sets `PeriodKey`).
- **Seed preconditions (P01 & analogous):** `Property.ManagementFeePercent` correct per property (fee is per-property, not per-owner); `AccumulatedDepreciation` seeded ≤ basis−annual and `ManualAnnualDepreciation` null.
- **Clock:** injected `TimeProvider` (dev/test), polled `SimulationClock` table, dev worker-trigger command table (per TSK-615). Not moving the OS clock.

## Ground-truth ledger — write protocol (durability-critical)
- **Schema:** design doc §6.1 (`events.csv` columns; `loans.csv`; `depreciation.csv`; `deposits.csv`). Event log = **one row per money event with full attributes**; report figures are derived views, never pre-summed.
- **Write incrementally:** as each property/month/loan is computed, **append its rows to disk immediately** and update running balances (tenant ledger balance; loan `balance_after` chain must chain period→period). Do NOT hold a year of math in memory.
- **Running balances:** per-lease tenant balance (charges − payments incl. opening balance) and per-loan amortization chain (first open = OriginalAmount; each `balance_after` = next open; final ties to `Loan.CurrentBalance`).
- **Verify-as-you-go:** re-run the divergence-identity check (README) after each property is authored, so drift is caught immediately, not at year-end.

## Decisions LOCKED (team-lead, 2026-07-01) — **UPDATED post-pause** (some supersede the first pass)
1. **CY2025 = 100% app-generated, cent-exact reconciliation spine.** CY2023–24 = **spot-checked**. (unchanged)
2. **Owner distributions = a REAL first-class `OwnerDistribution` record** (gap #2 being built). Book actual
   distributions as real records — **NOT memo-only, NOT as Expense.** ⟵ *supersedes the memo-column approach.*
3. **Payments/autopay = REAL Stripe TEST MODE** (user logs into Stripe via Chrome). Online-payment + autopay
   paths (L09, L17 + any online payments) run through **real Stripe test-mode payments** — reconcile against
   **Stripe's recorded test amounts**, and **allow for webhook/settlement timing** (not instant). Non-Stripe
   forms (check/cash/ACH/money-order/Zelle) stay as recorded Payments. ⟵ *supersedes "mark Paid / simulate."*
4. **Maintenance intake = BOTH:** captioned placeholder photos + a couple via **typed/voice** intake. (unchanged)
5. **git-ignore `e2e/corpus/generated/`** confirmed. (unchanged)
6. **Clock = INJECTED `TimeProvider` via TSK-615 dev endpoints.** (unchanged; contract below)
- Also: Phone photos = JPEG. Co-owned LLC = one OwnerEntity (no fractional-ownership table).

## REVISION SCOPE — new capabilities the revised corpus must EXERCISE (stop designing around their absence)
The 7 gaps become real features; the revision should USE them (not work around them):
- **Bulk transaction import** for the 2-yr history → replace the worker-backfill + per-payment `mark-paid`
  approach with the real bulk-import path (import CY2023–24 payments/expenses).
- **Rent proration** on real move-in/out partial months → L16 (03-15 in) and L20 (07-01) + L05 (05-31 out)
  use the app's proration, not hand-entered manual Payments.
- **Capital-improvement depreciation** → ADD a capital improvement (e.g. a roof) that **depreciates on its own
  schedule**, not expensed as Repairs. New depreciation lines in the ground truth.
- **Property SALE / disposition with gain/loss** → ADD a sale scenario (allowed now): sell one property mid/late
  CY2025, exercise sale-year depreciation + gain/loss + §1250 recapture in the year-end reports.
- **Application/screening-fee income** → real application fees (no lease needed) as income — add to the leasing
  funnel (L08/L16/L20 applicants + the declined one).
- **First-class eviction** → escalate the P09 delinquency (currently L14 lump-cures) into a real **eviction**
  path/record (delinquency → notices → eviction filing → resolution). Rework the L14 arc accordingly.
When the re-brief lands: read each landed feature's shape (entities/endpoints/reports), then update
`scenario.json` (+ `storyBeats`) and the builders (`build-ledger`, `derive-expected`, `build-manifest`) and re-run.

## Replay-driver contract (TSK-615 dev surface — endpoints not live yet; code to this)
- **Clock:** `POST /api/v1/dev/clock/set {instantUtc|date, timeZoneId?, mode?="offset"|"frozen"}` · `POST /api/v1/dev/clock/advance {days?,hours?,...}` · `GET /api/v1/dev/clock` · freeze/unfreeze/reset.
- **Workers:** `POST /api/v1/dev/workers/{key}/run-once` · `POST /api/v1/dev/workers/run-due` (fires all due in dependency order). Keys: `rent-charge, autopay, late-fee, recurring-expense, recurring-maintenance, debt-service, lease-expiry-reminder, notice-draft, daily-briefing`. Idempotent/re-fireable.
- Dev-only + admin-auth. **Two requirements:** (1) T0 `PUT /api/v1/notifications/settings` to enable `EnableRentCharges/EnableLateFees/NotifyTenants` (full-object overwrite) or nothing generates; (2) default simulated LocalTimeZone = **America/New_York**.

## Tester Scenario Catalog (NEW deliverable — in progress, 2026-07-01)
The team needs a **tester-facing playbook**: discrete, labeled, browser-agent-executable scenarios so
the (separate) tester-harness agents know EXACTLY what to do and what to assert. It *bridges* the data
(corpus + ground truth) and the automated replay calendar into per-scenario UI-intent + expected-result
assertions. **This is NOT the paused Phase-2 DATA regen** — it consumes what's on disk.

- **Where:** `e2e/corpus/scenarios/` — `README.md` (index + consumption guide + id scheme + legend),
  `act1-onboarding.md`, `act2-operations.md`, `act3-reconciliation.md`, and `scenarios.json` (machine
  index the harness iterates). **Scenario ids are STABLE** — downstream references depend on them.
- **Scenario id scheme:** Act 1 `ONB-NN`; Act 2 monthly-batch templates `TMPL-B1/B2/B3` instantiated as
  `OPS-<MON>-B1/B2/B3`, story beats `OPS-<MON>-<SLUG>`, gap spotlights `GAP<N>-<SLUG>`, Stripe
  `STRIPE-<SLUG>`; Act 3 `REC-<SLUG>`.
- **Assertion sourcing (cent-exact):** `expected/cy2025-by-property.json`, `expected/cy2025-portfolio.json`,
  `expected/spotcheck-2023-2024.json`, `ledger/loans.csv`, `ledger/depreciation.csv`, and specific
  `events.csv` rows (cited by event_id).
- **4 divergence identities (VERIFIED, pre-assert as EXPECTED not bugs):** DIV1 = propertyPnl.net −
  scheduleE.net = mortgageInterest + depreciation + Other-income; DIV2 = escrowed Taxes+Insurance excluded
  from NOI opex; DIV3 = trueCashFlow.cashFlow − scheduleE.net; **4th** = the $35 NSF `Other`-type income
  counted by Property-P&L but not Schedule E (P03; folds into DIV1's `nonScheduleEIncome`). Also note the
  owner-statement (Rent-only) vs Schedule E delta = the $250 L14 late fees (design divergence c).
- **7 gap features → author to LOCKED decisions (§82-108), flag `PENDING GAP<N>`:** GAP1 bulk import,
  GAP2 owner-distribution record, GAP3 proration, GAP4 capital-improvement depreciation, GAP5
  application/screening-fee income, GAP6 property sale/disposition, GAP7 first-class eviction (reworks the
  L14 cure arc). Stripe autopay (L09/L17) = real TEST MODE, authored as a standalone OFF-ledger scenario
  (spine keeps them marked Paid for determinism). Where a gap reworks the spine, the scenario notes the
  expected-figure shift needs corpus revision (does not silently change `expected/*.json`).
- **Dev surface the harness drives (final TSK-615 contracts):** `POST /api/v1/dev/clock/set|advance`,
  `/clock/{freeze|unfreeze|reset}`, `GET /clock`; `POST /api/v1/dev/workers/{key}/run-once`,
  `/workers/run-due` (order: rent-charge→notice-draft→lease-expiry-reminder→late-fee→autopay→debt-service→
  recurring-expense→recurring-maintenance), `GET /workers/commands/{id}`. Gated `Simulation:Enabled &&
  non-prod`; web needs `PUBLIC_SIMULATION_ENABLED=true`.
- **Progress:** **COMPLETE (v1 draft).** [x] README · [x] Act 1 (9: ONB-01..09) · [x] Act 2 (4 templates
  TMPL-B1/B2/B3/NOTICE + 36 monthly OPS-<MON>-B1/2/3 + 12 OPS-<MON>-NOTICES + ~28 story beats + 7 gap
  spotlights GAP2..7/STRIPE) · [x] Act 3 (17: REC-*) · [x] `scenarios.json` (**118 scenarios** + 4
  templates; JSON valid, unique ids/orders; 16 `pendingFeature`-tagged: GAP1×1, GAP2×5, GAP3×2, GAP4×1,
  GAP5×3, GAP6×1, GAP7×1, STRIPE×2).
  **Verified correction folded in (team-lead independently confirmed):** the 19 T0-active leases are
  L01–L07, L09–L15, L17–L19, L21, L22 (L08/L16 vacant, L20 = July turnover). `scenario.json`+`events.csv`
  authoritative (L08 = P05·3 lease-up). **Errata blocks placed** atop the operations-calendar doc and the
  design §3.2 ladder pointing to `scenarios/README.md`; README carries the "catalog is authoritative,
  calendar defers to it" authority note. The catalog is now the authoritative tester + replay instruction set.
  **Open decisions for the user (non-blocking):** GAP4 capital-improvement (proposed P09 roof $9,900 @
  2025-08-01) and GAP6 sale (proposed P08 @ $135k on 2025-10-31) are net-new/off-spine — exact
  asset/price/date to be confirmed on the post-features corpus revision.
  **True-to-life pass DONE (team-lead directive):** every step is classified by **execution surface** —
  `[UI]` (drive the real screen; default for user actions), `[DEV-CLOCK]`/`[WORKER]` (dev time-travel /
  worker-fire scaffolding — legit, no user equivalent), `[SEED]` (bounded bulk shortcut). README has an
  "Execution surfaces" section (tags + principle + the verified action→screen map + 5 product-gap findings
  PG-2..5). Converted the API-shortcut steps to `[UI]`: ONB-02 worker-enable→Settings; ONB-04 agreement
  review→`/scan/new-rental` with explicit management/account context; all CY2025 operational payments→lease-detail "Mark paid"/`/accounting/
  past-due`/`/scan`. ONB-08 2-yr history stays `[SEED]` (GAP1) + a required representative `[UI]` mark-paid.
  `scenarios.json`: every scenario+template now carries a **`surfaces`** array (62 pure UI, 26 dev-clock+worker,
  12 UI+dev-clock (B2), 10 worker+UI (notices), 4 UI+worker, 3 UI+dev-clock+worker, 1 UI+seed). Verified
  the web UI in `web/src/routes`+`web/src/lib/components` — nearly every action has a real screen; the dev
  clock/workers correctly have none (superadmin engine page = monitoring-only).
  **Act 3 posture (team-lead guardrail, hardened):** Act 3 asserts cent-exact against **today's**
  `expected/*.json` (app-as-built, NO gap features); every gap's dollar impact lives ONLY in the Act-3
  *Gap-pass reconciliation* **staged-delta table** to fold into `expected/*.json` at the corpus revision —
  never baked into a spine assertion (or reconciliation fails against today's data). Spine pass = today;
  gap pass = re-run after the revision.

## Phase-2 execution checklist
1. [x] **Freeze numbers + finance** — `scenario/scenario.json` frozen; `tools/emit-finance/` ran once → `ledger/{loans,loan-payments,depreciation}.csv` (authoritative, matches the app's calculators).
2. [x] **Full `ledger/events.csv`** CY2023–25 (1,573 rows) via `tools/build-ledger.mjs`, running balances maintained. (History = held-flat rent from max(2023-01, acquisition); documented simplification.)
3. [x] **`expected/*.json`** via `tools/derive-expected.mjs` — all 13 properties reconcile to the cent; 4 divergences asserted.
4. [x] **110 scan artifacts** via `tools/build-manifest.mjs` + `tools/generate.mjs` (9.5 MB, JPEG photos + born-digital PDFs, all verified legible/text-extractable).
5. [x] **Replay-driver spec** DESIGNED — `Docs/superpowers/specs/2026-07-01-e2e-replay-driver-design.md` (targets the TSK-615 contract; harness invariants; data→API mapping; report→expected mapping; 4 divergences). **Wire + dry-run when clock endpoints live.**
6. [ ] **Phase-5 reconcile** — pull each app report, diff vs `expected/*.json` (deriver = authority), pre-assert the 4 divergences. (Needs endpoints live + a `reconcile.mjs`.)
7. [ ] **Post-features corpus revision** — when the 7 gaps land, edit `scenario.json` + re-run the pipeline (owner-distribution record, proration, bulk import, app-fee income, eviction).
8. [x] **Tester Scenario Catalog** — `e2e/corpus/scenarios/` (README + act1/2/3 + `scenarios.json`, **118
   scenarios** + 4 templates). Bridges corpus→tester; consumes frozen data; gap-affected scenarios flagged
   `PENDING GAP<N>` (16 flagged). v1 draft complete; refine on tester-harness feedback + the corpus revision.
