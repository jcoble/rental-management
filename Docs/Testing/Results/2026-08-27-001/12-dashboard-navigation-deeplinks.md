# Exploratory Test Report: Dashboard, navigation, breadcrumbs, and deep links
Date: 2026-08-27
Tester: e2e-s12
Duration: about 60 minutes

## Scenario
Validate the landlord dashboard as a stable starting point, including navigation groups, active state, breadcrumbs, deep links, query/tab state, and browser history.

## Summary
I exercised the current seeded-admin experience at the required 1710x990 desktop viewport: the redesigned dashboard, every visible navigation group and destination, command-center unit picker, dashboard record links, tab/hash deep links, unknown-record routes, breadcrumbs, header actions, analytics redirect, and Back/Forward. Two confirmed issues were found: the actionable dashboard list is hidden behind the slower optional AI briefing, and the Units command-center entry has no active visual or accessibility state on `/units` or a unit detail page. The tested links, redirects, tab state, breadcrumbs, retry boundaries, and stable history flows otherwise behaved as expected; no records were created or changed.

## Bugs Found

### BUG-1: Do not block the overdue-action list on the optional AI briefing
**Severity:** Medium
**Location:** Dashboard `/`, `Needs attention` section
**Expected:** The past-due action list should become usable when its own past-due request finishes. The assistant line is intended to be independent: `DashboardBriefing.svelte` says that a slow assistant must never delay the list.
**Actual:** On a fresh dashboard load, after about 2.2 seconds the page showed `Needs attention · 3` but still rendered `data-testid="needs-attention-loading"` and had zero `data-testid="needs-attention-row-open"` links. The past-due request completed in 1,435 ms, while `/api/v1/ai/briefing` took 5,689 ms. The five actionable rows appeared only after the briefing request finished.
**Evidence:** Browser output from `dashboard-loading.js`: `LOADING_STATE {"briefing":"Reading your day…","attention":"Needs attention · 3","loadingSkeletonCount":1,"rowCount":0,"openLinkCount":0}` followed by `SETTLED_STATE` with `rowCount:5` and `openLinkCount:5`; response timings were `past-due=1435ms` and `ai/briefing=5689ms`. Screenshot: `/home/blackcolours/Workbox/screenshots/e2e-s12-dashboard-briefing-blocks-list.png`. The targeted run had no console, page, or HTTP errors.
**Code Reference:** `web/src/routes/(protected)/+page.svelte:184` derives `attentionLoading` from both `pastDueQuery.isLoading || briefingQuery.isLoading`, and `web/src/routes/(protected)/+page.svelte:270-275` replaces the entire list with the skeleton while that value is true. The intended independence is documented at `web/src/routes/(protected)/DashboardBriefing.svelte:2-4`.
**Suggested Fix:** Make `attentionLoading` depend only on `pastDueQuery.isLoading`; allow the past-due rows to render immediately and append briefing rows when the separate assistant query resolves.
**Why This Matters:** A landlord can open the dashboard to collect overdue rent, see a count of people behind, and still have no action link available for several seconds while a nonessential assistant request runs. They may miss the money task or assume the page is stuck.

### BUG-2: Mark the current Units location in the command-center navigation
**Severity:** Low
**Location:** Expanded desktop sidebar on `/units` and `/units/5`
**Expected:** The current Units location should have the same selected styling and current-location semantics as the other sidebar destinations, including on a unit deep link. The existing active-route helper explicitly treats `/units` and `/units/*` as the same navigation location.
**Actual:** On both `/units` and `/units/5`, the `nav-units-picker` entry had a neutral class, no `aria-current`, and no active sidebar anchor. On `/units/5`, the sidebar still showed a plain `Units` picker while the content showed `Unit 201`; the unit-specific selected styling was available only after opening the picker. The picker was closed in the default state, so the current unit was not visibly marked.
**Evidence:** Browser output from `unit-active-nav.js` for both routes reported `pickerAriaCurrent:null`, `activeAnchors:[]`, and a class containing `text-muted-foreground` without an active class. Screenshot: `/home/blackcolours/Workbox/screenshots/e2e-s12-unit-detail-unmarked-command-center.png`. The targeted run had no console, page, or HTTP errors.
**Code Reference:** `web/src/lib/components/CommandCenterNav.svelte:48-58` renders the collapsed Units link without active state, and `web/src/lib/components/CommandCenterNav.svelte:61-76` renders the expanded picker button without active state. The route matcher expects `/units` to match `/units/*` at `web/src/lib/components/nav-active.ts:10-15`; ordinary navigation links expose the selected state through `aria-current` at `web/src/lib/components/m3/NavItem.svelte:105-113`.
**Suggested Fix:** Pass the existing `/units` active result into `CommandCenterNav` and apply the selected class plus `aria-current="page"` to its route entry in both collapsed and expanded states while preserving the picker’s `aria-expanded` behavior.
**Why This Matters:** A landlord arriving from a dashboard or deep link cannot use the sidebar to orient themselves; keyboard and screen-reader users also receive no current-navigation signal for the section that owns the page.

## Potential Issues (need investigation)

- One reload probe briefly showed the persisted Settings group with `aria-expanded="true"` but without its child links at about one second; the children were present and the group was correct by three seconds. This may be a hydration/loading flash rather than a lasting defect. Relevant state hydration is in `web/src/lib/components/AppShell.svelte:417-450`.
- Non-management shells (owner, leasing, technician, and tenant) were not exercised because the local environment exposed only the seeded administrator identity. This is a coverage limitation, not a confirmed access bug.

## Observations

- The current dashboard is intentionally simplified to a ranked `Needs attention` list plus a compact summary; the older hero/KPI/activity layout named by the scenario is not present in the current source and was not treated as a defect.
- Dashboard summary links reached `/accounting/past-due`, `/properties`, and `/audit`. The five eventual attention links reached the expected unit money/lease views or inspection record, and browser Back returned to the dashboard with its list restored.
- Money, Rentals, Work, Inbox, and Settings groups opened/collapsed correctly. The visible destinations reached the expected routes and showed the correct active child after a stable wait: `/accounting/past-due`, `/accounting`, `/banking`, `/deposits`, `/reports`, `/properties`, `/owners`, `/tenants`, `/leases`, `/applications`, `/maintenance`, `/appointments`, `/vendors`, `/messages`, `/notices`, `/settings/notifications`, `/settings/lease-templates`, `/settings`, `/admin/users`, and `/audit`.
- The command-center picker expanded, filtered units by search, opened the selected unit, and returned to the dashboard. Header Scan/Add, messages, appointments, help, user menu, notification menu, and theme controls reached their expected destinations or menus.
- `/analytics`, `/analytics/`, and `/analytics?tab=reports&keep=yes` redirected to `/` without stable console errors. Accounting report tabs survived reload; malformed and duplicate tab parameters normalized to a valid single tab. Settings hash tabs survived direct load, selection, and reload. A stable `/properties?q=Maple` → `/accounting?tab=reports` history run returned to Properties on Back and Money on Forward.
- Unit and inspection breadcrumbs showed the expected property/section links. Unknown unit, property, tenant, repair, appointment, and settings routes presented clear not-found/retry boundaries rather than silently displaying another record.

## What Was Tested

- Logged in at `/login` with the approved seeded administrator and verified `1710x990` in a headless persistent browser context.
- Loaded the dashboard from a fresh navigation; captured the loading and settled states, summary links, attention links, delayed briefing behavior, and screenshot evidence.
- Followed dashboard deep links for overdue tenant accounts, lease management, and inspection records; verified destinations, selected tabs/subnavigation, and Back to the dashboard.
- Opened and collapsed all five navigation groups; visited every visible management destination and checked route/title/active state. Tested command-center expansion, unit search, selection, and Browse all.
- Tested header actions, user menu, notifications, theme toggle, direct `/analytics` variants, settings hash navigation, accounting tab/query normalization, unit deep-link tabs, and stable Back/Forward.
- Tested breadcrumbs on `/units/5`, `/maintenance/inspections/8`, and the accounting Reports tab; tested unknown IDs and an unknown settings path for clear boundaries.
- No dashboard source records were created, edited, deleted, or reset.

Browser cleanup: stopped e2e-s12
