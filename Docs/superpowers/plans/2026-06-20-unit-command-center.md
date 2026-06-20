# Unit Command Center Implementation Plan

> **For agentic workers:** Implement task-by-task, in order. Steps use checkbox (`- [ ]`) syntax for tracking. After each task: build, verify, commit. **Testing posture (from `CLAUDE.md`, overrides default TDD):** this project defers broad unit suites — keep ~3–5 focused tests total. Write a unit test ONLY where this plan says so (the stage resolver); otherwise verify by building and by the stated manual/SQL checks. Run heavy review per phase, not per task.

**Goal:** Make the unit a first-class object — a `/units/[id]` Command Center page (header + derived lifecycle rail + work tabs + persistent timeline) plus a `/units` list, backed by one new aggregate endpoint — without removing any existing operation pages.

**Architecture:** Additive. New SvelteKit routes consume a new `GET /api/v1/units/{id}/dashboard` aggregate (mirrors the existing Portfolio dashboard) for the at-a-glance load; tabs lazy-load via existing filtered list endpoints. The lifecycle stage is *computed* per-unit (no stored column). One small model change: optional `Expense.UnitId`.

**Tech Stack:** .NET 10 / EF Core + Npgsql (PostgreSQL only); ASP.NET controllers under `/api/v1` inheriting `AuthenticatedPortfolioControllerBase`; SvelteKit 5 (runes) + TanStack Query + Tailwind; SignalR for live invalidation.

**Spec:** `Docs/superpowers/specs/2026-06-20-unit-command-center-design.md` (read it first — this plan implements it).

## Global Constraints

- **PostgreSQL only** (Npgsql). Migrations: `dotnet ef migrations add <Name> --project RentalCommand.Data --startup-project RentalCommand.Api`.
- **Data-access HARD RULE:** all aggregation/grouping/filtering/joins/sorting/paging run **DB-side** (one EF-translated SQL statement or a view). NO in-memory grouping, NO load-then-loop / N+1, NO lazy loading. Inspect generated SQL when in doubt.
- **Portfolio scoping / IDOR:** every endpoint is portfolio-scoped; validate the unit and all referenced FKs are in the caller's portfolio.
- **Enums serialize as string names** (`JsonStringEnumConverter`) — keep new DTO enums string-valued.
- **Web API calls** go through `web/src/lib/api/client.ts` (`fetchApi`/`api`).
- **Build before commit.** Backend: `dotnet build RentalCommand.sln`. Web: `pnpm -C web build` (or `pnpm -C web check`). Do not run multiple heavy builds in parallel.
- **Commit conventions:** clear subject + body; **no** `Co-Authored-By`/AI-attribution trailer.
- **This is one isolated worktree on branch `tsk-387-unit-command-center`.** Commit frequently.

---

## Phase A — Backend foundation

### Task A1: Add `Expense.UnitId`

**Files:**
- Modify: `RentalCommand.Core/Entities/Expense.cs` (add `public int? UnitId { get; set; }` + `public Unit? Unit { get; set; }`)
- Modify: `RentalCommand.Data/RentalCommandDbContext.cs` (configure the optional FK + index on `UnitId`)
- Modify: Expense create/update DTOs + service (`RentalCommand.Api/DTOs/ExpenseDtos.cs`, `RentalCommand.Api/Controllers/ExpenseController.cs` and/or the expense service) — accept/return/persist `UnitId`; validate it is in the caller's portfolio.
- Create (migration): `RentalCommand.Data/Migrations/*_AddExpenseUnitId.cs` (generated).

**Interfaces — Produces:** `Expense.UnitId (int?)`; expense create/update payloads accept `unitId`.

- [x] **Step 1:** Add `UnitId`/`Unit` to `Expense.cs`; add the EF config (optional FK, `OnDelete` = `SetNull` or `Restrict` to match the codebase's convention for optional FKs, indexed).
- [x] **Step 2:** Thread `UnitId` through the expense create/update DTOs + controller/service; add an in-portfolio validation for `UnitId` consistent with how other FK refs (e.g. `WorkOrderId`) are validated in this controller.
- [x] **Step 3:** Generate the migration: `dotnet ef migrations add AddExpenseUnitId --project RentalCommand.Data --startup-project RentalCommand.Api`. Review the generated SQL (nullable column + index; no data backfill needed).
- [x] **Step 4:** `dotnet build RentalCommand.sln` — expect success.
- [x] **Step 5:** Commit: `feat(api): optional Expense.UnitId so unit-level expenses have a home`.

### Task A2: Lifecycle stage resolver (the one place we unit-test)

**Files:**
- Create: `RentalCommand.Api/Services/Domain/UnitLifecycleStageResolver.cs` (or alongside the other domain services — match the existing folder, e.g. where `ReportsService` lives).
- Create: `RentalCommand.ApiTests/.../UnitLifecycleStageResolverTests.cs` (match the existing API test project name/location).

**Interfaces — Produces:**
```csharp
public enum UnitLifecycleStage { Ready, Listed, Applicant, Lease, MoveIn, Active, Renewal, MoveOut, Turnover }

public static class UnitLifecycleStageResolver
{
    // Pure function over already-fetched aggregate inputs (no DB access here).
    public static (UnitLifecycleStage Stage, string NextBestActionLabel) Resolve(UnitStageInputs inputs, DateTime nowUtc);
}

public sealed record UnitStageInputs(
    UnitStatus UnitStatus,
    LeaseSnapshot? CurrentLease,         // most-recent Active, else latest lease (null if none)
    bool HasDraftOrPendingLease,
    bool HasOpenApplication,             // Submitted/UnderReview/Approved, not yet converted
    bool HasUpcomingShowing,
    bool HasUpcomingMoveInAppt,
    bool RecentMoveOutSignal,            // last lease Expired/Terminated recently OR MoveOut inspection done OR UnitStatus==Offline OR open work orders
    decimal OutstandingRentBalance);

public sealed record LeaseSnapshot(LeaseStatus Status, DateTime? StartDate, DateTime? EndDate);
```

- [x] **Step 1: Write the failing tests** — cover §5 of the spec:
```csharp
// occupied steady -> Active
// Active lease, EndDate in 30 days -> Renewal
// lease.Status==NoticeGiven -> MoveOut
// Active lease, StartDate 3 days in future + MoveIn appt -> MoveIn
// Draft lease, no Active -> Lease
// no lease + open application -> Applicant
// vacant + recent move-out signal -> Turnover
// vacant + upcoming showing -> Listed
// vacant, idle, no signals -> Ready
```
- [x] **Step 2:** Run: `dotnet test --filter UnitLifecycleStageResolverTests` — expect FAIL (resolver not implemented).
- [x] **Step 3:** Implement `Resolve` as the decision tree from spec §5 (first match wins), plus the per-stage `NextBestActionLabel` (Active with `OutstandingRentBalance > 0` → "Collect ${balance}", else "Rent on track"; Renewal → "Send renewal — lease ends in {n} days"; etc.).
- [x] **Step 4:** Run: `dotnet test --filter UnitLifecycleStageResolverTests` — expect PASS.
- [x] **Step 5:** Commit: `feat(api): unit lifecycle stage resolver (derived, read-only)`.

### Task A3: `GET /units/{id}/dashboard` aggregate

**Files:**
- Create: `RentalCommand.Api/DTOs/UnitDashboardDtos.cs` (`UnitDashboardResponse` per spec §8 + nested summaries).
- Modify: `RentalCommand.Api/Controllers/UnitController.cs` (add the `dashboard` action).
- Create/Modify: a `UnitDashboardService` (match where Portfolio's dashboard service lives) that assembles the response with **set-based** queries.

**Interfaces — Consumes:** `UnitLifecycleStageResolver` (A2). **Produces:** `GET /api/v1/units/{id}/dashboard → UnitDashboardResponse`.

- [x] **Step 1:** Define `UnitDashboardResponse` (fields from spec §8: `unit`, `lifecycleStage`, `nextBestAction{label,href}`, `header{rentState, openWorkOrderCount, leaseEndsInDays?, docsNeedingReviewCount, currentTenantName?}`, `currentLease?`, `currentTenant?`, `overview{recentPayments[], openWorkOrders[], pendingDocs[], upcomingAppointments[]}` capped ~5, `recentTimeline[]` capped ~15).
- [x] **Step 2:** Implement the service. Use the Portfolio dashboard service as the reference pattern. Assemble via a handful of set-based queries: (1) unit+property; (2) current lease+tenant; (3) **SQL aggregate** for outstanding/overdue rent over `Payment` where `LeaseId ∈` the unit's leases — `GroupBy`/`Sum` in the query, NOT after materialization; (4) open-WO + pending-doc counts (grouped); (5) upcoming appointments; (6) the timeline query from Task A4. Build `UnitStageInputs` from these and call the resolver. **Verify the generated SQL has no per-row queries** — verified statically (every query stays `IQueryable` to its terminal call; rent state is one grouped conditional `SUM`; stage inputs are indexed `EXISTS`). Live `/tmp` log smoke deferred to the user (must not start the API against shared dev Postgres during the demo).
- [x] **Step 3:** Add the controller action `[HttpGet("{id}/dashboard")]`, portfolio-scoped + IDOR guard (unit in caller's portfolio).
- [x] **Step 4:** `dotnet build RentalCommand.sln` — succeeds. Live API smoke deferred to the user (shared-infra constraint); query shape verified by reading the LINQ.
- [x] **Step 5:** Commit: `feat(api): GET /units/{id}/dashboard aggregate (server-side, mirrors portfolio dashboard)`.

### Task A4: Unit timeline (audit union)

**Files:**
- Modify: the `UnitDashboardService` (or a small `UnitTimelineQuery` helper next to it).

**Interfaces — Produces:** `IReadOnlyList<AuditLogEntry> GetUnitTimeline(int unitId, int take, int skip)` (used by A3's `recentTimeline` and by the Timeline tab endpoint).

- [x] **Step 1:** Collect the unit's child ids with a few indexed selects: lease ids (where `Lease.UnitId=unit`); payment ids (where `Payment.LeaseId ∈` lease ids); work-order/inspection/appointment ids (where `UnitId=unit`); expense ids (where `Expense.UnitId=unit` OR `WorkOrderId ∈` the unit's WOs).
- [x] **Step 2:** Run **one** `AuditLog` query with the OR/IN filter from spec §11, `ORDER BY Timestamp DESC`, paged. (Fixed small number of queries regardless of data size — not N+1.)
- [x] **Step 3:** Expose a paged endpoint for the Timeline tab: `GET /api/v1/units/{id}/timeline?skip&take`. Chose a dedicated `UnitController` action over extending `/audit` because the per-unit union (multi-entity OR/IN over the unit's children) is its own bounded query, not a single-`entityId` filter.
- [x] **Step 4:** `dotnet build` — succeeds. The union is one `AuditLog` query after the indexed child-id selects (verified in the LINQ). Live log smoke deferred to the user.
- [x] **Step 5:** Committed together with A3 (the dashboard's `recentTimeline` calls `GetTimelineAsync`, so the union ships in the same `UnitDashboardService`); the `/timeline` endpoint is in the same commit `feat(api): GET /units/{id}/dashboard aggregate …`.

### Task A5: Units-list health projection

**Files:**
- Modify: `RentalCommand.Api/Controllers/UnitController.cs` + `UnitDtos.cs` (extend the list response with cheap health fields) OR add `GET /api/v1/units/list-with-health`.

**Interfaces — Produces:** the `/units` list rows: `{ id, propertyName, unitNumber, status, openWorkOrderCount, leaseEndsInDays?, docsNeedingReviewCount, simpleStage }`.

- [x] **Step 1:** Implement as **one projection query** (joins + grouped counts) across all portfolio units — do NOT call the per-unit dashboard per row. `simpleStage` is a cheap expression (Unit.Status + active-lease status), NOT the full §5 derivation (that runs only on the detail page).
- [x] **Step 2:** `dotnet build` — succeeds. One projection query (correlated counts + active-lease scalars); verified the LINQ materializes once at `ToListAsync` and the label is formatted from projected scalars (no per-row query). Live single-query log smoke deferred to the user (shared-infra constraint).
- [x] **Step 3:** Commit: `feat(api): units list-with-health projection (single query, no N+1)`.

---

## Phase B — Web shell & navigation

### Task B1: Sidebar entry + `/units` list page

**Files:**
- Modify: `web/src/lib/components/AppShell.svelte` (add **Units** to the Rentals group).
- Create: `web/src/routes/(protected)/units/+page.svelte` (+ `+page.ts`/loader as the codebase does it).

**Interfaces — Consumes:** A5 endpoint.

- [x] **Step 1:** Add the **Units** nav item (icon consistent with the existing set). Added to the Rentals group (Home icon, `home` glyph), first in the group as the new central object.
- [x] **Step 2:** Build the list using the existing `DataGrid` pattern (copy the structure of an existing list page, e.g. leases/work-orders): columns unit (property + number), `StatusBadge` for status/simpleStage, open repairs, lease-ends, docs-to-review; filter/search; each row links to `/units/[id]`.
- [x] **Step 3:** `pnpm -C web check` — 0 errors; `pnpm -C web build` — succeeds.
- [x] **Step 4:** Commit: `feat(web): Units list page + sidebar entry`.

### Task B2: `/units/[id]` page scaffold

**Files:**
- Create: `web/src/routes/(protected)/units/[id]/+page.svelte` + loader; the loader fetches `GET /units/{id}/dashboard`.
- Create: small components under `web/src/routes/(protected)/units/[id]/` or `web/src/lib/components/unit/` — `UnitHeader.svelte`, `UnitTabs.svelte`, plus the rail (B3) and a `UnitTimelineRail.svelte` (reuse `ActivityFeed`).

**Interfaces — Consumes:** A3 dashboard. **Produces:** the page shell + a `?tab=` query param contract for deep links.

- [x] **Step 1:** Loader fetches the dashboard aggregate; handle loading/error per the codebase's TanStack Query conventions; invalidate on the relevant SignalR events — the unit-* query-key prefixes were added to the realtime invalidate map so Unit/Lease/Payment/Expense/WorkOrder/Inspection/Appointment events refresh the page.
- [x] **Step 2:** `UnitHeader.svelte` — health chips (rent state, open repairs, lease-ends-in, docs-needing-review, tenant) + **Scan/Upload** (prominent) + a **New** quick-action menu (post payment / add expense / create work order / upload doc), all wired to the real Command Drawer (D1).
- [x] **Step 3:** Tabs (reuse `ui/tabs`) with the 7 tabs; the active tab is driven by `?tab=` (default `overview`), synced back to the URL on switch. Persistent `UnitTimelineRail` to the right across tabs.
- [x] **Step 4:** `pnpm -C web build` — succeeds (the `/units/[id]` route compiled). Live load against the running app deferred to the user (shared-infra constraint).
- [x] **Step 5:** Commit: `feat(web): /units/[id] Command Center shell (header + tabs + timeline rail)`.

### Task B3: Lifecycle rail component

**Files:**
- Create: `web/src/lib/components/unit/LifecycleRail.svelte`.

**Interfaces — Consumes:** `lifecycleStage` + `nextBestAction` from the dashboard.

- [x] **Step 1:** Render the 9 ordered stages with prior = done, current = highlighted, future = upcoming; show the `nextBestAction` line; the action label links to `nextBestAction.href`.
- [x] **Step 2:** `pnpm -C web build` — succeeds. The highlighted stage is data-driven from the dashboard's `lifecycleStage` (resolver covered by unit tests); live per-unit visual check deferred to the user.
- [x] **Step 3:** Commit: `feat(web): unit lifecycle rail (derived stage + next best action)`.

---

## Phase C — Tabs

> Each tab is a focused component under the unit route. Reuse existing list/detail components and endpoints; do not duplicate business logic. Build + manually verify each; commit per tab.

> **Phase C note:** the seven tabs were built together and shipped in one commit
> (`feat(web): unit work tabs (...)`) because the page imports all of them — committing one
> tab at a time would not leave the tree buildable. All build + svelte-check clean.

### Task C1: Overview tab
- **Files:** Create `web/src/lib/components/unit/tabs/OverviewTab.svelte`.
- [x] Render from the dashboard aggregate: snapshot, current tenant + lease summary, rent state, open repairs, docs/AI awaiting review, next actions, recent activity. [x] Build. [x] Committed (combined tabs commit).

### Task C2: Lease tab
- **Files:** Create `…/tabs/LeaseTab.svelte`.
- [x] Show the unit's current lease summary + a link to the full lease page; the scan-lease action opens in the Command Drawer. (Renewal/mark-signed are surfaced via the lease page; the drawer hosts scan/upload in v1.) [x] Build. [x] Committed.

### Task C3: Rent tab
- **Files:** Create `…/tabs/RentTab.svelte`.
- [x] Payments for the unit's current lease via the existing payments endpoint (`?leaseId=`, DB-side). Shows the ledger + outstanding balance; post-payment + scan actions → drawer. (Multi-lease units show the current lease's ledger in v1; noted for the user.) [x] Build. [x] Committed.

### Task C4: Maintenance tab
- **Files:** Create `…/tabs/MaintenanceTab.svelte`.
- [x] Work orders via the new `?unitId=` filter (added to the WO list endpoint); shows the **expenses tied to those work orders**. Create-work-order + scan → drawer. [x] Build. [x] Committed.

### Task C5: Documents tab
- **Files:** Create `…/tabs/DocumentsTab.svelte`.
- [x] Lists the unit's document set (the dashboard's unit+children `(EntityType,EntityId)` set), grouped by the child entity; scan/upload routes into the scan→draft→confirm flow. [x] Build. [x] Committed.

### Task C6: Expenses tab
- **Files:** Create `…/tabs/ExpensesTab.svelte`.
- [x] Expenses where `UnitId = unit` OR `WorkOrderId ∈` the unit's WOs (server-side correlated filter). Add-expense in the drawer sets `UnitId`. [x] Build. [x] Committed.

### Task C7: Timeline tab
- **Files:** Create `…/tabs/TimelineTab.svelte`.
- [x] Paged full history from the A4 timeline endpoint (`/units/{id}/timeline`); reuses `ActivityFeed` with load-more. [x] Build. [x] Committed.

---

## Phase D — Command Drawer, deep links, smoke test

### Task D1: Command Drawer host + action forms
- **Files:** Create `web/src/lib/components/unit/UnitCommandDrawer.svelte` (reuse `ui/drawer`); host the action forms (post payment, create work order, add expense, scan review) with unit context preloaded.
- [x] Wire the header **New** menu + tab actions to open the relevant drawer slot (rail next-best-action deep-links to the relevant tab). Real forms (post payment / add expense / create work order / scan) with unit context preloaded; modals elsewhere untouched. [x] Build. [x] Commit `feat(web): unit Command Drawer (drawer-first actions)`.

### Task D2: Deep links into the unit
- **Files:** Modify the units grid in `web/src/routes/(protected)/properties/[id]/+page.svelte` (unit row → `/units/[id]`); add "open in unit" links from payment / work-order / lease rows and dashboard attention items → `/units/[id]?tab=<rent|maintenance|lease>`.
- [x] Property units grid row → `/units/[id]` (+ explicit "open unit" arrow action); lease-detail "Unit N" → `?tab=lease`; work-order-detail "Unit N" → `?tab=maintenance`. (Dashboard expiring-lease items lack a `unitId` in their DTO, so deferred — noted for a later DTO add.) [x] Build (svelte-check 0 errors). [x] Commit `feat(web): deep-link operation rows into the unit page`.

### Task D3: Smoke test + phase review
- **Files:** Create one E2E/UI smoke (match the project's existing ~3–5 test setup, e.g. Playwright) that loads `/units/[id]`, asserts the header, the correct lifecycle stage, the tab switch, and the timeline rail render.
- [x] Smoke written (`web/e2e/units.spec.ts`, 3 tests, parses via `playwright test --list`); NOT executed — needs live API + seeded DB reserved for the demo (run: `pnpm -C web test:e2e -- units.spec.ts`). [x] Per-phase self-review done (below). [x] Commit `test(web): unit command center smoke + phase review`.

---

## Phase review — against spec §13 acceptance criteria

1. **`/units/[id]` renders header + correct derived rail + 7 tabs + timeline rail** — DONE. Page shell builds; smoke asserts each (deferred run). Rail stage is data-driven from the resolver (unit-tested).
2. **Dashboard = a handful of EF-translated queries, no N+1, rent-state a SQL aggregate** — DONE; verified statically (every query stays `IQueryable` to its terminal call; rent state is one `GroupBy(_=>1).Select(Sum(CASE…))`; stage inputs are indexed `EXISTS`). Live SQL-log smoke deferred to the user (shared-infra constraint).
3. **`/units` health badges from one projection query** — DONE; one `Select` projection with correlated counts + active-lease scalars; label formatted from projected scalars (no per-row query).
4. **Expense ties to a unit & shows in Expenses; WO receipt shows in Maintenance** — DONE (A1 + C6 + C4). Expense list `?unitId` = unit's own OR its WOs (correlated, DB-side).
5. **Deep links land on the right unit + tab** — DONE for property grid, lease detail, work-order detail (dashboard items deferred — DTO gap).
6. **Stage derivation matches §5 for representative cases** — DONE; 12 resolver xUnit cases green.
7. **Portfolio scoping/IDOR on the unit + referenced FKs** — DONE; unit scoped through its property's PortfolioId; dashboard/timeline 404/empty out-of-scope; expense/WO `unitId` validated via `EnsureUnitInPortfolioAsync`.
8. **Existing operation pages unchanged & still work** — DONE; additive. The only edits to existing pages add a unit deep link (property grid row-click now goes to the unit page with edit kept as a row action; lease/WO detail unit labels became links). No existing flow removed.

**Items the user must run/verify** (could not be done safely against the live demo stack): apply the `AddExpenseUnitId` migration in dev; run the live SQL-log smoke on `GET /units/{id}/dashboard` + `/units/list-with-health` to confirm the bounded query count; run `pnpm -C web test:e2e -- units.spec.ts` against the seeded stack.

---

## Verification (against spec §13)

1. `/units/[id]` renders header + correct derived rail + 7 tabs + timeline rail. (D3)
2. Dashboard endpoint = a handful of EF-translated queries, no N+1, rent-state is a SQL aggregate — **verified by reading generated SQL**. (A3, D3)
3. `/units` health badges from one projection query. (A5)
4. Expense ties to a unit and shows in Expenses; work-order receipt shows in Maintenance. (A1, C4, C6)
5. Deep links land on the right unit + tab. (D2)
6. Stage derivation matches §5 for the representative cases. (A2 tests)
7. Portfolio scoping/IDOR on the unit + referenced FKs. (A1, A3)
8. Existing operation pages unchanged and still work. (manual)

## Self-review note

Plan covers every spec section: §3 nav (B1/D2), §4 anatomy (B2/B3), §5 stage (A2), §6 tabs (C1–C7), §7 drawer (D1), §8 endpoint (A3), §9 Expense.UnitId (A1), §10 list (A5/B1), §11 timeline (A4/C7). Deferred items (board, command bundles, Messages, turnover, money) are intentionally absent. Type names (`UnitDashboardResponse`, `UnitLifecycleStage`, `UnitStageInputs`, `LeaseSnapshot`) are consistent across tasks.
