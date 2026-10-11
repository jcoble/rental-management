# Scenario 12 — Dashboard, navigation, breadcrumbs, and deep links

## Purpose

Validate the landlord command center as the stable starting point: dashboard metrics and activity links, every navigation group, active-state behavior, breadcrumbs, direct routes, query/tab links, and browser Back/Forward.

## Preconditions and login

Log in as the seeded administrator in local dev or the approved preview. Use read-mostly exploration; only create additive QA data when a dashboard card or recent-activity link needs a known record.

## Read the implementation first

Before opening a browser, read these named source files and the relevant controller/service code they call:

- web/src/routes/(protected)/+page.svelte
- web/src/lib/components/AppShell.svelte and web/src/lib/components/NavigationLoader.svelte
- web/src/lib/navigation/record-href.ts and web/src/lib/utils/grid-url-state.svelte.ts
- web/src/routes/(protected)/+layout.svelte and web/src/routes/(protected)/+layout.server.ts
- web/src/routes/(admin)/+layout.svelte and web/src/routes/(superadmin)/+layout.svelte
- web/src/routes/(protected)/properties/+page.svelte, web/src/routes/(protected)/units/+page.svelte, web/src/routes/(protected)/tenants/+page.svelte, web/src/routes/(protected)/leases/+page.svelte, web/src/routes/(protected)/accounting/+page.svelte, web/src/routes/(protected)/maintenance/+page.svelte, web/src/routes/(protected)/messages/+page.svelte, web/src/routes/(protected)/notices/+page.svelte, web/src/routes/(protected)/settings/+page.svelte
- RentalCommand.Api/Controllers/PortfolioController.cs and AnalyticsController.cs, RentalCommand.Api/Services/Domain/DashboardService.cs, and RentalCommand.Api/Controllers/AuthController.cs

## Rough exploration areas

Use these as exploration goals, not a step-by-step script:

- Inspect hero actions, money/occupancy/attention metrics, recent activity, empty states, alerts, and any live updates; follow every link and verify the destination record and query context.
- Open and collapse Money, Rentals, Work, Inbox, and Settings groups; visit every visible item from AppShell and compare the active highlight, page heading, breadcrumb, and URL.
- Exercise dashboard-to-record deep links, unit command-center links, report tabs, ledger entry links, and notification intents; use browser Back/Forward and refresh after each navigation family.
- Open the legacy `/analytics` deep link directly; verify its redirect lands on the dashboard (`/`) and produces no console errors, including after refresh and browser Back/Forward.
- Compare management, owner, leasing, technician, and tenant shells with the appropriate test identities; inaccessible navigation should disappear or land on a clear access boundary.
- Check header search, messages, appointments, command center, help, and assistant entry points for correct role-dependent destinations.

## Specific edge cases worth trying

- Direct /, trailing-slash, encoded IDs, unknown IDs, missing query parameters, duplicate query parameters, and malformed tab names.
- Open a deep link in a new tab, use Back during a pending load, Forward after a form submit, and refresh while a group is collapsed.
- Zero-record dashboard, very large counts, long portfolio name, long activity title, simultaneous activity update, and API timeout.
- 390px navigation drawer, keyboard focus within collapsed groups, reduced-motion preference, and high browser zoom.

## What to verify visually

- Metric cards use consistent number/currency formatting, loading skeletons, and empty/error states; links look clickable without misleading disabled styling.
- Only the current group/item is highlighted; drawer and breadcrumbs never cover content, and long labels truncate with accessible names.
- Deep-linked tab/filter state is visible after reload and Back/Forward; no flash of the wrong page or stale route title.

## Data safety and evidence

Do not change or delete dashboard source records. If an additive record is necessary, mark every free-text field QA-YYYYMMDD and use it only for link verification.

Record actual-versus-expected behavior, URLs, response/status evidence, persistence after reload, and console errors. Report confirmed bugs separately from potential issues and observations using the BUG-N format from the exploratory tester definition.
