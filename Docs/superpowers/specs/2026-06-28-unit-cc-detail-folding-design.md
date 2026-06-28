# Unit Command Center — Detail Folding (TSK-457) — Design

**Date:** 2026-06-28
**Status:** Draft for review
**Surface:** SvelteKit web (`web/`)
**Task:** TSK-457 ("Command Center 2")

---

## 1. Goal & the systemic rule

Stop having standalone detail pages for records that belong to a unit. Instead, a record's
full detail lives **inside the Unit Command Center** (`/units/[id]`) under its matching tab —
the unit *is* the workspace. The grid/list pages stay exactly as they are; only the
destination of a **row click / link** changes.

> **The rule:** opening a record routes to **its unit's Command Center tab when the record is
> tied to a unit**; otherwise it opens the **existing generic detail page, unchanged.**

| Record | Unit tab it lands in | When **not** unit-tied → |
|---|---|---|
| Lease | **Lease** | (Leases always have a unit — see §5) |
| Work order | **Maintenance** | generic `/maintenance/{id}` |
| Expense | **Expenses** | generic `/accounting/expenses/{id}` |
| Payment | **Rent** | generic `/accounting/payments/{id}` |
| Application | **Applications** *(new tab)* | generic `/applications/{id}` |

## 2. Reference pattern (already in the codebase)

This mirrors the **EdiPlatform Order workspace** (`ediplatform-web/.../customer/orders/[id]/+page.svelte`):
an object page built from `Tabs`, each tab a self-contained component
(`DetailsTab/ShipTab/InvoiceTab/PaymentTab/…` from `$lib/components/order-workspace`), with a
lifecycle rail + live refresh. There is **no standalone ship/invoice page** — every aspect is a
tab; the orders grid only routes rows into the workspace.

Rental Command's Unit Command Center **already follows this pattern**:
`web/src/routes/(protected)/units/[id]/+page.svelte` (tabs via `resolveUnitTab` + `?tab=` URL
state, `setTab` at lines 35/48-54, persistent `UnitTimelineRail` at 249-252) +
`web/src/lib/components/unit/tabs/*.svelte`. TSK-457 finishes that pattern by making the tabs
host the **full** record detail, not just summaries/links-out.

## 3. Architecture — one component, two mount points

For each record type, extract the standalone detail page's body into a **reusable detail
component** that takes the record id (or record) as a prop and reports destructive actions via
callbacks (so it works outside a `+page` route):

```
<LeaseDetail leaseId  onDeleted />      <WorkOrderDetail workOrderId onDeleted />
<ExpenseDetail expenseId onDeleted />   <PaymentDetail paymentId onDeleted />
<ApplicationDetail applicationId onDeleted />
```

Mount each in **two** places:

1. **Unit tab (primary):** the tab renders its list; selecting a row renders `<XDetail>`
   **inline** in the same tab, with a "← back to list" affordance. The selection lives in the
   URL alongside the tab so it's deep-linkable:
   `/units/{unitId}?tab=maintenance&wo={id}` (param names per record: `wo`, `lease`, `expense`,
   `payment`, `app`). Lease is the exception — a unit has one current lease, so the Lease tab
   renders `<LeaseDetail>` directly (with a switch for prior leases).
2. **Generic page (fallback):** the existing route (`/maintenance/[id]`, etc.) becomes a **thin
   wrapper** that renders the same `<XDetail>` + page chrome. Used only when the record has no
   unit. No detail UI is duplicated.

All 5 detail pages are self-contained today (no server `load`; data via TanStack Query keyed on
`page.params.id`), so extraction = replace `page.params.id` with the prop and thread the
delete/redirect through `onDeleted`. Extraction difficulty (from the audit): Payment **low**,
Expense low-med, Work order med, Lease med, Application med (screening/adverse-action).

## 4. The routing resolver (single source of truth)

Add one shared resolver, `recordHref(type, record)` (mirrors the existing
`activityHref`/`scanHref` patterns), returning the unit-tab URL when unit-tied, else the generic
URL. Every grid row / link / redirect calls it. The unit-tie facts (from the type audit) and how
each resolves:

| Record | `unitId`? | Resolver logic |
|---|---|---|
| Lease (`types:267`) | required | always → `/units/{unitId}?tab=lease&lease={id}` |
| WorkOrder (`types:880`) | **optional** | `unitId ? /units/{unitId}?tab=maintenance&wo={id} : /maintenance/{id}` |
| Expense (`types:324`) | **optional** | `unitId ? /units/{unitId}?tab=expenses&expense={id} : /accounting/expenses/{id}` |
| Payment (`types:294`) | **absent** (only `leaseId`) | **needs `unitId` on the DTO** — see §6; then same pattern → `tab=rent&payment={id}` |
| RentalApplication (`applications.ts:13`) | **nullable** | `unitId ? /units/{unitId}?tab=applications&app={id} : /applications/{id}` |

## 5. Per-record-type plan

- **Lease** → **Lease tab.** Today `LeaseTab.svelte:26` links OUT (`/leases/{id}`). Replace the
  summary `DetailCard` + "Open lease" link with `<LeaseDetail>`. The standalone page
  (`leases/[id]`, a 4-tab Overview/Agreement/Ledger/History layout) becomes the component;
  its inner `?tab=` becomes internal sub-tabs of the component (works in both mount points). The
  `leases/[id]` route stays only as a wrapper — but since leases always have a unit, in practice
  the generic route just redirects to the unit tab.
- **Work order** → **Maintenance tab.** Today `MaintenanceTab.svelte:133` links OUT. Keep the
  unit's WO list; clicking a row sets `?wo={id}` and renders `<WorkOrderDetail>` inline (back to
  list clears it). Generic `/maintenance/{id}` wraps the same component for property-level WOs.
- **Expense** → **Expenses tab.** `ExpensesTab.svelte` already renders an inline expandable
  list; replace the ad-hoc inline-edit with `<ExpenseDetail>` for one source of truth. Generic
  page wraps it for portfolio/property-only expenses.
- **Payment** → **Rent tab.** `RentTab.svelte` already lists payments inline; render
  `<PaymentDetail>` on select. Requires the §6 backend change (Payment → unitId).
- **Application** → **new Applications tab.** Add `'applications'` to `UNIT_TABS`
  (`unit-tabs.ts`) + a `<Tabs.Trigger>`/`<Tabs.Content>` on `units/[id]/+page.svelte` + an
  `ApplicationsTab.svelte` that lists the unit's tied applications and renders
  `<ApplicationDetail>` on select. The tab may be hidden/empty when a unit has no tied apps.
  Un-tied apps (no `unitId`) stay on the generic `/applications/[id]` page. (Broader application
  rework — uploadable/configurable intake — is **TSK-205**, out of scope here.)

## 6. Backend change (Payment → unit)

`Payment` currently exposes no `unitId`/`propertyId` (only `leaseId`). Add `unitId` (and
`propertyId`) to the Payment DTO, populated **DB-side via the lease join** in the existing
payment projection (one SQL statement — **no in-memory/N+1**, per the project SQL rule). This
lets the resolver route payments to the unit without a client-side extra fetch. (Lease, Work
order, Expense, Application already carry the needed ids.)

## 7. Link / grid re-pointing

Point every site that navigates to the 5 detail routes at `recordHref(...)`. From the audit:

- **Leases:** `leases/+page.svelte:356`, `tenants/[id]:427`, `properties/[id]:643`, scan
  redirects (`scan/[draftId]:662`, `scan/new-rental:352`, `scan/batch/[id]:130`).
- **Work orders:** `maintenance/+page.svelte:365`, dashboard feed (`(protected)/+page.svelte:51,80`),
  `maintenance/inspections/[id]:318`, scan helpers (`scan/+page.svelte:290`, `scan-review-state.ts:28`).
- **Expenses/Payments:** accounting ledger (`accounting/+page.svelte:798,1072`), lease ledger
  (`leases/[id]:1083`), past-due (`accounting/past-due:167`), dashboard feed, scan helpers,
  and **`ActivityFeed.svelte:112` + the dashboard `activityHref` (TSK-459)** which build detail
  hrefs — route these through `recordHref` too (and reconcile the server-provided `detailHref`).
- **Applications:** `applications/+page.svelte:207`.
- The existing reverse cross-links (lease/WO/expense → `/units/{unitId}?tab=...`) already point
  the right way; keep them.

## 8. Edge cases

- **No unit** (property-only WO/expense, portfolio expense, app with no unit selected) →
  generic page, unchanged. Resolver null-checks `unitId`.
- **Multiple leases per unit:** Lease tab shows the current lease by default + a switcher to
  prior leases (via `?lease={id}`).
- **Deleted record** in a unit tab → `onDeleted` returns to that tab's list (not a 404); in the
  generic page → existing redirect (`/maintenance`, `/accounting`, …).
- **Stale links / bad ids** (e.g. `?wo=` for a WO not on this unit) → fall back to the list.

## 9. Out of scope

Application intake rework (TSK-205); a Property-level command center; mobile (TSK-454 covers the
mobile IA); the dashboard/sidebar IA. This task is purely: detail-component extraction +
unit-tab hosting + the routing resolver + re-pointing + the new Applications tab + the Payment
DTO `unitId`.

## 10. Suggested decomposition (implementation order)

1. **Work order** end-to-end as the **reference vertical** (extract `<WorkOrderDetail>`, host in
   Maintenance tab with `?wo=`, wrap generic page, add `recordHref`, re-point WO links). Proves
   the pattern.
2. **Expense** + **Payment** (Payment needs the §6 DTO change first).
3. **Lease** (largest body; its inner tabs).
4. **Applications** tab (new tab) + **Application** detail.
5. Final pass: route every remaining link site through `recordHref`; sweep for missed detail-page links.

## 11. Testing

Per project posture (few targeted tests). Unit-test `recordHref` (the unit-tied vs generic
branches incl. the Payment-via-lease and null-`unitId` cases). One UI/E2E check per vertical:
grid row → lands on the correct unit tab with the detail shown; a no-unit record → generic page.
Verify the Payment DTO `unitId` is populated DB-side (no N+1).
