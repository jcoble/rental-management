# Scenario 17 — Tenant portal, public application, files, and signed links

## Purpose

Test the tenant-facing shell and unauthenticated public edges as a different product surface: dashboard, account/lease, payments, maintenance, appointments, messages, notifications, profile/security, unlinked state, public application, e-sign, and file delivery.

## Preconditions and login

Use a tenant identity linked to a QA household, or local fixtures for portal-only work. Staff and tenant browser contexts must stay separate. Public apply/sign/file links must be tested without staff cookies. Provider-backed payment or messaging may be inspected only to the point the environment explicitly supports.

## Read the implementation first

Before opening a browser, read these named source files and the relevant controller/service code they call:

- web/src/routes/(portal)/+layout.svelte and web/src/routes/(portal)/+layout.server.ts
- web/src/routes/(portal)/portal/+page.svelte, web/src/routes/(portal)/portal/account/+page.svelte, web/src/routes/(portal)/portal/lease/+page.svelte, web/src/routes/(portal)/portal/payments/+page.svelte
- web/src/routes/(portal)/portal/maintenance/+page.svelte, web/src/routes/(portal)/portal/appointments/+page.svelte, web/src/routes/(portal)/portal/messages/+page.svelte
- web/src/routes/(portal)/portal/notifications/+page.svelte, web/src/routes/(portal)/portal/profile/+page.svelte, web/src/routes/(portal)/portal/security/+page.svelte, and web/src/routes/(portal)/portal/unlinked/+page.svelte
- web/src/routes/apply/[token]/+page.svelte and web/src/routes/(public)/sign/[token]/+page.svelte
- web/src/routes/application-file/[id]/+server.ts, web/src/routes/document-file/[id]/+server.ts, web/src/routes/expense-file/[id]/+server.ts, web/src/routes/scan-file/[id]/+server.ts, and web/src/routes/workorder-file/[id]/+server.ts
- RentalCommand.Api/Controllers/PortalController.cs, RentalCommand.Api/Controllers/PublicApplicationsController.cs, RentalCommand.Api/Controllers/SignController.cs, RentalCommand.Api/Controllers/DocumentsController.cs, RentalCommand.Api/Controllers/TenantController.cs, RentalCommand.Api/Controllers/TenantAccountsController.cs, and RentalCommand.Api/Controllers/NotificationsController.cs

## Rough exploration areas

Use these as exploration goals, not a step-by-step script:

- Walk every tenant portal destination and compare dashboard balances, lease identity, next actions, payment history, maintenance requests, appointments, messages, notifications, profile, and security.
- Submit a marked maintenance request, add a safe comment/photo if supported, and confirm staff sees the same record and permission-controlled fields.
- Open account history, pay/autopay entry points, print statement, and provider-unavailable states; do not complete a real charge in preview.
- Start a portal message and reply in a second context; inspect unread/read transitions, URL-selected thread, compose validation, and tenant-only visibility.
- Use an unlinked tenant identity and verify the explanatory unlinked page rather than a series of 403 cards.
- Exercise public application, e-sign, and file links without staff authentication; confirm the token only grants the intended document/action and reflects back to staff.

## Specific edge cases worth trying

- Tenant with multiple accounts/leases, no account, overdue/zero balance, focused ledger entry, invalid account/entry IDs, and pagination boundary.
- Empty maintenance/message/notification/appointment lists, long message/photo metadata, duplicate operation key, double submit, and offline retry.
- Expired/reused/tampered apply/sign/file token, wrong tenant token, missing file, non-previewable content type, and partial download.
- Portal direct links to /accounting, /settings, /admin/users, another tenant’s portal route, and a stale session.

## What to verify visually

- Tenant shell navigation, account/lease identity, unread markers, payment provider copy, and empty states are clearly separate from landlord controls.
- Mobile 390px cards, message split-pane behavior, compose dialogs, upload previews, loading/error/retry states, and focus order are usable.
- Public pages never leak staff nav or unrelated tenant information; signed/file results show clear success/failure and return links.

## Data safety and evidence

Use QA-YYYYMMDD in tenant message/application/request text and file metadata. Never pay, refund, delete, void, or alter a shared preview lease/account.

Record actual-versus-expected behavior, URLs, response/status evidence, persistence after reload, and console errors. Report confirmed bugs separately from potential issues and observations using the BUG-N format from the exploratory tester definition.
