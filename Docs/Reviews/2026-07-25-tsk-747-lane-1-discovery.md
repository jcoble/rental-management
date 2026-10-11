# TSK-747 Lane 1 Discovery — Rental Spine and Unit Command Center

## Evidence boundary

- Browser target: Azure preview at `1920 × 1080`.
- Browser-reviewed: dashboard, getting started, guided setup, properties list/detail, Units list, all six Unit Command Center destinations and their secondary views, tenants list/detail, leases list/detail, lease templates, applications list, owners list/detail, and the public feature/register/account-recovery surfaces that remain reachable while signed in.
- Source-reviewed or access-blocked: leasing-only workspace, owner portal, token-bound activation/reset/application routes, and public login routes that redirect an authenticated session.
- Production code remained frozen during discovery.

## User outcome

A landlord should be able to choose a rental from a persistent **Command Center** control, understand the rental’s current state at a glance, and complete one focused job at a time without learning database language. Unit-owned records must fold back into the correct Unit Command Center destination.

## Confirmed failures

### 1. The Command Center was removed from navigation

The current `CommandCenterNav.svelte` is only a legacy link labelled **Rentals**. `AppShell.svelte` and `shell-hierarchy.test.ts` explicitly prevent the old Command Center component from appearing.

Commit `0bfdd6a6` contains the safer version to restore and adapt: the dropdown is named **Command Center**, queries only while open, searches on the server, and requests 20 rows. Do not restore the older 500-row/client-filter implementation.

### 2. Unit-owned records escape their Unit

The canonical helper already knows the correct destination:

`/units/:unitId?tab=tenant-lease&view=agreements&leaseManagement=:id`

However, the lease list, property detail, tenant detail, applications move-in success, and the Unit lease card bypass it with direct `/leases/:id` navigation. Browser proof from the lease grid opened `/leases/1`, not Unit 1’s lease destination.

### 3. A Unit destination renders several complete workflows at once

`units/[id]/+page.svelte` changes `view` in the URL but still renders every child surface:

- Leasing: listing and applications.
- Tenant & lease: agreements and residents.
- Maintenance: work orders, inspections, recurring work, and turnover.
- Documents & history: documents and activity.

At `1920 × 1080`, Maintenance is a long wall containing several independent creation and management workflows. The top controls look like navigation but mostly scroll within the wall. The route model already defines the correct secondary destinations; the page should render only the active one.

### 4. Internal model names are presented as user language

Examples confirmed in browser or source:

- `TenantAccount`
- `PaymentReceipt`
- `LeaseAgreement`
- `NotAvailable`, `PastDue`, `NoticeOpen`, and `InProgress`
- “tenant relationship”
- “governing agreement”
- “account context”
- “possession”
- “Ownership entities consumed by owner statements”
- “Lease ends in 311d”

Legal or accounting terms can remain where accuracy requires them, but they need a short plain-English explanation. Internal entity names and enum spellings must not be visible.

### 5. The Unit header competes with the work

The Unit header uses a large gradient hero and a row of many pills that repeat state shown below. Important facts such as who lives here, monthly rent, lease end date, balance, and urgent repair state do not read in a stable priority order.

### 6. Critical controls break component consistency

The Unit applications and lease-management surfaces contain native `<select>` controls even though the app has a shared select component. These are especially visible inside already-complex dialogs.

### 7. Contextual help is inconsistent

The Help area already has relevant written articles, including `/docs/settings-and-notifications`, but core Unit and lease surfaces rarely point to the relevant article. A help link must resolve to a published article; do not add placeholder slugs.

### 8. Unit tab changes feel like page loads

The primary and secondary Unit controls call SvelteKit `goto(...)`. That enters the global navigation state, lights the navigation loader, and makes an in-place workspace feel like a new page. The URL should still change for back, forward, and deep links, but tab/view selection is local workspace state and should use shallow routing. Unit data already held by TanStack Query must remain available; a tab change must not blank the Command Center or replay its full loading state.

### 9. Unit Money hides payment detail and exposes accounting internals

Expense rows expose their detail affordance, while payment detail is available only from a separate receipt list below several other sections. Account-activity payment rows are not interactive. The screenshot supplied by the user confirms especially opaque copy:

- “Charge allocation and open amount”
- “$0.00 allocated of $1,050.00”
- “$1,050.00 open”

The underlying accounting behavior remains intact, but the visible explanation should be:

- Section title: **Rent and charges**
- Supporting line: **See what has been paid and what is still owed.**
- Unpaid row: **$0 paid of $1,050** and **$1,050 still owed**
- Part-paid row: **$300 paid of $1,050** and **$750 still owed**
- Paid row: **$1,050 paid** and **Paid in full**

Payment rows must open `PaymentDetail` inside the Unit Money destination through the existing `payment` query parameter, just as expense detail folds into the operating-cost view.

## What is already sound

- The Unit route has six durable top-level destinations and query-string deep links.
- `recordHref` preserves generic detail routes when a record genuinely lacks Unit context.
- Property detail uses one understandable workspace navigation row and does not stack every property workflow on its Summary view.
- Applications inside a Unit already support an inline selected-record state.
- Dashboard attention and work-order links already fold to Unit-owned destinations.
- Loading, empty, and retry states exist on most rental-spine pages and should be preserved.

## Proposed information architecture

### Persistent shell

1. Dashboard
2. Guided Setup
3. Scan / Add
4. **Command Center** — searchable rental picker plus “Browse all rentals”
5. Money
6. Rentals — portfolio-wide lists such as Properties, Tenants, Leases, Templates, and Applications
7. Work
8. Inbox
9. Ask, Help, Settings

The Rentals group remains useful for global lists. The Command Center is the fast, persistent doorway into one rental.

### Unit Command Center

Keep the six approved primary destinations:

1. Summary
2. Leasing
3. Tenant & lease
4. Money
5. Maintenance
6. Documents & history

Where a destination has multiple jobs, show a quiet secondary navigation row and render only the selected view:

- Leasing: **Listing** | **Applications**
- Tenant & lease: **Lease & move-in** | **Residents**
- Money: keep the current tenant/operating scopes, but use plain labels and one visible ledger at a time.
- Maintenance: **Work orders** | **Inspections** | **Recurring work** | **Move-out & turnover**
- Documents & history: **Documents** | **Activity**

The URL remains the source of selection so bookmarks, back, and cross-page links keep working.
Use SvelteKit shallow `pushState`/`replaceState` for in-place Unit tabs, views, and folded details. Do not call `goto` for those state-only changes and do not trigger the global navigation loader.

## Plain-English copy direction

| Current | Replace with |
| --- | --- |
| Units | Command Center |
| Tenant relationship | Lease and household |
| Governing agreement | Current lease document |
| Account context | Rent account |
| Possession active | Resident has moved in |
| No governing Agreement is on file | No signed lease document is attached |
| TenantAccount | Rent account |
| PaymentReceipt | Payment |
| LeaseAgreement | Lease document |
| NotAvailable | Not available |
| NoticeOpen | Notice in progress |
| InProgress | In progress |
| Lease ends in 311d | Lease ends June 1, 2027 |
| Ownership entities consumed by owner statements | People or companies that own your rentals and receive owner statements |
| Transfer to Unit | Move this household to another rental |
| Cancel planned relationship | Cancel this planned move-in |
| Tenant account | Rent and payments |
| Operating costs | Property expenses |
| Charge allocation and open amount | Rent and charges |
| $0 allocated of $1,050 | $0 paid of $1,050 |
| $1,050 open | $1,050 still owed |

## Implementation boundary

Lane 1 owns:

- `web/src/lib/components/AppShell.svelte`
- `web/src/lib/components/CommandCenterNav.svelte`
- `web/src/lib/components/shell-hierarchy.test.ts`
- `web/src/lib/navigation/record-href.ts` and focused tests if needed
- `web/src/routes/(protected)/units/**`
- `web/src/lib/components/unit/**`
- `web/src/routes/(protected)/leases/+page.svelte`
- `web/src/routes/(protected)/properties/[id]/+page.svelte`
- `web/src/routes/(protected)/tenants/[id]/+page.svelte`
- focused rental-spine contract tests

Lane 1 does not redesign legal lease mutations, change database/API contracts, or delete the generic `/leases/[id]` fallback. Lane 2 owns notification/work/comms UI. Lane 3 owns money/intake/settings/help/admin UI. Shared files require explicit coordination before edits.

## Acceptance checks

- Command Center is visible in the expanded and collapsed management shell.
- Opening the picker does one server-paged query; search remains DB-side and no client filtering is introduced.
- A Unit-owned lease row from Leases, Property, or Tenant opens the correct Unit lease view.
- Selecting a secondary Unit view shows one workflow and hides sibling workflows.
- Primary tabs, secondary views, and folded payment/expense detail use shallow routing and never show the global page-navigation loader.
- Payment and expense rows both open their detail inside the correct Unit Money view.
- Rent charges say what has been paid and what is still owed; the words “allocation” and “open amount” are absent from changed user-facing copy.
- Changed Unit surfaces show no raw entity or enum names.
- Changed native selects use the established select component.
- A contextual Help action points to an existing article.
- Existing loading, empty, retry, access, and generic fallback behavior remains intact.
- Browser proof is captured at `1920 × 1080` plus a focused narrow-width overflow check.

## Risks for the reviewer

1. Restoring the picker must use current capability-based access checks, not the prior role helper.
2. Reusing the monolithic `/leases/[id]` UI inside Unit would recreate the wall; this pass should route and progressively disclose without copying that page wholesale.
3. URL-driven secondary views must not unmount a dialog during a save or lose server state unexpectedly.
4. Copy changes must not erase legally meaningful distinctions between a household relationship, a lease document, possession, and a rent account.
5. Avoid broad select replacement in unrelated legal dialogs unless the focused surface is touched and verified.
