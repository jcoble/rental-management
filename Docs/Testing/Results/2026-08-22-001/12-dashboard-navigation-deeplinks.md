# Exploratory Test Report: Dashboard, Navigation, Breadcrumbs, and Deep Links (Scenario 12)
Date: 2026-08-22
Duration: ~20 minutes
Viewport: 1710x990 (desktop), 390x844 (mobile)
Session: e2e-s12 (headless)

## Scenario
Validate the landlord command center as the stable starting point: dashboard metrics and activity links, every navigation group, active-state behavior, breadcrumbs, direct routes, query/tab links, browser Back/Forward, /analytics redirect, header quick actions, sidebar collapse/expand, mobile viewport pass.

## Summary
The dashboard and navigation are in solid shape post-refactor. All 20+ nav items across 5 groups show correct active state, correct group auto-expansion, and correct page titles. Dashboard metrics load and render correctly. Deep links from activity rows navigate to the correct unit command center tab. The /analytics legacy redirect works. Browser Back/Forward preserves nav state. Mobile layout is responsive with no horizontal scroll. One low-severity console error found (404 on deposit endpoint for units without a security deposit).

## Bugs Found

### BUG-1: Console 404 error on unit Money tab when no security deposit exists
**Severity:** Low
**Location:** Unit command center, Money tab (e.g. /units/6?tab=money)
**Expected:** When a tenant account has no security deposit, the Money tab should handle the absence gracefully without producing a console error.
**Actual:** The frontend calls `GET /api/v1/tenant-accounts/1/deposit` which returns HTTP 404 "Tenant account deposit not found". This appears as a console error: `Failed to load resource: the server responded with a status of 404`.
**Evidence:** Console log at `.playwright-cli/console-2026-08-22T07-30-40-630Z.log` -- `[ERROR] Failed to load resource: the server responded with a status of 404 () @ https://localhost:5667/api/v1/tenant-accounts/1/deposit:0`. Reproduced consistently on both `/units/6?tab=money` and `/units/6?tab=money&tab=maintenance`. (verified)
**Code Reference:** `web/src/lib/api/endpoints/securityDeposits.ts:127` calls `api.get<TenantAccountDeposit>(/tenant-accounts/${tenantAccountId}/deposit)`. Backend at `RentalCommand.Api/Controllers/TenantAccountsController.cs:98-106` returns NotFound when deposit is null.
**Why This Matters:** Console 404s are noisy for developers monitoring errors and could mask real failures. Users are not affected visually, but monitoring tools may flag these.
**Suggested Fix:** Either have the backend return 200 with a null/empty deposit body instead of 404, or have the frontend query handle 404 as a valid "no deposit" state without logging it as an error (e.g. use a query that expects 404 and returns null).

## Potential Issues (Need Investigation)

### PI-1: localStorage group state diverges from visual state
The `$effect` that auto-expands the active route's group does not persist to localStorage. Manual user toggles via `toggleGroup` do write to localStorage via `writeOpenGroups`. After navigating from /accounting (Money group, manually opened) to /maintenance (Work group, auto-opened), localStorage retains `{"money": true}` while the visual state correctly shows Work expanded and Money collapsed. On reload, the `$effect` correctly overrides the stale stored state, so there is no user-visible bug. However, the divergence could become confusing if the auto-expand logic ever changes. (verified via `localStorage.getItem('rc.nav.groups.v2')` on /maintenance route)

### PI-2: Help/Docs link navigates outside AppShell
The "Help" nav item (`/docs`) navigates to a `(public)` route that has its own layout, so the user leaves the AppShell entirely. The nav highlight disappears because the AppShell is no longer rendered. This is by design (docs are public), but could be jarring for users who expect to stay within the app. Worth considering whether the docs should render inside the AppShell for authenticated users.
**Code Reference:** `web/src/routes/(public)/docs/+page.svelte` vs `web/src/routes/(protected)/+layout.svelte`

## Observations

1. **Dashboard data load time:** The dashboard takes 5-8 seconds to fully load all sections (hero, money snapshot, messages, work orders, recent activity). During this time, a well-designed skeleton loading state is shown. The skeleton accurately matches the final layout. (verified)

2. **Accordion nav behavior is excellent:** Opening one group auto-closes all others (one-at-a-time invariant). Navigating to a route auto-expands the containing group and collapses others. This is smooth and predictable.

3. **Active state implementation is thorough:** The `isActive` function in `AppShell.svelte:388-405` handles special cases correctly: `/` (exact match only), `/reports` (also matches `/reports/` and `/owners-report`), `/settings` (matches subpaths but not `/settings/notifications/`), `/units` (matches unit subpaths). (verified)

4. **Duplicate query params handled gracefully:** Navigating to `/units/6?tab=money&tab=maintenance` selects the Money tab (first param wins via `URLSearchParams.get()`). No crash or unexpected behavior. (verified)

5. **Unknown tab names default to Summary:** `/units/6?tab=nonexistent` loads the unit with the Summary tab selected. No error state. (verified)

6. **Trailing slashes normalized:** `/properties/` redirects to `/properties`. (verified)

7. **Unknown record IDs show proper error:** `/units/99999` shows "This unit could not be loaded. Try again / Back to units" error state with retry and navigation options. (verified)

8. **Mobile layout is fully responsive:** All 8 tested pages (/, /properties, /tenants, /leases, /maintenance, /messages, /accounting, /settings) show `bodyScrollWidth === windowWidth` (no horizontal scroll) at 390x844. KPI cards stack vertically. Hamburger menu toggle works, overlay dismisses drawer. Nav item click auto-closes drawer. (verified)

9. **Sidebar collapse/expand works correctly:** Collapses to 56px (w-14), expands to 240px. Toggle button switches between PanelLeftClose and PanelLeftOpen icons. (verified)

10. **Money snapshot formatting:** Uses `maximumFractionDigits: 0` for currency display, so amounts show as `$0` rather than `$0.00`. Consistent across hero, money snapshot, and KPI cards. (verified, `+page.svelte:38-40`)

## What Was Tested

### Dashboard (/)
- Skeleton loading state renders during data fetch (verified)
- Error state accessible via `data-testid="dashboard-error"` (code review, not triggered in test)
- Hero card: portfolio name, management company, chips (open work orders, occupancy rate), action buttons (Open money -> /accounting, View properties -> /properties, Guided Setup -> /onboarding?from=dashboard) (verified)
- Hero status: overdue amount ($0), net this month ($0) (verified)
- Money snapshot: collected ($0), spent ($0), kept ($0) with explanations (verified)
- Past-due banner: DIV (not link) when pastDueCount=0, shows "Everyone is caught up" (verified)
- KPI cards: Occupancy (0%, 0/5 occupied), Who's behind ($0), Kept this month ($0), Open Work Orders (0, 0 emergency) (verified)
- Latest Messages: 2 messages from S14 testing, links to /messages/2 and /messages/1 (verified)
- Latest Work Orders: "No open work orders" empty state (verified)
- Recent Activity: 8 rows, all links to unit command center tabs (verified)
- Upcoming Appointments: "No upcoming appointments" empty state (verified)
- Leasing Mix: "Upcoming 1" (verified)

### Navigation (all 20+ items)
- Pinned: Dashboard (/), Guided Setup (/onboarding), Scan / Add (/scan) -- all tested with correct active state
- Money group: Overview (/accounting), Banking (/banking), Deposits (/deposits), Reports (/reports) -- all correct
- Rentals group: Properties (/properties), Owners (/owners), Tenants (/tenants), Leases (/leases), Lease Templates (/lease-templates), Applications (/applications) -- all correct
- Work group: Work Orders (/maintenance), Appointments (/appointments), Vendors (/vendors) -- all correct
- Inbox group: Messages (/messages), Tenant notices (/notices) -- all correct
- Bottom rail: Ask (/ai), Help (/docs) -- /ai correct, /docs exits AppShell (by design)
- Settings group: My alerts, Team routing, Tenant notices, Settings, Team (/admin/users), Activity history (/audit) -- all correct

### Deep links
- Dashboard activity row -> unit command center tab (/units/6?tab=tenant-lease&view=agreements&leaseManagement=1) -- correct tab selected, breadcrumb shows property/units path (verified)
- Dashboard activity row -> tenant account money tab (/units/6?tab=money&view=tenant-account&tenantAccount=1) -- verified
- Dashboard message -> /messages/2 -- correct nav active state (verified)
- Hero "Open money" -> /accounting (verified)
- Hero "View properties" -> /properties (verified)
- Hero "Guided Setup" -> /onboarding?from=dashboard (verified)

### Edge cases
- /analytics -> 307 redirect to / (verified)
- /properties/ trailing slash -> /properties (verified)
- /units/99999 unknown ID -> error state with retry (verified)
- /units/6?tab=nonexistent -> defaults to Summary tab (verified)
- /units/6?tab=money&tab=maintenance duplicate params -> Money tab selected (first wins) (verified)
- Browser Back/Forward -> correct navigation, active state preserved (verified)
- Page refresh -> nav group state correctly restored via $effect (verified)
- Sidebar collapse -> 56px rail with icon-only nav (verified)
- Sidebar expand -> 240px with labels (verified)

### Mobile (390x844)
- Sidebar hidden, hamburger menu visible (verified)
- Drawer opens on toggle, overlay visible (verified)
- Overlay click closes drawer (verified)
- Nav item click navigates and closes drawer (verified)
- No horizontal scroll on 8 tested pages (verified)
- KPI cards stack vertically (verified)

### Header quick actions
- Scan / Add button (verified)
- Messages link -> /messages (verified)
- Appointments link -> /appointments (verified)
- Help link -> /docs (verified)
- Theme toggle present (verified)
- Notification bell present (verified)
