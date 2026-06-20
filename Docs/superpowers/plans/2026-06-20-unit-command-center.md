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

- [ ] **Step 1:** Add `UnitId`/`Unit` to `Expense.cs`; add the EF config (optional FK, `OnDelete` = `SetNull` or `Restrict` to match the codebase's convention for optional FKs, indexed).
- [ ] **Step 2:** Thread `UnitId` through the expense create/update DTOs + controller/service; add an in-portfolio validation for `UnitId` consistent with how other FK refs (e.g. `WorkOrderId`) are validated in this controller.
- [ ] **Step 3:** Generate the migration: `dotnet ef migrations add AddExpenseUnitId --project RentalCommand.Data --startup-project RentalCommand.Api`. Review the generated SQL (nullable column + index; no data backfill needed).
- [ ] **Step 4:** `dotnet build RentalCommand.sln` — expect success.
- [ ] **Step 5:** Commit: `feat(api): optional Expense.UnitId so unit-level expenses have a home`.

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

- [ ] **Step 1: Write the failing tests** — cover §5 of the spec:
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
- [ ] **Step 2:** Run: `dotnet test --filter UnitLifecycleStageResolverTests` — expect FAIL (resolver not implemented).
- [ ] **Step 3:** Implement `Resolve` as the decision tree from spec §5 (first match wins), plus the per-stage `NextBestActionLabel` (Active with `OutstandingRentBalance > 0` → "Collect ${balance}", else "Rent on track"; Renewal → "Send renewal — lease ends in {n} days"; etc.).
- [ ] **Step 4:** Run: `dotnet test --filter UnitLifecycleStageResolverTests` — expect PASS.
- [ ] **Step 5:** Commit: `feat(api): unit lifecycle stage resolver (derived, read-only)`.

### Task A3: `GET /units/{id}/dashboard` aggregate

**Files:**
- Create: `RentalCommand.Api/DTOs/UnitDashboardDtos.cs` (`UnitDashboardResponse` per spec §8 + nested summaries).
- Modify: `RentalCommand.Api/Controllers/UnitController.cs` (add the `dashboard` action).
- Create/Modify: a `UnitDashboardService` (match where Portfolio's dashboard service lives) that assembles the response with **set-based** queries.

**Interfaces — Consumes:** `UnitLifecycleStageResolver` (A2). **Produces:** `GET /api/v1/units/{id}/dashboard → UnitDashboardResponse`.

- [ ] **Step 1:** Define `UnitDashboardResponse` (fields from spec §8: `unit`, `lifecycleStage`, `nextBestAction{label,href}`, `header{rentState, openWorkOrderCount, leaseEndsInDays?, docsNeedingReviewCount, currentTenantName?}`, `currentLease?`, `currentTenant?`, `overview{recentPayments[], openWorkOrders[], pendingDocs[], upcomingAppointments[]}` capped ~5, `recentTimeline[]` capped ~15).
- [ ] **Step 2:** Implement the service. Use the Portfolio dashboard service as the reference pattern. Assemble via a handful of set-based queries: (1) unit+property; (2) current lease+tenant; (3) **SQL aggregate** for outstanding/overdue rent over `Payment` where `LeaseId ∈` the unit's leases — `GroupBy`/`Sum` in the query, NOT after materialization; (4) open-WO + pending-doc counts (grouped); (5) upcoming appointments; (6) the timeline query from Task A4. Build `UnitStageInputs` from these and call the resolver. **Verify the generated SQL has no per-row queries** (enable EF query logging in Development and read `/tmp/rentalcommand-api.log`).
- [ ] **Step 3:** Add the controller action `[HttpGet("{id}/dashboard")]`, portfolio-scoped + IDOR guard (unit in caller's portfolio).
- [ ] **Step 4:** `dotnet build RentalCommand.sln`; smoke it: run the API, `GET /api/v1/units/{id}/dashboard` for a seeded unit, confirm shape + that the log shows a bounded set of SQL statements (no N+1).
- [ ] **Step 5:** Commit: `feat(api): GET /units/{id}/dashboard aggregate (server-side, mirrors portfolio dashboard)`.

### Task A4: Unit timeline (audit union)

**Files:**
- Modify: the `UnitDashboardService` (or a small `UnitTimelineQuery` helper next to it).

**Interfaces — Produces:** `IReadOnlyList<AuditLogEntry> GetUnitTimeline(int unitId, int take, int skip)` (used by A3's `recentTimeline` and by the Timeline tab endpoint).

- [ ] **Step 1:** Collect the unit's child ids with a few indexed selects: lease ids (where `Lease.UnitId=unit`); payment ids (where `Payment.LeaseId ∈` lease ids); work-order/inspection/appointment ids (where `UnitId=unit`); expense ids (where `Expense.UnitId=unit` OR `WorkOrderId ∈` the unit's WOs).
- [ ] **Step 2:** Run **one** `AuditLog` query with the OR/IN filter from spec §11, `ORDER BY Timestamp DESC`, paged. (Fixed small number of queries regardless of data size — not N+1.)
- [ ] **Step 3:** Expose a paged endpoint for the Timeline tab: `GET /api/v1/units/{id}/timeline?skip&take` (or extend the existing `/audit` endpoint with a `unitId` convenience that runs this union — pick whichever is cleaner; document the choice in the commit).
- [ ] **Step 4:** `dotnet build`; smoke the endpoint; confirm one audit query in the log.
- [ ] **Step 5:** Commit: `feat(api): per-unit timeline as a bounded AuditLog union`.

### Task A5: Units-list health projection

**Files:**
- Modify: `RentalCommand.Api/Controllers/UnitController.cs` + `UnitDtos.cs` (extend the list response with cheap health fields) OR add `GET /api/v1/units/list-with-health`.

**Interfaces — Produces:** the `/units` list rows: `{ id, propertyName, unitNumber, status, openWorkOrderCount, leaseEndsInDays?, docsNeedingReviewCount, simpleStage }`.

- [ ] **Step 1:** Implement as **one projection query** (joins + grouped counts) across all portfolio units — do NOT call the per-unit dashboard per row. `simpleStage` is a cheap expression (Unit.Status + active-lease status), NOT the full §5 derivation (that runs only on the detail page).
- [ ] **Step 2:** `dotnet build`; smoke with a portfolio of several units; confirm a single query in the log.
- [ ] **Step 3:** Commit: `feat(api): units list-with-health projection (single query, no N+1)`.

---

## Phase B — Web shell & navigation

### Task B1: Sidebar entry + `/units` list page

**Files:**
- Modify: `web/src/lib/components/AppShell.svelte` (add **Units** to the Rentals group).
- Create: `web/src/routes/(protected)/units/+page.svelte` (+ `+page.ts`/loader as the codebase does it).

**Interfaces — Consumes:** A5 endpoint.

- [ ] **Step 1:** Add the **Units** nav item (icon consistent with the existing set).
- [ ] **Step 2:** Build the list using the existing `DataGrid` pattern (copy the structure of an existing list page, e.g. leases/work-orders): columns unit (property + number), `StatusBadge` for status/simpleStage, open repairs, lease-ends, docs-to-review; filter/search; each row links to `/units/[id]`.
- [ ] **Step 3:** `pnpm -C web build` (or `check`) — expect success.
- [ ] **Step 4:** Commit: `feat(web): Units list page + sidebar entry`.

### Task B2: `/units/[id]` page scaffold

**Files:**
- Create: `web/src/routes/(protected)/units/[id]/+page.svelte` + loader; the loader fetches `GET /units/{id}/dashboard`.
- Create: small components under `web/src/routes/(protected)/units/[id]/` or `web/src/lib/components/unit/` — `UnitHeader.svelte`, `UnitTabs.svelte`, plus the rail (B3) and a `UnitTimelineRail.svelte` (reuse `ActivityFeed`).

**Interfaces — Consumes:** A3 dashboard. **Produces:** the page shell + a `?tab=` query param contract for deep links.

- [ ] **Step 1:** Loader fetches the dashboard aggregate; handle loading/error per the codebase's TanStack Query conventions; invalidate on the relevant SignalR events (follow the existing dashboard page).
- [ ] **Step 2:** `UnitHeader.svelte` — health chips (rent state, open repairs, lease-ends-in, docs-needing-review, tenant) + **Scan/Upload** (prominent) + a **New** quick-action menu (post payment / add expense / create work order / upload doc). Wire actions to the Command Drawer (Phase D) — for now the menu can open placeholder drawer slots.
- [ ] **Step 3:** `UnitTabs.svelte` (reuse `ui/tabs`) with the 7 tabs; the active tab is driven by `?tab=` (default `overview`). Persistent `UnitTimelineRail` to the right across tabs.
- [ ] **Step 4:** `pnpm -C web build`; load `/units/{id}` for a seeded unit; confirm header + tab shell + timeline render.
- [ ] **Step 5:** Commit: `feat(web): /units/[id] Command Center shell (header + tabs + timeline rail)`.

### Task B3: Lifecycle rail component

**Files:**
- Create: `web/src/lib/components/unit/LifecycleRail.svelte`.

**Interfaces — Consumes:** `lifecycleStage` + `nextBestAction` from the dashboard.

- [ ] **Step 1:** Render the 9 ordered stages with prior = done, current = highlighted, future = upcoming; show the `nextBestAction` line; the action label links to `nextBestAction.href`.
- [ ] **Step 2:** `pnpm -C web build`; verify the correct stage highlights for a couple of seeded units (occupied → Active; vacant → Ready/Listed).
- [ ] **Step 3:** Commit: `feat(web): unit lifecycle rail (derived stage + next best action)`.

---

## Phase C — Tabs

> Each tab is a focused component under the unit route. Reuse existing list/detail components and endpoints; do not duplicate business logic. Build + manually verify each; commit per tab.

### Task C1: Overview tab
- **Files:** Create `web/src/lib/components/unit/tabs/OverviewTab.svelte`.
- [ ] Render from the dashboard aggregate: snapshot, current tenant + lease summary, rent state, open repairs, docs/AI awaiting review, next actions, recent activity. [ ] Build. [ ] Commit `feat(web): unit Overview tab`.

### Task C2: Lease tab
- **Files:** Create `…/tabs/LeaseTab.svelte`.
- [ ] Show the unit's current lease (reuse existing lease detail components); actions (upload/extract lease, generate/send renewal, mark signed) open in the Command Drawer. [ ] Build. [ ] Commit `feat(web): unit Lease tab`.

### Task C3: Rent tab
- **Files:** Create `…/tabs/RentTab.svelte`.
- [ ] Payments for the unit's lease id(s) via the existing payments endpoint (`?leaseId=`; if multiple leases, request per lease id or extend the filter — keep it DB-side). Show schedule/charges/payments/balance/late fees/receipts. Actions (post payment, add charge/credit, upload payment doc, send reminder) → drawer. [ ] Build. [ ] Commit `feat(web): unit Rent tab`.

### Task C4: Maintenance tab
- **Files:** Create `…/tabs/MaintenanceTab.svelte`.
- [ ] Work orders via `?unitId=`; reuse `WorkOrderTimeline`/work-order components. Show the **expenses tied to those work orders**; snap-a-receipt → create expense linked to the work order (drawer). Actions: create ticket, attach photo, assign vendor, upload invoice, close. [ ] Build. [ ] Commit `feat(web): unit Maintenance tab (with work-order receipts)`.

### Task C5: Documents tab
- **Files:** Create `…/tabs/DocumentsTab.svelte`.
- [ ] List `StoredFile`s for the unit + children (by the unit's `(EntityType,EntityId)` set), grouped by type; reuse the scan/draft-confirm UI for scan/upload + extraction review. Actions: scan/upload, review AI extraction, re-attach, change type. [ ] Build. [ ] Commit `feat(web): unit Documents tab`.

### Task C6: Expenses tab
- **Files:** Create `…/tabs/ExpensesTab.svelte`.
- [ ] Expenses where `UnitId = unit` OR `WorkOrderId ∈` the unit's WOs (server-side filter). Create-expense + attach-receipt + link-to-work-order in the drawer (sets `UnitId`). [ ] Build. [ ] Commit `feat(web): unit Expenses tab`.

### Task C7: Timeline tab
- **Files:** Create `…/tabs/TimelineTab.svelte`.
- [ ] Paged, filterable full history from the A4 timeline endpoint; reuse `ActivityFeed`. [ ] Build. [ ] Commit `feat(web): unit Timeline tab`.

---

## Phase D — Command Drawer, deep links, smoke test

### Task D1: Command Drawer host + action forms
- **Files:** Create `web/src/lib/components/unit/UnitCommandDrawer.svelte` (reuse `ui/drawer`); host the action forms (post payment, create work order, add expense, scan review) with unit context preloaded.
- [ ] Wire the header **New** menu + tab actions + rail next-best-action to open the relevant drawer slot. Reuse existing create/edit form logic; do not remove modals elsewhere. [ ] Build. [ ] Commit `feat(web): unit Command Drawer (drawer-first actions)`.

### Task D2: Deep links into the unit
- **Files:** Modify the units grid in `web/src/routes/(protected)/properties/[id]/+page.svelte` (unit row → `/units/[id]`); add "open in unit" links from payment / work-order / lease rows and dashboard attention items → `/units/[id]?tab=<rent|maintenance|lease>`.
- [ ] Build. [ ] Commit `feat(web): deep-link operation rows into the unit page`.

### Task D3: Smoke test + phase review
- **Files:** Create one E2E/UI smoke (match the project's existing ~3–5 test setup, e.g. Playwright) that loads `/units/[id]`, asserts the header, the correct lifecycle stage, the tab switch, and the timeline rail render.
- [ ] Run the smoke; [ ] run a per-phase self-review against the spec's §13 acceptance criteria (esp. **inspect the dashboard endpoint's generated SQL** for N+1 / in-memory grouping); [ ] fix gaps. [ ] Commit `test(web): unit command center smoke + phase review`.

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
