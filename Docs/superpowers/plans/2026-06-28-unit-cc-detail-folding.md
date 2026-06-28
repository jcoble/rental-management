# Unit Command Center — Detail Folding (TSK-457) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fold the standalone Lease/WorkOrder/Expense/Payment/Application detail pages into the Unit Command Center tabs — one detail component per record, mounted both inside the unit tab (when the record is unit-tied) and inside the existing generic page (otherwise), with a single routing resolver re-pointing every grid row/link.

**Architecture:** Extract each detail page body into a route-independent `<XDetail recordId onDeleted>` component. Unit tabs render the component inline on row-select (URL: `/units/{id}?tab=…&{param}={recordId}`); the generic `/…/[id]` routes become thin wrappers around the same component. A shared `recordHref(type, record)` resolver returns the unit-tab URL when unit-tied, else the generic URL. Mirrors the EdiPlatform order-workspace pattern (which the Unit CC already half-follows).

**Tech Stack:** SvelteKit 5 (runes), TanStack Query, Tailwind, bits-ui Tabs, .NET 10 API (EF Core/Npgsql) for the one DTO change.

**Spec:** `Docs/superpowers/specs/2026-06-28-unit-cc-detail-folding-design.md`

## Global Constraints

- **SvelteKit 5 runes** (`$state/$derived/$effect/$props`). Reuse existing components: `DetailCard`, `HelpPopover`, `$lib/components/ui/tabs`, `DataGrid`, `RecordHistory`, `DocumentsPanel`. Do NOT introduce new detail UI — extract what exists.
- **Detail components must be route-independent:** take the record id as a prop (NOT `page.params`), do their own TanStack queries keyed on that prop, and report deletes/redirects via an `onDeleted` callback prop. They render identically in a unit tab and in the generic page.
- **One resolver:** `recordHref` is the only place that decides unit-tab-vs-generic. Every grid row / link / redirect calls it.
- **URL params** (record selection inside a unit tab): `tab=maintenance&wo=`, `tab=lease&lease=`, `tab=expenses&expense=`, `tab=rent&payment=`, `tab=applications&app=`.
- **⛔ SQL rule:** the Payment DTO `unitId` is populated DB-side via the lease join in the existing projection — ONE SQL statement, no in-memory join, no N+1.
- **Enums serialize as string names** (preserve `JsonStringEnumConverter`).
- `data-testid` on new interactive elements; keep existing testids.
- Commits: clear subject + body, **NO `Co-Authored-By`**.
- Verify each task: `cd web && pnpm exec svelte-check --tsconfig ./tsconfig.json --threshold error` (0 errors) + `pnpm test:unit` for any `*.test.ts` touched; backend task: `MSBUILDDISABLENODEREUSE=1 dotnet build` + focused `dotnet test --filter`.

## File structure

- **Create** `web/src/lib/navigation/record-href.ts` + `record-href.test.ts` — the resolver (pure, unit-tested).
- **Create** `web/src/lib/components/records/{WorkOrderDetail,ExpenseDetail,PaymentDetail,LeaseDetail,ApplicationDetail}.svelte` — extracted detail bodies.
- **Create** `web/src/lib/components/unit/tabs/ApplicationsTab.svelte`.
- **Modify** `web/src/lib/components/unit/unit-tabs.ts` (+`'applications'`), `web/src/routes/(protected)/units/[id]/+page.svelte` (Applications tab/content), the 4 existing tabs (host the detail on select), the 5 generic `[id]` routes (→ wrappers), the Payment DTO (backend), and the link sites in §Task 8.

---

### Task 1: `recordHref` routing resolver

**Files:**
- Create: `web/src/lib/navigation/record-href.ts`
- Test: `web/src/lib/navigation/record-href.test.ts`

**Interfaces:**
- Produces: `recordHref(type: RecordType, rec: RecordRef): string` where
  `type = 'lease'|'workOrder'|'expense'|'payment'|'application'` and
  `RecordRef = { id: number; unitId?: number | null }`. Returns the unit-tab URL when `unitId`
  is a positive number, else the generic detail URL. (Payment callers pass the unit resolved
  via the lease — see Task 5; `recordHref` itself only looks at `unitId`.)

- [ ] **Step 1: Write the failing test** — `web/src/lib/navigation/record-href.test.ts`

```ts
import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { recordHref } from './record-href.ts';

describe('recordHref', () => {
	it('routes a unit-tied work order to the unit Maintenance tab', () => {
		assert.equal(recordHref('workOrder', { id: 12, unitId: 3 }), '/units/3?tab=maintenance&wo=12');
	});
	it('routes a property-only work order (no unit) to the generic page', () => {
		assert.equal(recordHref('workOrder', { id: 12, unitId: null }), '/maintenance/12');
	});
	it('routes lease / expense / payment / application to the right tab + param', () => {
		assert.equal(recordHref('lease', { id: 5, unitId: 3 }), '/units/3?tab=lease&lease=5');
		assert.equal(recordHref('expense', { id: 7, unitId: 3 }), '/units/3?tab=expenses&expense=7');
		assert.equal(recordHref('payment', { id: 9, unitId: 3 }), '/units/3?tab=rent&payment=9');
		assert.equal(recordHref('application', { id: 2, unitId: 3 }), '/units/3?tab=applications&app=2');
	});
	it('falls back to the generic page when unitId is missing/0', () => {
		assert.equal(recordHref('expense', { id: 7, unitId: 0 }), '/accounting/expenses/7');
		assert.equal(recordHref('payment', { id: 9 }), '/accounting/payments/9');
		assert.equal(recordHref('application', { id: 2, unitId: null }), '/applications/2');
	});
});
```

- [ ] **Step 2: Run it, verify FAIL** — `cd web && pnpm exec node --test --experimental-strip-types src/lib/navigation/record-href.test.ts` → fails (module not found).

- [ ] **Step 3: Implement** — `web/src/lib/navigation/record-href.ts`

```ts
export type RecordType = 'lease' | 'workOrder' | 'expense' | 'payment' | 'application';
export type RecordRef = { id: number; unitId?: number | null };

const UNIT_TAB: Record<RecordType, { tab: string; param: string }> = {
	lease: { tab: 'lease', param: 'lease' },
	workOrder: { tab: 'maintenance', param: 'wo' },
	expense: { tab: 'expenses', param: 'expense' },
	payment: { tab: 'rent', param: 'payment' },
	application: { tab: 'applications', param: 'app' }
};

const GENERIC: Record<RecordType, (id: number) => string> = {
	lease: (id) => `/leases/${id}`,
	workOrder: (id) => `/maintenance/${id}`,
	expense: (id) => `/accounting/expenses/${id}`,
	payment: (id) => `/accounting/payments/${id}`,
	application: (id) => `/applications/${id}`
};

/** Unit-tied record → its unit Command Center tab; otherwise the generic detail page. */
export function recordHref(type: RecordType, rec: RecordRef): string {
	if (rec.unitId && rec.unitId > 0) {
		const { tab, param } = UNIT_TAB[type];
		return `/units/${rec.unitId}?tab=${tab}&${param}=${rec.id}`;
	}
	return GENERIC[type](rec.id);
}
```

- [ ] **Step 4: Run it, verify PASS** — same command → all pass.
- [ ] **Step 5: Commit** — `git add web/src/lib/navigation/record-href.* && git commit -m "feat(nav): recordHref resolver — unit-tab vs generic detail routing (TSK-457)"`

---

### Task 2: Payment DTO carries `unitId` (DB-side via lease join)

**Files:**
- Modify: the Payment response DTO in `RentalCommand.Api/DTOs/` (find the `PaymentResponse`/`PaymentDto` class) — add `int? UnitId` + `int? PropertyId`.
- Modify: the payment projection in `RentalCommand.Api/Services/Domain/PaymentService.cs` (the `.Select(p => new PaymentResponse{…})` over `_db.Payments`) — set `UnitId = p.Lease.UnitId`, `PropertyId = p.Lease.PropertyId` (EF translates the lease join into the same SQL statement).
- Test: `RentalCommand.Api.Tests/.../PaymentServiceTests` (extend or create) — assert a listed/returned payment exposes its lease's `UnitId`, and that resolution is a single set-based query (no per-row lease fetch).

**Interfaces:**
- Produces: `PaymentResponse.UnitId: int?` + `.PropertyId: int?`. Web `Payment` type (`web/src/lib/types/index.ts:294`) gains `unitId?: number` + `propertyId?: number`.

- [ ] **Step 1: Write/extend the failing test** — assert `(await service.GetAsync(paymentId)).UnitId == lease.UnitId` for a payment whose lease is on a unit; capture the SQL (as the dashboard test does) to assert the lease is JOINed, not separately queried.
- [ ] **Step 2: Run, verify FAIL** — `MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests --filter "FullyQualifiedName~Payment"`.
- [ ] **Step 3: Implement** — add the two nullable props to the DTO; in the projection set them from `p.Lease.UnitId` / `p.Lease.PropertyId` (Payments always have a `LeaseId`; the lease has both). Add the fields to the web `Payment` type.
- [ ] **Step 4: Run, verify PASS** — same filter passes; `dotnet build-server shutdown`.
- [ ] **Step 5: Commit** — `git commit -am "feat(payments): expose unitId/propertyId on the Payment DTO via lease join (TSK-457)"`

---

### Task 3: Work-order vertical (the reference pattern)

**Files:**
- Create: `web/src/lib/components/records/WorkOrderDetail.svelte`
- Modify: `web/src/routes/(protected)/maintenance/[id]/+page.svelte` → thin wrapper
- Modify: `web/src/lib/components/unit/tabs/MaintenanceTab.svelte` (host on `?wo=`)
- Modify (re-point WO links): `web/src/routes/(protected)/maintenance/+page.svelte:365`, `web/src/routes/(protected)/maintenance/inspections/[id]/+page.svelte:318`

**Interfaces:**
- Produces: `<WorkOrderDetail workOrderId={number} onDeleted={() => void} />` — self-contained; queries `workOrders.get(workOrderId)`, renders the request/costs/vendor/status-history/documents/history sections (moved verbatim from the current page), runs its own status/dispatch/rate/delete dialogs, calls `onDeleted()` instead of `goto('/maintenance')`.

- [ ] **Step 1 — Extract the component.** Create `WorkOrderDetail.svelte`. MOVE the entire `<script>` + markup body of `maintenance/[id]/+page.svelte` into it, with these mechanical edits: (a) `let { workOrderId, onDeleted }: { workOrderId: number; onDeleted: () => void } = $props();`; (b) every `page.params.id` / the derived `id` → `workOrderId`; (c) the delete-success `goto('/maintenance')` → `onDeleted()`; (d) remove the top breadcrumb (the host supplies chrome) — keep the rest. Keep all `data-testid`s.
- [ ] **Step 2 — Thin-wrap the generic route.** Replace `maintenance/[id]/+page.svelte` body with:

```svelte
<script lang="ts">
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import WorkOrderDetail from '$lib/components/records/WorkOrderDetail.svelte';
</script>
<WorkOrderDetail workOrderId={Number(page.params.id)} onDeleted={() => goto('/maintenance')} />
```

- [ ] **Step 3 — Host in the Maintenance tab.** In `MaintenanceTab.svelte`: read `const selectedWo = $derived(Number(page.url.searchParams.get('wo')) || null)`. When `selectedWo`, render `<WorkOrderDetail workOrderId={selectedWo} onDeleted={clearSelection} />` with a "← Back to work orders" button that calls `clearSelection()` (`goto('/units/{unitId}?tab=maintenance', { replaceState: true })`); else render the existing list. Change the list row's `goto(workOrderDetailHref(w.id))` (line 133) to set `?wo={w.id}` on the unit URL.
- [ ] **Step 4 — Re-point external WO links** to `recordHref('workOrder', wo)`: `maintenance/+page.svelte:365` (grid `onRowClick`), `maintenance/inspections/[id]:318`. (Dashboard feed + scan helpers handled in Task 8.)
- [ ] **Step 5 — Verify.** `pnpm exec svelte-check --threshold error` (0 errors). Manual/E2E: maintenance grid row (unit-tied) → `/units/{u}?tab=maintenance&wo={id}` showing the detail; a property-only WO → `/maintenance/{id}` generic; back-to-list works; delete returns to list.
- [ ] **Step 6 — Commit** — `git commit -am "feat(unit-cc): fold work-order detail into the Maintenance tab (TSK-457)"`

---

### Task 4: Expense vertical

Same recipe as Task 3 with these substitutions:
- Create `web/src/lib/components/records/ExpenseDetail.svelte` from `accounting/expenses/[id]/+page.svelte` (props `expenseId`, `onDeleted`; `goto('/accounting')` → `onDeleted()`).
- Wrapper: `accounting/expenses/[id]/+page.svelte` → `<ExpenseDetail expenseId={Number(page.params.id)} onDeleted={() => goto('/accounting')} />`.
- Host in `ExpensesTab.svelte`: read `?expense=`; on select render `<ExpenseDetail>` (replacing the current ad-hoc inline-edit for one source of truth) + back-to-list; list rows set `?expense={id}`.
- Re-point: accounting ledger row click `accounting/+page.svelte:798,1072` → `recordHref('expense', expenseRecord)` (the ledger row must carry `unitId`; if it doesn't, surface it on the transaction projection the same DB-side way as Task 2 — verify during implementation).
- Verify + commit `feat(unit-cc): fold expense detail into the Expenses tab (TSK-457)`.

---

### Task 5: Payment vertical (depends on Task 2)

Same recipe:
- Create `web/src/lib/components/records/PaymentDetail.svelte` from `accounting/payments/[id]/+page.svelte` (props `paymentId`, `onDeleted`).
- Wrapper: `accounting/payments/[id]/+page.svelte` → `<PaymentDetail paymentId={Number(page.params.id)} onDeleted={() => goto('/accounting')} />`.
- Host in `RentTab.svelte`: read `?payment=`; on select render `<PaymentDetail>` + back-to-list; list rows set `?payment={id}`.
- Re-point payment links via `recordHref('payment', { id, unitId })` — `unitId` now present on the Payment DTO (Task 2): `accounting/+page.svelte:798,1072`, `leases/[id]:1083` (lease ledger payments grid), `accounting/past-due:167`.
- Verify + commit `feat(unit-cc): fold payment detail into the Rent tab (TSK-457)`.

---

### Task 6: Lease vertical

Same recipe, with the lease's inner tabs preserved inside the component:
- Create `web/src/lib/components/records/LeaseDetail.svelte` from `leases/[id]/+page.svelte` (props `leaseId`, `onDeleted`). Its existing 4 inner tabs (Overview/Agreement/Ledger/History via `resolveLeaseDetailTab`) become internal component state (a nested `?leaseTab=` or local state — do NOT collide with the unit's `?tab=`). `goto('/leases')` delete → `onDeleted()`.
- Host in `LeaseTab.svelte`: replace the summary `DetailCard` + "Open lease" link (line 26) with `<LeaseDetail leaseId={currentLeaseId} onDeleted={...} />`; support `?lease={id}` to show a prior lease (default = current). 
- Wrapper: `leases/[id]/+page.svelte` → since leases ALWAYS have a unit (`unitId` required), redirect to the unit tab: `goto(recordHref('lease', { id: leaseId, unitId }), { replaceState: true })` after fetching the lease's `unitId` (or keep a thin `<LeaseDetail>` wrapper as a safety fallback).
- Re-point lease links via `recordHref('lease', lease)`: `leases/+page.svelte:356`, `tenants/[id]:427`, `properties/[id]:643`, scan redirects (`scan/[draftId]:662`, `scan/new-rental:352`, `scan/batch/[id]:130`).
- Verify + commit `feat(unit-cc): fold lease detail into the Lease tab (TSK-457)`.

---

### Task 7: Applications tab (new) + Application vertical

**Files:**
- Modify: `web/src/lib/components/unit/unit-tabs.ts` — add `'applications'` to `UNIT_TABS`.
- Modify: `web/src/routes/(protected)/units/[id]/+page.svelte` — add `<Tabs.Trigger value="applications">Applications</Tabs.Trigger>` + `<Tabs.Content value="applications"><ApplicationsTab .../></Tabs.Content>`.
- Create: `web/src/lib/components/unit/tabs/ApplicationsTab.svelte` — lists the unit's tied applications (query applications filtered by `unitId`); on `?app=` renders `<ApplicationDetail>`; empty state when none.
- Create: `web/src/lib/components/records/ApplicationDetail.svelte` from `applications/[id]/+page.svelte` (props `applicationId`, `onDeleted`); preserves screening + adverse-action flows.
- Wrapper: `applications/[id]/+page.svelte` → `<ApplicationDetail applicationId={Number(page.params.id)} onDeleted={() => goto('/applications')} />` (used for un-tied apps).
- Re-point: `applications/+page.svelte:207` → `recordHref('application', a)` (un-tied apps with `unitId == null` go generic).

- [ ] Steps mirror Task 3: add the tab const + trigger/content; build `ApplicationsTab` (list + `?app=` host); extract `ApplicationDetail`; thin-wrap the route; re-point the grid; svelte-check; commit `feat(unit-cc): add Applications tab + fold application detail (TSK-457)`.

---

### Task 8: Final link sweep + feed reconciliation

**Files:** `web/src/routes/(protected)/+page.svelte:51,53,80,82,84` (dashboard `activityHref`/bullet hrefs — TSK-459), `web/src/lib/components/shared/ActivityFeed.svelte:112` (`detailHref`), `web/src/routes/(protected)/scan/+page.svelte:290,296`, `web/src/lib/scans/scan-review-state.ts:28,34`, `web/src/routes/(protected)/scan/[draftId]/+page.svelte:662`.

- [ ] **Step 1** — Route the dashboard activity/bullet href builders and the scan-redirect helpers through `recordHref(...)` (they have the record's `entityId`/ids; ensure they also carry `unitId` — for activity rows, the server's audit/`detailHref` or the activity label-resolver from TSK-459 may need `unitId` added the same DB-side way).
- [ ] **Step 2** — `ActivityFeed.svelte:112` uses a server-provided `detailHref`; reconcile so audit/timeline rows for these 5 types also resolve to the unit tab (update the server `detailHref` builder, or override client-side via `recordHref`).
- [ ] **Step 3** — Grep sweep: `grep -rnE "/(leases|maintenance|accounting/(expenses|payments)|applications)/\$?\{?[a-zA-Z]" web/src` and confirm every remaining navigational hit goes through `recordHref` (or is an intentional generic-page link). 
- [ ] **Step 4** — `pnpm exec svelte-check --threshold error` + `pnpm test:unit`. Commit `feat(unit-cc): route all detail links through recordHref (TSK-457)`.

---

## Self-review (against the spec)

- **Coverage:** rule §1 → Task 1 (resolver) + Tasks 3-7 (per type) + Task 8 (links). Reference pattern §2 → Tasks 3-7. Architecture §3 (component + 2 mounts) → each vertical's extract + wrapper + host steps. Resolver §4 → Task 1. Per-type §5 → Tasks 3-7. Payment backend §6 → Task 2. Link audit §7 → Tasks 3-8 (each lists its sites). Edge cases §8 → resolver null-checks (Task 1 tests) + back-to-list/onDeleted (each host step). Applications tab §5/§9 → Task 7. Out of scope §9 → not touched. Decomposition §10 → task order matches.
- **Placeholders:** none — resolver + DTO + wrapper code given in full; extractions are mechanical-move recipes with exact edits; re-point sites enumerated by file:line.
- **Type consistency:** `recordHref(type, {id, unitId})` and the `?param=` names (`wo/lease/expense/payment/app`) are identical across Task 1 and every host/re-point step; component props (`{x}Id`, `onDeleted`) consistent across Tasks 3-7.
