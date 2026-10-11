# Act 3 — Reconciliation (2025-12-31, clock FROZEN)

Year-end: pull **every app report** and diff it against the independently-kept ground-truth ledger. The
CY2025 spine is **cent-exact** — each figure below is the authoritative value in `expected/*.json` (derived
by `tools/derive-expected.mjs` from `ledger/events.csv` + the emit-finance CSVs). A mismatch on a spine
figure is a **bug**. The **4 deliberate divergences** are pre-asserted as **EXPECTED** (not bugs).

> **POSTURE (per team-lead) — reconcile the app-as-built:** every assertion below is cent-exact against
> the **CURRENT** `expected/*.json` — the CY2025 spine **as it exists today, WITHOUT any of the 7 gap
> features**. Do **NOT** bake any gap's numeric impact into these spine assertions — the app hasn't built
> them yet, so folding them in would make reconciliation fail against today's data. Each gap's dollar
> impact is held as an explicitly-tracked **staged delta** in *§ Gap-pass reconciliation* (bottom), to be
> **folded into `expected/*.json` during the post-features corpus revision** and asserted only in the
> **gap pass**. Spine pass = this act as written; gap pass = re-run after the revision with the staged
> deltas applied.

**Global preconditions:** Act 1 + Act 2 complete against one DB; clock **frozen at 2025-12-31**
(`POST /api/v1/dev/clock/set {"date":"2025-12-31","timeZoneId":"America/New_York","mode":"frozen"}`).
**Actor:** Dana (Admin) for all reconciliation reads (owner-scoped reports also viewable by Marcus for
OE-BELL). All scenarios are **read-only** — no idempotency concerns; re-read freely.

**Execution surface: every REC-* is `[UI]`** — the browser agent **opens the report screen** and reads the
figure off the page (`/reports/[report]`, `/tax`, `/accounting`, `/accounting/year-end`, `/owners-report`,
`/deposits`, the portfolio dashboard — see the README action→screen map). Endpoint paths cited below (e.g.
`/accounting/schedule-e`) name the **route behind the screen**, not an API call to make — the **action is
opening the screen**; `expected/*.json` and any `GET` are the **secondary cross-check**, not the primary
read. No `[DEV-CLOCK]`/`[WORKER]`/`[SEED]` in Act 3.

Authoritative sources: `expected/cy2025-portfolio.json` (portfolio + owner + deposits + delinquency +
vendor1099), `expected/cy2025-by-property.json` (per-property + `divergenceChecks`),
`expected/spotcheck-2023-2024.json` (history), `ledger/loans.csv`, `ledger/depreciation.csv`.

---

## REC-SCHED-E — Schedule E (portfolio + per property)
- **Goal:** the tax-year Schedule E reconciles to the cent, portfolio and per property.
- **Steps:** open `/accounting/schedule-e` for CY2025 (portfolio), then drill into each property.
- **Assertions — portfolio** (`cy2025-portfolio.json → portfolio.scheduleE`):
  - income **265818.72**, deductibleExpenses **57562.33**, mortgageInterest **72830.15**, depreciation
    **69709.09**, totalExpenses **200101.57**, netIncome **65717.15**.
- **Assertions — per property** (`cy2025-by-property.json → byProperty.<P>.scheduleE`, spot the range):
  | Prop | income | deductible | interest | depreciation | net |
  |---|---|---|---|---|---|
  | P01 | 15337.50 | 2780 | 3762.90 | 3854.55 | 4940.05 |
  | P03 | 14007 | 2380 | 0 | 3272.73 | 8354.27 |
  | P09 | 37373.67 | 7577.33 | 14232.56 | 10618.18 | 4945.60 |
  | P10 | 18574.50 | 4370 | 9392.49 | 5527.27 | **−715.26** |
  | P12 | 19200 | 4510 | 10522.85 | 5890.91 | **−1723.76** |
  - (All 13 in the file; P10 & P12 are deliberately **negative** net — high-rate recent acquisitions.)
  - Income = Rent + LateFee (+ Utility, none here) Paid/Partial by PaidDate year; **no Utility payments** →
    Schedule-E income == Cash-Flow-P&L income (divergence (a) delta = 0).
- **Idempotency/cleanup:** read-only.

## REC-SCHED-E-DETAIL — category / depreciation / interest detail
- **Goal:** the Schedule E line detail — deductible-by-category, the depreciation line (formula, not an
  expense row), and the interest line (from `LoanPayment`, not an expense row).
- **Steps:** open the Schedule E category/detail view per property; check depreciation + interest lines.
- **Assertions:**
  - **Deductible by category, portfolio:** Taxes **30360**, Insurance **13520**, Other(HOA) **2220**,
    Utilities(trash) **2700**, CleaningMaintenance **5820** (lawn 3360 + pest 1800 + carpet 520 + gutter
    140), Repairs **2942.33** (182.33+245+680+640+210+985). Σ = **57562.33**.
  - **Per-property categories** match `cy2025-by-property.json → byProperty.<P>.scheduleE.deductibleByCategory`
    (spot: P02 `{Utilities 540, CleaningMaintenance 360, Taxes 2400, Insurance 1050, Repairs 640}`; P09
    `{Utilities 540, CleaningMaintenance 360, Repairs 427.33, Taxes 4200, Insurance 2050}`; P11 `{Other
    2220, Insurance 320, Taxes 1350}`).
  - **Depreciation lines** = `ledger/depreciation.csv → dep_2025` per property (P01 3854.55, P05 7636.36,
    P07 6400.00, P09 10618.18, P11 3454.55 (land 0 → full basis), P12 5890.91). No manual Depreciation
    expense row is counted.
  - **Interest lines** = `ledger/loans.csv → interest_2025` per loan (P01 3762.90, P09 14232.56, P12
    10522.85; P03 & P08 = 0, paid off). Principal is **never** deductible.
- **Idempotency/cleanup:** read-only.

## REC-PROP-PNL — Property P&L (portfolio + per property)  ·  DIV1 + 4th-divergence surface
- **Goal:** the Property-P&L, which counts **all Paid** payment types (incl. `Other`) and does **not**
  deduct interest/depreciation — the report where the $35 NSF income appears.
- **Steps:** open `/reports/property-pnl` (portfolio + per property).
- **Assertions:**
  - **Portfolio** (`portfolio.propertyPnl`): income **265853.72**, expense **57562.33**, net **208291.39**.
  - **Portfolio income 265853.72 = Schedule-E income 265818.72 + $35** (the P03 NSF `Other` fee) — the
    **4th deliberate divergence**.
  - **P03** (`byProperty.P03.propertyPnl`): income **14042** (= 14007 rent + 35 NSF), expense 2380, net
    11662 — vs Schedule-E income 14007. Every other property's propertyPnl income == its Schedule-E income.
  - **DIV1 per property** = `propertyPnl.net − scheduleE.netIncome` = interest + depreciation +
    nonScheduleEIncome; matches `byProperty.<P>.divergenceChecks`… (see `REC-DIVERGENCES`).
- **Idempotency/cleanup:** read-only.

## REC-CASHFLOW-PNL — Cash-Flow P&L (portfolio + monthly)
- **Goal:** the cash-basis P&L (Rent+LateFee Paid/Partial by paid date; all expenses by paid date).
- **Steps:** open `/reports/cash-flow` for CY2025 (portfolio + monthly breakdown).
- **Assertions:**
  - **Portfolio** (`portfolio.cashflowPnl`): income **265818.72**, expense **57562.33**, net **208256.39**.
  - Income == Schedule-E income (no Utility payments). Monthly view: the L14 delinquency shows as **lower
    collected** in Apr/May/Jun (partials/misses) and a **catch-up spike** in **Sep** (the 2025-09-05 lump,
    `events.csv` E01157 etc.); P04 shows **zero rent in June** (vacant).
- **Idempotency/cleanup:** read-only.

## REC-NOI — True Cash Flow / NOI (per property)  ·  DIV2 surface
- **Goal:** the operating view — income − opex(**excl. escrowed Taxes/Insurance**) − debt service — where
  escrowed taxes/insurance are deliberately excluded from opex (DIV2).
- **Steps:** open `/accounting/cash-flow` (True Cash Flow / NOI) per property + portfolio.
- **Assertions:**
  - **Portfolio** (`portfolio.trueCashFlow`): income **265818.72**, operatingExpenses **18462.33**, NOI
    **247356.39**, debtService **159416.52**, cashFlow **87939.87**.
  - **DIV2:** portfolio opex 18462.33 = total deductible 57562.33 − **escrowed Taxes+Insurance 39100**
    (escrowed taxes 27360 + escrowed insurance 11740). Escrowed T+I are excluded here but counted by
    Schedule E / Cash-Flow-P&L / Property-P&L.
  - **Per property:** escrow props show operatingExpenses = repairs/cleaning only (P01 opex **0** — its only
    2025 expenses were escrowed Taxes+Insurance; DIV2 delta 2780). Paid-off **P03 opex 2380** and **P08
    opex 2220** (their Taxes+Insurance are direct → **counted**, DIV2 = 0). P11 opex **2540** (HOA 2220 +
    direct HO-6 insurance 320; escrowed tax excluded).
  - **debtService per property** = `ledger/loans.csv → debt_service_2025` (P01 9387.12, P09 27172.68, P12
    17848.56; P03/P08 = 0).
- **Idempotency/cleanup:** read-only.

## REC-OWNER-STMT — Owner statements (4 entities)  ·  mgmt-fee + late-fee delta
- **Goal:** per-owner statements with the 8% management fee on OE-BELL/OE-TRUST and the Rent-only income
  basis (which differs from Schedule E by late fees).
- **Steps:** open `/accounting/owner-statement` for each of the 4 owner entities (Marcus can view OE-BELL).
- **Assertions** (`cy2025-portfolio.json → ownerStatements`):
  | Owner | income (Rent only) | expenses | mgmtFee | netToOwner |
  |---|---|---|---|---|
  | OE-LLC (0%) | 182505.22 | 42782.33 | 0 | 139722.89 |
  | OE-BELL (8%) | 56456.50 | 10180 | **4516.52** | 41759.98 |
  | OE-TRUST (8%) | 14007 | 2380 | **1120.56** | 10506.44 |
  | OE-PERS (0%) | 12600 | 2220 | 0 | 10380 |
  - mgmtFee = round(income × fee%/100, 2): OE-BELL 56456.50 × 8% = 4516.52; OE-TRUST 14007 × 8% = 1120.56.
    P05 fee 2326.28, P07 fee 2190.24 (`ownerStatements.OE-BELL.properties`).
  - **Late-fee delta (design divergence c):** owner-statement income is **Rent only**, so OE-LLC's income
    (182505.22) is **$250 less** than the sum of its properties' Schedule-E income (182755.22) — the L14 5×
    $50 late fees (P09 owner-stmt income 37123.67 vs Schedule-E 37373.67).
  - **`GAP2` note:** cash actually distributed to Marcus is memo-only in the spine (`events.csv` E01146/
    E01295/E01446/E01573); with `GAP2-OWNERDIST` landed, an Owner-Distributions report should show
    cash-distributed = these nets (OE-BELL 41759.98) at 0 variance.
- **Idempotency/cleanup:** read-only.

## REC-DELINQ — Delinquency aging (year-end)
- **Goal:** the only outstanding balance at year-end is L14's fresh December miss.
- **Steps:** open `/reports/delinquency` as of 2025-12-31.
- **Assertions** (`cy2025-portfolio.json → delinquencyAtYearEnd`):
  - **Total delinquency = $899.75**, entirely **L14** (P09·2): Dec rent 849.75 + Dec late fee 50, all in the
    **current (0–30)** bucket (`events.csv` E01570 late fee unpaid). All other buckets 0.
  - The Apr–Aug L14 arrears are **NOT** here — they cured 2025-09-05 (`OPS-SEP-L14-CURE`).
  - **`GAP7` note:** if `GAP7-EVICTION` replaces the cure, this figure is far larger (Apr–Dec unpaid) — a
    corpus-revision, not a bug. The spine value is $899.75.
- **Idempotency/cleanup:** read-only.

## REC-DEPOSITS — Security-deposit register (year-end)
- **Goal:** the deposit register nets held/deductions/returned to the year-end balance.
- **Steps:** open `/reports/security-deposits`.
- **Assertions** (`cy2025-portfolio.json → deposits.totals`):
  - held **23905**, deductions **450**, returned **1000**, balance **22455**.
  - Only **L05** has activity (`byLease.L05`: held 1450, deductions **450** carpet, returned **1000**,
    balance 0 — `events.csv` E01248/E01249). All other 21 leases: held = deposit, balance = held.
  - Held total 23905 = the 19 T0 deposits (20735) + L08 850 + L16 825 + L20 1495 (added live in Act 2).
- **Idempotency/cleanup:** read-only.

## REC-RENT-ROLL — Rent roll + occupancy (year-end snapshot)
- **Goal:** the year-end rent roll reflects the 21 active leases at their **renewed** rents and 100%
  occupancy.
- **Steps:** open `/reports/rent-roll` and `/reports/occupancy` as of 2025-12-31.
- **Assertions:**
  - **Occupancy 21/21 = 100%** (P04 re-let via L20; P05·3 & P09·4 leased up; no vacancies at year-end).
  - **Rent roll Σ monthly rent ≈ $22,925.55/mo** — the 21 active leases at current rents per the §1b renewal
    ladder (e.g. L01 1287.50, L09 1436.85, L17 1570.75, L03 1004.25, L02 950 M2M). *(Derived from the
    ladder — not a pre-stored `expected/*.json` key; cross-check against Σ current lease rents.)*
  - Leases with EndDate = 2025-12-31 (L12, L19) are **included** (EndDate ≥ today).
- **Idempotency/cleanup:** read-only. (This figure is derived, not in `expected/*.json`; flag any variance
  for a ladder re-check rather than as a hard spine bug.)

## REC-LOAN-SCHED — Loan schedules / amortization (11 loans)
- **Goal:** each loan's amortization chain and CY2025 interest/principal/escrow totals + closing balances.
- **Steps:** open `/loans/{id}/payments` for each of the 11 loans.
- **Assertions** (`ledger/loans.csv`):
  - **Portfolio 2025 totals:** interest **72830.15**, principal **31326.37**, escrow **55260.00**, debt
    service **159416.52** (= sum of the 3).
  - **Per loan** (spot): P01 interest 3762.90 / principal 1904.22 / escrow 3720 / DS 9387.12, close
    **87500.86**; P09 interest 14232.56 / principal 4300.12 / escrow 8640 / DS 27172.68, close
    **256425.15**; P11 (condo, escrow covers **tax only**) interest 2703.47 / escrow 2160, close 66686.78.
  - Amortization: interest = round(balance × rate/12, 2); the `balance_after` chain ties period→period and
    the final row = `balance_2025_12_close`. Reports sum by DueDate and **ignore** `LoanPayment.Status`.
- **Idempotency/cleanup:** read-only.

## REC-VENDOR-1099 — Vendor 1099 report
- **Goal:** per-vendor annual paid totals and the 1099 threshold flag.
- **Steps:** open `/reports/vendor-1099` for CY2025.
- **Assertions** (`cy2025-portfolio.json → vendor1099`, all `needs1099=true`):
  Clark County Treasurer **30360**, Buckeye Mutual Insurance **13520**, Lawn care **3360**, Trash
  collection **2700**, Condo HOA dues **2220**, Pest control **1800**, Rooter Brothers Plumbing **1167.33**
  (182.33 + 985), Freshcoat Painters **680**, Summit Roofing **640**. (Valley Carpet 520, CoolFlow HVAC
  245, ApplianceCare 210, Handy Hank 140 are under some 1099 thresholds — verify the app's flag logic; the
  paid totals must still reconcile.)
- **Idempotency/cleanup:** read-only.

## REC-GL — General ledger (running net)
- **Goal:** the chronological GL of Paid payments (+) and expenses (−) reconciles to a closing net.
- **Steps:** open `/reports/general-ledger` for CY2025.
- **Assertions:** entries appear by date with a running net; the year's Paid income and paid expenses tie to
  the Cash-Flow-P&L (income **265818.72**, expense **57562.33**). Spot-check that the 2025-09-05 L14 lump
  and the 2025-06 P04 make-ready expenses appear on their dates.
- **Idempotency/cleanup:** read-only.

## REC-ACCOUNTING-SUMMARY — Accounting summary / snapshot (all-time)  ·  divergence (e)
- **Goal:** confirm the accounting **summary** KPIs are **all-time** (2023–2025), not year-scoped — an
  expected divergence from the CY2025 reports.
- **Steps:** open `/accounting/summary` (and `/accounting/snapshot`).
- **Assertions:** the collected/outstanding/overdue totals are **all-time** (include CY2023–24 history), so
  they are **larger** than the CY2025 figures — **expected**, not a bug (design divergence (e)). Overdue ≈
  the year-end delinquency (899.75) if the summary's overdue is point-in-time; confirm the summary's scope
  in-app and assert accordingly.
- **Idempotency/cleanup:** read-only.

## REC-DASHBOARD — Portfolio dashboard KPIs (year-end)
- **Goal:** the dashboard's headline KPIs are internally consistent at year-end.
- **Steps:** open `/portfolios/{id}/dashboard` and `/analytics/overview`.
- **Assertions:** 13 properties / 21 units / **21 occupied** / 11 loans; December collected vs scheduled
  reflects the L14 Dec miss (899.75 outstanding); overdue = **899.75**. Occupancy 100%. (Cross-checks
  REC-RENT-ROLL + REC-DELINQ.)
- **Idempotency/cleanup:** read-only.

## REC-DIVERGENCES — the 4 deliberate divergences (pre-asserted, cross-report)
- **Goal:** a single consolidated check that the 4 known divergences hold **as expected** — so a tester
  never files them as bugs.
- **Steps:** compute each delta from the reports pulled above (or read `cy2025-by-property.json →
  divergenceChecks`).
- **Assertions — portfolio:**
  - **DIV1** propertyPnl.net 208291.39 − scheduleE.net 65717.15 = **142574.24** = interest 72830.15 +
    depreciation 69709.09 + Other-income 35. ✓
  - **DIV2** cashflowPnl.expense 57562.33 − trueCashFlow.opex 18462.33 = **39100** (escrowed Taxes 27360 +
    Insurance 11740). ✓
  - **DIV3** trueCashFlow.cashFlow 87939.87 − scheduleE.net 65717.15 = **22222.72** (cash vs taxable net). ✓
  - **4th (Other-income)** propertyPnl.income 265853.72 − scheduleE.income 265818.72 = **35** (P03 NSF). ✓
- **Assertions — per property:** each property's `divergenceChecks` entry has `ok:true` with
  `delta==expect` for DIV1/DIV2/DIV3 (e.g. P01 DIV1 7617.45, DIV2 2780, DIV3 1010.33; P03 DIV1 3307.73 with
  `nonScheduleEIncome:35`, DIV2 0, DIV3 3272.73). All 13 tie to the cent.
- **Also assert (design divergence c):** owner-statement income (Rent-only) < Schedule-E income by the
  **$250** L14 late fees (OE-LLC 182505.22 vs 182755.22). And (a): Schedule-E income == Cash-Flow-P&L income
  (no Utility payments → delta 0).
- **Idempotency/cleanup:** read-only.

## REC-YEAR-END-PACKET — Year-end packet (composite)
- **Goal:** the composite year-end PDF/packet aggregates the above without contradiction.
- **Steps:** generate `/accounting/year-end-packet` for CY2025.
- **Assertions:** the packet's Schedule E (net **65717.15**), owner statements (4 entities as above),
  depreciation schedule (**69709.09** total), interest (**72830.15**), deposits (**22455** balance), and
  vendor-1099s all match their standalone reports. The packet's **capital-improvement caveat** ("large
  repairs may be capital improvements") is present — the hook for `GAP4-CAPIMPROVE`.
- **Idempotency/cleanup:** read-only.

## REC-HISTORY-SPOTCHECK — CY2023 & CY2024 (spot-check, not cent-exact)
- **Goal:** the imported history years reconcile at the summary level (held-flat rent — spot-check only).
- **Steps:** pull Schedule E for CY2023 and CY2024.
- **Assertions** (`expected/spotcheck-2023-2024.json`):
  - **2023:** income **221995.00**, totalExpenses **171276.20**, net **50718.80**.
  - **2024:** income **248820.00**, totalExpenses **197646.47**, net **51173.53**.
  - These are **spot-check** targets (history is held-flat, not cent-exact); small formula differences are
    acceptable and noted, not failed. `GAP1` (bulk import) may regenerate these on the corpus revision.
- **Idempotency/cleanup:** read-only.

---

## Gap-pass reconciliation — STAGED DELTAS (fold into `expected/*.json` at the corpus revision)
The reconciliation above is the **spine pass** — cent-exact against **today's** `expected/*.json` (the
app-as-built, no gap features). The table below is a **staged appendix**: each gap's numeric impact on the
year-end figures, held here **explicitly and NOT asserted in the spine pass**. When the features land, the
corpus revision (`scenario.json` + re-run the 4 builders → new `expected/*.json`) **applies** these deltas;
only then does the **gap pass** re-run Act 3 asserting the revised figures. Do **not** assert any row below
against today's data — it will fail because the app hasn't built the feature yet (that is by design).

| Gap | Impact class | Staged delta vs **today's** spine figures | Fold-in action at the revision |
|---|---|---|---|
| **GAP1** bulk import | spine (mechanism only) | **none** — materializes the same CY2023–24 history | swap the per-payment mark-paid pass for the bulk-import path; `spotcheck-2023-2024.json` unchanged |
| **GAP2** owner distribution | additive | **+4 `OwnerDistribution` records** (OE-BELL Σ = **41759.98**); P&L / Schedule E / owner-statement net **unchanged** | add the distribution records + an Owner-Distributions report reconciling cash-distributed = computed net (0 variance) |
| **GAP3** proration | value-preserving | L16 first month = **$452.42** (same value, now app-computed) | replace hand-entered `events.csv` E01118 with the app's prorated payment; figure stays 452.42 |
| **GAP4** capital improvement | net-new (off-spine) | portfolio depreciation **69709.09 → +~135** (P09 roof $9,900, Aug in-service); the item is a new asset, **not** in today's ledger | add a depreciating-asset line to `depreciation.csv` + events; Schedule-E depreciation rises by the partial-year amount |
| **GAP5** app-fee income | additive | income **+~$180** (4 applicants × ~$45, incl. the declined one), **no lease** | add application-fee income events; the income bucket rises by the fees |
| **GAP6** sale / disposition | net-new (spine-disrupting) | **P08:** −Nov/Dec rent (~−$2,100), partial-year depreciation, +gain/loss, +§1250 recapture; occupancy/rent-roll drop P08 | re-author P08 for a 2025-10-31 disposition; regenerate P08 + portfolio rollups |
| **GAP7** eviction | reworks-spine | L14 year-end delinquency **≫ $899.75** (Apr–Dec unpaid, no Sep cure) → P09 / OE-LLC income **down** | re-author the L14 arc to eviction (**supersedes** `OPS-SEP-L14-CURE`/`-RENEW`); regenerate delinquency + income |
| **STRIPE** autopay | off-ledger | **none** — L09/L17 stay marked Paid in the spine | **not** folded into `expected/*.json`; path coverage only, reconciled against the Stripe test dashboard |

Spine pass asserts today's numbers; the gap pass asserts the revised numbers. Nothing in this table is a
regression — each is a pre-declared, tracked consequence of a feature that has not shipped yet.
