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

### Task B1: Deposit-as-income fix
**Files:** Modify `RentalCommand.Api/Services/Domain/ReportsService.cs` (cash-flow query ~L532–539) + the money-snapshot / owner-statement / year-end income queries.
- [ ] In every income sum, add a DB-side `WHERE Payment.PaymentType IN (Rent, LateFee)` (per §7; exclude `SecurityDeposit`). Confirm via generated SQL it's a filter, not a post-filter. Leave the security-deposit register untouched.
- [ ] `dotnet build`; verify the cash-flow query SQL excludes deposits. [ ] Commit `fix(api): exclude security deposits from income (they're a liability)`.

### Task B2: True cash-flow computation + endpoint (UNIT-TESTED logic)
**Files:** Modify `ReportsService` (extend cash flow) + a `CashFlowResult` DTO; controller endpoint (extend `/reports/cash-flow` or add `/accounting/cash-flow?propertyId&from&to`); tests for the aggregation shape on a seeded worked example.
**Interfaces — Produces:** per property + portfolio: `{ income, operatingExpenses, noi, debtService, cashFlow }` by period.
- [ ] Implement per §9 **DB-side**: income (Rent+LateFee, paid/partial, no deposits); operating expenses EXCLUDING escrow-funded Taxes/Insurance when the property's active loan escrows them; NOI; debtService = Σ LoanPayment.TotalAmount; cashFlow = NOI − debtService. Keep it `IQueryable` (`GroupBy`/`Sum` in SQL).
- [ ] Test on a seeded worked example (rent, a few expenses, one loan with escrow) asserting income/opex/NOI/debtService/cashFlow and that escrowed taxes aren't double-counted and deposits/depreciation are absent.
- [ ] `dotnet build`; **read the generated SQL** — confirm no whole-table pull / N+1. [ ] Commit `feat(api): true cash flow (rent − opex − debt service), escrow-aware, DB-side`.

### Task B3: Schedule-E completion (UNIT-TESTED logic)
**Files:** Modify the Schedule-E computation in `ReportsService`/Accounting; tests.
- [ ] Implement per §10: rentalIncome (no deposits) − deductible expenses by category (INCLUDING taxes/insurance even if escrowed) − mortgage **interest** (Σ LoanPayment.InterestAmount; principal excluded) − depreciation (Σ A4). When a Loan exists for a property, ignore legacy manual `MortgageInterest` expenses for that property (avoid double-count); same for `Depreciation` vs computed.
- [ ] Test a worked example: assert interest (not principal) deducted, depreciation deducted, deposits excluded, no double-count.
- [ ] `dotnet build`; check SQL. [ ] Commit `feat(api): Schedule-E with mortgage interest + depreciation, principal excluded`.

### Task B4: Year-end view endpoint
**Files:** Extend `/accounting/year-end-packet` (or add `/accounting/year-end`) + DTO.
- [ ] Per property + portfolio total, return the three blocks of §11: cash-flow block (B2), tax/Schedule-E block (B3, incl. depreciation), rent roll. Reuse existing rent-roll/packet logic. Keep DB-side.
- [ ] `dotnet build`; smoke the endpoint shape; read SQL. [ ] Commit `feat(api): year-end view — cash flow vs taxable income + rent roll`.

---

## Phase C — Web (inline forms, app's existing components)

> Reuse the app's existing inline create/edit form components (how lease/work-order/expense forms are built). No new modal/drawer framework. Build + manually verify each; commit per task.

### Task C1: Mortgage section on Property detail
- **Files:** add a Loan section to `web/src/routes/(protected)/properties/[id]/+page.svelte` (or a child component) + `web/src/lib/api/loans.ts`.
- [ ] Inline add/edit loan (lender, balance, rate, term, P&I, escrow, escrow-covers flags) + a read-only amortization schedule (LoanPayment history). [ ] `pnpm -C web build`. [ ] Commit `feat(web): per-property mortgage section + amortization schedule`.

### Task C2: Property basis fields (depreciation) — inline
- **Files:** add inline fields to the Property detail form (purchase price, land value, in-service date, optional manual depreciation).
- [ ] [ ] `pnpm -C web build`. [ ] Commit `feat(web): property cost-basis fields`.

### Task C3: Recurring expenses — inline
- **Files:** a "Recurring expenses" inline section (Property or Money area) + `recurring-expenses.ts` client.
- [ ] Inline add/edit (category, amount, frequency, start). [ ] `pnpm -C web build`. [ ] Commit `feat(web): recurring expenses`.

### Task C4: Year-end / cash-flow view
- **Files:** a view in the Money/Reports area consuming B4 + B2.
- [ ] Property selector + portfolio total + period (year/month) + export; render the three blocks with **cash flow and taxable income as clearly distinct numbers** (depreciation + debt service visible). [ ] `pnpm -C web build`. [ ] Commit `feat(web): year-end / cash-flow view (cash flow vs taxable income)`.

### Task C5: Smoke + phase review
- [ ] One UI smoke that loads the year-end view and asserts the cash-flow + tax blocks render. [ ] Per-phase self-review vs spec §16 acceptance criteria (esp. **read the generated SQL** for the report queries). [ ] Commit `test(web): year-end view smoke + phase review`.

---

## Verification (spec §16)

A1–A2 loans + amortization worker (split correct, balance amortizes, final payment zeroes/PaidOff). B1 deposits excluded (DB-side). B2 cash flow correct + escrow not double-counted + no deposits/depreciation. B3 Schedule-E has interest+depreciation, excludes principal. A4 depreciation correct + override + capped. B4/C4 year-end shows cash flow vs taxable income distinctly. All report queries DB-side — **confirmed by reading generated SQL.** IDOR enforced everywhere.

## Self-review note

Covers spec §4 (A1/A3/A5), §5 (A2), §6 (A4), §7 (B1), §8 (A5), §9 (B2), §10 (B3), §11 (B4/C4), §12 (A2), §13 (controllers across A/B), §14 (C1–C4), §15 (DB-side throughout). The financial cores (§5/§6/§9/§10) are unit-tested. NOTE: incorporate the financial-correctness reviewer's findings before/while building Phase B.
