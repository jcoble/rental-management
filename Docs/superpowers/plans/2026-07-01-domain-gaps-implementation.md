# 7 Domain Gaps Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the 7 pre-test domain feature gaps (bulk import, owner-distribution record, rent proration, capital-improvement depreciation, application-fee income, property sale/disposition, first-class eviction) as **complete, reachable, verified** full-stack features — backend + wired web UI + Flutter parity (or logged deferral) + running-app verification.

**Architecture:** Operationalizes `Docs/e2e/investigations/2026-07-01-domain-gaps-design.md`. Four foundation refactors (F1 nullable-lease income, F2 generalized depreciation, F3 business-tz proration primitive, F4 CSV import seam) land first, then the 7 gaps in dependency order **5 → 1 → 2 → 3 → 4 → 6 → 7**. Every new entity is portfolio-scoped, soft-deletable, RLS-enrolled; every aggregation is one server-side SQL statement or a DB view + covering index (HARD RULE); every gap with a UI implication wires a reachable web screen **and** Flutter parity.

**Tech Stack:** .NET 10 (C#), EF Core + Npgsql on PostgreSQL, ASP.NET Identity + JWT. SvelteKit 5 (runes) + TanStack Query + Tailwind + bits-ui dialogs (`web/`). Flutter + Riverpod + Dio + go_router (`mobile/`).

---

## Global Constraints

Copied verbatim from the design + project CLAUDE.md + the E2E Definition of Done. Every task's requirements implicitly include this section.

- **Data-access HARD RULE:** all aggregation / grouping / filtering / joins / sorting / paging run **server-side** — one EF-translated SQL statement or a DB **view** + covering index. NEVER materialize-then-`GroupBy`/`Sum`/`Where` in memory; no N+1; no lazy-load. The one sanctioned in-memory step is the per-property / per-asset depreciation & owner-statement rounding loop over an **already-bounded, small** set (matches existing `ScheduleEService`/`OwnerStatementService`). A dedup existence check is allowed only as **one** bounded query + a HashSet membership test.
- **New money enums:** `HasConversion<int>()`; new money columns `HasPrecision(18, 2)`. JSON/SignalR stay **string** via the global `JsonStringEnumConverter` (do not touch). Append new enum members at the **end** (ordinal int backing must not shift).
- **Every new portfolio-scoped table needs 4 things:** (1) a `DbSet` property in `RentalCommandDbContext.cs` (lines 17–104), (2) an inline `modelBuilder.Entity<T>(...)` config block, (3) a soft-delete `entity.HasQueryFilter(e => e.DeletedAt == null)`, (4) an **RLS enrollment row** in a migration (copy `RentalCommand.Data/Migrations/20260618110855_AddAccountingRls.cs`). Omitting RLS silently breaks cross-portfolio isolation.
- **Portfolio scope / IDOR:** controllers derive `ManagementControllerBase` (staff-only) and pass `GetPortfolioId()` from the JWT claim — scope never comes from the client. Services validate every inbound FK via a `PortfolioScopeGuards` helper (`RentalCommand.Api/Services/Domain/PortfolioScopeGuards.cs`); an out-of-scope FK returns `null` → controller returns 404.
- **Migrations:** PostgreSQL only. `dotnet ef migrations add <Name> --project RentalCommand.Data --startup-project RentalCommand.Api`. One migration per gap. Migrations self-apply on boot under an advisory lock.
- **Serialize .NET builds:** ONE `dotnet build`/`dotnet test` at a time (RAM rule + concurrent same-`.csproj` builds corrupt `bin/obj`). Set `MSBUILDDISABLENODEREUSE=1`; `dotnet build-server shutdown` after heavy batches. Authoring (reads/edits that don't compile) may parallelize; the moment a lane needs to build, it holds the single build slot.
- **Testing posture (project CLAUDE.md — OVERRIDES writing-plans' TDD-everywhere default):** unit-test the **pure Core primitives** (depreciation, proration, dedup-key logic) cent-exact — they are cheap and load-bearing. For services/controllers/UI, rely on **running-app verification** (the `/verify` skill) + a couple of focused integration tests where meaningful; do **not** author broad regression suites now. Solution must build green; existing tests must stay green.
- **Definition of Done — NO half-done features (per task):** (1) Backend entity/migration/service/endpoint, portfolio-scoped, DB-side. (2) **Web UI built AND wired into a reachable place** (nav / record page / grid / report) — no orphan components. (3) **Flutter parity where a host surface exists** — built, OR an explicit deferral task with an id (never a silent skip). (4) **Reachability verified in the running app** — create the record, see it render, see the report change. (5) Tests where meaningful; builds green.
- **Branch naming:** `tsk-<id>-<slug>` lowercase so the GitHub→Notion sync links + closes the task. Gap→task id: gap1=TSK-617, gap2=TSK-618, gap3=TSK-619, gap4=TSK-620, gap5=TSK-621, gap6=TSK-622, gap7=TSK-623.
- **Commit convention:** clear subject + body only. **No `Co-Authored-By: Claude` / AI-attribution trailer.**

---

## Shared recipes (defined once — referenced by tasks, DRY)

Every task below says "apply Recipe X" instead of repeating boilerplate. Read these once.

### Recipe A — New portfolio-scoped, soft-deletable entity
Template files: `RentalCommand.Core/Entities/WorkOrder.cs`, DbContext config `RentalCommand.Data/RentalCommandDbContext.cs:1266–1319`.
1. Create `RentalCommand.Core/Entities/<Name>.cs` — `public class <Name> : IAuditable, IPortfolioScoped`. Always include `int Id`, `int PortfolioId`, `DateTime CreatedAt`, `DateTime UpdatedAt`, `DateTime? DeletedAt`. `IAuditable` is an empty marker (auto-audited by the interceptor); `IPortfolioScoped` requires only `int PortfolioId { get; }`.
2. Add `public DbSet<<Name>> <Plural> => Set<<Name>>();` in `RentalCommandDbContext.cs` (with the other DbSets, lines 17–104).
3. Add a `modelBuilder.Entity<<Name>>(entity => { ... });` block in `OnModelCreating`: `entity.HasKey(e => e.Id)`; money `.HasPrecision(18,2)`; enums `.HasConversion<int>()`; strings `.HasMaxLength(...)`; `entity.HasIndex(e => e.PortfolioId)` + the gap's covering index; `entity.HasQueryFilter(e => e.DeletedAt == null)`; FK to Portfolio `OnDelete(Cascade)`, required principal FK `OnDelete(Cascade)`, optional FKs `OnDelete(SetNull)`.
4. **RLS migration** (see Recipe C): add the table name to a `tenant_isolation` policy block.

### Recipe B — New EF migration
`MSBUILDDISABLENODEREUSE=1 dotnet ef migrations add <Name> --project RentalCommand.Data --startup-project RentalCommand.Api` (holds the build slot). New table → copy the `CreateTable` shape from `Migrations/20260604014042_AddWorkOrderStatusEvents.cs` (identity `Id`, enum cols `type:"integer"`, strings `character varying(N)`, dates `timestamp with time zone`, FK `onDelete`). New columns on an existing table → copy `Migrations/20260620234900_AddPropertyBasis.cs` (decimals `numeric(18,2)`, `nullable:false, defaultValue: 0m` for non-null money). Verify both `Up` and `Down` are correct before moving on.

### Recipe C — RLS enrollment
Template: `RentalCommand.Data/Migrations/20260618110855_AddAccountingRls.cs`. In the gap's migration `Up`, for each new portfolio-scoped table run (via `migrationBuilder.Sql`):
```sql
ALTER TABLE "<Table>" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "<Table>" FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON "<Table>"
  USING ("PortfolioId" = NULLIF(current_setting('app.current_portfolio_id', true), '')::int
         OR current_setting('app.is_admin', true) = 'true')
  WITH CHECK ("PortfolioId" = NULLIF(current_setting('app.current_portfolio_id', true), '')::int
         OR current_setting('app.is_admin', true) = 'true');
```
`Down` drops the policy + disables RLS.

### Recipe D — New CRUD service + controller
Templates: `RentalCommand.Api/Services/Domain/WorkOrderService.cs` (+ `IWorkOrderService.cs`), `RentalCommand.Api/Controllers/WorkOrderController.cs`, `RentalCommand.Api/DTOs/WorkOrderDtos.cs`.
1. Service ctor injects `RentalCommandDbContext _db`, `IDataUpdateService _dataUpdate`, `TimeProvider _timeProvider`. `ListPageAsync` = `.AsNoTracking().Where(x => x.PortfolioId == portfolioId)` + conditional `.Where(...)` + `EF.Functions.ILike($"%{term}%")` + `CountAsync` + `SortField` switch + a single `.Select(projection)` (LEFT-JOIN names via `x.Nav != null ? x.Nav.Name : null`) + `.Skip(NormalizedSkip).Take(NormalizedTake)`. `GetAsync` = `.Include(...)` + `FirstOrDefaultAsync(x => x.Id == id && x.PortfolioId == portfolioId)`. `CreateAsync`/`UpdateAsync` validate FKs via `PortfolioScopeGuards` (add sibling `Ensure...InPortfolioAsync` helpers as needed) returning `null`→404 on out-of-scope; construct with `PortfolioId = portfolioId`, `CreatedAt/UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime`; `SaveChanges`; `_dataUpdate` broadcast. `DeleteAsync` sets `DeletedAt` (soft delete).
2. Controller: `[ApiController] [Route("api/v1/<kebab>")] [Produces("application/json")] public class <Name>Controller : ManagementControllerBase`. Endpoints map GET list/`page`/`{id}`, POST, PATCH, DELETE to the service, each passing `GetPortfolioId()`. `CreatedAtAction(nameof(Get), new { id }, created)` on create.
3. DTOs (`class` + static `FromEntity`, NOT records): `<Name>Response`, `<Name>ListResponse { Items; TotalCount; Skip; Take; }`, `<Name>ListQuery : ListQuery`, `Create<Name>Request`, `Update<Name>Request` (nullable-means-untouched). DataAnnotations `[Required]/[Range]/[MaxLength]/[EnumDataType]`.
4. Register `services.AddScoped<I<Name>Service, <Name>Service>();` in `RentalCommand.Api/Extensions/ServiceCollectionExtensions.cs` (`AddDomainServices`, ~line 62).

### Recipe E — New web CRUD feature (list + create/edit dialog + detail)
Templates: list+dialog `web/src/routes/(protected)/vendors/+page.svelte`; simple section `web/src/lib/components/property/PropertyRecurringExpensesSection.svelte`; money-action dialog `web/src/routes/(protected)/deposits/[id]/+page.svelte:452–514`; endpoint module `web/src/lib/api/endpoints/workOrders.ts`.
1. Endpoint module `web/src/lib/api/endpoints/<resource>.ts` — interfaces + a `<resource>` object with `list`/`listPage`/`get`/`create`/`update`/`delete` using `api.get/post/patch/delete` and `buildListQuery` from `web/src/lib/api/list-params.ts`.
2. Screen — `createQuery`/`createMutation`/`useQueryClient` **inline** (no central queryKeys file); keys are inline arrays like `['<resource>', portfolioId, ...]`; on success `showSuccess` + close + `queryClient.invalidateQueries({ queryKey: ['<resource>', portfolioId] })`; on error `showError(apiErrorMessage(err))`. Forms validate with a zod schema in `web/src/lib/schemas/index.ts` via `parseForm`. Money actions copy the deposits Add-deduction `Dialog.Root` block.
3. **Wire it (the DoD clause):** add a nav item to the right group in `web/src/lib/components/AppShell.svelte` (`staffNavGroups` 111–156 + a glyph in `navGlyphByHref` 186–210), OR mount a `<XSection propertyId={id}/>` in the host record page, OR add an action button to the host detail component. If it is a first-class record with a detail page, add a `RecordType` in `web/src/lib/navigation/record-href.ts`.

### Recipe F — New Flutter parity feature
Templates: repo+providers `mobile/lib/features/properties/properties_repository.dart`; screens `mobile/lib/features/properties/*`; nav registration `mobile/lib/features/home/mobile_destination.dart` + `mobile_domain_navigation.dart`; read-only-with-one-action example `mobile/lib/features/owner_reports/`.
1. Model — feature-local `mobile/lib/features/<f>/<f>_models.dart` with `factory X.fromJson` + `toJson`.
2. Repository — `class <F>Repository { <F>Repository(this._dio); ... }`, one method per endpoint wrapped `try/on DioException catch (e) { throw ApiException.fromDioException(e); }`; `final <f>RepositoryProvider = Provider((ref) => <F>Repository(ref.watch(dioProvider)));`; list `Notifier`/detail `FutureProvider.family`.
3. Screen(s) — `*_list_screen.dart` / `*_detail_screen.dart` / `*_form_sheet.dart` using `ref.watch(...)` + `asyncState.when(loading/error/data)`.
4. Register — add to `MobileDestinationId` enum, a `MobileDestination(...)` entry in the right group, and the `_shellTargetFor` tab switch. Or, for an action on an existing detail screen, add a button + form sheet + repo method only.

---

## Shared indexes, hazards, and corpus-revision triggers

**Shared index ownership (do once, don't duplicate).** The Payment/Expense **date & category indexes** are owned by the **data-access hardening lane (TSK-626)** and consumed here — do NOT add competing indexes:
- `IX_Payments_Portfolio_Type_PaidDate` on `Payments(PortfolioId, PaymentType, PaidDate)` — benefits gap1 dedup + gap5 income predicate + reports.
- `IX_Expenses_Portfolio_Category_IncurredAt` on `Expenses(PortfolioId, Category, IncurredAt)` — benefits gap1 dedup + gap4 P&L.
If TSK-626 has not landed when a gap needs one, add it **in that gap's migration** and leave a `// SHARED with TSK-626` comment so the other lane dedupes. The **new-table covering indexes** (`OwnerDistribution(PortfolioId,OwnerEntityId,Date)`, `CapitalAsset(PortfolioId,PropertyId,InServiceDate)`, `PropertyDisposition(PortfolioId,PropertyId)`, `Eviction(PortfolioId)/(LeaseId)/(Status)`) are gap-owned (no overlap).

**Cross-gap ordering hazards:**
- **F1 before {5,1,2}.** Gap5 is the reference impl of F1 — build F1, then gap5 to validate it, before gap1/gap2.
- **F2 before {4,6}. Gap4 before Gap6.** Gap6's `AccumulatedDepreciationAtSale = Property.AccumulatedDepreciation + Σ CapitalAsset.AccumulatedDepreciation` reads gap4's `CapitalAsset`. Building gap6 first would compute a wrong basis.
- **F3 before gap3.** Gap3's proration primitive + business-tz day helper come from F3. F3 also **unifies with the already-shipped clock `IAppTimeZoneProvider`** (TSK-615 Phase A, `RentalCommand.Core/Time/IAppTimeZoneProvider.cs`) — reuse it, do not add a second tz seam.
- **F4 before gap1.** The import seam + lease-reference resolver come from F4.
- **Gap7 reworks the delinquency arc** (annotates `GetDelinquencyAsync`) but touches no other gap's math — fully parallel-authorable.
- **Api build serialization:** every gap touches `RentalCommand.Api`. Author lanes {5,1,2}, {4,6}, {3}, {7} in parallel, but **build one at a time**.

**Corpus-revision triggers (SA must re-author `scenario.json` + re-run the 4 builders when these land — flag in each PR):**
| Gap | Cent-exact spine impact |
|---|---|
| gap3 proration | L16 first-month rent 452.42, L05/L20 boundaries move from hand-entered to app-computed. |
| gap4 capital depreciation | New depreciation lines (e.g. P09 roof ≈ $135 partial-year); expense reclassified out of Repairs. |
| gap5 app-fee income | Schedule E income gains application fees (income without a lease) → income totals shift. |
| gap6 disposition | Net-new sale (e.g. P08) → sale-year depreciation + gain/loss + §1250 in year-end; sold property excluded as-of sale date. |
| gap7 eviction | L14 delinquency **alternate**: eviction path supersedes the September lump-cure (never both on one DB). |
Gaps 1 & 2 do **not** shift the cent-exact spine (import reproduces the same rows; distributions are a new record reconciled against computed net) — but SA still exercises the new paths.

---

## Build sequence & branch strategy

Design order: **F1, F2, F3, F4 → gap5 → gap1 → gap2 → gap3 → gap4 → gap6 → gap7.**

**Branch strategy — RESOLVED (team-lead, 2026-07-01): SEQUENTIAL, one lane at a time in dependency order.** Because .NET Api builds serialize *and* the gap files overlap heavily (shared report services, DbContext, DTOs), parallel authoring across lanes would race on build + merge. Implement strictly in the order below, one stacked per-gap branch at a time (each rebased on the prior toward the "E2E-ready" integration line); each foundation folds into its first consumer's branch and carries that task id (F1→621, F2→620, F3→619, F4→617):
1. `tsk-621-app-fee-income`: **F1 → gap5** (F1's reference impl).
2. `tsk-617-bulk-import`: **F4 → gap1** (needs F1).
3. `tsk-618-owner-distribution`: **gap2**.
4. `tsk-619-rent-proration`: **F3 → gap3**.
5. `tsk-620-capital-depreciation`: **F2 → gap4**.
6. `tsk-622-property-disposition`: **gap6** (needs gap4).
7. `tsk-623-eviction`: **gap7** (independent — sequenced last, but has no cross-gap dependency).
Gap builders are dispatched lane-by-lane as the build slot frees; do NOT run two build lanes concurrently.

---

# Part F1 — Nullable-lease income path (foundation; TSK-621; unblocks 5, 1, 2)

**Goal:** let income exist without a lease (application fees, future lease-less income) without silently dropping it. Two silent-drop landmines fixed together: the Payment global query filter (EF INNER JOIN) and the accounting view's INNER JOIN Leases. Then `Payment.LeaseId int→int?`.

### Task F1.1: Make `Payment.LeaseId` nullable + fix the EF query filter

**Files:**
- Modify: `RentalCommand.Core/Entities/Payment.cs:10`
- Modify: `RentalCommand.Data/RentalCommandDbContext.cs:1727` (Payment query filter) + the Payment→Lease FK config
- Test: `RentalCommand.Data.Tests` (or `RentalCommand.IntegrationTests`) — a filter-retention test

**Interfaces:**
- Produces: `Payment.LeaseId` is now `int?`; the Payment global query filter retains lease-less payments; Payment→Lease FK is `OnDelete(SetNull)`. Consumed by gap5 (app-fee), gap1 (import), all income predicates.

- [ ] **Step 1 — Entity.** In `Payment.cs:10` change `public int LeaseId { get; set; }` → `public int? LeaseId { get; set; }`.

- [ ] **Step 2 — Query filter.** In `RentalCommandDbContext.cs:1727` change:
```csharp
// BEFORE: modelBuilder.Entity<Payment>().HasQueryFilter(e => e.Lease!.DeletedAt == null);
modelBuilder.Entity<Payment>().HasQueryFilter(e => e.Lease == null || e.Lease.DeletedAt == null);
```
This is the EF mirror of the view change (F1.2). Without it EF emits an INNER JOIN and lease-less payments vanish from every query.

- [ ] **Step 3 — FK OnDelete.** Find the Payment→Lease relationship config in `RentalCommandDbContext.cs` (search `HasOne(e => e.Lease)` / the `Payments` config block ~1019–1059). Ensure it is `.HasForeignKey(e => e.LeaseId).OnDelete(DeleteBehavior.SetNull)` (was Cascade). A nullable FK with SetNull is required so deleting a lease doesn't cascade-delete historical app-fee-adjacent payments.

- [ ] **Step 4 — Failing test.** Add an integration test: insert a `Payment { LeaseId = null, PaymentType = ApplicationFee-placeholder/Other, Status = Paid, Amount = 50 }` (use `PaymentType.Other` until gap5 adds ApplicationFee), then `await db.Payments.CountAsync()` includes it. Run → expect FAIL before the filter change (or compile error on the nullable assignment before Step 1).

- [ ] **Step 5 — Run test → PASS.** `MSBUILDDISABLENODEREUSE=1 dotnet test --filter <TestName>`.

- [ ] **Step 6 — Commit.** `feat(payments): allow lease-less payments (nullable LeaseId + query filter)`.

**Verify:** `dotnet build RentalCommand.sln` green; the retention test passes; no other test regresses (`p.Lease!.X` sites now compile against `int?` — Step 2 of F1.3 handles the report predicates).

### Task F1.2: Rewrite the accounting view — INNER JOIN → LEFT JOIN Leases

**Files:**
- Create: `RentalCommand.Data/Migrations/<ts>_AddNullableLeaseToPaymentAndView.cs` (Recipe B)
- Reference: `RentalCommand.Data/Migrations/20260628065844_AddUnitIdToAccountingTransactionsView.cs:63–66` (current view SQL)

**Interfaces:**
- Produces: `Payments.LeaseId` column is nullable (FK SetNull); `vw_accounting_transactions` LEFT-JOINs Leases so lease-less payments appear in the ledger with null property/unit/lease-number.

- [ ] **Step 1 — Generate migration.** Run `dotnet ef migrations add AddNullableLeaseToPaymentAndView ...` (Recipe B). EF emits the `AlterColumn` for `LeaseId` (nullable) + the FK change from F1.1.

- [ ] **Step 2 — Rewrite the view in the migration.** In the generated migration `Up`, after the `AlterColumn`, `migrationBuilder.Sql` a full `CREATE OR REPLACE VIEW vw_accounting_transactions ...` copied from `20260628065844_...cs` but with the Payments branch changed:
```sql
FROM "Payments" p
LEFT JOIN "Leases" l       ON l."Id" = p."LeaseId" AND l."DeletedAt" IS NULL   -- was INNER JOIN
LEFT JOIN "Properties" prop ON prop."Id" = l."PropertyId" AND prop."DeletedAt" IS NULL
LEFT JOIN "Tenants" ten     ON ten."Id" = l."TenantId"   AND ten."DeletedAt" IS NULL
WHERE p."LeaseId" IS NULL OR l."Id" IS NOT NULL   -- CORRECTION (GapLane1): keep genuinely lease-less rows, but STILL DROP payments whose lease is soft-deleted. A bare LEFT JOIN retains non-matching left rows, so a soft-deleted lease (excluded by the ON's DeletedAt filter) would leak its payments into the ledger and break the AccountingTransactionsViewTests soft-delete-drop guarantee.
```
Keep `GRANT SELECT ON vw_accounting_transactions TO rentalcommand_api;`. `l."PropertyId"`, `l."UnitId"`, `l."LeaseNumber"` in the SELECT already tolerate NULL (they become null for lease-less rows). `Down` recreates the prior INNER-JOIN view verbatim.

- [ ] **Step 3 — Apply + round-trip.** Start the dev DB, let the migration self-apply (or `dotnet ef database update`). Insert a lease-less `Payment` (SQL or a test) and `SELECT * FROM vw_accounting_transactions WHERE "LeaseId" IS NULL` → the row appears with null property/lease-number.

- [ ] **Step 4 — Commit.** `feat(accounting): LEFT JOIN leases in transactions view for lease-less income`.

**Verify:** migration applies clean up **and** down; a lease-less payment is visible in `vw_accounting_transactions`; a payment whose lease is **soft-deleted stays absent** (AccountingTransactionsViewTests soft-delete-drop guarantee still holds — the WHERE guard); `AccountingService.GetTransactionsAsync` (queries the view) returns the lease-less row without throwing.

### Task F1.3: Make the income predicates + PaymentService null-lease-safe

**Files:**
- Modify: `RentalCommand.Api/Services/Domain/ReportsService.cs` (property-scoping joins ~597, 693–701, 989)
- Modify: `RentalCommand.Api/Services/Domain/ScheduleEService.cs:53` (property scoping)
- Modify: `RentalCommand.Api/Services/Domain/PaymentService.cs:240` (conditional lease guard)
- Modify: `RentalCommand.Api/DTOs/PaymentDtos.cs:114` (relax `[Required]` — deferred flip)

**Interfaces:**
- Consumes: `Payment.LeaseId` is `int?` (F1.1).
- Produces: report property-scoping predicates use null-safe navigation; `PaymentService.CreateAsync` guards the lease only when provided. `CreatePaymentRequest.LeaseId` becomes `int?` (its lease-XOR-application enforcement lands in gap5 G5.3).

- [ ] **Step 1 — Report predicates.** Anywhere a report scopes by `p.Lease!.PropertyId` (e.g. `ReportsService.cs:597`, the `GetTrueCashFlowAsync` subquery 693–701, `ScheduleEService.cs:53`), the `!` null-forgiving is now unsafe. EF translates `p.Lease.PropertyId == propertyId` on an optional nav to a LEFT JOIN where a null lease yields null ≠ propertyId (row excluded) — which is correct for the current Rent/LateFee/Utility predicates (those types always have a lease). Change `p.Lease!.PropertyId` → `p.Lease != null && p.Lease.PropertyId == propertyId` only where a `!` would NRE in LINQ-to-objects test paths; leave pure EF-translated expressions (they're fine). Compile the solution and run the existing report tests to confirm no regression.

- [ ] **Step 2 — PaymentService guard.** In `PaymentService.CreateAsync` (`:240`) wrap the lease guard: `if (request.LeaseId is { } leaseId && !await _db.EnsureLeaseInPortfolioAsync(portfolioId, leaseId, ct)) return null;`. Leave the rest (auto-fill `PaidDate` from `DueDate` for Paid rows) intact.

- [ ] **Step 3 — DTO.** In `PaymentDtos.cs:114` change `CreatePaymentRequest.LeaseId` from `[Required] int` to `int?`. (Gap5 adds the "exactly one of LeaseId/ApplicationId" validation; until then the generic create path still expects a lease — document that in a `// gap5 completes the XOR` comment.)

- [ ] **Step 4 — Verify + commit.** `dotnet build` green; existing Payment + report tests pass. `git commit -m "refactor(payments): tolerate null lease in create + report predicates"`.

**Verify:** solution builds; `RentalCommand.Api.Tests` payment + report suites green.

---

# Part F2 — Generalize the depreciation primitive (foundation; TSK-620; unblocks 4, 6)

**Goal:** a pure Core calculator that depreciates any asset by `(costBasis, inServiceDate, method, recoveryYears, convention)` — not just residential SL 27.5-yr — plus a §1250 recapture helper. No I/O, no UI (a primitive).

### Task F2.1: Depreciation method/convention enums + class-life table

**Files:**
- Create: `RentalCommand.Core/Enums/DepreciationMethod.cs`, `RentalCommand.Core/Enums/DepreciationConvention.cs`
- Create: `RentalCommand.Core/Services/RecoveryClass.cs`
- Test: `RentalCommand.Core.Tests/Services/DepreciationCalculatorTests.cs` (extend)

**Interfaces:**
- Produces:
```csharp
public enum DepreciationMethod { StraightLine = 0, Macrs = 1 }              // HasConversion<int>
public enum DepreciationConvention { MidMonth = 0, HalfYear = 1, MidQuarter = 2 }
public static class RecoveryClass {
    public const decimal ResidentialBuilding = 27.5m;   // §1250 real property
    public const decimal LandImprovement    = 15m;      // fences, paving (MACRS 15-yr)
    public const decimal Appliance          = 5m;       // appliances, carpet, 5-yr personal
    public const decimal Furniture          = 7m;       // 7-yr personal property
}
```

- [ ] **Step 1** — Create the two enums (append-only ordering; `StraightLine = 0` is the default). Create `RecoveryClass` with the constants above.
- [ ] **Step 2** — Test: `RecoveryClass.ResidentialBuilding == 27.5m` etc. (trivial guard against typos). Run → PASS.
- [ ] **Step 3 — Commit.** `feat(core): depreciation method/convention enums + class-life table`.

### Task F2.2: Generalized `AnnualForYear` overload + §1250 recapture helper

**Files:**
- Modify: `RentalCommand.Core/Services/DepreciationCalculator.cs`
- Test: `RentalCommand.Core.Tests/Services/DepreciationCalculatorTests.cs`

**Interfaces:**
- Consumes: `DepreciationMethod`, `DepreciationConvention` (F2.1).
- Produces:
```csharp
// Generic overload — any asset class:
public static DepreciationResult AnnualForYear(
    decimal costBasis, DateTime inServiceDate, DepreciationMethod method,
    decimal recoveryYears, DepreciationConvention convention,
    decimal accumulatedDepreciation, int year);
// §1250 unrecaptured gain = min(totalGain, accumulatedDepreciation), floored at 0:
public static decimal UnrecapturedSec1250Gain(decimal totalGain, decimal accumulatedDepreciation);
```
The existing `AnnualForYear(PropertyDepreciationBasis, year)` is kept and **delegates** to the generic overload with `method=StraightLine, recoveryYears=27.5, convention=MidMonth` so residential building depreciation is byte-for-byte unchanged.

- [ ] **Step 1 — Failing tests.** Add to `DepreciationCalculatorTests.cs`:
```csharp
// SL 5-yr appliance, mid-month, placed 2025-07-01, basis 5000, year 2025:
//   full = 5000/5 = 1000; monthsAfter = 12-7 = 5; (5+0.5)/12 => 1000*5.5/12 = 458.33
[Fact] public void StraightLine_FiveYear_MidMonth_FirstYear() =>
    Assert.Equal(458.33m, DepreciationCalculator.AnnualForYear(5000m, new(2025,7,1), DepreciationMethod.StraightLine, 5m, DepreciationConvention.MidMonth, 0m, 2025).Amount);

// MACRS 5-yr, half-year, year 1 = 20% of basis (200%-DB half-year table):
[Fact] public void Macrs_FiveYear_HalfYear_Year1() =>
    Assert.Equal(2000m, DepreciationCalculator.AnnualForYear(10000m, new(2025,8,1), DepreciationMethod.Macrs, 5m, DepreciationConvention.HalfYear, 0m, 2025).Amount);

// Recapture floor + cap:
[Fact] public void Recapture_IsMinOfGainAndAccumDepr() {
    Assert.Equal(9000m, DepreciationCalculator.UnrecapturedSec1250Gain(40000m, 9000m));   // depr < gain
    Assert.Equal(5000m, DepreciationCalculator.UnrecapturedSec1250Gain(5000m, 9000m));    // gain < depr
    Assert.Equal(0m,    DepreciationCalculator.UnrecapturedSec1250Gain(-3000m, 9000m));   // loss => 0
}

// Existing residential path unchanged (regression):
[Fact] public void Residential_Delegation_Unchanged() {
    var basis = new PropertyDepreciationBasis(300000m, 60000m, new(2020,3,1), null, 0m);
    Assert.Equal(DepreciationCalculator.AnnualForYear(basis, 2021).Amount,
                 DepreciationCalculator.AnnualForYear(240000m, new(2020,3,1), DepreciationMethod.StraightLine, 27.5m, DepreciationConvention.MidMonth, 0m, 2021).Amount);
}
```
Run → FAIL (overload missing).

- [ ] **Step 2 — Implement the generic overload.** In `DepreciationCalculator.cs`:
  - **StraightLine + MidMonth:** existing math — `full = costBasis/recoveryYears`; in-service year `monthsInService = (12 - inServiceDate.Month) + 0.5m`, else 12; `computed = Round(full * monthsInService / 12m)`; cap at `remaining = max(0, costBasis - accumulatedDepreciation)`. Flag `IsFirstYearEstimate` in the in-service year.
  - **StraightLine + HalfYear:** in-service year and the year after `recoveryYears` both get a half-year; interior years full. (`monthsInService = 6` in-service year.)
  - **MACRS (200%-declining-balance with half-year, switching to SL) for 5/7/15-yr:** use the standard published percentage tables (5-yr HY: 20/32/19.2/11.52/11.52/5.76; 7-yr HY: 14.29/24.49/17.49/12.49/8.93/8.92/8.93/4.46; 15-yr HY: 5/9.5/8.55/...). Compute the year index `n = year - inServiceDate.Year` (0-based), look up the table row, `computed = Round(costBasis * pct[n])`, cap at remaining. Keep it a small `static readonly decimal[]` per class keyed by `recoveryYears`. `IsFirstYearEstimate` = true when `n == 0`.
  - Round via the existing private `Round`. Both overloads share the cap logic.
- [ ] **Step 3 — Delegate the existing overload.** Rewrite `AnnualForYear(PropertyDepreciationBasis basis, int year)` to compute `buildingBasis = PurchasePrice - (LandValue ?? 0)`, honor the manual override exactly as today, else call the generic overload with `StraightLine, 27.5, MidMonth`. Manual-override + null-basis behavior must stay identical (the regression test guards it).
- [ ] **Step 4 — Run tests → PASS.** `dotnet test --filter DepreciationCalculatorTests`.
- [ ] **Step 5 — Commit.** `feat(core): generalize depreciation (method/convention/recovery) + §1250 recapture`.

**Verify:** all `DepreciationCalculatorTests` pass incl. the residential regression; `ScheduleEService`/`AccountingService` (which call the property overload) still produce identical figures — run their tests.

---

# Part F3 — Business-tz day-boundary + proration primitive (foundation; TSK-619; unblocks 3)

**Goal:** a pure `ProrationCalculator` + a business-tz "today" helper reused from the shipped clock, and fix the Engine-vs-API day-boundary inconsistency so proration is computed identically on both sides. No UI (primitives).

### Task F3.1: Business-tz "today" helper (reuse the clock's `IAppTimeZoneProvider`)

**Files:**
- Reference (already shipped): `RentalCommand.Core/Time/IAppTimeZoneProvider.cs` (has `BusinessTimeZone`)
- Create: `RentalCommand.Core/Time/BusinessClockExtensions.cs`
- Test: `RentalCommand.Core.Tests/Time/BusinessClockExtensionsTests.cs`

**Interfaces:**
- Produces: `public static DateOnly TodayInBusiness(this IAppTimeZoneProvider tz, TimeProvider clock)` → `DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(clock.GetUtcNow().UtcDateTime, tz.BusinessTimeZone))`. One seam for "what day is it for this landlord," used by RentChargeService (already uses `_tz`) and LeaseService (currently uses raw `UtcNow.Date` — the bug F3.3 fixes).

- [ ] **Step 1** — Create the extension. **Do not** add a new tz provider — the clock's `IAppTimeZoneProvider` (TSK-615 Phase A) is the single seam.
- [ ] **Step 2** — Test with a fake `TimeProvider` at `2025-01-01T02:00Z` + `America/New_York` → `TodayInBusiness` = `2024-12-31` (UTC-5 rolls back a day). Run → PASS.
- [ ] **Step 3 — Commit.** `feat(core): business-tz TodayInBusiness helper on IAppTimeZoneProvider`.

### Task F3.2: `ProrationCalculator` + `ProrationConvention`

**Files:**
- Create: `RentalCommand.Core/Enums/ProrationConvention.cs`, `RentalCommand.Core/Services/ProrationCalculator.cs`
- Test: `RentalCommand.Core.Tests/Services/ProrationCalculatorTests.cs`

**Interfaces:**
- Produces:
```csharp
public enum ProrationConvention { ActualDays = 0, ThirtyDay = 1 }
public static class ProrationCalculator {
    // Rent owed for the inclusive occupied span [periodStart, periodEnd] within its calendar month.
    // ActualDays: monthlyRent * occupiedDays / DaysInMonth(month)
    // ThirtyDay:  monthlyRent * min(occupiedDays, 30) / 30
    public static decimal Prorate(decimal monthlyRent, DateTime periodStart, DateTime periodEnd, ProrationConvention convention);
}
```

- [ ] **Step 1 — Failing test (the corpus cent-check).** L16 moves in 2025-03-15, rent $825/mo, actual-days:
```csharp
[Fact] public void ActualDays_L16_HalfMarch() =>
    Assert.Equal(452.42m, ProrationCalculator.Prorate(825m, new(2025,3,15), new(2025,3,31), ProrationConvention.ActualDays));
    // 825 * 17 / 31 = 452.41935… => 452.42  (matches events.csv E01118)
[Fact] public void ThirtyDay_HalfMonth() =>
    Assert.Equal(412.50m, ProrationCalculator.Prorate(825m, new(2025,3,16), new(2025,3,31), ProrationConvention.ThirtyDay)); // 16 days => 825*16/30
```
Run → FAIL.
- [ ] **Step 2 — Implement.** `occupiedDays = (periodEnd.Date - periodStart.Date).Days + 1` (inclusive); `daysInMonth = DateTime.DaysInMonth(periodStart.Year, periodStart.Month)`; round to cents `AwayFromZero`. Guard `monthlyRent <= 0` or inverted span → 0.
- [ ] **Step 3 — Run → PASS.** Confirm 452.42 exactly.
- [ ] **Step 4 — Commit.** `feat(core): ProrationCalculator (actual-days + 30-day)`.

### Task F3.3: Fix the Engine-vs-API day-boundary inconsistency

**Files:**
- Modify: `RentalCommand.Api/Services/Domain/LeaseService.cs:309` (uses raw `_timeProvider.UtcNow().Date`)
- Reference (correct side): `RentalCommand.Engine/Services/RentChargeService.cs:54–57` (already business-tz)

**Interfaces:**
- Consumes: `IAppTimeZoneProvider.TodayInBusiness` (F3.1).
- Produces: `LeaseService.EnsureRentChargesThroughTodayAsync` computes "today" in the business zone, matching RentChargeService, so create-time and worker-time period sets never diverge.

- [ ] **Step 1** — Inject `IAppTimeZoneProvider _tz` into `LeaseService` (constructor + DI already resolves it). Change `:309` from `_timeProvider.UtcNow().Date` to `_tz.TodayInBusiness(_timeProvider).ToDateTime(TimeOnly.MinValue)` (or pass the `DateOnly` through if `GetDuePeriods` is adjusted in gap3).
- [ ] **Step 2 — Verify + commit.** Build green; existing lease tests pass. `git commit -m "fix(leases): compute rent-charge 'today' in business tz (match Engine)"`.

**Verify:** create a lease near a UTC/business-tz day boundary and confirm the generated rent periods match what the Engine worker would generate for the same instant.

---

# Part F4 — CSV import seam (foundation; TSK-617; unblocks 1)

**Goal:** widen the import pipeline to accept new entity types + add a lease reference resolver + dedup-result fields. The actual payment/expense/loan importers land in gap1.

### Task F4.1: Widen `SupportedEntityTypes` + dispatch + dedup-result DTO + lease resolver

**Files:**
- Modify: `RentalCommand.Api/Services/Import/ICsvImportService.cs:15` (SupportedEntityTypes), the result DTOs
- Modify: `RentalCommand.Api/Services/Import/CsvImportService.cs` (Canonicalize, column arrays 20–22, dispatch 82–93, add `ResolveLeaseReferenceAsync`)
- Test: `RentalCommand.Api.Tests` — resolver + dedup-DTO tests

**Interfaces:**
- Produces: `SupportedEntityTypes` includes `"Payment","Expense","Loan"`; `CsvImportResult` gains `int DuplicateRows`; `CsvImportRowResult` gains `bool IsDuplicate` + `string? SkipReason`; `Task<(int? id, string? error)> ResolveLeaseReferenceAsync(int portfolioId, string? leaseNumber, string? propertyName, string? unitNumber, CancellationToken ct)` mirroring the Unit→Property `Take(2)` ambiguity pattern (`CsvImportService.cs:237–259`). Consumed by gap1's `TryImport*` methods.

- [ ] **Step 1** — Add `"Payment","Expense","Loan"` to `SupportedEntityTypes` + `Canonicalize` synonyms ("payments","rent"→Payment, etc.). Add the three column-header arrays (payment: `leaseNumber,propertyName,unitNumber,paymentType,amount,paidDate,method,externalReference,notes`; expense: `propertyName,category,description,amount,incurredAt,paidAt,notes`; loan: `propertyName,lender,originalAmount,currentBalance,annualInterestRatePct,termMonths,startDate,dayOfMonthDue,monthlyPrincipalInterest,monthlyEscrow`).
- [ ] **Step 2** — Add empty `case "Payment"/"Expense"/"Loan"` dispatch arms that currently return a "not yet implemented" row error (filled in gap1). Add the `DuplicateRows`/`IsDuplicate`/`SkipReason` fields to the result DTOs (default 0/false/null so existing importers are unaffected).
- [ ] **Step 3** — Implement `ResolveLeaseReferenceAsync`: if `leaseNumber` present, `_db.Leases.Where(l => l.PortfolioId == portfolioId && l.LeaseNumber == leaseNumber).Select(l => l.Id).Take(2)` → 0=not-found, >1=ambiguous, else id. Else resolve `propertyName`(+optional `unitNumber`) → the property's active lease with the same `Take(2)` ambiguity guard.
- [ ] **Step 4 — Tests.** Resolver returns the id for a unique lease number, an ambiguity error for two, a not-found error for none. Result DTO defaults unchanged for tenant/property/unit imports (regression). Run → PASS.
- [ ] **Step 5 — Commit.** `feat(import): widen entity types + lease resolver + dedup result fields`.

**Verify:** existing tenant/property/unit import tests unaffected; resolver unit tests pass.

---

# Gap 5 — Application/screening-fee income (TSK-621) · reference impl of F1

**Definition of Done:** record a real application fee as **income before any lease exists**, attached to a `RentalApplication` (+ optional property). Backend (nullable-lease Payment + ApplicationFee type + income predicate) · **web wired** on the application detail (reachable via Applications nav) · **mobile parity** on the application detail · running-app verify (fee shows in the accounting ledger + on Schedule E) · flags the corpus revision.

### Task G5.1: `PaymentType.ApplicationFee` + `Payment.ApplicationId?`/`PropertyId?`

**Files:**
- Modify: `RentalCommand.Core/Enums/PaymentType.cs` (append `ApplicationFee = 5`)
- Modify: `RentalCommand.Core/Entities/Payment.cs` (add `int? ApplicationId`, `int? PropertyId`, `RentalApplication? Application`, `Property? Property` navs)
- Modify: `RentalCommand.Data/RentalCommandDbContext.cs` (Payment config: FKs + query-filter guard)
- Create: migration `<ts>_AddApplicationFeeToPayment.cs` (Recipe B)

**Interfaces:**
- Consumes: nullable `Payment.LeaseId` (F1).
- Produces: `PaymentType.ApplicationFee`; `Payment.ApplicationId`/`PropertyId` FKs (SetNull); the accounting view maps `5 → 'ApplicationFee'`.

- [ ] **Step 1** — Append `ApplicationFee = 5` at the **end** of `PaymentType` (do not renumber; the view CASE + stored ints depend on 0–4).
- [ ] **Step 2** — Add to `Payment.cs`: `public int? ApplicationId { get; set; }`, `public int? PropertyId { get; set; }`, `public RentalApplication? Application { get; set; }`, `public Property? Property { get; set; }`.
- [ ] **Step 3** — In the Payment config block, copy the `ScreeningResult`/`AdverseActionNotice` ApplicationId FK pattern (`RentalCommandDbContext.cs:693–696`): `entity.HasOne(e => e.Application).WithMany().HasForeignKey(e => e.ApplicationId).OnDelete(DeleteBehavior.SetNull)`; same for `Property`. Add `entity.HasIndex(e => e.ApplicationId)`. Update the Payment query filter (F1.1 already made it `e.Lease == null || ...`) — no Application guard needed since ApplicationId is optional and not soft-delete-joined.
- [ ] **Step 4** — Migration: `AddColumn ApplicationId`, `PropertyId` (nullable int) + FKs + index; then `migrationBuilder.Sql` recreate `vw_accounting_transactions` adding `WHEN 5 THEN 'ApplicationFee'` to the Payments `CASE p."PaymentType"` (else it renders `'5'`). `Down` recreates the F1.2 view.
- [ ] **Step 5 — Verify + commit.** Migration applies; a `PaymentType.ApplicationFee` row shows `'ApplicationFee'` in the view. `git commit -m "feat(payments): ApplicationFee type + application/property links"`.

### Task G5.2: Lease-less create path + `RecordApplicationFee` endpoint

**Files:**
- Modify: `RentalCommand.Api/DTOs/PaymentDtos.cs` (`CreatePaymentRequest`: `ApplicationId?`, `PropertyId?`)
- Modify: `RentalCommand.Api/Services/Domain/PaymentService.cs:237` (XOR validation + guards)
- Modify: `RentalCommand.Api/Services/Domain/PortfolioScopeGuards.cs` (add `EnsureApplicationInPortfolioAsync`)
- Modify: `RentalCommand.Api/Controllers/ApplicationsController.cs` (add `POST {id}/fee`)
- Modify: `RentalCommand.Api/DTOs/ApplicationDtos.cs` (add `RecordApplicationFeeRequest { decimal Amount; string? Method; DateTime? PaidDate; }`)

**Interfaces:**
- Consumes: F1.3 conditional lease guard.
- Produces: `POST /api/v1/applications/{id}/fee` → creates a Paid `Payment { PaymentType=ApplicationFee, ApplicationId=id, PropertyId=application.PropertyId, LeaseId=null }`; `PaymentService.CreateAsync` enforces "exactly one of LeaseId / ApplicationId."

- [ ] **Step 1** — Add `EnsureApplicationInPortfolioAsync(this RentalCommandDbContext db, int portfolioId, int applicationId, CancellationToken ct)` to `PortfolioScopeGuards` (copy `EnsureLeaseInPortfolioAsync`).
- [ ] **Step 2** — `CreatePaymentRequest`: add `int? ApplicationId`, `int? PropertyId`. In `PaymentService.CreateAsync`: require exactly one of `LeaseId`/`ApplicationId` (else return a validation failure); guard whichever is set + optional `PropertyId`; when `ApplicationId` set default `PaymentType = ApplicationFee`, `PropertyId ??= application.PropertyId`.
- [ ] **Step 3** — `ApplicationsController.RecordApplicationFee(int id, RecordApplicationFeeRequest req)` → builds a `CreatePaymentRequest { ApplicationId=id, PaymentType=ApplicationFee, Status=Paid, Amount=req.Amount, PaidDate=req.PaidDate ?? now, Method=req.Method }` and calls `_paymentService.CreateAsync(GetPortfolioId(), ...)`; null→404. `CreatedAtAction`.
- [ ] **Step 4 — Verify + commit.** `POST /applications/{id}/fee` creates the row; a lease-less `CreatePaymentRequest` without an application is rejected. `git commit -m "feat(applications): record application-fee income (lease-less payment)"`.

### Task G5.3: Income predicates count ApplicationFee (lease-less branch)

**Files:**
- Modify: `RentalCommand.Api/Services/Domain/ReportsService.cs` (`GetCashFlowAsync` 588–594, `GetTrueCashFlowAsync` 693–701)
- Modify: `RentalCommand.Api/Services/Domain/ScheduleEService.cs:41–56` (income predicate + property scoping)

**Interfaces:**
- Produces: application fees count as income in Cash Flow + Schedule E as **one SQL statement**; property scoping uses `p.LeaseId != null ? p.Lease.PropertyId : p.PropertyId`.

- [ ] **Step 1 — ScheduleE.** Extend the predicate (`:43–45`) to `(p.PaymentType == Rent || LateFee || Utility || p.PaymentType == PaymentType.ApplicationFee)`; change property scoping (`:53`) to the null-lease-safe form above. Keep the `SumAsync` Partial-aware amount. Still one statement.
- [ ] **Step 2 — Cash flow.** Same in `GetCashFlowAsync`/`GetTrueCashFlowAsync`. For the per-property correlated subquery, scope ApplicationFee rows by `p.PropertyId`.
- [ ] **Step 3 — Test.** Integration: create a $50 ApplicationFee on a property, run Schedule E → income += 50 for that property. Run → PASS.
- [ ] **Step 4 — Commit.** `feat(reports): count application-fee income (lease-less) in cash flow + Schedule E`.

**Flag in the PR:** this shifts Schedule E income vs today's `expected/*.json` → **corpus-revision trigger** (SA re-derives). Decision confirmed by corpus (SA-STATE §109 "real application fees as income").

### Task G5.4: Web — "Record application fee" on the application detail (WIRED)

**Files:**
- Modify: `web/src/lib/components/records/ApplicationDetail.svelte` (actions block ~396–411 + a Dialog)
- Modify: `web/src/lib/api/endpoints/applications.ts` (add `recordFee`)
- Modify: `web/src/lib/schemas/index.ts` (add `applicationFeeSchema`)

**Interfaces:**
- Consumes: `POST /applications/{id}/fee` (G5.2).
- Produces: reachable UI — Applications are already in the `rentals` nav group; the action lives on the existing detail page.

- [ ] **Step 1** — `applications.ts`: `recordFee: (id, body) => api.post(\`/applications/${id}/fee\`, body)`.
- [ ] **Step 2** — In `ApplicationDetail.svelte` add a **"Record application fee"** `Button` in the actions row (~line 401) opening a money `Dialog.Root` (copy the deposits Add-deduction block `deposits/[id]/+page.svelte:452–514`): fields Amount, Method (select), Paid date. Submit via `parseForm(applicationFeeSchema, ...)` → `recordFeeMutation` → on success `showSuccess` + close + `invalidateQueries(['application', id])` + `invalidateQueries(['accounting-summary', portfolioId])`.
- [ ] **Step 3 — Verify in running app (`/verify`).** Log in as Tomás, open an application, Record application fee $50 → toast; open `/accounting` ledger → the $50 ApplicationFee row appears; open Schedule E for that property → income up $50.
- [ ] **Step 4 — Commit.** `feat(web): record application fee from application detail`.

### Task G5.5: Mobile parity — application-fee action

**Files:**
- Modify: `mobile/lib/features/applications/application_detail_screen.dart` (action + form sheet)
- Modify: `mobile/lib/features/applications/applications_repository.dart` (add `recordFee`)

**Interfaces:** Consumes `POST /applications/{id}/fee`. The applications detail surface already exists on mobile (Recipe F, action-on-existing-screen variant).

- [ ] **Step 1** — Repo: `Future<void> recordFee(int id, {required double amount, String? method, DateTime? paidDate})` → `_dio.post('/applications/$id/fee', data: {...})` wrapped in the `ApiException` try/catch.
- [ ] **Step 2** — Add a "Record fee" action + a small `showModalBottomSheet` form (amount/method/date) to `application_detail_screen.dart`; on success invalidate the detail provider + show a snackbar.
- [ ] **Step 3 — Verify + commit.** Run the app (or `flutter test` the repo), record a fee. `git commit -m "feat(mobile): record application fee"`.

> **If the orchestrator elects to batch mobile:** replace G5.5 with a logged deferral task `TSK-621-M — mobile application-fee action (deferred)`. Do not silently skip.

---

# Gap 1 — Bulk transaction import (TSK-617) · needs F1 + F4

**Definition of Done:** import CY2023–24 payments/expenses via the real bulk-import path with **natural-key dedup** (re-import is a no-op). Backend importers · **web wired** into the existing import page (new entity types + dedup results) · **mobile deferral** (no import surface) · running-app verify (import a CSV, re-import, see duplicates skipped).

### Task G1.1: `TryImportPaymentAsync` + batched natural-key dedup

**Files:**
- Modify: `RentalCommand.Api/Services/Import/CsvImportService.cs` (Payment arm + `DedupePaymentsAsync`)
- Modify ctor to inject `IPaymentService`
- Test: `RentalCommand.Api.Tests` — dedup skips a re-import

**Interfaces:**
- Consumes: F4.1 resolver + dedup DTO; `IPaymentService.CreateAsync` (F1/gap5).
- Produces: payment rows imported; natural key `(PortfolioId, LeaseId, Amount, PaidDate ?? DueDate, ExternalReference)`; duplicates skipped via **one** bounded query.

- [ ] **Step 1** — `TryImportPaymentAsync(portfolioId, row, dryRun, ct)`: parse row → resolve lease via `ResolveLeaseReferenceAsync` → build `CreatePaymentRequest` → dedup check → if new & `!dryRun` call `_paymentService.CreateAsync`.
- [ ] **Step 2 — Batched dedup (HARD RULE compliant).** Before the row loop, gather the batch's `(amount, effectiveDate)` candidates and run **one** query: `_db.Payments.Where(p => p.PortfolioId == portfolioId && amounts.Contains(p.Amount) && (p.PaidDate ?? p.DueDate) >= minDate && (p.PaidDate ?? p.DueDate) <= maxDate).Select(p => new { p.LeaseId, p.Amount, Eff = p.PaidDate ?? p.DueDate, p.ExternalReference }).ToListAsync(ct)` → build a `HashSet<string>` of natural keys. Per row: O(1) membership → set `IsDuplicate=true, SkipReason="duplicate of existing payment"`, increment `DuplicateRows`, skip create. (One DB round-trip over a bounded window + HashSet membership — not aggregation.)
- [ ] **Step 3 — Test.** Import 3 payment rows (dry-run then commit) → 3 created; re-import the same CSV → 0 created, 3 duplicates. Run → PASS.
- [ ] **Step 4 — Commit.** `feat(import): payment importer with natural-key dedup`.

### Task G1.2: `TryImportExpenseAsync` + dedup

**Files:** `CsvImportService.cs` (Expense arm + `DedupeExpensesAsync`), inject `IExpenseService`. Test.

**Interfaces:** Natural key `(PortfolioId, PropertyId, Amount, IncurredAt, Description)`; property resolved by the existing Unit→Property `Take(2)` name pattern.

- [ ] **Step 1** — `TryImportExpenseAsync`: resolve `propertyName`→propertyId (existing resolver), build `CreateExpenseRequest`, dedup (same one-query+HashSet shape as G1.1 keyed on `(PropertyId, Amount, IncurredAt, Description)`), create when new.
- [ ] **Step 2 — Test + commit.** Re-import is a no-op. `feat(import): expense importer with dedup`.

### Task G1.3: `TryImportLoanAsync` (mortgage → Loan, not raw LoanPayment)

**Files:** `CsvImportService.cs` (Loan arm), inject `ILoanService`. Test.

**Interfaces:** Mortgage import creates a **`Loan`** via `ILoanService.CreateAsync` (DebtServiceService amortizes it later); **no raw `LoanPayment` write path** (would break the `BalanceAfter` chain — locked decision §105). One-off mortgage payments import as `Expense { Category = MortgageInterest }`.

- [ ] **Step 1** — `TryImportLoanAsync`: resolve property, build `CreateLoanRequest` (lender/originalAmount/currentBalance/rate/term/start/dayDue/monthlyPI/monthlyEscrow), create via `ILoanService.CreateAsync`. Dedup on `(PortfolioId, PropertyId, Lender, OriginalAmount, StartDate)`.
- [ ] **Step 2 — Test + commit.** `feat(import): loan importer (amortized via DebtService, no raw LoanPayment)`.

### Task G1.4: Web — new import types + dedup results (WIRED)

**Files:**
- Modify: `web/src/routes/(protected)/import/+page.svelte` (`ENTITY_OPTIONS` 37–65, result UI 435–466)
- Modify: `web/src/lib/api/endpoints/import.ts` (`ImportEntityType`, `ImportResult`/`ImportRowResult`)

**Interfaces:** Consumes the widened `POST /api/v1/import/{entityType}` + dedup result fields.

- [ ] **Step 1** — `import.ts`: add `'payment' | 'expense' | 'loan'` to `ImportEntityType`; add `duplicateRows` to `ImportResult`, `isDuplicate`/`skipReason` to `ImportRowResult`.
- [ ] **Step 2** — `import/+page.svelte`: add three cards to `ENTITY_OPTIONS` (Payment/Expense/Loan with descriptions); in the preview + result cards show "X duplicates skipped" and a per-row "Skipped (duplicate)" `StatusBadge` in `rowsTable`. Template download works via the widened `GetTemplate`.
- [ ] **Step 3 — Verify (`/verify`).** Open `/import` (reachable via Guided Setup / the import route), pick Payment, upload a small CSV → preview shows valid rows → commit → rows created; re-upload → "N duplicates skipped." Confirm the payments appear in `/accounting`.
- [ ] **Step 4 — Commit.** `feat(web): bulk import for payments/expenses/loans with dedup results`.

### Task G1.5: Mobile — explicit deferral

- [ ] Create the deferral task in Notion + the team task list: **`TSK-617-M — Mobile bulk import (DEFERRED)`** with body: "No import/CSV surface exists in `mobile/lib` (confirmed). Bulk import is a desktop/web-first workflow; revisit if mobile onboarding needs it." This satisfies DoD clause 3 (deferral logged, not silent).

**Flag:** gap1 reproduces the same ledger rows the worker-backfill produced → **not** a spine-figure shift, but SA switches the corpus's history-load path to this importer (SA-STATE §100).

---

# Gap 2 — Owner-distribution record (TSK-618)

**Definition of Done:** a real `OwnerDistribution` record (NOT a memo, NOT an Expense) with cash-distributed reconciled against computed net-to-owner. Backend entity/service/endpoint · **web wired** on `/owners-report` (Record distribution + Undistributed + list) · **mobile parity** (first write on the read-only owner-reports surface) · verify.

### Task G2.1: `OwnerDistribution` entity + `DistributionMethod` enum + migration

**Files:**
- Create: `RentalCommand.Core/Entities/OwnerDistribution.cs` (Recipe A), `RentalCommand.Core/Enums/DistributionMethod.cs`
- Modify: `RentalCommand.Data/RentalCommandDbContext.cs` (DbSet + config)
- Create: migration `<ts>_AddOwnerDistribution.cs` (Recipe B + RLS Recipe C)

**Interfaces:**
- Produces:
```csharp
public enum DistributionMethod { Check = 0, Ach = 1, Wire = 2, Cash = 3, Other = 4 }
public class OwnerDistribution : IAuditable, IPortfolioScoped {
    public int Id; public int PortfolioId;
    public int OwnerEntityId;          // FK -> OwnerEntity (Restrict: never orphan a distribution)
    public int? PropertyId;            // optional attribution (SetNull)
    public DateTime Date; public decimal Amount;   // (18,2)
    public DistributionMethod Method; public string? Memo;
    public DateTime CreatedAt, UpdatedAt; public DateTime? DeletedAt;
    public OwnerEntity? OwnerEntity; public Property? Property; public Portfolio? Portfolio;
}
```
Covering index `(PortfolioId, OwnerEntityId, Date)`. FK→OwnerEntity `OnDelete(Restrict)` (a distribution must never be silently orphaned); PropertyId `SetNull`.

- [ ] **Step 1** — Entity + enum (Recipe A). Config block: `Amount.HasPrecision(18,2)`, `Method.HasConversion<int>()`, `Memo.HasMaxLength(500)`, `HasIndex(e => new { e.PortfolioId, e.OwnerEntityId, e.Date })`, query filter, FKs.
- [ ] **Step 2** — DbSet `OwnerDistributions`. Migration (Recipe B) + RLS enrollment (Recipe C) for `OwnerDistributions`.
- [ ] **Step 3 — Verify + commit.** Migration applies up/down; RLS policy present. `feat(owners): OwnerDistribution entity + migration`.

### Task G2.2: `OwnerDistributionService` + controller (Recipe D)

**Files:**
- Create: `RentalCommand.Api/Services/Domain/IOwnerDistributionService.cs` + `OwnerDistributionService.cs`
- Create: `RentalCommand.Api/Controllers/OwnerDistributionsController.cs`
- Create: `RentalCommand.Api/DTOs/OwnerDistributionDtos.cs`
- Modify: `RentalCommand.Api/Extensions/ServiceCollectionExtensions.cs` (register)

**Interfaces:**
- Produces: `GET/POST/PATCH/DELETE /api/v1/owner-distributions` (list filterable by `ownerEntityId`, `year`); `ListForOwnerYearAsync(portfolioId, ownerEntityId, year)` + `SumForOwnerYearAsync(...)` (DB-side) consumed by G2.3.

- [ ] **Step 1** — Service (Recipe D) with `EnsureOwnerEntityInPortfolioAsync` (exists) + `EnsurePropertyInPortfolioAsync` guards. `ListPageAsync` filters by ownerEntityId/year DB-side. Add `SumForOwnerYearAsync` = `.Where(...).SumAsync(d => d.Amount)` (one statement).
- [ ] **Step 2** — Controller (Recipe D) `[Route("api/v1/owner-distributions")] : ManagementControllerBase`. DTOs (Recipe D). Register in `AddDomainServices`.
- [ ] **Step 3 — Verify + commit.** POST a distribution → GET lists it, scoped to portfolio. `feat(owners): owner-distribution CRUD API`.

### Task G2.3: Owner statement gains Distributions + Undistributed

**Files:** Modify `RentalCommand.Api/Services/Domain/OwnerStatementService.cs` (extend `OwnerPropertyNetRow` 189 + the grouping 120–153; add distributions aggregate) + the statement DTO.

**Interfaces:**
- Consumes: `SumForOwnerYearAsync` (G2.2).
- Produces: `OwnerStatementReport` gains `TotalDistributed` + `Undistributed = TotalNetToOwner - TotalDistributed` (both DB-side; two aggregates).

- [ ] **Step 1** — In `GetForOwnerAsync`, after computing `TotalNetToOwner`, add a second DB aggregate `distributed = await _db.OwnerDistributions.Where(d => d.PortfolioId == portfolioId && d.OwnerEntityId == ownerId && d.Date.Year == year).SumAsync(d => d.Amount)`. Set `TotalDistributed`, `Undistributed`. **Never** treat a distribution as an expense (it must not enter `NetToOwner`).
- [ ] **Step 2** — In `ListOwnersWithNetAsync` add distributed per owner via a single grouped aggregate (one statement) if the list view shows it.
- [ ] **Step 3 — Test + commit.** Owner net $10,000; distribute $6,000 → Undistributed $4,000. `feat(owners): distributions + undistributed on owner statement`.

### Task G2.4: Web — Record distribution + Undistributed + list on `/owners-report` (WIRED)

**Files:**
- Modify: `web/src/routes/(protected)/owners-report/+page.svelte` (actions row ~165, grand-total cards ~214, add a list section)
- Create: `web/src/lib/api/endpoints/owner-distributions.ts`
- Modify: `web/src/lib/schemas/index.ts` (`ownerDistributionSchema`)

**Interfaces:** Consumes the G2.2 API + G2.3 statement fields. Reachable: `/owners-report` is linked from the Reports tab + owners nav.

- [ ] **Step 1** — Endpoint module (Recipe E): `ownerDistributions.list({ownerEntityId, year})`, `create`, `delete`.
- [ ] **Step 2** — Add a **"Record distribution"** button in the statement actions row (~165) → money Dialog (copy deposits Add-deduction): Amount, Date, Method, optional Property, Memo → `createDistributionMutation` → invalidate the statement + distributions queries. Add an **"Undistributed"** 5th grand-total card (~214) bound to `report.undistributed`. Add a **Distributions** list section under the property breakdown (copy `PropertyRecurringExpensesSection` grid) with per-row delete.
- [ ] **Step 3 — Verify (`/verify`).** Open `/owners-report`, pick an owner, Record distribution $6,000 → toast; the distribution lists; Undistributed drops by 6,000.
- [ ] **Step 4 — Commit.** `feat(web): record + list owner distributions on owners report`.

### Task G2.5: Mobile parity — distributions on owner reports

**Files:** Modify `mobile/lib/features/owner_reports/` — add a repo write method `POST /owner-distributions` + a "Record distribution" action + list on the owner statement screen (Recipe F, first write on a read-only surface — flag scope).

- [ ] **Step 1** — Repo `createDistribution` + `listDistributions`. **Step 2** — action + form sheet + list on the statement screen; show Undistributed from the statement payload. **Step 3 — verify + commit.** `feat(mobile): owner distributions`.

> **Decision needed:** owner-reports is read-only on mobile today; this adds the first write action there. Default = build (host surface exists). Alternative = logged deferral `TSK-618-M`.

---

# Gap 3 — Rent proration (TSK-619) · uses F3 · shifts the cent-exact spine

**Definition of Done:** the app computes partial-month rent on move-in/out (L16 half-March = 452.42; L05/L20 boundaries). Backend (schedule emits stub periods; both creators prorate; per-portfolio convention) · **web wired** (settings toggle + move-out surface + lease ledger shows the prorated line) · **mobile parity** (lease ledger renders the prorated line) · verify · corpus-revision flag.

### Task G3.1: `GetDuePeriods` emits partial stub periods

**Files:**
- Modify: `RentalCommand.Api/Services/Domain/RentChargeSchedule.cs` (record + emit logic)
- Test: `RentalCommand.Api.Tests` — partial first/last month emitted with correct span

**Interfaces:**
- Produces: `RentChargePeriod(string PeriodKey, DateTime DueDate, DateTime PeriodStart, DateTime PeriodEnd, bool IsPartial)` — the partial first/last months are **emitted** (with the occupied span) instead of dropped at `:38`.

- [ ] **Step 1 — Failing test.** For `leaseStart=2025-03-15, rentDueDay=1`, `GetDuePeriods` returns a March period with `IsPartial=true, PeriodStart=03-15, PeriodEnd=03-31` (today it's dropped). Run → FAIL.
- [ ] **Step 2 — Implement.** Extend the record. For each month cursor compute the occupied span = `[max(leaseStart, monthStart), min(leaseEnd-1day, monthEnd)]`; if the span is a full calendar month → `IsPartial=false, PeriodStart/End = month bounds`; if partial (first or last month) → `IsPartial=true` with the clipped span; emit both (do not drop when `dueDate < start`). Keep `PeriodKey = dueDate.ToString("yyyy-MM")` and the `cutoff`/lead-window gating.
- [ ] **Step 3 — Run → PASS. Commit.** `feat(rent): emit partial-month stub periods for proration`.

### Task G3.2: Both creators prorate partial periods (per-portfolio convention)

**Files:**
- Modify: `RentalCommand.Engine/Services/RentChargeService.cs:138–151` (rent-charge Amount)
- Modify: `RentalCommand.Api/Services/Domain/LeaseService.cs:330–344` (create-time Amount)
- Modify: portfolio settings read for `ProrationConvention` (both call sites)

**Interfaces:**
- Consumes: `ProrationCalculator` (F3.2), the partial stub periods (G3.1), the per-portfolio `prorationConvention` setting (G3.3).
- Produces: partial periods get `Amount = ProrationCalculator.Prorate(lease.MonthlyRent, period.PeriodStart, period.PeriodEnd, convention)`; full periods keep `lease.MonthlyRent`. Idempotent on `(LeaseId, PaymentType, PeriodKey)`.

- [ ] **Step 1** — In both creators, when building the `Payment`, set `Amount = period.IsPartial ? ProrationCalculator.Prorate(lease.MonthlyRent, period.PeriodStart, period.PeriodEnd, convention) : lease.MonthlyRent`. Read `convention` from the portfolio settings (G3.3); default `ActualDays`.
- [ ] **Step 2 — Integration test.** Create L16 (start 03-15, rent 825) with backfill → the March Payment `Amount == 452.42`. Run → PASS.
- [ ] **Step 3 — Commit.** `feat(rent): prorate partial-month charges in both creators`.

### Task G3.3: Per-portfolio `ProrationConvention` setting

**Files:** Modify the portfolio settings read path (both creators) to parse `prorationConvention` from `Portfolio.Settings` JSON; helper in `RentalCommand.Api` (e.g. extend the existing portfolio-settings reader). Default `ActualDays`.

- [ ] **Step 1** — Add a `ProrationConvention` getter over `Portfolio.Settings` JSON (key `"prorationConvention"`, values `"ActualDays"|"ThirtyDay"`). **Step 2** — thread it into G3.2. **Step 3 — commit.** `feat(portfolio): prorationConvention setting`.

### Task G3.4: Move-out proration

**Files:** Modify `RentalCommand.Api/Services/Domain/LeaseService.cs` (terminate/move-out path) + surface on the move-out statement (`SecurityDepositService`/move-out statement generator).

**Interfaces:** On terminate with a `MoveOutDate` mid-month, ensure the final month's Payment is prorated to `[monthStart, MoveOutDate]` (idempotent on that month's `(LeaseId, Rent, PeriodKey)` — if a full charge already exists, adjust its `Amount` down to the prorated figure or write a negative `Rent` credit for the unused days). **Decision:** prorate the existing final-month charge down (single Paid/Scheduled row per month keeps reconciliation clean) rather than a separate credit row.

- [ ] **Step 1** — In the terminate path, if `MoveOutDate` falls mid-month and a full-month charge exists for that `PeriodKey`, set its `Amount = ProrationCalculator.Prorate(...[monthStart, MoveOutDate])`. **Step 2** — include the prorated final month on the move-out statement. **Step 3 — test (L05 05-31 out) + commit.** `feat(rent): prorate final month on move-out`.

### Task G3.5: Web — settings toggle + move-out + ledger surface (WIRED)

**Files:**
- Modify: `web/src/routes/(protected)/settings/+page.svelte` (`MANAGED_KEYS` 318, `settingsModel` 322–332, load/serialize, Everyday-defaults block ~733)
- Modify: `web/src/lib/components/records/LeaseDetail.svelte` (show the prorated partial line on the ledger; terminate confirm shows the prorated final amount)

- [ ] **Step 1** — Settings: add `prorationConvention` to `MANAGED_KEYS` + `settingsModel` (mirror `rentCollectionDay` at 351–353 / 388–389), render a `Select` (Actual days / 30-day) in Everyday defaults after line 733. Saved via the existing "Save portfolio" mutation.
- [ ] **Step 2** — `LeaseDetail`: the ledger already renders payment rows; confirm the prorated partial-month row displays its clipped span/amount (add a "prorated" badge when `amount != monthlyRent`). Terminate/move-out confirm shows the computed final prorated amount.
- [ ] **Step 3 — Verify (`/verify`).** Set convention = Actual days; scan/create a lease starting mid-month → the ledger's first month shows the prorated amount; terminate a lease mid-month → final month prorated on the move-out statement.
- [ ] **Step 4 — Commit.** `feat(web): proration convention setting + prorated line on lease ledger`.

### Task G3.6: Mobile parity — prorated line in lease ledger

**Files:** Modify `mobile/lib/features/leases/lease_ledger_view.dart` — the ledger reads `GET /leases/{id}/ledger` (server-driven), so the prorated amount flows automatically; add a "prorated" chip when a row's amount ≠ monthly rent.

- [ ] **Step 1** — Render the chip. **Step 2 — verify** a mid-month lease shows the prorated first month in the mobile ledger. **Step 3 — commit.** `feat(mobile): show prorated line in lease ledger`.

**Flag:** L16=452.42, L05/L20 boundaries move to app-computed → **corpus-revision trigger** (SA-STATE §102–103).

---

# Gap 4 — Capital-improvement depreciation (TSK-620) · uses F2 · feeds Gap 6

**Definition of Done:** a capital improvement depreciates on its own schedule (NOT expensed as Repairs). Backend (`CapitalAsset` entity + capitalize-expense + Schedule E/P&L integration) · **web wired** ("Capitalize this expense" + a capital-assets section on the property) · **mobile parity** (property Tax Basis surface) · verify (roof ≈ $135 first-year) · corpus flag.

### Task G4.1: `CapitalAsset` entity + `Expense.CapitalizedAssetId` + migration

**Files:**
- Create: `RentalCommand.Core/Entities/CapitalAsset.cs` (Recipe A)
- Modify: `RentalCommand.Core/Entities/Expense.cs` (add `int? CapitalizedAssetId`, `CapitalAsset? CapitalizedAsset` nav)
- Modify: `RentalCommand.Data/RentalCommandDbContext.cs` (DbSet + both configs)
- Create: migration `<ts>_AddCapitalAsset.cs` (Recipe B + RLS Recipe C)

**Interfaces:**
- Consumes: `DepreciationMethod`, `DepreciationConvention` (F2).
- Produces:
```csharp
public class CapitalAsset : IAuditable, IPortfolioScoped {
    public int Id; public int PortfolioId;
    public int PropertyId; public int? UnitId; public int? SourceExpenseId;
    public string Description; public decimal CostBasis;   // (18,2)
    public DateTime InServiceDate;
    public DepreciationMethod Method; public decimal RecoveryYears;
    public DepreciationConvention Convention;
    public decimal AccumulatedDepreciation;                // (18,2), manually maintained like Property
    public DateTime? DisposedOnDate;
    public DateTime CreatedAt, UpdatedAt; public DateTime? DeletedAt;
    public Property? Property; public Unit? Unit; public Expense? SourceExpense; public Portfolio? Portfolio;
}
```
Covering index `(PortfolioId, PropertyId, InServiceDate)`. FK→Property `Cascade`, Unit/SourceExpense `SetNull`. `Expense.CapitalizedAssetId` FK→CapitalAsset `SetNull`.

- [ ] **Step 1** — Entity (Recipe A) + config. Add `Expense.CapitalizedAssetId` + nav + FK config.
- [ ] **Step 2** — DbSet `CapitalAssets`. Migration: create `CapitalAssets` + add `CapitalizedAssetId` column to `Expenses` + FKs + covering index + RLS enrollment for `CapitalAssets`.
- [ ] **Step 3 — Verify + commit.** Migration up/down; RLS present. `feat(depreciation): CapitalAsset entity + expense link + migration`.

### Task G4.2: `CapitalAssetService` + `CapitalizeExpense` (Recipe D)

**Files:**
- Create: `RentalCommand.Api/Services/Domain/ICapitalAssetService.cs` + `CapitalAssetService.cs`
- Create: `RentalCommand.Api/Controllers/CapitalAssetsController.cs`
- Modify: `RentalCommand.Api/Controllers/ExpenseController.cs` (add `POST {id}/capitalize`)
- Create/modify: `RentalCommand.Api/DTOs/CapitalAssetDtos.cs`, `ExpenseDtos.cs` (`CapitalizeExpenseRequest`)
- Modify: `ServiceCollectionExtensions.cs` (register)

**Interfaces:**
- Produces: `GET/POST/PATCH/DELETE /api/v1/capital-assets` (list by propertyId); `POST /api/v1/expenses/{id}/capitalize { InServiceDate, Method, RecoveryYears, Convention }` → creates a `CapitalAsset` from the expense, sets `Expense.CapitalizedAssetId`, and marks the expense excluded from deductible; `CapitalAssetService.AnnualDepreciationForYear(asset, year)` (delegates to F2's generic overload).

- [ ] **Step 1** — Service (Recipe D). `CapitalizeExpenseAsync(portfolioId, expenseId, req)`: guard expense in-portfolio; create `CapitalAsset { PropertyId = expense.PropertyId, UnitId = expense.UnitId, SourceExpenseId = expenseId, CostBasis = expense.Amount, InServiceDate = req.InServiceDate, Method, RecoveryYears, Convention, AccumulatedDepreciation = 0 }`; set `expense.CapitalizedAssetId`; save in one transaction; broadcast.
- [ ] **Step 2** — Controller + `ExpenseController.Capitalize`. DTOs. Add `EnsureCapitalAssetInPortfolioAsync` guard. Register service.
- [ ] **Step 3 — Verify + commit.** Capitalize a $9,900 expense → a CapitalAsset row exists; the expense now has `CapitalizedAssetId`. `feat(depreciation): capital-asset CRUD + capitalize-expense`.

### Task G4.3: Schedule E depr line + P&L excludes capitalized

**Files:** Modify `RentalCommand.Api/Services/Domain/ScheduleEService.cs:104–149` (add Σ capital-asset annual to the depreciation line) + the deductible-expense predicate (exclude `CapitalizedAssetId != null`).

**Interfaces:**
- Consumes: F2 generic overload; `CapitalAsset` (G4.1).
- Produces: Schedule E depreciation = property building depr + Σ per-property capital-asset annual (bounded per-property loop, same shape as the existing property-basis loop). Deductible expenses exclude capitalized ones.

- [ ] **Step 1** — After the property-basis depreciation loop, load per-property capital assets `_db.CapitalAssets.Where(c => c.PortfolioId == portfolioId && (propertyFilter) && c.InServiceDate.Year <= year && c.DisposedOnDate == null).Select(small DTO)` (one query, bounded) and add `DepreciationCalculator.AnnualForYear(c.CostBasis, c.InServiceDate, c.Method, c.RecoveryYears, c.Convention, c.AccumulatedDepreciation, year)` to that property's depreciation. Fold into `totalDepreciation` (:149).
- [ ] **Step 2** — In the deductible-expenses sum, add `&& e.CapitalizedAssetId == null` so a capitalized improvement is not double-counted as a repair.
- [ ] **Step 3 — Test.** Capitalize a $9,900 roof @ 2025-08-01, SL 27.5-yr mid-month → that property's 2025 depreciation increases by ≈ $135 (`9900/27.5 = 360; monthsAfter Aug = 4; (4+0.5)/12 => 360*4.5/12 = 135.00`); the $9,900 no longer appears in repairs. Run → PASS.
- [ ] **Step 4 — Commit.** `feat(reports): capital-asset depreciation on Schedule E; exclude capitalized from deductible`.

### Task G4.4: AccountingService depreciation parity

**Files:** Modify `RentalCommand.Api/Services/Domain/AccountingService.cs:994–1021` — mirror G4.3 (add capital-asset depreciation to the accounting depreciation figure) so the accounting summary and Schedule E agree.

- [ ] **Step 1** — Same per-property capital-asset load + F2 calc added to the depreciation total. **Step 2 — verify + commit.** `feat(accounting): include capital-asset depreciation in summary`.

### Task G4.5: Web — "Capitalize this expense" (WIRED)

**Files:** Modify `web/src/lib/components/records/ExpenseDetail.svelte` (actions ~343–353 + a Dialog); modify `web/src/lib/api/endpoints/expenses.ts` (`capitalize`); `web/src/lib/schemas/index.ts` (`capitalizeExpenseSchema`).

- [ ] **Step 1** — `expenses.ts`: `capitalize: (id, body) => api.post(\`/expenses/${id}/capitalize\`, body)`.
- [ ] **Step 2** — Add a **"Capitalize this expense"** button in the ExpenseDetail header actions (~350) → Dialog: In-service date, Method (SL/MACRS), Recovery years (select 5/7/15/27.5), Convention → `capitalizeMutation` → invalidate `['expense', id]`, `['expenses', portfolioId]`, `['accounting-summary', portfolioId]`, `['capital-assets', propertyId]`. Show a "Capitalized" badge when `expense.capitalizedAssetId` is set (hide the button then).
- [ ] **Step 3 — Verify (`/verify`).** Open an expense (reachable via `/accounting` → expense detail), Capitalize it → badge; Schedule E depreciation for the property rises; the expense drops from repairs.
- [ ] **Step 4 — Commit.** `feat(web): capitalize expense action on expense detail`.

### Task G4.6: Web — capital-assets section on the property (WIRED)

**Files:** Create `web/src/lib/components/property/PropertyCapitalAssetsSection.svelte` (copy `PropertyRecurringExpensesSection.svelte`); mount in `web/src/routes/(protected)/properties/[id]/+page.svelte` after line 679; create `web/src/lib/api/endpoints/capital-assets.ts`.

- [ ] **Step 1** — Endpoint module `capital-assets.ts` (list by propertyId, create/update/delete). **Step 2** — Section component: `DataGrid` of assets (Description, Cost basis, In-service, Method, Accumulated depr, this-year depr) + an Add dialog + delete. Mount `<PropertyCapitalAssetsSection propertyId={id} />`.
- [ ] **Step 3 — Verify.** Property detail shows the capitalized roof in the Capital assets section. **Step 4 — commit.** `feat(web): capital-assets section on property detail`.

### Task G4.7: Mobile parity — capital assets on property detail

**Files:** Modify `mobile/lib/features/properties/` — repo methods `GET/POST /capital-assets`, a capital-assets list on `property_detail_screen.dart` (near the existing Tax Basis section), and "Capitalize" on the expense detail (`features/money/`). Recipe F.

- [ ] **Step 1** — Repo + providers. **Step 2** — list widget on property detail + capitalize action on expense detail. **Step 3 — verify + commit.** `feat(mobile): capital assets + capitalize expense`.

**Flag:** new depreciation lines + expense reclassified → **corpus-revision trigger** (SA-STATE §104). Placeholder asset = P09 roof $9,900 @ 2025-08-01 (confirm at revision).

---

# Gap 6 — Property sale/disposition + gain/loss (TSK-622) · MUST follow Gap 4

**Definition of Done:** sell a property mid/late CY2025 with sale-year depreciation + gain/loss + §1250 recapture, and exclude the sold property from operating reports as-of the sale date. Backend (`PropertyDisposition` + `PropertyStatus.Sold`/`SoldOnDate` + sold-exclusion predicate + year-end section) · **web wired** (Record sale + year-end disposition section) · **mobile parity** (property detail) · verify · corpus flag.

### Task G6.1: `PropertyStatus.Sold` + `Property.SoldOnDate` + `PropertyDisposition` entity

**Files:**
- Modify: `RentalCommand.Core/Enums/PropertyStatus.cs` (append `Sold = 3`)
- Modify: `RentalCommand.Core/Entities/Property.cs` (add `DateTime? SoldOnDate`)
- Create: `RentalCommand.Core/Entities/PropertyDisposition.cs` (Recipe A)
- Modify: `RentalCommand.Data/RentalCommandDbContext.cs` (DbSet + config)
- Create: migration `<ts>_AddPropertyDisposition.cs` (Recipe B + RLS Recipe C)

**Interfaces:**
- Produces:
```csharp
public class PropertyDisposition : IAuditable, IPortfolioScoped {
    public int Id; public int PortfolioId; public int PropertyId;
    public DateTime SaleDate; public decimal SalePrice, SellingCosts;       // (18,2)
    public decimal AccumulatedDepreciationAtSale, AdjustedBasis;            // (18,2) computed
    public decimal RealizedGainLoss, UnrecapturedSec1250Gain, Sec1231Gain; // (18,2) computed
    public string? Memo; public DateTime CreatedAt, UpdatedAt; public DateTime? DeletedAt;
    public Property? Property; public Portfolio? Portfolio;
}
```
Covering index `(PortfolioId, PropertyId)`. FK→Property `Restrict`.

- [ ] **Step 1** — Append `Sold = 3`; add `Property.SoldOnDate`. Entity + config (Recipe A).
- [ ] **Step 2** — DbSet + migration (add `SoldOnDate` to Properties, create `PropertyDispositions`) + RLS.
- [ ] **Step 3 — Verify + commit.** `feat(disposition): PropertyDisposition + PropertyStatus.Sold + migration`.

### Task G6.2: `PropertyDispositionService.RecordSale` (needs Gap 4 for the accumulated-depr Σ)

**Files:**
- Create: `RentalCommand.Api/Services/Domain/IPropertyDispositionService.cs` + `PropertyDispositionService.cs`
- Modify: `RentalCommand.Api/Controllers/PropertyController.cs` (add `POST {id}/disposition`) or a new `DispositionsController`
- Create: `RentalCommand.Api/DTOs/PropertyDispositionDtos.cs`
- Modify: `ServiceCollectionExtensions.cs`

**Interfaces:**
- Consumes: F2 `UnrecapturedSec1250Gain` + generic depreciation; gap4 `CapitalAsset`.
- Produces: `POST /api/v1/properties/{id}/disposition { SaleDate, SalePrice, SellingCosts }` → computes and persists the disposition, sets `Property.Status = Sold`, `Property.SoldOnDate = SaleDate`.

- [ ] **Step 1 — Compute (all DB-side except the F2 math over a bounded set):**
  - `capitalDepr = await _db.CapitalAssets.Where(c => c.PortfolioId==pid && c.PropertyId==id).SumAsync(c => c.AccumulatedDepreciation)` (one aggregate).
  - `AccumulatedDepreciationAtSale = property.AccumulatedDepreciation + capitalDepr` (+ optional sale-year partial depreciation via F2 up to `SaleDate`).
  - `AdjustedBasis = (property.PurchasePrice ?? 0) - AccumulatedDepreciationAtSale`.
  - `RealizedGainLoss = SalePrice - SellingCosts - AdjustedBasis`.
  - `UnrecapturedSec1250Gain = DepreciationCalculator.UnrecapturedSec1250Gain(RealizedGainLoss, AccumulatedDepreciationAtSale)`.
  - `Sec1231Gain = RealizedGainLoss - UnrecapturedSec1250Gain`.
- [ ] **Step 2** — Persist the disposition + flip the property status in one transaction; broadcast. Controller + DTOs + DI.
- [ ] **Step 3 — Test.** Sell P08 (paid-off) @ $135,000, selling costs $8,000; accumulated depr $X → assert gain/loss + §1250 = min(gain, accumDepr). Run → PASS.
- [ ] **Step 4 — Commit.** `feat(disposition): record sale with gain/loss + §1250 recapture`.

### Task G6.3: As-of-date sold-exclusion predicate everywhere (the "L" part)

**Files:** Modify operating-report property-sets to exclude sold-as-of-range-end:
- `RentalCommand.Api/Services/Domain/ReportsService.cs` (`GetTrueCashFlowAsync` 682, `GetPropertyProfitAndLossAsync` 989, `GetOccupancyAsync` 1069, and any other operating property-set)
- `RentalCommand.Api/Services/Domain/OwnerStatementService.cs:37`
- Dashboard property-set (Portfolio dashboard service)

**Interfaces:**
- Produces: a reusable predicate `p.SoldOnDate == null || p.SoldOnDate > rangeEnd` added to each operating property-set (still one SQL statement each). Historical/tax reports that must include the sale year (Schedule E, year-end) do **not** exclude it — they report the sale.

- [ ] **Step 1** — Add the predicate to each operating property `.Where(...)`. **Do not** add it to Schedule E income/depreciation for the sale year or to the year-end disposition section (those must include the sold property through its sale). Confirm each query remains a single statement (no new client-side filtering).
- [ ] **Step 2 — Test.** A property sold 2025-06-30 is absent from NOI/occupancy for a range ending 2025-12-31 but present for a range ending 2025-03-31, and still appears in the sale-year Schedule E. Run → PASS.
- [ ] **Step 3 — Commit.** `feat(reports): exclude sold properties from operating reports as-of sale date`.

### Task G6.4: Year-end disposition section (replace the stub)

**Files:** Modify `RentalCommand.Api/Services/Domain/ReportsService.cs:803` (replace the "NOT computed" note with a computed disposition section) + the `YearEndView` DTO.

- [ ] **Step 1** — In `GetYearEndAsync`, load the year's `PropertyDispositions` (`SaleDate.Year == year`) and project a disposition section (per-property: sale price, adjusted basis, gain/loss, §1250, §1231). Remove the stub string at :803. **Step 2 — commit.** `feat(reports): year-end disposition section`.

### Task G6.5: Web — Record sale on the property + sold badge (WIRED)

**Files:** Create `web/src/lib/components/property/PropertyDispositionSection.svelte` (copy a section) + mount in `properties/[id]/+page.svelte` after line 679; modify `web/src/lib/api/endpoints/properties.ts` (`recordSale`); schema `propertySaleSchema`.

- [ ] **Step 1** — `properties.ts`: `recordSale: (id, body) => api.post(\`/properties/${id}/disposition\`, body)`. **Step 2** — Section: a "Record sale" button (or a `DetailCard` when already sold) → Dialog (Sale date, Sale price, Selling costs) → on success show computed Gain/loss + §1250; add a **Sold** status badge to the property header (`properties/[id]/+page.svelte`). Invalidate `['property', id]` + reports.
- [ ] **Step 3 — Verify (`/verify`).** Record a sale on a property → gain/loss + §1250 render; property shows Sold; it drops out of NOI/occupancy for the full-year range. **Step 4 — commit.** `feat(web): record property sale + disposition display`.

### Task G6.6: Web — year-end disposition section (WIRED)

**Files:** Modify `web/src/routes/(protected)/accounting/year-end/+page.svelte` (add a 4th `<section>` after ~line 218) + the `YearEndView` type in `web/src/lib/types`.

- [ ] **Step 1** — Add a **Dispositions** `<section>` (copy the Cash flow section table pattern) bound to `view.dispositions`. **Step 2 — verify** the sold property's gain/loss + §1250 shows on `/accounting/year-end` (reachable via the Reports tab). **Step 3 — commit.** `feat(web): year-end disposition section`.

### Task G6.7: Mobile parity — record sale on property detail

**Files:** Modify `mobile/lib/features/properties/property_detail_screen.dart` — repo `recordSale` (`POST /properties/{id}/disposition`) + a "Record sale" action + gain/loss display + a Sold badge. Recipe F.

- [ ] **Step 1** — Repo + action + form sheet. **Step 2 — verify + commit.** `feat(mobile): record property sale`.

**Flag:** net-new off-spine sale → **corpus-revision trigger** (SA-STATE §106). Placeholder = P08 @ $135k on 2025-10-31 (confirm at revision). **Must build after Gap 4** (accumulated-depr Σ over CapitalAssets).

---

# Gap 7 — First-class eviction (TSK-623) · independent · reworks the L14 arc

**Definition of Done:** escalate a delinquency into a real eviction record with a status timeline; resolving-with-possession terminates the lease; delinquency report annotates leases with an active eviction. Backend (`Eviction` + `EvictionStatusEvent`) · **web wired** (new `/evictions` nav + list/detail + "Start eviction" from past-due) · **mobile parity** (evictions screen) · verify · corpus flag.

### Task G7.1: `Eviction` + `EvictionStatusEvent` entities + enums + migration

**Files:**
- Create: `RentalCommand.Core/Entities/Eviction.cs`, `RentalCommand.Core/Entities/EvictionStatusEvent.cs` (copy `WorkOrder.cs` + `WorkOrderStatusEvent.cs`)
- Create: `RentalCommand.Core/Enums/EvictionStatus.cs`, `RentalCommand.Core/Enums/EvictionOutcome.cs`
- Modify: `RentalCommand.Data/RentalCommandDbContext.cs` (2 DbSets + 2 config blocks — copy `1266–1333`)
- Create: migration `<ts>_AddEviction.cs` (copy `AddWorkOrderStatusEvents.cs` + RLS Recipe C)

**Interfaces:**
- Produces:
```csharp
public enum EvictionStatus { NoticeToQuit = 0, Filed = 1, Hearing = 2, Judgment = 3, Writ = 4, Resolved = 5, Dismissed = 6 }
public enum EvictionOutcome { PossessionRegained = 0, TenantCured = 1, Settled = 2, Dismissed = 3, Other = 4 }
public class Eviction : IAuditable, IPortfolioScoped {
    public int Id; public int PortfolioId; public int PropertyId; public int? UnitId;
    public int LeaseId; public int TenantId;
    public DateTime? FilingDate; public EvictionStatus Status;
    public string? CourtName, CaseNumber; public EvictionOutcome? Outcome;
    public DateTime? ClosedDate; public int? StoredFileId; public string? Notes;
    public DateTime CreatedAt, UpdatedAt; public DateTime? DeletedAt;
    public List<EvictionStatusEvent> StatusEvents = [];
    public Property? Property; public Unit? Unit; public Lease? Lease; public Tenant? Tenant; public Portfolio? Portfolio;
}
// EvictionStatusEvent: exact copy of WorkOrderStatusEvent (Id, PortfolioId, EvictionId, From/ToStatus, Note, ChangedByUserId, ChangedByLabel, CreatedAtUtc, Eviction? nav)
```
Indexes `(PortfolioId)`, `(LeaseId)`, `(Status)` on Eviction; `(EvictionId)`, `(PortfolioId)` on the child.

- [ ] **Step 1** — Entities (copy the WorkOrder pair) + enums. **Step 2** — DbSets + config blocks (copy `1266–1333`, swap types; child soft-delete via the transitive filter pattern `e.Eviction!.DeletedAt == null`). **Step 3** — migration (copy `AddWorkOrderStatusEvents.cs`) + RLS for both tables.
- [ ] **Step 4 — Verify + commit.** Migration up/down; RLS present. `feat(eviction): Eviction + EvictionStatusEvent entities + migration`.

### Task G7.2: `EvictionService` + controller + status transitions (Recipe D)

**Files:**
- Create: `RentalCommand.Api/Services/Domain/IEvictionService.cs` + `EvictionService.cs`
- Create: `RentalCommand.Api/Controllers/EvictionsController.cs`
- Create: `RentalCommand.Api/DTOs/EvictionDtos.cs`
- Modify: `ServiceCollectionExtensions.cs`

**Interfaces:**
- Produces: `GET/POST/PATCH/DELETE /api/v1/evictions` (+ `POST {id}/status`). Create writes an initial `null→NoticeToQuit` status event; each status change appends an `EvictionStatusEvent` in the **same save** (copy WorkOrder's status-event write). Resolving with `Outcome = PossessionRegained` drives `Lease.Status = Terminated`.

- [ ] **Step 1** — Service (Recipe D) with `EnsureLeaseInPortfolioAsync`/`EnsureTenantInPortfolioAsync`/`EnsurePropertyInPortfolioAsync` guards. `UpdateStatusAsync` appends the event; on `PossessionRegained` also set the lease Terminated (guarded, same transaction). **Step 2** — Controller + DTOs + DI.
- [ ] **Step 3 — Verify + commit.** Create eviction → status events accrue; resolve-with-possession → lease Terminated. `feat(eviction): eviction service + status timeline + lease termination`.

### Task G7.3: Delinquency report annotates active evictions (one EXISTS)

**Files:** Modify `RentalCommand.Api/Services/Domain/ReportsService.cs:469–497` (per-lease delinquency projection) + the delinquency DTO.

**Interfaces:** Produces `DelinquentLease.HasActiveEviction` via a correlated `_db.Evictions.Any(ev => ev.LeaseId == l.Id && ev.PortfolioId == portfolioId && ev.Status != Resolved && ev.Status != Dismissed)` inside the existing grouped projection (mirrors `WorkOrderService` `HasActiveDispatch` at `:221`) — still one SQL statement.

- [ ] **Step 1** — Add the `Any(...)` boolean to the per-lease projection. **Step 2 — test** a delinquent lease with an open eviction shows `HasActiveEviction=true`. **Step 3 — commit.** `feat(reports): annotate delinquency with active eviction`.

### Task G7.4: Web — evictions nav + list/detail + "Start eviction" (WIRED)

**Files:**
- Create: `web/src/routes/(protected)/evictions/+page.svelte` (list, copy vendors) + `web/src/routes/(protected)/evictions/[id]/+page.svelte` + `web/src/lib/components/records/EvictionDetail.svelte` (status timeline, copy the work-order timeline)
- Create: `web/src/lib/api/endpoints/evictions.ts`
- Modify: `web/src/lib/components/AppShell.svelte` (`staffNavGroups` — add `/evictions` to the `work` or `rentals` group + a glyph)
- Modify: `web/src/lib/navigation/record-href.ts` (add `Eviction` RecordType)
- Modify: `web/src/routes/(protected)/accounting/past-due/+page.svelte` (per-row "Start eviction" action ~254)

**Interfaces:** Consumes the eviction API. Reachable via the new nav item + the past-due action.

- [ ] **Step 1** — Endpoint module. **Step 2** — List page (DataGrid: tenant, property, status, filing date) + detail with the status timeline + status-change action (copy work-order). **Step 3** — Add the nav item + glyph in `AppShell.svelte`; add the `Eviction` RecordType; add a **"Start eviction"** button per row in `accounting/past-due/+page.svelte` that opens a create dialog prefilled with the lease/tenant.
- [ ] **Step 4 — Verify (`/verify`).** From `/accounting/past-due`, Start eviction on the L14 lease → it appears in `/evictions`; advance status; resolve-with-possession → the lease shows Terminated.
- [ ] **Step 5 — Commit.** `feat(web): evictions list/detail + start-from-past-due + nav`.

### Task G7.5: Mobile parity — evictions screen

**Files:** Create `mobile/lib/features/evictions/` (repo + list + detail with timeline) + register in `mobile/lib/features/home/mobile_destination.dart` (+ `mobile_domain_navigation.dart` enum + `_shellTargetFor`, likely the `work` tab). Recipe F.

- [ ] **Step 1** — Repo + providers. **Step 2** — list + detail screens + a "Start eviction" action reachable from the lease detail / overdue screen. **Step 3** — register the destination. **Step 4 — verify + commit.** `feat(mobile): evictions`.

**Flag:** reworks the L14 delinquency arc — the eviction path is the **alternate** to the September lump-cure (never both on one DB) → **corpus-revision trigger** (SA-STATE §110–111).

---

## Self-Review

**Spec coverage** — every design element maps to a task:
- F1 (nullable-lease income): F1.1 (entity+filter), F1.2 (view LEFT JOIN + migration), F1.3 (predicates+DTO). ✓
- F2 (generalized depreciation): F2.1 (enums+table), F2.2 (generic overload+recapture, residential delegates unchanged). ✓
- F3 (business-tz + proration): F3.1 (today helper reuses clock provider), F3.2 (ProrationCalculator, 452.42 cent-check), F3.3 (LeaseService tz bug-fix). ✓
- F4 (import seam): F4.1 (types + resolver + dedup DTO). ✓
- Gap5: G5.1–G5.3 backend (ApplicationFee type, links, income predicate), G5.4 web wired, G5.5 mobile parity. ✓
- Gap1: G1.1–G1.3 importers+dedup (one bounded query), G1.4 web wired, G1.5 mobile deferral logged. ✓
- Gap2: G2.1–G2.3 backend (record + statement), G2.4 web wired, G2.5 mobile parity. ✓
- Gap3: G3.1–G3.4 backend (stub periods, both creators, convention, move-out), G3.5 web wired, G3.6 mobile parity. ✓
- Gap4: G4.1–G4.4 backend (entity, capitalize, Schedule E, accounting), G4.5/G4.6 web wired (capitalize + property section), G4.7 mobile parity. ✓
- Gap6: G6.1–G6.4 backend (entity, RecordSale, sold-exclusion, year-end), G6.5/G6.6 web wired, G6.7 mobile parity. Follows Gap4. ✓
- Gap7: G7.1–G7.3 backend (entities, service, delinquency annotation), G7.4 web wired (nav + list/detail + start-from-past-due), G7.5 mobile parity. ✓

**DoD "UI wired" clause** — called out explicitly per gap: G5.4 (application detail), G1.4 (import page), G2.4 (owners-report), G3.5 (settings + lease ledger), G4.5/G4.6 (expense detail + property section), G6.5/G6.6 (property + year-end), G7.4 (new nav + past-due action). No orphan components.

**Mobile** — parity built where a host surface exists (G5.5, G2.5, G3.6, G4.7, G6.7, G7.5); explicit deferral only for bulk import (G1.5, no mobile import surface). Never a silent skip.

**Type consistency** — `DepreciationMethod`/`DepreciationConvention`/`ProrationConvention`/`DistributionMethod`/`EvictionStatus`/`EvictionOutcome`/`PropertyStatus.Sold`/`PaymentType.ApplicationFee` are each defined once (F2.1, F3.2, G2.1, G7.1, G6.1, G5.1) and referenced consistently. `AnnualForYear` generic overload signature is identical in F2.2 (produced) and G4.3/G6.2 (consumed). `RentChargePeriod` extended fields (G3.1) are consumed by G3.2. `CsvImportResult.DuplicateRows` (F4.1) consumed by G1.1/G1.4.

**No placeholders** — pure-logic tasks carry the exact cent-check code (452.42, $135 roof, §1250 min); entity/enum/DTO shapes are shown in full; boilerplate references a named copy-template + the exact transformation. Testing posture matches the project's light-test rule (unit-test primitives, verify full-stack in the running app).

**Decisions — RESOLVED (team-lead, 2026-07-01):** (1) branch strategy → **SEQUENTIAL, one lane at a time** in the order above (not parallel lanes); (2) mobile build-vs-defer for gaps 2/3/5/7 → **routed to the user** (in-flight mobile work TSK-633/634/635 + Codex's mobile pass) — the orchestrator folds the answer in at dispatch; until then the mobile parity tasks (G2.5/G3.6/G4.7/G5.5/G6.7/G7.5) stand as written, each convertible to a logged deferral (`TSK-6NN-M`) rather than a silent skip; (3) gap3 move-out → **prorate the final charge down** (confirmed); (4) ApplicationFee **counts on Schedule E** (confirmed; triggers a corpus revision); (5) GAP4 roof / GAP6 sale exact asset/price/date → **stay corpus-revision placeholders** (feature is generic; SA confirms at revision).
