# Unit Command Center — Design Spec

- **Date:** 2026-06-20
- **Task:** TSK-387 (Notion Command Center, project Rental Command)
- **Branch:** `tsk-387-unit-command-center`
- **Status:** Approved for implementation (design locked during brainstorming 2026-06-20). User delegated build to a background agent; this spec is the contract.
- **Source materials:** `~/Downloads/rental-command-cmd-center.md`; mockups `rental-command-unit-command-center.png`, `rental-command-lifecycle-board.png`, `rental-command-document-timeline-flow.png` (attached to TSK-387).

---

## 1. Context & goal

The app is **operation-centric**: rent, leases, maintenance, documents, and expenses are managed on separate top-level pages, and **there is no unit detail page** — a unit exists only as a row in a grid inside the property page (`web/src/routes/(protected)/properties/[id]/+page.svelte`). A landlord navigates *by operation, not by unit*.

**Goal:** make the **unit the central object** — open a unit and its whole story (state, lease, rent, maintenance, documents, expenses, timeline, lifecycle position) is in one place. This mirrors the EdiPlatform restructure where the trading partner became a real object-page with a stage rail; here the unit is that object.

**This is additive.** All existing operation pages (Money, Work Orders, Leases, Documents, search) stay — they remain the cross-unit triage surfaces. The unit page is the primary *drill-down destination*.

## 2. Scope

**In scope (v1):**
- New route `/units/[id]` — the Unit Command Center page (header + lifecycle rail + work tabs + persistent timeline rail).
- New route `/units` — a simple, filterable Units list (entry point + portfolio-wide unit view).
- New aggregate endpoint `GET /api/v1/units/{id}/dashboard`.
- A small data-model add: optional `Expense.UnitId`.
- Sidebar: add **Units** to the Rentals group; deep-link existing rows (payments / work orders / leases / dashboard items) into the relevant unit tab.

**Out of scope / deferred (do NOT build):**
- Portfolio **Lifecycle Board** (kanban of units) and any **stored** `UnitLifecycleStage` column. The rail in v1 is *derived/computed*, read-only.
- **Command Bundles** (grouping a scan's record cascade into one timeline event) — needs `AuditLog.CorrelationId`; deferred.
- **Messages/Notes** tab (messaging is tenant/lease-scoped today; its own design later).
- **Turnover module**, **mortgage/debt modeling**, **true cash-flow view** — separate tracked efforts (TSK-386/388/389 cluster), not this spec.

## 3. Information architecture & navigation

- **Coexist.** Keep every existing operation page. Add the unit object on top.
- **Entry points into a unit:** (a) the property page units grid → `/units/[id]`; (b) the new `/units` list; (c) deep links from operation rows and dashboard attention items, landing on the right tab via `/units/[id]?tab=<tab>` (e.g. a payment row → `?tab=rent`, a work order → `?tab=maintenance`, a lease/expiring item → `?tab=lease`).
- **Sidebar:** add **Units** under the Rentals group in `web/src/lib/components/AppShell.svelte`.

## 4. Page anatomy (`/units/[id]`)

Matches `rental-command-unit-command-center.png`:

1. **Header — unit health summary.** Property + unit number + current state; chips for rent state, open repairs, lease-ends-in, docs-needing-review, current tenant. Primary actions: **Scan / Upload** (visually prominent — core product strength) and a **New** quick-action menu (post payment / add expense / create work order / upload document). No explicit "Change Stage" button in v1 (see §5).
2. **Lifecycle rail** — the 9 stages with the current one highlighted, prior ones marked done, plus a **next-best-action** line. Derived, read-only (§5).
3. **Work tabs** — Overview · Lease · Rent · Maintenance · Documents · Expenses · Timeline (§6). Each scoped to this unit.
4. **Persistent timeline right-rail** — recent unit history, visible across tabs; reuse `ActivityFeed` (§11).

## 5. Lifecycle rail — derived stage

Stages: **Ready → Listed → Applicant → Lease → Move-In → Active → Renewal → Move-Out → Turnover** (→ Ready again).

There is **no stored stage**. `UnitStatus` today is `{ Vacant, Occupied, Reserved, Offline }` (`RentalCommand.Core/Enums/UnitStatus.cs`) — occupancy only. The stage is **computed per-unit** in the dashboard service from already-fetched related data (a pure O(1) function over the loaded aggregate — NOT a per-row scan). Decision tree (first match wins):

```
current lease = the unit's Active lease, else most-recent lease
IF current lease is Active:
    IF lease.Status == NoticeGiven                      -> Move-Out
    ELIF lease.StartDate in future OR a MoveIn appt upcoming
         OR lease.StartDate within last 14 days         -> Move-In
    ELIF lease.EndDate within 90 days                   -> Renewal
    ELSE                                                -> Active
ELIF a lease exists in {Draft, PendingSignature}        -> Lease
ELSE (unit not leased):
    IF an open RentalApplication {Submitted, UnderReview, Approved}
       not yet converted to a lease                     -> Applicant
    ELIF recent move-out / make-ready signal
         (last lease Expired|Terminated recently, OR a MoveOut
          inspection done, OR Unit.Status == Offline, OR open
          WorkOrders on the unit)                       -> Turnover
    ELIF a Showing appointment is scheduled (upcoming)  -> Listed
    ELSE                                                -> Ready
```

This is a best-effort heuristic; edge cases resolve to the nearest sensible stage. It is the seed for a future stored stage + board — keep the derivation isolated in one service method (`UnitLifecycleStageResolver` or similar) so it can later be promoted to a persisted/recomputed field.

**Next-best-action per stage** (deep-links into the relevant flow/drawer):
- Ready → "List this unit" · Listed → "Review applicants / schedule showing" · Applicant → "Screen & decide on {applicant}" · Lease → "Send lease for signature / mark signed" · Move-In → "Confirm move-in / collect deposit" · Active → "Rent on track" or "Collect ${balance}" if a balance is due · Renewal → "Send renewal — lease ends in {n} days" · Move-Out → "Schedule move-out inspection" · Turnover → "Track make-ready / mark rent-ready" (full turnover module deferred).

## 6. Tabs (shows / actions / data source)

Each tab reuses an **existing filtered list endpoint** for its content; the only new endpoint is the dashboard aggregate (§8). All actions open in the **Command Drawer** (§7), not modals.

- **Overview** — snapshot (beds/baths/market rent), current tenant + lease summary, rent state, open repairs, docs/AI awaiting review, next actions, recent activity. *Source:* dashboard aggregate.
- **Lease** — current lease (status, dates, rent, deposit, renewal terms) + signed lease & related docs. *Actions:* upload/extract lease, generate & send renewal, mark signed. *Source:* the unit's current lease (`GET /leases/{id}`); reuse existing lease detail components.
- **Rent** — this unit's rent schedule, charges, payments, balance, late fees, receipts, history. *Actions:* post payment, add charge/credit, upload payment doc, send reminder. *Source:* payments for the unit's lease ids (`GET /payments?leaseId=…`; extend filter if needed).
- **Maintenance** — open/closed work orders, photos, vendor assignments, invoices, **plus the expenses tied to those work orders** (snap-a-receipt → expense linked to the work order). *Actions:* create ticket, attach photo, assign vendor, upload invoice, close. *Source:* `GET /workorders?unitId=…`; expenses where `WorkOrderId ∈` those.
- **Documents** — every file on the unit *and its children* (lease, work orders, payments, inspections), grouped by type. *Actions:* scan/upload, review AI extraction, re-attach, change type. *Source:* `StoredFile` by the unit's `(EntityType,EntityId)` set; reuse scan/draft-confirm UI (`/scan/[draftId]`).
- **Expenses** — all unit-relevant expenses (incl. non-maintenance: appliances, permits, unit-specific costs). *Actions:* create expense, attach receipt, link to a work order. *Source:* expenses where `UnitId = unit` OR `WorkOrderId ∈` the unit's work orders (requires §9).
- **Timeline** — deep, filterable full history (the right-rail is the always-on summary; this tab is the archive). *Source:* §11.

## 7. Command Drawer pattern

Adopt a reusable right-side **Command Drawer** (reuse `web/src/lib/components/ui/drawer/*`) for the unit page's actions — scan review, post payment, create work order, add expense, etc. — with unit context preloaded. This replaces modals **for the new unit-page actions only**; do not rip out modals elsewhere. The drawer always shows context (unit, what will be created/updated) and confirms with an explicit primary action.

## 8. Backend — `GET /api/v1/units/{id}/dashboard`

Mirror the existing Portfolio dashboard pattern (`GET /api/v1/portfolios/{id}/dashboard` → `DashboardResponse`). The Unit controller inherits `AuthenticatedPortfolioControllerBase` (portfolio-scoped; keep the cross-tenant IDOR guard — the unit must be in the caller's portfolio).

**`UnitDashboardResponse` (sketch):**
```
unit:            UnitResponse
lifecycleStage:  string                 // computed (§5)
nextBestAction:  { label, href }
header:          { rentState, openWorkOrderCount, leaseEndsInDays?,
                   docsNeedingReviewCount, currentTenantName? }
currentLease:    LeaseSummary?
currentTenant:   TenantSummary?
overview:        { recentPayments[], openWorkOrders[],
                   pendingDocs[], upcomingAppointments[] }   // each capped (~5)
recentTimeline:  AuditLogEntry[]          // capped (~15), see §11
```

**HARD RULE — server-side aggregation (project data-access rule):** every count/sum/group MUST translate to SQL (EF-translated or a view). NO in-memory grouping, NO load-then-loop, NO N+1. Concretely, this endpoint is a *handful* of set-based queries, e.g.:
1. Unit + Property.
2. Current lease (+ tenant) — most-recent Active, else latest.
3. Rent state — a SQL `SUM`/`GROUP` over `Payment` where `LeaseId ∈` the unit's leases (outstanding / overdue), not materialize-then-sum.
4. Counts — open work orders, pending docs (one grouped query each or combined).
5. Upcoming appointments (`Appointment` where `UnitId`, future).
6. Recent timeline (§11) — one audit query.

The lifecycle stage is derived in the service from values already fetched in (1)–(5) — a pure function, not another query and not a row loop. **Inspect the generated SQL to confirm no client-side evaluation.**

**Per-tab heavy data is NOT in this endpoint** — tabs lazy-load via the existing filtered list endpoints (§6), so opening a unit stays fast regardless of history size.

## 9. Data-model change — `Expense.UnitId`

Add nullable `int? UnitId` (FK → `Unit`, indexed) to `Expense` (`RentalCommand.Core/Entities/Expense.cs`). Today an expense links only to `Property` / `Vendor` / `WorkOrder` — a non-maintenance unit expense has nowhere to live.

- EF migration: `dotnet ef migrations add AddExpenseUnitId --project RentalCommand.Data --startup-project RentalCommand.Api` (PostgreSQL/Npgsql only).
- Wire `UnitId` through the Expense create/update DTOs + service + the create/edit forms.
- Expenses tab query: `UnitId = unit` OR `WorkOrderId ∈` the unit's work orders.
- Validate `UnitId` is in the caller's portfolio (IDOR guard, consistent with other FK refs).

## 10. `/units` list page

New `web/src/routes/(protected)/units/+page.svelte` — a filterable `DataGrid` of all units in the portfolio with health badges: unit (property + number), status, open repairs, lease-ends, docs-to-review, and a **simplified** stage label.

**Scale guard:** the list's health/badge fields MUST be computed **DB-side in one projection query (or a view)** — do NOT call the per-unit dashboard for each row (that would be N+1). To keep the list query cheap, the list may use a *simplified* status (Unit.Status + active-lease status + an open-WO count via a grouped join); the full 9-stage derivation (§5) runs only on the detail page. Extend `GET /api/v1/units` (or add a list-with-health projection) accordingly.

## 11. Timeline

Reuse `web/src/lib/components/shared/ActivityFeed.svelte`. Source = the unified `AuditLog` (`RentalCommand.Core/Entities/AuditLog.cs`; fields PortfolioId, EntityType, EntityId, Operation, OldValues/NewValues, ActorLabel, Timestamp). There is **no `CorrelationId`** today, so v1 shows per-entity events (no command-bundle grouping).

Build the unit's event set with a **bounded, set-based** query: collect the unit's child ids (its lease ids; payment ids for those leases; work-order / inspection / appointment ids with `UnitId`; expense ids via `UnitId`/work-order) with a few indexed selects, then **one** `AuditLog` query:
```
WHERE PortfolioId = @p AND (
   (EntityType='Unit'      AND EntityId = @unitId) OR
   (EntityType='Lease'     AND EntityId IN @leaseIds) OR
   (EntityType='Payment'   AND EntityId IN @paymentIds) OR
   (EntityType IN ('WorkOrder','Inspection','Appointment') AND EntityId IN @childIds) OR
   (EntityType='Expense'   AND EntityId IN @expenseIds)
) ORDER BY Timestamp DESC LIMIT @n
```
This is a fixed, small number of queries regardless of data size (not N+1). The right-rail shows the latest ~15; the Timeline tab pages + filters by category.

## 12. Reuse inventory

- **Components:** `ActivityFeed.svelte`, `WorkOrderTimeline.svelte`, `StatusBadge.svelte` (`web/src/lib/components/shared/`); `ui/drawer/*`, `ui/tabs/*`; the `DataGrid` used on existing list pages; `AppShell.svelte` sidebar.
- **Scan/draft-confirm:** `/scan/[draftId]`, `/scan/batch/[id]` + extraction-review UI.
- **API client:** `web/src/lib/api/client.ts` (`fetchApi`/`api`) — bearer + refresh-on-401.
- **Backend pattern:** Portfolio dashboard service/DTO; `AuthenticatedPortfolioControllerBase`; enums serialize as **strings** (`JsonStringEnumConverter`) — keep new DTO enums string-valued.

## 13. Acceptance criteria

1. `/units/[id]` renders header (health chips), the lifecycle rail with the **correct derived stage**, 7 tabs, and a persistent timeline rail.
2. `GET /units/{id}/dashboard` returns in a handful of EF-translated SQL queries — **verified by inspecting the generated SQL**: no N+1, no in-memory grouping, rent-state is a SQL aggregate.
3. `/units` lists all portfolio units with health badges from **one** projection query (no per-row dashboard calls).
4. An expense can be tied to a unit (`UnitId`) and shows in the unit's Expenses tab; a work-order receipt shows under Maintenance.
5. Deep links from a payment / work-order / lease / dashboard item land on the correct unit + tab.
6. Stage derivation matches §5 for representative cases: occupied steady → Active; lease ending ≤90d → Renewal; `NoticeGiven` → Move-Out; vacant + showing → Listed; vacant idle → Ready; open application, no lease → Applicant.
7. Portfolio scoping/IDOR enforced on the unit and all referenced FKs.
8. Existing operation pages and flows are unchanged and still work (coexist).

## 14. Future hooks (not now)

- Derived stage → later promote to a stored `UnitLifecycleStage` + the portfolio **Lifecycle Board**.
- The **Turnover** stage is where the turnover/make-ready module (separate task) will plug in as a stage workspace.
- `Expense.UnitId` enables future unit-level P&L.
- `AuditLog.CorrelationId` → later enables **Command Bundles** in the timeline.
