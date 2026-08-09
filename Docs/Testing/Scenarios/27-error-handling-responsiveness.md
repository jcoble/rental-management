# Scenario 27 — 404s, forbidden routes, offline/API errors, loading states, responsiveness, and console health

## Purpose

Challenge the application’s recovery and responsive contract across landlord, relationship, public, and admin surfaces. A route that fails must explain what happened and provide a safe next action; a 390px viewport must remain usable.

## Preconditions and login

Use local dev where network throttling and controlled API failure are safe; use preview read-mostly for static route checks only. Test administrator, owner, technician, leasing, and tenant contexts without changing their shared records.

## Read the implementation first

Before opening a browser, read these named source files and the relevant controller/service code they call:

- web/src/routes/+layout.svelte and web/src/routes/+layout.server.ts
- web/src/routes/(protected)/+layout.svelte and web/src/routes/(protected)/+layout.server.ts, web/src/routes/(portal)/+layout.svelte and web/src/routes/(portal)/+layout.server.ts, web/src/routes/(admin)/+layout.server.ts, and web/src/routes/(superadmin)/+layout.server.ts
- web/src/hooks.server.ts, web/src/lib/api/client.ts, web/src/lib/utils/toast.ts, and web/src/lib/components/shared/LoadingState.svelte
- web/src/lib/components/AppShell.svelte and web/src/lib/components/data-grid/management-route-error-contract.test.ts
- Representative pages: web/src/routes/(protected)/+page.svelte, web/src/routes/(protected)/properties/[id]/+page.svelte, web/src/routes/(protected)/accounting/+page.svelte, web/src/routes/(protected)/maintenance/[id]/+page.svelte, web/src/routes/(protected)/messages/[id]/+page.svelte, web/src/routes/(portal)/portal/+page.svelte, and web/src/routes/login/+page.svelte
- RentalCommand.Api/Controllers/AuthController.cs, PortfolioController.cs, PropertyController.cs, PortalController.cs, AdminAuditController.cs, and DevWorkersController.cs

## Rough exploration areas

Use these as exploration goals, not a step-by-step script:

- Open unknown route, unknown property/unit/tenant/lease/work-order/report IDs, malformed dynamic segments, and an invalid report/tab/query; verify friendly 404/not-found behavior and a route back to safety.
- Use role identities to open forbidden management/admin/owner/tenant/technician routes directly; confirm denial is explicit, does not render partial data, and does not leak another portfolio.
- Throttle/offline the API during initial load, list paging, filter, form submit, file upload, message send, and token refresh; inspect retry/toast/error-card behavior and recovery after reconnect.
- Observe loading skeletons/spinners while responses are slow, then navigate Back/Forward or reload; no stale controls should submit against a different record.
- Resize to 390px wide (and a desktop width), rotate if available, use keyboard-only navigation, zoom, reduced-motion, and check console errors and failed requests throughout.

## Specific edge cases worth trying

- 404 with trailing slash/encoded slash, forbidden ID, deleted/missing source file, expired session, expired public token, and server 500/503/429/409 response.
- Offline during an optimistic mutation, duplicate retry after reconnect, API response delayed past navigation, browser Back while a toast is visible, and two tabs with different roles.
- Very long labels/table cells, empty/many rows, nested modal/drawer, virtual keyboard, landscape 390px, high contrast, and browser zoom 200%.
- TLS/certificate warning in local dev, blocked third-party provider, malformed JSON/error body, and unexpected console exception.

## What to verify visually

- 404/403/error cards identify the route and next action; retry/back/home controls are visible and keyboard reachable.
- Loading, empty, error, and success states have distinct copy and spacing; no invisible overlay blocks clicks.
- At 390px the shell, drawer, forms, grids, dialogs, toasts, and action buttons fit or scroll intentionally; no horizontal page overflow.
- Console is free of uncaught errors and failed requests are explainable; record exact URL/status when not.

## Data safety and evidence

Do not reset data or use destructive failure injection on preview. Controlled local network throttling is preferred; log only synthetic QA identifiers and no credentials.

Record actual-versus-expected behavior, URLs, response/status evidence, persistence after reload, and console errors. Report confirmed bugs separately from potential issues and observations using the BUG-N format from the exploratory tester definition.
