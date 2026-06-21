# Mortgages, True Cash Flow & Year-End — Implementation Plan

> **For agentic workers:** Implement task-by-task, in order. Steps use checkbox (`- [ ]`). After each task: build, verify, commit. **Testing posture:** the project defers broad suites, BUT this feature is financial — so the **calculation cores get real unit tests** (amortization split, depreciation, cash-flow aggregation, Schedule-E). UI/CRUD is verified by build + the stated checks. Read the spec FIRST: `Docs/superpowers/specs/2026-06-20-mortgage-cashflow-yearend-design.md` — it holds the exact formulas this plan references by section.

**Goal:** Model per-property mortgages + monthly debt service, fix the deposit-as-income bug, compute true cash flow (rent − opex − debt service) and a correct Schedule-E (interest + depreciation, principal excluded), and surface a year-end view showing cash flow vs. taxable income per property + portfolio.

**Architecture:** New `Loan`/`LoanPayment`/`RecurringExpense` entities + Engine workers mirroring `RentChargeWorker`. Reports extend the existing `ReportsService`/Accounting endpoints — all computations DB-side. Web uses the app's existing inline-form components.

**Tech Stack:** .NET 10 / EF Core + Npgsql (PostgreSQL only); ASP.NET `/api/v1` controllers inheriting `AuthenticatedPortfolioControllerBase`; Engine background workers; SvelteKit 5 (runes) + TanStack Query.

## Global Constraints

- **PostgreSQL only.** Migrations: `dotnet ef migrations add <Name> --project RentalCommand.Data --startup-project RentalCommand.Api`.
- **MIGRATION COORDINATION:** a sibling branch (`tsk-387-unit-command-center`) also adds a migration (`Expense.UnitId`). Both branch from `main`. Expect a model-snapshot conflict at merge — whichever merges second **regenerates its migration after rebasing**. Keep this feature's migrations self-contained.
- **Data-access HARD RULE:** every sum/group/filter is DB-side, EF-translated SQL (or a view). NO in-memory grouping, NO load-then-loop / N+1, NO lazy loading. **Inspect generated SQL** for each report query.
- **Money is `decimal`, rounded to cents.** Never `double` for money.
- **Portfolio scoping / IDOR** on every entity + FK ref.
- **Enums serialize as strings.** Commit messages: subject + body, **no AI-attribution trailer.** Build before commit (`dotnet build RentalCommand.sln`, `pnpm -C web build`). One isolated worktree on `tsk-389-390-mortgage-cashflow`; commit per task. Do not start the live dev app or apply migrations against the shared dev DB on :5432.

---

## Phase A — Data model + workers  ✅ COMPLETE (commits d8b8d78 → 33664ed)

### Task A1: `Loan` + `LoanPayment` entities + migration ✅
**Files:** `RentalCommand.Core/Entities/Loan.cs`, `LoanPayment.cs`, `RentalCommand.Core/Enums/LoanStatus.cs` + `LoanPaymentStatus.cs`; `RentalCommandDbContext.cs`; migration `AddLoans`.
- [x] Entities per spec §4 + §18: Loan (per-Property FK, P&I, escrow + `EscrowCoversTaxes/Insurance`, `CurrentBalance` documented as a **derived cache**, status, soft delete); LoanPayment (interest/principal/escrow/total split, `PeriodKey`, `BalanceAfter`, `PaymentDoesNotCoverInterest` neg-am flag). `LoanStatus { Active, PaidOff, Closed }`.
- [x] FKs + unique `(LoanId, PeriodKey)` idempotency index; soft-delete query filter (LoanPayment filters via `Loan.DeletedAt`). Enums stored as int (matches Payment/Expense); JSON serializes as strings.
- [x] `dotnet ef migrations add AddLoans` (reviewed; not applied to shared DB). Build + commit.

### Task A2: Amortization split + `DebtServiceWorker` (UNIT-TESTED) ✅
**Files:** `RentalCommand.Core/Services/AmortizationCalculator.cs` (pure); `RentalCommand.Engine/Services/{IDebtServiceService,DebtServiceService}.cs` + `Workers/DebtServiceWorker.cs`; tests `RentalCommand.Core.Tests/AmortizationCalculatorTests.cs` (13) + `RentalCommand.Engine.Tests/Automation/DebtServiceServiceTests.cs` (5).
**Produces:** `AmortizationCalculator.Split(openingBalance, annualRatePct, monthlyPI, monthlyEscrow)` → `{ OpeningBalance, Interest, Principal, Escrow, Total, BalanceAfter, PaidOff, DoesNotCoverInterest }`; plus `BuildSchedule(...)` for the immutable chain.
- [x] §5 split with the §18 fixes (TDD red→green): final-payment close; **negative-amortization guard** (P&I ≤ interest → principal 0, balance never grows, flagged). All decimal/cents.
- [x] `BuildSchedule` chains each period from the prior `BalanceAfter` (**immutable opening balance**) and stops at the earlier of payoff or term (**maturity stop**) — never a period past `TermMonths`.
- [x] `DebtServiceService` mirrors `RentChargeService`: hourly worker, idempotent by `(LoanId, PeriodKey)` (unique index backstop), catches up missed months chaining from the prior row (not the live `CurrentBalance`), honors the maturity stop, updates the cached balance/status (PaidOff). Build + commit.

### Task A3: Property basis fields (depreciation inputs) + migration ✅
**Files:** `Property.cs` (+ `PurchasePrice`, `LandValue`, `InServiceDate`, `ManualAnnualDepreciation`, **`AccumulatedDepreciation` default 0** per §4/§18); Property DTOs + `PropertyService`; migration `AddPropertyBasis`.
- [x] Fields threaded through Property response/create/update DTOs + service; `AccumulatedDepreciation` is read-only on the wire (system-maintained). Migration backfills 0 accumulated. Build + commit.

### Task A4: Depreciation calculator (UNIT-TESTED) ✅
**Files:** `RentalCommand.Core/Services/DepreciationCalculator.cs` (pure); tests `RentalCommand.Core.Tests/DepreciationCalculatorTests.cs` (13).
**Produces:** `DepreciationCalculator.AnnualForYear(PropertyDepreciationBasis basis, int year)` → `{ Amount, IsFirstYearEstimate }`.
- [x] §6 + §18 (TDD red→green): building basis = purchase − land; ÷ 27.5; **in-service year = IRS mid-month** (`monthsAfterInServiceMonth + 0.5`), flagged as first-year estimate; manual override wins; **cumulative cap at building basis via `AccumulatedDepreciation`**; 0 before in-service / once fully depreciated. Worked examples pinned: full year 8,727.27; **July → 4,000.00** (mid-month 5.5mo, NOT the old §6 6-month 4,363.64); Jan → 8,363.64; Dec → 363.64. Build + commit.

### Task A5: `RecurringExpense` + worker + migration ✅
**Files:** `RecurringExpense.cs` + `Enums/RecurringExpenseFrequency.cs` `{ Monthly, Quarterly, Annual }`; `RentalCommand.Engine/Services/{IRecurringExpenseGenerationService,RecurringExpenseGenerationService}.cs` + `Workers/RecurringExpenseWorker.cs`; migration `AddRecurringExpenses`; tests `RentalCommand.Engine.Tests/Automation/RecurringExpenseGenerationServiceTests.cs` (5).
- [x] Entity per §4; worker mirrors `RecurringMaintenanceService`'s idempotent pattern — idempotency by **schedule advancement** (one expense per template per period; inserts + NextRunDate advance commit in one transaction). Catches up missed periods (one dated `Expense` each, correct Category/Property/Unit/IncurredAt), capped at 36 to avoid flooding. Build + commit.

### Task A6 (added): Payment.AmountPaid (actual cash received) ✅
**Files:** `Payment.cs` (+ `AmountPaid` decimal?); `RentalCommandDbContext.cs`; migration `AddPaymentAmountPaid`.
- [x] §18: income must sum cash received, not amount due. Field + EF config + migration are **byte-identical to `feat/scan-front-door`** (same column/type/nullability + verbatim XML-doc) so the branches reconcile (not duplicate); the population (`PaymentService.NormalizeAmountPaid`) + DTO/aggregation plumbing live on scan-front-door and arrive at merge. Report queries read it via the canonical `Partial ? AmountPaid : Amount` idiom.

### Task A7 (added): Loan + RecurringExpense CRUD ✅
**Files:** `Api/DTOs/{LoanDtos,RecurringExpenseDtos}.cs`; `Api/Services/Domain/{I,}LoanService.cs`, `{I,}RecurringExpenseService.cs`; `Api/Controllers/{Loan,RecurringExpense}Controller.cs`; DI in `ServiceCollectionExtensions.cs`; tests `LoanServiceTests` (6) + `RecurringExpenseServiceTests` (5).
- [x] Full CRUD under `/api/v1/loans` (+ `{id}/payments` amortization read) and `/api/v1/recurring-expenses`, portfolio-scoped, in-portfolio FK IDOR guards, soft-delete. Build + commit.

---

## Phase B — Computations & reports (DB-side)

> **§18 deltas binding on all of Phase B (override §§7–10 where they conflict):**
> - **Income = actual cash received:** sum `AmountPaid` for `Status IN (Paid, Partial)` via the canonical
>   `Partial ? (AmountPaid ?? 0) : Amount` idiom (a `Paid` row's `AmountPaid` is null → its full `Amount`);
>   types `Rent + LateFee`; exclude `SecurityDeposit`. Never sum `Amount` for income.
> - **B2 `GetCashFlowAsync` is REWRITTEN, not extended:** the L503–572 in-memory `.ToListAsync()` + GroupBy/Sum
>   is replaced by SQL `GroupBy(month)` + `Sum` (copy `ScheduleEService`'s DB-side pattern). The old in-memory
>   version must be **gone**. Escrow exclusion is **category-specific**: drop `Taxes` only if `EscrowCoversTaxes`,
>   `Insurance` only if `EscrowCoversInsurance`.
> - **B3 depreciation is per-property** (from each property's own basis/in-service, placed on that property's
>   block, summed for the portfolio total) using the A4 calculator; deterministic legacy double-count exclusion
>   (Loan exists → exclude category `MortgageInterest`; computed depreciation applies → exclude `Depreciation`).
> - **B4** surfaces the §18 "see your accountant" labels (mid-year sale, capex-vs-repairs, passive-loss).

### Task B1: Deposit-as-income fix ✅
**Files:** `RentalCommand.Api/Services/Domain/ReportsService.cs` (the cash-flow query) + tests.
- [x] Done as part of the B2 rewrite: the cash-flow income sum now filters `PaymentType IN (Rent, LateFee)`, `Status IN (Paid, Partial)`, sums the `Partial ? (AmountPaid ?? 0) : Amount` idiom, and excludes `SecurityDeposit` — all SQL-side. Verified by a generated-SQL test (GROUP BY + SUM in the DB) + a worked example (deposit excluded, partial uses AmountPaid, late fee included). **Scope note:** the AccountingService / DashboardService / OwnerStatementService income sums are already AmountPaid-correct on `feat/scan-front-door`; re-editing them here would duplicate that work and conflict at merge, so they were intentionally left to that branch (reconcile, not duplicate). The general ledger legitimately lists all cash movements (deposits included) and is untouched. The security-deposit register is unaffected.

### Task B2: True cash-flow computation + endpoint (UNIT-TESTED) ✅
**Files:** `ReportsService.GetCashFlowAsync` (REWRITTEN) + `GetTrueCashFlowAsync`; `Api/DTOs/CashFlowDtos.cs`; `GET /accounting/cash-flow`; tests in `ReportsServiceTests`.
- [x] `GetCashFlowAsync` REWRITTEN DB-side (range filter + `GroupBy(month)` + `Sum` as one EF query; the old in-memory `.ToListAsync()` + GroupBy is gone — confirmed by a `ToQueryString()` assertion).
- [x] `GetTrueCashFlowAsync` per §9/§18 DB-side: income (AmountPaid idiom, no deposits); opex EXCLUDING escrow-funded categories **category-specifically** (Taxes only if `EscrowCoversTaxes`, Insurance only if `EscrowCoversInsurance`, rolled up from active loans SQL-side); NOI; debtService = Σ LoanPayment.TotalAmount; cashFlow = NOI − debtService. Depreciation excluded. Shared `IncomeByPropertyAsync` / `DebtServiceByPropertyAsync` helpers.
- [x] Tests: worked examples (escrow taxes excluded vs. kept on a non-escrow loan, deposits excluded, partials via AmountPaid, NOI/cashFlow). Build + commit.

### Task B3: Schedule-E completion (UNIT-TESTED) ✅
**Files:** `ScheduleEService.GetReportAsync` (extended); `Api/DTOs/ScheduleEDtos.cs` (+ MortgageInterest / Depreciation / first-year-estimate fields); tests in `ScheduleEServiceTests`.
- [x] Income = actual cash received (Rent + LateFee + Utility reimbursements, AmountPaid idiom, no deposits). Mortgage **interest** = Σ LoanPayment.InterestAmount/yr per property (principal excluded). **Depreciation per property** from each property's own basis (A4 calculator, mid-month first year flagged), summed for the portfolio total. Deterministic legacy double-count exclusion (loan present → drop manual `MortgageInterest`; computed depreciation → drop manual `Depreciation`); the modeled lines are folded into ExpensesByCategory so the CSV/packet show them and totals net.
- [x] Tests: interest-not-principal, computed depreciation, deposits excluded, no double-count, partial-via-AmountPaid. The existing year-end packet tests still pass. Build + commit.

### Task B4: Year-end view endpoint ✅
**Files:** `ReportsService.GetYearEndAsync`; `Api/DTOs/YearEndViewDtos.cs`; `GET /accounting/year-end`; test in `ReportsServiceTests`.
- [x] Composes the three §11 blocks per property + portfolio: cash-flow (B2), tax/Schedule-E (B3, incl. depreciation), rent roll (DB-side; a Partial owes only Amount − AmountPaid). Surfaces the §18 "see your accountant" caveats (first-year mid-month estimate, passive-loss limitation, capex-vs-repairs, mid-year disposition / §1250 recapture, owner-occupied/mixed-use). Test asserts cash flow ≠ taxable income. Build + commit.

---

## Phase C — Web (inline forms, app's existing components) ✅ COMPLETE

> Reused the app's existing inline patterns (DataGrid + Dialog + `InlineField` + Zod schema + TanStack Query). No new modal/drawer framework. `pnpm -C web build` passes.

### Task C1: Mortgage section on Property detail ✅
- **Files:** `web/src/lib/components/property/PropertyLoansSection.svelte` + `web/src/lib/api/endpoints/loans.ts`, wired into the Property detail page.
- [x] Inline add/edit loan (lender, amounts, rate, term, P&I, escrow + escrow-covers flags, status) + an expandable read-only amortization schedule (LoanPayment history) that flags any period whose payment didn't cover interest. Build + commit.

### Task C2: Property basis fields (depreciation) — inline ✅
- [x] Inline basis fields (purchase price, land value, in-service date, optional manual depreciation) on the property edit form via `propertyBasisSchema`, saved alongside the property; accumulated depreciation shown read-only. Build + commit.

### Task C3: Recurring expenses — inline ✅
- **Files:** `web/src/lib/components/property/PropertyRecurringExpensesSection.svelte` + `web/src/lib/api/endpoints/recurring-expenses.ts`.
- [x] Inline add/edit (category, amount, frequency, start) on the Property detail page. Build + commit.

### Task C4: Year-end / cash-flow view ✅
- **Files:** `web/src/routes/(protected)/accounting/year-end/+page.svelte` (linked from the accounting Reports tab) + `accounting.cashFlow` / `accounting.yearEnd` clients.
- [x] Tax-year + property selector + portfolio/per-property totals + export (reuses the year-end packet PDF); the three blocks render with **cash flow and taxable income as two distinct headline numbers** (depreciation + debt service visible), and the §18 "see your accountant" caveats as a labeled panel. Build + commit.

### Task C5: Smoke + phase review ✅
- [x] `web/e2e/year-end.spec.ts`: the year-end view renders both headline numbers + all three blocks (survives a year switch); reachable from the Reports tab; property detail shows the mortgage + recurring-expense sections + basis card. Specs compile/list clean; **not executed here against shared infra** (Father's-Day demo safety) — they run in CI / local dev.

---

## Verification (spec §16) ✅

- **§16.1** Loan create + `DebtServiceWorker` split/amortize/payoff — `DebtServiceServiceTests` (5) + `AmortizationCalculatorTests` (13): split correct, balance amortizes from the immutable prior balance, maturity stop, neg-am guard, final payment zeroes + `PaidOff`.
- **§16.2** Cash flow = income − opex − debt service, **excludes deposits + depreciation**, escrow **not double-counted** — `ReportsServiceTests.GetTrueCashFlow…` on worked examples.
- **§16.3** Schedule-E has mortgage **interest** + **depreciation**, **excludes principal + deposits** — `ScheduleEServiceTests`.
- **§16.4** Depreciation straight-line + mid-month first year + manual override + accumulated cap — `DepreciationCalculatorTests` (13).
- **§16.5** Deposit-as-income fix is DB-side (a `PaymentType` filter in SQL); deposit register unaffected.
- **§16.6** Recurring expenses materialize once per period (idempotent) — `RecurringExpenseGenerationServiceTests` (5).
- **§16.7** Year-end shows cash flow vs taxable income as **distinct numbers** + rent roll — `GetYearEndAsync` test + the web view.
- **§16.8** Every report query is **DB-side — confirmed by reading the generated SQL** (`GetCashFlow_IncomeQuery_AggregatesInSql_NotInMemory` asserts GROUP BY + SUM; all report queries do `GroupBy`/`Sum` before `ToListAsync`, the old in-memory `GetCashFlowAsync` is gone). §18 worker stops at maturity + never neg-amortizes; income uses AmountPaid incl. partials; per-property depreciation with the accumulated cap; legacy `MortgageInterest`/`Depreciation` excluded when modeled.
- **§16.9** Portfolio scoping / IDOR on loans, payments (schedule read), recurring expenses, and all FK refs — `LoanServiceTests` + `RecurringExpenseServiceTests`.

**Test totals:** Core 26, Engine (financial) 12, Api (financial domain) 57 = 95 green. Full solution + `pnpm -C web build` clean.

## Self-review note

Covers spec §4 (A1/A3/A5/A6), §5 (A2), §6 (A4), §7 (B1), §8 (A6), §9 (B2), §10 (B3), §11 (B4/C4), §12 (A2), §13 (A7 + B controllers), §14 (C1–C4), §15 (DB-side throughout), §18 (woven through A2/A4/A5/B1–B4). Financial cores (§5/§6/§9/§10) unit-tested.

**Merge coordination:** `AddPaymentAmountPaid` duplicates the same-named migration on `feat/scan-front-door` (intentional — the column/config/migration are byte-identical so the branches reconcile); dedupe / regenerate the model snapshot on whichever merges second. `feat/scan-front-door` also carries the `Payment.AmountPaid` population + DTO/aggregation plumbing. The sibling `tsk-387` adds `Expense.UnitId`; expect a model-snapshot conflict at merge (regenerate after rebase).
