# 7 Domain Gaps — Design Blueprint + Sequenced Plan (2026-07-01)

**Source:** DomainGapsArchitect (read-only, code-grounded). **Status:** design accepted; open-question
defaults **ACCEPTED** as-is (see end). This is the implementation blueprint for the 7 pre-test features.

## Orienting facts (shape every gap)
- Money enums stored as **`int`** (`RentalCommandDbContext.cs:997-998,1034`); money `HasPrecision(18,2)`
  (`:992-993,851-854`). JSON/SignalR still emit **string** names via the global `JsonStringEnumConverter`
  — DB storage and wire format are independent. **New money enums: `HasConversion<int>()`, `(18,2)`.**
- Idempotency precedent = a **filtered unique index** `(LeaseId, PaymentType, PeriodKey) WHERE PeriodKey
  IS NOT NULL` (`:1007-1009`). Reuse for import dedup (1) + proration-stub idempotency (3).
- **Reports never filter `PropertyStatus`** — only `PortfolioId` + `DeletedAt == null`. → gap 6 must add
  a *new* sold-exclusion predicate everywhere.
- **Disposition stub already noted:** `ReportsService.cs:800` ("sale-year depreciation, gain/loss, §1250
  recapture NOT computed"). Gap 6 fills exactly this.
- **Depreciation primitive exists + pure:** `DepreciationCalculator.AnnualForYear(basis, year)`
  (`Core/Services/DepreciationCalculator.cs:41-92`), SL 27.5-yr, IRS mid-month. `Property.AccumulatedDepreciation`
  is **manually maintained** (read at `ScheduleEService.cs:118`, `AccountingService.cs:1000`). Gaps 4/6 extend.
- Owner statements key off **`OwnerEntityId`** (`OwnerStatementService.cs:37,162-167`); legacy `Owner` is vestigial.
- **DB-side hard rule already honored** across Reports/ScheduleE/OwnerStatement/Accounting (EF `GroupBy`/`Sum`/
  correlated subqueries or `vw_accounting_transactions`). New work must match — no materialize-then-compute.

## Foundation refactors FIRST
- **F1 — Nullable-lease income path (M; unblocks 5, 1-payment-side; clarifies 2).** Two silent-drop landmines
  to fix together: global query filter `RentalCommandDbContext.cs:1697` (`e.Lease!.DeletedAt == null` →
  `e.Lease == null || e.Lease.DeletedAt == null`) and the accounting view's `INNER JOIN Leases … DeletedAt IS
  NULL` (`20260628065844_AddUnitIdToAccountingTransactionsView.cs:~31-66` → `LEFT JOIN` +
  `(l.DeletedAt IS NULL OR p.LeaseId IS NULL)`, null-guard name joins). Then `Payment.LeaseId int→int?`; FK
  Cascade→`SetNull`; relax DTO `[Required]`; portfolio-scoped income predicate for lease-less income.
- **F2 — Generalize depreciation primitive (S-M; unblocks 4, 6).** Generic overload keyed on
  `(costBasis, inServiceDate, method, recoveryYears, convention)` + MACRS/class-life table (5/15/27.5-yr) +
  keep SL. Recapture helper `UnrecapturedSec1250Gain = min(totalGain, accumulatedDepreciation)`. Pure Core.
- **F3 — Business-tz day-boundary + proration primitive (S-M; unblocks 3; fixes a bug).** Fixes Engine
  (`App:TimeZone`, `RentChargeService.cs:20,51-54`) vs API (`DateTime.UtcNow.Date`, `LeaseService.cs:305`)
  inconsistency; use `Portfolio.TimeZone` (`Portfolio.cs:11`). Add `BusinessClock` day helper +
  `ProrationCalculator(monthlyRent, start, end, convention)` (actual-days + 30-day). **[Note: coordinate with
  the Master Clock's `IAppTimeZoneProvider` — same business-tz seam; unify, don't duplicate.]**
- **F4 — Generalize CSV import (M; unblocks 1).** Import is a stateless double-upload dry-run
  (`CsvImportService.cs:39-129`, web `import/+page.svelte:75-153`), calls the same domain `CreateAsync`, no
  dedup, `ManagementControllerBase` (staff-only). Generalize `SupportedEntityTypes` (`ICsvImportService.cs:15`)
  + dispatch (`:82-93`) + web; add lease-by-reference resolution (mirror Unit→Property `Take(2)` `:242-259`) + dedup.

## Sequence (dependency order)
```
F1 ─┬─► Gap 5 (app-fee income)  [validates F1, smallest]
    ├─► Gap 1 (bulk import)  ◄── also needs F4
    └─► Gap 2 (owner distribution)
F2 ─┬─► Gap 4 (capital depreciation)
    └─► Gap 6 (disposition/recapture) ◄── MUST follow Gap 4
F3 ─►  Gap 3 (rent proration)
     ► Gap 7 (eviction)  [fully independent — parallelize anytime]
```
**Suggested order:** F1,F2,F3,F4 → 5 → 1 → 2 → 3 → 4 → 6 → 7. **Build rule:** entities land in Core → Data
(one migration/gap) → Api. Every gap touches `RentalCommand.Api` → **serialize Api builds** (author in parallel,
don't swarm). Independent authoring lanes: {5,1,2} vs {4,6} vs {3} vs {7}.

## Per-gap (design + reports DB-side + reconcilability + size)
- **Gap 1 Bulk import (M):** no new entity. Column arrays + `TryImportPayment/ExpenseAsync` (inject
  `IPaymentService`/`IExpenseService`/`ILoanService`); lease-resolution `Take(2)`; **dedup** natural-key hash —
  Payment `(PortfolioId,LeaseId,Amount,PaidDate??DueDate,ExternalReference)`, Expense
  `(PortfolioId,PropertyId,Amount,IncurredAt,Description)` — one batched `.Where(...Contains(keys))` DB query →
  skip-on-match. Mortgage → import a **`Loan`** (DebtServiceService amortizes); one-offs as `Expense{MortgageInterest}`.
  Needs F1 + F4.
- **Gap 2 Owner distribution (S-M):** new `OwnerDistribution : IAuditable, IPortfolioScoped`
  `(Id,PortfolioId,OwnerEntityId,PropertyId?,Date,Amount(18,2),Method,Memo?,…,DeletedAt?)`; FK→OwnerEntity
  `Restrict`; `/api/v1/owner-distributions`; statement gains Distributions + Undistributed (earned−distributed,
  two DB aggregates). **Never an Expense.** Covering index `(PortfolioId,OwnerEntityId,Date)`.
- **Gap 3 Rent proration (M):** `RentChargeSchedule.GetDuePeriods` (`RentChargeSchedule.cs:38`) *emits* stub
  periods w/ `(PeriodStart,PeriodEnd)` instead of dropping; both creators (`RentChargeService.cs:143-154`,
  `LeaseService.cs:328-339`) use F3's `ProrationCalculator` so Engine/API never diverge; move-out proration =
  prorated `Rent` Payment or negative credit, idempotent on the stub month's `(LeaseId,PaymentType,PeriodKey)`;
  per-portfolio `ProrationConvention`. Surface on move-out statement.
- **Gap 4 Capital depreciation (M):** new `CapitalAsset : IAuditable, IPortfolioScoped`
  `(…,PropertyId,UnitId?,SourceExpenseId?,CostBasis(18,2),InServiceDate,Method,RecoveryYears,Convention,
  AccumulatedDepreciation(18,2),DisposedOnDate?,…)`; new enums `DepreciationMethod`/`Convention`
  (`HasConversion<int>`); "Capitalize this expense" creates the asset + excludes the expense from deductible
  (link `Expense.CapitalizedAssetId`). Schedule E depr line = property building + Σ capital-asset annual (bounded
  per-property loop, matches `ScheduleEService.cs:104-149`); P&L excludes capitalized. Uses F2; **feeds gap 6**.
  Covering index `(PortfolioId,PropertyId,InServiceDate)`.
- **Gap 5 App-fee income (S once F1):** `PaymentType.ApplicationFee`; optional `Payment.ApplicationId?`/`PropertyId?`;
  DTO drops forced `LeaseId`. Income predicate gains a lease-less branch (`ReportsService.cs:585-591`,
  `ScheduleEService.cs:41-56`) — still one SQL statement. Stripe-test-mode compatible. **Reference impl of F1 —
  build first among {1,2,5}.**
- **Gap 6 Disposition/§1250 (M-L):** new `PropertyDisposition : IAuditable, IPortfolioScoped`
  `(…,SaleDate,SalePrice,SellingCosts,AccumulatedDepreciationAtSale,AdjustedBasis,RealizedGainLoss,
  UnrecapturedSec1250Gain,Sec1231Gain,…)`; `AccumulatedDepreciationAtSale = Property.AccumulatedDepreciation +
  Σ CapitalAsset.AccumulatedDepreciation` (**build 4 first**); new `PropertyStatus.Sold` + `SoldOnDate`. **The L
  part:** add as-of-date sold-exclusion predicate (`SoldOnDate == null || SoldOnDate > rangeEnd`) to operating
  report property-sets in Reports/OwnerStatement/Dashboard (new everywhere — status isn't filtered today), each
  still one SQL statement. Year-end gains a disposition section; replace the `:800` stub.
- **Gap 7 Eviction (M, independent):** copy `WorkOrder` + `WorkOrderStatusEvent` precedent
  (`RentalCommandDbContext.cs:1236-1303`, child filter `:1738`): `Eviction : IAuditable, IPortfolioScoped`
  `(…,LeaseId,TenantId,FilingDate?,Status,CourtName?,CaseNumber?,Outcome?,ClosedDate?,StoredFileId?,…)` +
  `EvictionStatusEvent`; enum `EvictionStatus (NoticeToQuit/Filed/Hearing/Judgment/Writ/Resolved/Dismissed)`;
  `/api/v1/evictions`; resolving-with-possession drives lease→`Terminated`. Delinquency annotated via one
  correlated `EXISTS` in the existing SQL (`:444-510`). Indexes `(PortfolioId)`,`(LeaseId)`,`(Status)`.

## Open-question defaults — ALL ACCEPTED (orchestrator)
1. Depreciation default → **straight-line by class life** (27.5/5/15), MACRS opt-in per asset. ✓
2. Proration convention → **actual-days**, per-portfolio 30-day switch. ✓
3. App-fee attachment → **`RentalApplication` (`ApplicationId`)** + optional `PropertyId`, portfolio fallback. ✓
4. Owner-distribution FK → **`OwnerEntityId`** + nullable `PropertyId`. ✓
5. Sold property → **`PropertyStatus.Sold` + `SoldOnDate`**, exclude as-of report date (NOT soft-delete). ✓
6. Mortgage import → **import a `Loan`** (DebtServiceService amortizes); one-offs as `Expense{MortgageInterest}`;
   **no raw `LoanPayment` write path** (would break the `BalanceAfter` chain). ✓
7. Import dedup → **natural-key row hash**, one batched DB query, skip-on-match, reported in the result DTO. ✓
8. Owner statement → **show both** earned `NetToOwner` (existing SQL) + `Distributions` (new SQL) + undistributed. ✓
9. New enum storage → **`HasConversion<int>()`**; JSON stays string via the global converter. ✓

## Reconcilability
Every gap's aggregation stays **one server-side SQL statement or a view + covering index**. Only in-memory
steps are depreciation math (4/6) + owner-statement rounding (2), over already-bounded per-property/per-asset
sets (consistent with existing ScheduleE/OwnerStatement). No N+1, no lazy-load, no materialize-then-compute.

## Orchestrator note
F3 overlaps the Master Simulation Clock's `IAppTimeZoneProvider` (both add a business-tz day-boundary seam on
`Portfolio.TimeZone`/`App:TimeZone`). **Unify these** — build the clock's tz provider first, then F3/gap-3 reuses it.
