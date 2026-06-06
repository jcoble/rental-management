# Unify Activity feed + Audit log — one real trail, working viewer

**Notion task:** TSK-11 (Rental Command) — High (legally-sensitive trail)
**Status:** Spec — approved in brainstorming 2026-06-06; not yet implemented.
**Scope:** the **MVP slice** only. Deep legal diffs, per-record History tabs, and the
admin-only deep view are explicitly deferred (see *Deferred* at the end).

## Problem

`/activity` reads the `ActivityLog` table, but **nothing writes to it** — so the page (and
the dashboard "recent activity" widget, which reads the same table) is always empty. A dead
viewer.

Meanwhile a real, append-only `AuditLog` already exists and is populated by
`AuditTrailService.LogAsync` (`RentalCommand.Api/Services/AuditTrailService.cs`) from Scan,
Screening, Lease e-sign, Vendor dispatch, W-9, inbound SMS, and Applications — capturing
actor / IP / operation / old→new values / timestamp / reason. But it has **no controller and
no UI**, so all of that is invisible.

Two trails, both useless to the user: one empty-with-a-viewer, one populated-but-invisible.

## Goal

Collapse to **one trail (`AuditLog`)** with a **working viewer**, and make the trail actually
fill up by auto-recording create / edit / delete / status-change on every business entity.

Current state verified:
- `RentalCommand.Core/Entities/ActivityLog.cs` — empty table; only read by `ActivityService`,
  `DashboardService` (recent activity), and `SandboxService` (wipes it on graduation).
- `RentalCommand.Core/Entities/AuditLog.cs` — rich schema (PortfolioId, UserId?, ActorLabel?,
  EntityType, EntityId, Operation `AuditLogOperation`, OldValues/NewValues jsonb, ChangeReason,
  Timestamp, IpAddress); append-only; written via `IAuditTrailService` from ~8 services.
- `AuditLog` already exists in `20260530231947_InitialCreate`; indexes on PortfolioId,
  (EntityType, EntityId), Timestamp, UserId.

## Capture mechanism — Hybrid (decided)

Interceptor as the engine for generic CRUD **+** keep the existing explicit `LogAsync` calls
for the legally-rich semantic events. The interceptor **defers to** an explicit log for the
same (entityType, entityId, operation) recorded in the same `SaveChanges`, so the trail never
double-records.

(Rejected: explicit-only — tedious and forgettable, exactly how `ActivityLog` ended up empty.)

## Design — 6 pieces

### 1. `AuditSaveChangesInterceptor` (`RentalCommand.Data`)

A `SaveChangesInterceptor` registered on `RentalCommandDbContext`.

- **`SavingChanges`**: walk `ChangeTracker.Entries()`; for each entry whose entity implements
  `IAuditable` and is `Added` / `Modified` / `Deleted`, snapshot a pending audit:
  - operation: `Added → Created`, `Deleted → Deleted`, `Modified → Updated`
    (**soft-delete special case**: a `Modified` entry that sets `DeletedAt` from null → value
    is recorded as **`Deleted`**, not `Updated`).
  - values: `Modified` → only changed properties as `{prop: {old, new}}`; `Created` → full
    snapshot in `NewValues`; `Deleted`/soft-delete → snapshot in `OldValues`.
  - **PII redaction**: a property deny-list (SSN, tax IDs, secrets/tokens, password hashes)
    is dropped from the JSON. (Business entities are in scope; Identity tables are not marked
    `IAuditable`, so auth secrets are already excluded.)
- **`SavedChanges`**: DB-generated PKs are now populated → fill each pending audit's `EntityId`,
  add the `AuditLog` rows, and persist. A **re-entrancy guard** (instance flag) prevents the
  audit-write save from recursing; `AuditLog` is not `IAuditable` so it is never audited.
  Audit rows are written through the same context/transaction as the change (atomic).
- **De-dupe with explicit logs**: the interceptor and the explicit `AuditTrailService.LogAsync`
  path coordinate through a request-scoped `IAuditScope` keyed by (entityType, entityId,
  operation) so a given change is recorded once — the rich explicit event wins, the generic
  twin is suppressed. This must be **order-independent**: explicit `LogAsync` can run either
  before or after the entity's `SaveChanges`. The implementation plan settles the exact
  mechanism (e.g. explicit path registers intent in the scope *before* its own flush, and/or
  the interceptor enriches rather than duplicates an already-scoped key); the requirement is:
  **never two rows for one (entityType, entityId, operation) in a single request.**

**Which entities (opt-in marker `IAuditable`, `RentalCommand.Core`):** Payment, Expense, Lease,
Tenant, Property, Unit, WorkOrder, Vendor, OwnerEntity, Appointment, Inspection,
RentalApplication, Deposit (if present), Portfolio. Infra/high-volume tables are simply never
marked → zero noise: `AuditLog`, `ActivityLog` (being dropped), `OutboxMessage`, refresh
tokens, ASP.NET Identity, **`BankTransaction` (excluded — Plaid sync would flood the trail)**,
notification/SMS log tables. A future entity opts in by adding the marker.

**PortfolioId**: read from the entity via an `IPortfolioScoped { int PortfolioId }` interface
(most business entities already carry `PortfolioId`); entities without it (e.g. Portfolio
itself) supply their own id.

### 2. `ICurrentActor` (actor attribution, `RentalCommand.Api` + ambient for Engine)

Scoped service resolving who/where for a write:
- API requests: authenticated user's int id + display name from claims via
  `IHttpContextAccessor`; client IP from `HttpContext.Connection.RemoteIpAddress`, honoring
  Traefik's `X-Forwarded-For`.
- Engine workers (no HttpContext): an ambient (`AsyncLocal`) actor label set by the worker
  (e.g. `engine:RentChargeWorker`), defaulting to `system`.

The interceptor reads `ICurrentActor` to fill `UserId` / `ActorLabel` / `IpAddress`.

### 3. `AuditDescriber` (humanizer, `RentalCommand.Api`)

Deterministic (no LLM) `(operation, entityType, values) → plain English` for the non-technical
landlord. Examples: *"Recorded a $1,200 rent payment"*, *"Lease L-001 — status Active →
Terminated"*, *"Deleted expense 'Roof repair'"*, *"Added work order: Leaky faucet"*. Generic
fallback: *"{Operation} {EntityType} #{Id}"*. Per-entityType mapping; soft-deletes phrased as
"Deleted". This is what makes the audit trail read like a friendly activity feed.

### 4. `AuditController` + `AuditQueryService` (`/api/v1/audit`)

Replaces `ActivityController` / `ActivityService` (both deleted). `AuditController :
AuthenticatedPortfolioControllerBase` — portfolio-scoped with the standard cross-tenant IDOR
guard.
- `GET /api/v1/audit` — filters: `search` (actor/description/entity), `operation`
  (`AuditLogOperation`), `entityType`, `entityId`, `from`/`to` date, plus `ListQuery`
  (skip/take/sort; default `-timestamp`, newest first).
- **MVP DTO** (`AuditEntryResponse`): `id, portfolioId, operation, operationName, entityType,
  entityId, actor (label), description (humanized), detailHref (link to the entity's detail
  page), timestamp`. **IP and raw old→new JSON are intentionally NOT in the MVP DTO** — they
  belong to the deferred admin deep-view.

### 5. Web viewer (`web/`)

Repoint at `/api/v1/audit`; keep the friendly **"Activity"** label (nicer than "Audit log" for
a landlord), now backed by the real trail.
- `web/src/lib/api/endpoints/activity.ts` → call `/audit`; new `AuditEntry` type in
  `web/src/lib/types/index.ts` (replacing the `ActivityLog` interface usage).
- `web/src/routes/(protected)/activity/+page.svelte` — filters become operation + entityType +
  search + date range, via the existing responsive `DataGrid`; rows link via `detailHref`.
- `web/src/lib/components/shared/ActivityFeed.svelte` + the dashboard recent-activity widget →
  same new endpoint/type. Both come alive.

### 6. Retire `ActivityLog`

- Delete `ActivityLog.cs`, `RentalActivityType.cs` (if unused elsewhere), `IActivityService` /
  `ActivityService`, `ActivityController`, `ActivityDtos`, and the `ActivityLogs` `DbSet` + EF
  config in `RentalCommandDbContext.cs`.
- Repoint `DashboardService` (recent activity) and `SandboxService` (graduation cleanup clears
  the sandbox portfolio's `AuditLogs` instead of `ActivityLogs`) at `AuditLog`.
- **One migration**: drop the empty `ActivityLogs` table; add composite index
  `IX_AuditLogs_PortfolioId_Timestamp` (PortfolioId, Timestamp desc) for the paged viewer query.
  No data loss (table is empty).

## Testing (focused — per project posture, a few targeted tests)

- **Interceptor**: create / update / soft-delete an `IAuditable` entity → asserts the expected
  `AuditLog` row (operation, entityId, changed values, actor); asserts `AuditLog` itself is not
  audited; asserts de-dupe when an explicit `LogAsync` already covered the same op.
- **Humanizer**: a handful of (entityType, operation) → expected sentence.
- **AuditQuery**: filters + portfolio scoping (cross-tenant request denied / returns nothing).
- **Web**: ~1 smoke that `/activity` renders real rows from `/audit`.

## Risks / notes

- The interceptor is the riskiest piece: re-entrancy (guarded), PK timing (write EntityId in
  `SavedChanges`), actor attribution (HttpContext vs ambient), performance (allow-list keeps
  volume sane), and PII (property deny-list). It is isolated in one class and directly tested.
- Most existing explicit `LogAsync` calls are `Updated`-with-reason or `Approved`/`Rejected`
  semantic events; the de-dupe keeps those authoritative and suppresses the generic twin.
- `AuditLog` already exists, so the migration only **drops** a table and **adds** an index.

## Deferred (separate fast-follow tasks — NOT in this slice)

- Deep legal old→new diffs on Leases (create/edit/terminate), **signatures**, deposits/refunds,
  payment reversals.
- Per-record **"History" tab** on detail pages (Lease, Payment, …).
- **Admin-only deep view** (`/admin/audit`): IP address + raw old→new JSON, surfaced from the
  fields the MVP DTO holds back.
