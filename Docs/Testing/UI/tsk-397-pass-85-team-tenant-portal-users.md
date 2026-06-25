# TSK-397 Pass 85 - Team Tenant Portal Users

Date: 2026-06-25
Branch: `tsk-397-410-real-user-pass-85-team-portal-users`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

## Scope

Real-user verification of the staff Team dialog at `/admin/users` for tenant portal user creation, covering TSK-410.

Covered route and controls:

- `/tenants` create tenant workflow
- `/admin/users` Team page
- Add member dialog
- Email, display name, role, tenant selector, and temporary password inputs
- Add member submit button disabled/enabled states
- Generated temporary password modal
- Staff sign out and tenant login
- Tenant `/portal` landing route and tenant-only navigation
- Tenant direct access denial for `/admin/users`

Deferred boundaries:

- Production data, sensitive data, real email/SMS delivery, Plaid banking, connected QuickBooks/accounting providers, and final Go Live remain out of scope.
- The generated-password value was not recorded in committed docs because it is a one-time credential.

## Acceptance Criteria

- Staff can create a new tenant from local sanitized data before creating a portal user.
- Team page loads for an admin user with no console errors or failed dynamic app requests.
- Add member starts disabled until required fields are valid.
- Selecting role `Tenant` reveals the tenant selector and keeps submit disabled until a tenant is selected.
- Tenant selector loads portfolio-scoped tenants from the server-side paged endpoint and includes the newly created tenant.
- Creating a tenant user with a supplied temporary password persists both the Identity user and `UserAccount` with the selected `TenantId`.
- The new tenant user can sign in with the supplied temporary password and lands on `/portal` with tenant-only navigation.
- The tenant user cannot directly access `/admin/users` and receives a staff/admin access denial.
- Creating a tenant user without a supplied password succeeds and shows the one-time generated-password modal.
- The generated-password modal cannot be closed through the primary close control until the admin acknowledges saving/copying the password.
- Clean final state has no browser console warnings/errors beyond the deliberate tenant 403 access-denial resource line.

## Risk-Based Edge Cases Covered

- Empty invite state: Add member remains disabled with no email.
- Tenant-role linking: `Tenant` role requires a tenant selection before any account is submitted.
- Cross-surface account linkage: the tenant id is written to Identity and the app-domain `UserAccount`, so JWT tenant scoping works after login.
- Supplied password path: no generated-password modal is shown when the admin supplies a temporary password.
- Generated password path: the one-time credential modal requires explicit copy/manual-save acknowledgement before close.
- Role guard: a tenant session receives a 403 for the staff Team route.
- Local-only safety: no real outbound invite email, SMS, payment, banking, or provider action was triggered.

## Evidence Log

Local stack:

- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Browser setup:

- Playwright CLI/browser state was closed before continuing the pass.
- No lingering Playwright/Claude browser worker processes were found in the process scan.
- Logged in with the local dev admin shortcut on `/login`; the app displayed the example-data safety banner.

Tenant setup:

- Created local tenant `Pass85 Tenant0610` from `/tenants`.
- DB proof: tenant id `55`, email `tsk397.pass85.tenant.20260625.0610@example.local`, created at `2026-06-25 06:10:52.114482+00`.
- Screenshot proof: `output/playwright/pass85-tenant-created.png`.

Team dialog states:

- `/admin/users` loaded the Team page for the admin account.
- Add member dialog initially kept `Add member` disabled with no email.
- Selecting role `Tenant` revealed the tenant selector.
- Submit stayed disabled until a tenant was selected.
- Tenant search/list request returned `GET /api/v1/tenants/page?take=100&sort=name&portfolioId=1 => 200` and included `Pass85 Tenant0610`.
- Screenshot proof before submit: `output/playwright/pass85-team-invite-tenant-ready.png`.

Supplied-password tenant portal user:

- Created portal user `tsk397.pass85.portal.20260625.0610@example.local` with display name `Pass85 Portal User`, role `Tenant`, tenant `Pass85 Tenant0610`, and a supplied temporary password.
- Submit returned `POST /api/v1/admin/users => 201`; the Team list refreshed with `GET /api/v1/admin/users/page?take=20&sort=-createdAt => 200`.
- Toast proof: `Pass85 Portal User added to the team.`
- Screenshot proof after create: `output/playwright/pass85-team-tenant-created.png`.
- DB proof:
  - `UserAccounts.Id = 32`
  - `Email = tsk397.pass85.portal.20260625.0610@example.local`
  - `DisplayName = Pass85 Portal User`
  - `Role = Tenant`
  - `TenantId = 55`
  - `IsActive = true`
  - linked Identity user id `32`
  - `EmailConfirmed = true`
  - Identity `PortfolioId = 1`
  - Identity `TenantId = 55`
  - Identity role `Tenant`

Tenant login and access guard:

- Signed out the admin user and signed in as `tsk397.pass85.portal.20260625.0610@example.local`.
- Login routed to `https://localhost:6042/portal`.
- Page title was `Tenant Dashboard - Rental Command`.
- Tenant-only navigation was visible: Dashboard, Messages, Notifications, Maintenance, Payments, Lease, and Appointments.
- Screenshot proof: `output/playwright/pass85-tenant-portal-login.png`.
- Direct tenant navigation to `/admin/users` returned the app error page with `Error 403` and `Admin access required`.
- Request proof: `GET https://localhost:6042/admin/users => 403`.
- Screenshot proof: `output/playwright/pass85-tenant-admin-denied.png`.

Generated-password modal:

- Re-signed in as admin and created another tenant portal user for `Pass85 Tenant0610` without filling temporary password.
- Submit returned `POST /api/v1/admin/users => 201` and opened the `Temporary password - save it now` modal.
- Modal showed the one-time warning, generated-password field, `Copy`, `I saved it manually`, disabled `I've copied the password`, and no committed documentation includes the generated password value.
- Clicking `I saved it manually` showed `Password marked as saved.`, enabled the primary close control, and changed `Copy` to `Copied`.
- Clicking `I've copied the password` closed the modal and left the generated user visible in the Team list.
- Local screenshot proof was captured but intentionally not referenced in committed docs because it contains a one-time temporary credential.

Console and network:

- Staff create flow produced no console errors.
- Tenant access-denial flow produced the expected browser resource error for the deliberate `403`; no unexpected dynamic app request failures were observed for the covered create/login workflow.

## Regression Coverage

Focused API regression tests were added in `RentalCommand.Api.Tests/Auth/AdminUsersControllerTests.cs`:

- `Create_TenantMember_RequiresTenantLinkBeforeCreatingIdentityUser`
- `Create_TenantMember_PersistsTenantLinkToIdentityUserAndUserAccount`

These tests assert that tenant-role member creation rejects missing tenant links before Identity creation and persists the selected tenant id to both the Identity user and app-domain user account.

## Status

Pass after regression-test coverage. TSK-410 did not reproduce on the current branch in local real-user testing, but the tenant-link contract is now covered by focused API tests. Deferred boundaries remain production/sensitive data, real SMS/email delivery, Plaid banking, connected QuickBooks/provider accounting, and final Go Live.
