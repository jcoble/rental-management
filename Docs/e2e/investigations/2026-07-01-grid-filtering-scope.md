# Grid Date/Period Filtering — Scope + Decisions (2026-07-01)

**Source:** GridFilterScout (read-only). **Headline:** the premise ("RC lacks server-side grids /
filters in memory, mirror EdiPlatform") is **inverted by evidence** — RC already has a shared
server-side pagination/sort/filter stack that's *better* than EdiPlatform's. The real gap is narrow:
propagate the date-range filter that already works on the Accounting grid to the other temporal
grids, reusing existing components; plus one genuine unbounded-sub-grid hardening item.

## RC already has (verified)
- **Shared server-side DTO** `RentalCommand.Api/DTOs/ListQuery.cs` (Skip/Take clamped MaxTake=200,
  Search, Sort with `-field` desc), subclassed per entity. **All 12 top-level grids page/sort/
  filter/count/aggregate DB-side.** Backend audit: **zero** post-materialization
  `.Where/.OrderBy/.Skip/.Take/.Sum/.Count/.GroupBy`, zero sync `.AsEnumerable()`, no N+1.
- **`DataGrid.svelte`** with `serverSide` mode (client sort/slice short-circuited when serverSide,
  `:280-302`), used by 13 pages; params via `buildListQuery` (`web/src/lib/api/list-params.ts:19-33`),
  URL state in `grid-url-state.svelte.ts`, calls through `api/client.ts`.
- **A working reference of the exact feature:** the Accounting ledger grid does server-side `from`/`to`
  date filtering end-to-end (`AccountingService.cs:433,482-492`; page
  `accounting/+page.svelte:84-85,155-156,1289-1290`; endpoint `accounting.ts:19-26,78-83`).
- **An already-built preset picker** `web/src/lib/components/shared/RangeDatePicker.svelte:113-155`
  (YTD / This year / Last 30 days / MTD / Last month) — used on reports/settings, **not yet on grids.**

## Per-grid state (all server-side; only Accounting has date-range)
Payments (`PaymentService.cs:153`), Expenses (`ExpenseService.cs:31`), WorkOrders
(`WorkOrderService.cs:68`), Activities/Audit (`AuditQueryService.cs:24`), Appointments
(`AppointmentService.cs:30`), Leases (`LeaseService.cs:550`), Tenants/Units/Properties/Vendors/
Inspections — all page/sort/filter in SQL; **none has a date-range filter** except Accounting.
Correlated child counts are DB-side subqueries (compliant).

## EdiPlatform comparison
EdiPlatform's grid architecture (URL-driven `+page.server.ts` loaders, hand-written 3-line paging per
grid, per-endpoint envelopes, **client-side sort**) is **weaker** than RC's shared pattern. The only
asset worth porting *if* per-column filtering is ever wanted: `EdiPlatform.Core/Querying/ColumnFiltering.cs`
(declarative, whitelisted, SQL-translatable `f.<col>=range:from,to`). Out of scope for a first pass.

## HARD-RULE audit
- **Top-level grids: clean** — no in-memory aggregation/paging/sort, no N+1, no lazy load. No changes.
- **Real scale defect (highest risk):** `web/src/lib/components/records/LeaseDetail.svelte:1594` sorts/
  slices a lease's **entire** payment set in JS, backed by `LeaseService.cs:712` which loads full
  payment history unpaged (`ToListAsync`, shapes ledger rows in memory `:746`). Bounded per-lease
  today, **grows unbounded over a multi-year tenancy.** (Totals are a separate DB aggregate `:811` — fine.)
- Lower-risk client-side detail sub-grids: `properties/[id]` units/leases (`:655,684`), `tenants/[id]`
  leases (`:402`), `PropertyRecurringExpensesSection.svelte:152`, `PropertyLoansSection.svelte:256`.
- **Envelope gaps (not violations):** Inspections (`InspectionService.cs:40`) + landlord Audit
  (`AuditController.cs:28`) return bare lists with no `TotalCount` → grid can't show "page X of N."

## Approach (extend RC's own pattern — do NOT port EdiPlatform's mechanics)
- **Backend:** add nullable `From`/`To` to `ListQuery` (or per-entity subclass where the date column
  differs) + a shared `ApplyDateRange(query, dateSelector, from, to)` helper, half-open `[from,to)`,
  mirroring `AccountingService.cs:482-492`. EF-translated, one SQL statement. **No new framework.**
- **Frontend:** add `from`/`to` to each `*ListParams` (via `buildListQuery` extra bag like
  `accounting.ts`) + drop the existing `RangeDatePicker` (presets) into each grid toolbar, URL-persisted.
- **DB:** covering index on the filtered date column (portfolio-scoped) for high-volume tables
  (Payments, Expenses, audit). No new views.

## Task breakdown (sequenced, highest-value first)
1. Backend foundation — `ListQuery.From/To` + `ApplyDateRange` helper (half-open, DB-side).
2. **Payments** date filter + covering index (highest-volume temporal table; Schedule-E relevant).
3. **Expenses** date filter + covering index (tax-year scoping).
4. **Activities/Audit** `Timestamp` filter + covering index (fastest-growing; optionally add `TotalCount`).
5. **WorkOrders** date filter. 6. **Appointments** date filter (it's calendar data — surprising omission).
7. Frontend toolbar rollout (`RangeDatePicker` + URL state) across grids 2–6 (mechanical).
8. (Lower) Leases/Inspections filters; Tenants/Units/Properties/Vendors deferrable.
9. **(Separate hardening track)** lease-payments → serverSide paging (`LeaseService.cs:712` +
   `LeaseDetail.svelte:1594`); add `TotalCount` to Inspections + landlord Audit.

## Orchestrator decisions on the 6 open questions

> **User-confirmed (2026-07-01):** (1) use RC's pattern, per-column filtering OFF for now; (2) date
> columns = orchestrator's best judgment (below); (3) range picker now, per-column later.
> **Absolute mandate (reiterated by the user):** ALL filtering runs on the PostgreSQL server
> (EF→SQL, ONE statement) — NEVER materialized into the API/Engine and filtered in memory, and
> ESPECIALLY never filtered on the client. This makes the client-side sub-grid conversions
> (decision 6) MANDATORY, not optional.
1. **Confirm "mirror EdiPlatform" intent** → **Extend RC's own (superior) pattern**, do NOT adopt
   EdiPlatform's loader/URL/client-sort mechanics. (Satisfies the user's "like EdiPlatform" intent
   better — RC already has the bones.) Port `ColumnFiltering.cs` only if per-column filtering is later wanted.
2. **Date column per entity (single fixed column, first pass):** Payments → **PaidDate** (cash-basis;
   matches how income buckets + the reconciliation spine); Expenses → **PaidAt** (Schedule E cash-basis);
   WorkOrders → **RequestedAt**; Appointments → **ScheduledStart**; Audit → **Timestamp**. A column
   selector (e.g. Payments DueDate ↔ PaidDate) is a fast-follow, not first pass.
3. **Single date-range per grid** now (reuse `RangeDatePicker`); per-column filters out of scope.
4. **Presets** = the existing `RangeDatePicker` set; **default = unbounded** on first load (server-side
   paging already caps the first page, so no scale problem and no surprising hidden data).
5. **Boundary/tz — INVESTIGATED (user asked for the best answer, not a default):** column-type-aware,
   because the target date columns split two ways (all verified `DateTime`):
   - **Date-only fields stored UTC-midnight** (`Payment.PaidDate`, `Expense.PaidAt`/`IncurredAt`, `DueDate`):
     filter in **UTC-day terms** — `>= from.ToUtc() && < to.ToUtc().AddDays(1)` — **exactly matching
     `AccountingService.GetTransactionsAsync:482-492`** and the web's UTC-pinned date-only handling
     (`date.ts`). No tz conversion (these are timezone-less calendar dates); this makes the money grids
     **agree with the Accounting grid**.
   - **True-instant fields** (`AuditLog.Timestamp`, `Appointment.ScheduledStart`, `CreatedAt` — real
     time-of-day): convert the picked `[from, to]` day boundaries from the **business tz**
     (`Portfolio.TimeZone` via the unified `IAppTimeZoneProvider`) to UTC instants; filter half-open
     `>= fromUtcInstant && < startOfNextDayAfterTo(businessTz)→UTC`, so "this month" = the landlord's
     local month (an 11pm-ET-Dec-31 audit event stays in Dec, not next-year-UTC).
   All DB-side (EF→SQL). **Depends on the clock's `IAppTimeZoneProvider`** — build that seam once (clock),
   reuse in grids + gaps F3. Half-open throughout.
6. **HARD-RULE hardening (item 9): IN + EXPANDED** — per the user's absolute no-client-filtering
   mandate, convert **all** client-side detail sub-grids to `serverSide` (DB-side paging/sort/filter),
   highest-risk first: lease payments (`LeaseService.cs:712` unpaged + `LeaseDetail.svelte:1594` JS
   slice), then `properties/[id]` units+leases, `tenants/[id]` leases, `PropertyRecurringExpensesSection`,
   `PropertyLoansSection`. Add `TotalCount` to Inspections + landlord Audit lists. This is a real defect
   track, not optional polish.

**Sizing:** small subsystem (extend an existing pattern + one hardening item), not a from-scratch build.
Queues in the serialized build order after the clock/gaps; items 2–6 parallelize once (1) lands.
