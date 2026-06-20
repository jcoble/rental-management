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

## Phase A — Data model + workers

### Task A1: `Loan` + `LoanPayment` entities + migration
**Files:** Create `RentalCommand.Core/Entities/Loan.cs`, `LoanPayment.cs`, `RentalCommand.Core/Enums/LoanStatus.cs`; modify `RentalCommandDbContext.cs`; generate migration.
- [ ] Create entities exactly per spec §4 (Loan: per-Property FK, P&I, escrow fields, balance, status; LoanPayment: split amounts, PeriodKey, BalanceAfter). `LoanStatus { Active, PaidOff, Closed }`.
- [ ] Configure FKs + indexes (Loan.PropertyId; LoanPayment.LoanId; unique (LoanId, PeriodKey) for idempotency).
- [ ] `dotnet ef migrations add AddLoans`; review SQL.
- [ ] `dotnet build RentalCommand.sln`. [ ] Commit `feat(api): Loan + LoanPayment entities`.

### Task A2: Amortization split + `DebtServiceWorker` (UNIT-TESTED)
**Files:** Create `RentalCommand.Core/Services/AmortizationCalculator.cs` (pure); `RentalCommand.Engine/Workers/DebtServiceWorker.cs`; tests `RentalCommand.*Tests/AmortizationCalculatorTests.cs`.
**Interfaces — Produces:** `static LoanPaymentSplit AmortizationCalculator.Split(decimal balance, decimal annualRatePct, decimal monthlyPI, decimal monthlyEscrow)` → `{ Interest, Principal, Escrow, Total, BalanceAfter, PaidOff }`.
- [ ] **Write failing tests** encoding spec §5: e.g. balance 200000, rate 6%, P&I 1199.10 → interest 1000.00, principal 199.10, balanceAfter 199800.90; final-payment guard (principal ≥ balance → balanceAfter 0, PaidOff true); payment < interest → principal 0 (no negative amortization for v1, flag), balance unchanged or grows by deficit per reviewer guidance.
- [ ] Run tests → FAIL.
- [ ] Implement `Split` per §5 (decimal, round cents, final-payment guard, no negative balance).
- [ ] Run tests → PASS.
- [ ] Implement `DebtServiceWorker` mirroring `RentChargeWorker` (monthly, idempotent by (LoanId, PeriodKey), creates `LoanPayment` via `Split`, updates `Loan.CurrentBalance`/`Status`).
- [ ] `dotnet build`. [ ] Commit `feat(engine): amortization split + monthly debt-service worker`.

### Task A3: Property basis fields (depreciation inputs) + migration
**Files:** Modify `Property.cs` (+ `PurchasePrice`, `LandValue`, `InServiceDate`, `ManualAnnualDepreciation` per §4); Property DTOs; migration.
- [ ] Add fields + thread through Property create/update DTOs/service. [ ] `dotnet ef migrations add AddPropertyBasis`; review SQL. [ ] `dotnet build`. [ ] Commit `feat(api): property cost-basis fields for depreciation`.

### Task A4: Depreciation calculator (UNIT-TESTED)
**Files:** Create `RentalCommand.Core/Services/DepreciationCalculator.cs` (pure); tests.
**Interfaces — Produces:** `static decimal DepreciationCalculator.AnnualForYear(PropertyBasis basis, int year)`.
- [ ] **Write failing tests** encoding §6: building basis = purchase − land; ÷ 27.5; partial-year proration by months in service; manual override wins; cap at building basis (cumulative); 0 before in-service / after fully depreciated. (e.g. purchase 300k, land 60k → building 240k; full-year 8727.27; in-service July → 6 months → 4363.64.)
- [ ] Run → FAIL. [ ] Implement per §6 (decimal, round cents). [ ] Run → PASS. [ ] `dotnet build`. [ ] Commit `feat(api): straight-line depreciation calculator (27.5yr, override)`.

### Task A5: `RecurringExpense` + worker + migration
**Files:** Create `RecurringExpense.cs` (+ frequency enum); `RecurringExpenseWorker.cs`; migration.
- [ ] Entity per §4; worker mirrors `RentChargeWorker` (idempotent per template+period → materializes `Expense` rows with correct Category/Property/Unit/IncurredAt; advances NextRunDate). [ ] `dotnet ef migrations add AddRecurringExpenses`; review SQL. [ ] `dotnet build`. [ ] Commit `feat(engine): recurring expenses (enter once, materialize monthly)`.

---

## Phase B — Computations & reports (DB-side)

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
