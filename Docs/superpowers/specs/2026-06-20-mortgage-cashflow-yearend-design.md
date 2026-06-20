# Mortgages, True Cash Flow & Year-End Picture — Design Spec

- **Date:** 2026-06-20
- **Tasks:** TSK-389 (model mortgages / debt service) + TSK-390 (true cash-flow view / owner ledger). Depreciation folded in (year-end tax picture).
- **Branch:** `tsk-389-390-mortgage-cashflow`
- **Status:** Approved for implementation; financial formulas below are the contract. A financial-correctness review of this spec runs before/with the build.

> **Correctness rule for this feature:** numbers touch the owner's taxes and his "am I making money" decision. Where this spec gives a formula, implement it exactly. Where a value is non-cash (depreciation) or not-deductible (mortgage principal), keep it on the correct side of the cash-flow / tax divide. When unsure, prefer the conservative, clearly-labeled number over a clever one.

---

## 1. Context & goal

The owner (a 15–40 unit landlord, the first real user) cares most about the **year-end picture** — last year's tool never showed him what he needed at tax time, so "enter everything" felt worthless. Today Rental Command has Schedule-E, owner-statement, and year-end-packet reports, **but they are incomplete and partly wrong**:
- **No mortgage / debt service modeled at all** (only a `MortgageInterest` expense *label*). Debt service is his single biggest outflow → every "cash flow" / "net" figure is overstated.
- The cash-flow query **counts security deposits as income** (no `PaymentType` filter) → income inflated.
- **Depreciation** (his biggest paper deduction) is only a manual expense category, not computed from basis.

**Goal:** complete and correct the money picture so he sees, per property and portfolio:
1. **True cash flow** — rent in − operating expenses − debt service (what actually hits his pocket).
2. **Tax / Schedule-E** — income − deductible expenses − mortgage *interest* − **depreciation** (taxable rental income).
3. **Per-property P&L** and **rent roll**.

Mortgages are **one loan per property** (several separate loans), possibly some paid off.

## 2. Scope

**In:**
- `Loan` entity (per-property) + monthly **debt-service generation** with principal/interest/escrow split (amortization).
- **Property basis** fields + computed **straight-line depreciation** (27.5-yr residential) with manual override.
- **Recurring-expense** concept (insurance/tax/HOA entered once).
- **Deposit-as-income fix.**
- **True cash-flow** computation + view (per property + portfolio; monthly + annual), DB-side.
- **Schedule-E / year-end** completion: mortgage interest + depreciation in; principal out; deposits out.
- Web: inline forms (loan, property basis, recurring costs) + the year-end / cash-flow view, reusing the app's existing form components (same inline pattern as the rest of the app).

**Out / deferred:** cost-segregation & bonus depreciation; commercial (39-yr) — residential 27.5 only for v1 (override covers the rest); blanket-loan allocation (we do per-property; a blanket loan can be entered as one loan on a primary property for now); bank-feed auto-import; multi-entity tax returns.

## 3. Locked design decisions (the financial choices)

1. **Debt service is its own thing, not an `Expense`.** A `LoanPayment` carries the principal/interest/escrow split. This keeps principal out of deductions and lets cash flow use the full payment.
2. **Cash flow ≠ taxable income.** Two separate computations off the same data:
   - **Cash flow** subtracts the **full** mortgage payment (P+I+escrow) and **excludes** non-cash depreciation.
   - **Schedule-E** subtracts mortgage **interest** + **depreciation**, and **excludes principal**.
3. **Escrow, no double-count.** If a loan escrows taxes+insurance, the escrow portion is the *cash* paid for them — those expenses must NOT also be counted as separate cash outflows. See §9.
4. **Depreciation is computed, overridable.** Straight-line (building basis ÷ 27.5), prorated for partial years; a manual annual override wins when set.
5. **Security deposits are a liability, never income.** Excluded from every income/cash-flow figure (the deposit register already exists).
6. **DB-side only** (the owner's explicit ask): every sum/group is EF-translated SQL; never pull whole tables and compute in memory.

## 4. Data model changes

All PostgreSQL/Npgsql; migrations via `dotnet ef migrations add <Name> --project RentalCommand.Data --startup-project RentalCommand.Api`.

**New `Loan` entity** (`RentalCommand.Core/Entities/Loan.cs`), per-property:
```
Id, PortfolioId, PropertyId (FK),
Lender (string), OriginalAmount (decimal), CurrentBalance (decimal),
AnnualInterestRatePct (decimal),       // e.g. 6.5
TermMonths (int), StartDate (date), DayOfMonthDue (int),
MonthlyPrincipalInterest (decimal),    // the P&I payment
MonthlyEscrow (decimal, default 0),    // taxes+insurance escrowed, 0 if none
EscrowCoversTaxes (bool), EscrowCoversInsurance (bool),
Status (enum LoanStatus { Active, PaidOff, Closed }),
CreatedAt, UpdatedAt, DeletedAt?
```

**New `LoanPayment` entity** (`RentalCommand.Core/Entities/LoanPayment.cs`):
```
Id, PortfolioId, LoanId (FK), PeriodKey (string "YYYY-MM", idempotency),
DueDate (date), PaidDate (date?),
InterestAmount, PrincipalAmount, EscrowAmount, TotalAmount (decimals),
BalanceAfter (decimal), Status (Scheduled | Paid),
CreatedAt
```

**Property basis fields** (extend `RentalCommand.Core/Entities/Property.cs`) — for depreciation:
```
PurchasePrice (decimal?), LandValue (decimal?),        // land is NOT depreciable
InServiceDate (date?),
ManualAnnualDepreciation (decimal?)                    // override; null => compute
```
(Annual property-tax / insurance amounts for Schedule-E may be entered as recurring expenses — §8 — or, if escrowed, captured on the loan; see §9.)

**New `RecurringExpense` entity** (`RentalCommand.Core/Entities/RecurringExpense.cs`):
```
Id, PortfolioId, PropertyId (FK?), UnitId (FK?), Category (ScheduleECategory),
Amount (decimal), Frequency (enum { Monthly, Quarterly, Annual }),
StartDate (date), NextRunDate (date), Active (bool), Notes
```
A worker materializes these into `Expense` rows (idempotent by period) — §8.

## 5. Amortization & payment split (formula)

For each generated monthly `LoanPayment` on an Active loan:
```
monthlyRate      = AnnualInterestRatePct / 100 / 12
interestAmount   = round(CurrentBalance * monthlyRate, 2)
principalAmount  = round(MonthlyPrincipalInterest - interestAmount, 2)
escrowAmount     = MonthlyEscrow
totalAmount      = MonthlyPrincipalInterest + MonthlyEscrow
balanceAfter     = CurrentBalance - principalAmount
```
Then set `Loan.CurrentBalance = balanceAfter`. Guard: if `principalAmount >= CurrentBalance` (final payment), set `principalAmount = CurrentBalance`, `balanceAfter = 0`, `Status = PaidOff`. Never let balance go negative. All money is `decimal`, rounded to cents.

## 6. Depreciation (formula)

Per property, for a given tax year `Y`:
```
if ManualAnnualDepreciation set -> annualDepreciation = ManualAnnualDepreciation
else if PurchasePrice and InServiceDate set:
    buildingBasis      = PurchasePrice - (LandValue ?? 0)     // land not depreciable
    fullYearDepr       = buildingBasis / 27.5                  // residential straight-line
    monthsInService    = months of year Y on/after InServiceDate (cap 12; 0 before in-service;
                         stop after 27.5 yrs fully depreciated)
    annualDepreciation = round(fullYearDepr * monthsInService / 12, 2)
else annualDepreciation = 0
```
Depreciation is **non-cash**: it appears in Schedule-E / taxable income ONLY, never in cash flow. Cumulative depreciation must not exceed `buildingBasis`.

## 7. Deposit-as-income fix

Wherever income is summed (the cash-flow query `ReportsService.GetCashFlowAsync` ~L532–539, the money snapshot, owner-statement, year-end), **filter `Payment.PaymentType`**: income = `Rent` (+ `LateFee`; `Utility` only if it's reimbursement income per existing behavior). **Exclude `SecurityDeposit`.** Keep it DB-side (a `WHERE PaymentType IN (...)`, not a post-filter). Security deposits remain tracked in the existing deposit register (liability), unchanged.

## 8. Recurring costs

A `RecurringExpenseWorker` (RentalCommand.Engine, mirror `RentChargeWorker`'s idempotent monthly pattern with `PeriodKey`) materializes due `RecurringExpense` templates into `Expense` rows (correct `Category`, `PropertyId`/`UnitId`, `IncurredAt`), advancing `NextRunDate`. This lets insurance / property tax / HOA / management fees be entered once and flow into every report. Idempotent: never double-create for the same template+period.

## 9. True cash-flow computation (DB-side)

Per property and portfolio, for a period (month or year). All set-based SQL — extend the existing `ReportsService` cash-flow query; do **not** materialize tables and compute in C#.

```
income            = Σ Payment.AmountPaid  WHERE type IN (Rent, LateFee) AND paid/partial   (NO deposits)
operatingExpenses = Σ Expense.Amount      WHERE NOT escrow-funded (see below)
NOI               = income - operatingExpenses
debtService       = Σ LoanPayment.TotalAmount  (P + I + escrow) for the period
cashFlow          = NOI - debtService
```
- **Escrow no-double-count:** if a property's loan has `MonthlyEscrow > 0` and `EscrowCoversTaxes/Insurance`, then `Taxes`/`Insurance` expenses for that property are **escrow-funded** and are EXCLUDED from `operatingExpenses` in the **cash-flow** calc (the escrow portion inside `debtService` already represents that cash). They are STILL counted in the **tax** calc (§10), because they remain deductible. Implement an `escrow-funded` determination in SQL (property has an escrowing active loan + expense category in {Taxes, Insurance}).
- Show **NOI** and **cash flow** as distinct lines so the owner sees operating performance vs. after-debt cash.
- Capex (if later tracked) subtracts after cash flow; out of scope v1.

## 10. Schedule-E / tax integration

Extend the existing `/accounting/schedule-e` + `/year-end-packet`. Taxable rental income per property, year:
```
rentalIncome     = Σ income (Rent + LateFee; NO deposits)
deductible       = Σ operatingExpenses by ScheduleECategory   (INCLUDING Taxes/Insurance, even if escrowed)
mortgageInterest = Σ LoanPayment.InterestAmount for the year     (from §5 split; principal NOT included)
depreciation     = Σ §6 annualDepreciation across the owner's properties
taxableIncome    = rentalIncome - deductible - mortgageInterest - depreciation
```
- **Principal is never deductible** — only `InterestAmount` flows here.
- The legacy manual `MortgageInterest` expense category stays usable, but when a `Loan` exists, interest comes from the **loan split** (don't double-count: if a loan exists for the property, ignore manual `MortgageInterest` expenses for that property, or warn).
- Depreciation populates the Schedule-E depreciation line from §6 (replacing/када augmenting the manual `Depreciation` category; manual override respected).

## 11. Year-end view

One screen (build on the existing year-end-packet/owner-statement) that, per property + portfolio total, shows three clearly-separated blocks:
1. **Cash flow** (§9): income, operating expenses, NOI, debt service, **cash flow**.
2. **Tax / Schedule-E** (§10): rental income, deductible expenses by category, mortgage interest, **depreciation**, **taxable income**.
3. **Rent roll**: units, tenant, rent, paid/behind, occupancy.
Exportable (reuse existing packet export). This is the "what last year's tool never showed him" deliverable — the two numbers (cash flow vs taxable income) side by side, with depreciation and debt service finally present.

## 12. Debt-service worker

`DebtServiceWorker` in `RentalCommand.Engine` (mirror `RentChargeWorker.cs`): monthly, idempotent by `(LoanId, PeriodKey)`, generates the `LoanPayment` (§5) for each Active loan and updates `CurrentBalance`. Runs under the same scheduling/locking pattern as the rent worker. Never double-generate a period.

## 13. API surface (all `/api/v1`, portfolio-scoped, IDOR-guarded)

- `Loan` CRUD: `GET/POST/PATCH/DELETE /loans` (+ `?propertyId=`); validate `PropertyId` in portfolio.
- `LoanPayment` read: `GET /loans/{id}/payments` (the amortization schedule / history).
- `RecurringExpense` CRUD: `GET/POST/PATCH/DELETE /recurring-expenses`.
- Property: extend Property DTOs with the basis fields (§4).
- Reports: extend `GET /reports/cash-flow` (deposit fix + debt service + escrow handling), `GET /accounting/schedule-e` (interest + depreciation), `GET /accounting/year-end-packet` (the §11 three-block view). Add `GET /accounting/cash-flow?propertyId&from&to` if a dedicated per-property cash-flow endpoint is cleaner.
- Enums serialize as **strings**.

## 14. Web UI

Inline forms, reusing the app's **existing form components/conventions** (same inline pattern used across the app — see how lease/work-order/expense forms are built). No new modal/drawer framework.
- **Mortgages:** on the Property detail page, a "Mortgage / Loan" section — inline add/edit loan + a read-only amortization schedule (LoanPayment history). 
- **Property basis (depreciation):** inline fields on the Property detail (purchase price, land value, in-service date, optional manual depreciation).
- **Recurring costs:** a "Recurring expenses" section (Property or Money area) — inline add/edit.
- **Year-end / cash-flow view:** in the Money/Reports area — the §11 three-block view with a property selector + portfolio total + period (year / month) + export.

## 15. Data-access HARD RULE

Every aggregation/grouping/filter/join/sort/page is **DB-side**, one EF-translated SQL statement or a view. NO in-memory grouping, NO load-then-loop / N+1, NO lazy loading. The cash-flow, Schedule-E, and year-end computations especially: keep them `IQueryable` projections / `GroupBy` / `Sum` in the database. **Inspect the generated SQL** for each report query to confirm no whole-table materialization.

## 16. Acceptance criteria

1. A per-property `Loan` can be created; the `DebtServiceWorker` generates a monthly `LoanPayment` with a correct principal/interest/escrow split (§5), and `CurrentBalance` decreases by principal each month; final payment zeroes the balance and marks `PaidOff`.
2. Cash flow (§9) = income − operating expenses − debt service, **excludes deposits**, **excludes depreciation**, and does **not double-count** escrowed taxes/insurance — verified on a worked example.
3. Schedule-E (§10) includes mortgage **interest** and **depreciation**, **excludes principal**, **excludes deposits** — verified on a worked example.
4. Depreciation (§6) computes straight-line from basis, prorates partial years, respects the manual override, and never exceeds building basis.
5. The deposit-as-income fix is DB-side (a `PaymentType` filter in SQL), and the security-deposit register is unaffected.
6. Recurring expenses materialize once per period (idempotent) into `Expense` rows.
7. The year-end view shows, per property + portfolio, the cash-flow block, the tax/Schedule-E block (with depreciation), and the rent roll — **cash flow and taxable income are distinct numbers**.
8. Every report query is DB-side — **confirmed by reading the generated SQL** (no N+1, no in-memory grouping).
9. Portfolio scoping / IDOR enforced on loans, payments, recurring expenses, and all FK refs.

## 17. Non-goals / future

- Cost-seg / bonus depreciation, commercial 39-yr (the manual override covers these for v1).
- Blanket-loan cross-property allocation (enter as one loan for now).
- Bank-feed import / auto-reconciliation of debt-service payments to bank transactions.
- The legacy manual `MortgageInterest` / `Depreciation` expense categories remain but defer to the modeled loan/basis when present (avoid double-count — see §18).

## 18. Financial-review corrections (AUTHORITATIVE — override §§4–16 where they conflict)

An independent financial-correctness review confirmed the **architecture is sound** (the cash-flow vs. taxable-income divide, escrow no-double-count, deposit fix, and the amortization split when the payment is the true P&I are all correct). It caught defects in the cents-level mechanics that would otherwise put a wrong number on his tax return. **Apply ALL of these.**

**Data-model additions (§4):**
- **Actual cash received (partial payments).** Income must sum **cash received, not amount due.** `Payment` has only `Amount` (= amount *due*); a `Partial` row's `Amount` overstates cash, and the existing reports drop `Partial` rows entirely (understating). Add/confirm a real **`AmountPaid` (decimal)** on `Payment` (= `Amount` when `Paid`; = the partial cash when `Partial`), populate it, and **sum `AmountPaid` WHERE `Status IN (Paid, Partial)`** everywhere income is computed (§7/§9/§10). (A partial amount-paid field may already exist on `feat/scan-front-door` — reconcile, don't duplicate.) Never sum `Amount` for income.
- **`Property.AccumulatedDepreciation` (decimal, default 0)** — persist it; the cumulative-≤-basis cap (§6) and future sale-year recapture both need it.

**§5 amortization (3 fixes):**
- **Maturity stop.** `DebtServiceWorker` must STOP once `periodIndex >= Loan.TermMonths` (counted from `StartDate`). Final scheduled month: force-close (principal = remaining balance, balanceAfter = 0, `PaidOff`); if a balance remains at term, stop + flag "balance remaining at maturity." Never generate month `TermMonths+1` (a payment a few cents low otherwise amortizes forever and over-counts interest).
- **Negative-amortization guard.** If `interestAmount >= MonthlyPrincipalInterest`: set `principalAmount = 0` (interest-only that period, balance unchanged), flag "payment doesn't cover interest." `principalAmount` may never be < 0; `CurrentBalance` may never grow. Reject/flag at loan entry if `MonthlyPrincipalInterest < firstMonthInterest`.
- **Immutable schedule.** Each period's opening balance comes from the **prior `LoanPayment.BalanceAfter`** (or is derived from OriginalAmount/rate/term), NOT from the live, user-editable `Loan.CurrentBalance`. Keep `CurrentBalance` a derived cache so edits/re-runs can't silently change a filed interest figure.

**§6 depreciation (2 fixes):**
- **Per-property,** not portfolio-summed: compute from each property's own basis/in-service, place on that property's Schedule-E block, sum for the portfolio total (must foot with per-property income/expense).
- **In-service-year proration = IRS mid-month approximation:** `monthsInService (in-service year) = (whole months strictly AFTER the in-service month) + 0.5`. Label that first-year figure "estimate (IRS mid-month) — confirm with accountant." Enforce cumulative ≤ building basis via `AccumulatedDepreciation`.

**§7 income:** sum `AmountPaid`, `Status IN (Paid, Partial)`, types `Rent + LateFee`; **include tenant utility reimbursements as taxable income** (with their offsetting deductible cost). Exclude `SecurityDeposit`.

**§9 cash flow (critical — the owner's DB-side rule):** `ReportsService.GetCashFlowAsync` (L517–542) currently `.ToListAsync()`s then groups/sums **in memory** — that violates the hard rule and must be **REWRITTEN (not "extended")** to push the range filter + `GroupBy(month)` + `Sum` into SQL. Copy the already-correct DB-side pattern from `ScheduleEService`. Escrow exclusion must be **category-specific:** drop `Taxes` from cash-flow opex only if the property's loan `EscrowCoversTaxes`, and `Insurance` only if `EscrowCoversInsurance`.

**§10 Schedule-E:** deterministic legacy double-count exclusion (not "ignore or warn"): when a `Loan` exists for a property, EXCLUDE category `MortgageInterest` from that property's deductible; when computed depreciation applies, EXCLUDE category `Depreciation`. Depreciation per-property (above).

**New labeled non-goals — surface these in the UI so he never trusts a wrong number:**
- **Mid-year purchase/sale (disposition):** sale-year half-month depreciation, gain/loss, and §1250 recapture are NOT computed — label any sale-year figure "property sold — recapture not computed."
- **Capex vs. repairs:** a large improvement entered under `Repairs` over-deducts. Add a `CapitalImprovement` flag or a guidance note (IRS $2,500 de-minimis); don't silently expense improvements.
- **Passive-loss limitation (Form 8582 / $25k allowance):** a negative `taxableIncome` may not be a usable loss — label it "before passive-loss limitation; deductible loss may be limited — see accountant."
- **Owner-occupied / mixed-use:** no expense/depreciation allocation — note it where a property is partly owner-occupied.

**Acceptance additions (§16):** worker stops at maturity and never neg-amortizes; income uses `AmountPaid` incl. partials; `GetCashFlowAsync` is verified DB-side by generated SQL (the in-memory version is gone); depreciation is per-property with mid-month first year and the accumulated cap; legacy `MortgageInterest`/`Depreciation` categories are excluded when modeled equivalents exist.
