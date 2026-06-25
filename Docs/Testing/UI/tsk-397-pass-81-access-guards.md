# TSK-397 Pass 81 Access Guards And Stale Sessions

Date: 2026-06-25
Branch: `tsk-397-real-user-pass-81`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

## Scope

Verify the still-open admin/support guard variants using real browser sessions and local sanitized data:
tenant-only direct access to staff/admin/superadmin routes, admin self-row lockout, stale-session redirect,
and post-login redirect recovery.

This pass does not use production data, real SMS/email delivery, Plaid, QuickBooks, or final Go Live.

## Local Stack

- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`
- Browser: Playwright CLI with installed Chrome channel

## Accounts

- Owner/admin sample-data account: `tsk397.pass80.sandbox.1782362697@example.local`
- Tenant-only account: Blake Hayes Portal, `blake.hayes.portal.pass55@example.local`

## Acceptance Criteria

- An owner/admin can open `/admin/users`, see the Team grid, and cannot change their own role/status.
- Tenant-only users must not see the staff app shell when directly visiting staff-only settings or audit URLs.
- Tenant-only users must not see Admin Team or Admin Audit data.
- Superadmin Engine must remain hidden from non-allowlisted users when `PLATFORM_ADMIN_EMAILS` is unset.
- Guard/error pages must provide a recovery path that returns the tenant to the tenant portal, not a staff dashboard.
- Clearing cookies mid-session and opening a protected portal URL must redirect to `/login` with a safe local `redirectTo`, then return to the requested portal URL after login.
- Console and failed-request evidence must contain only the expected deliberate guard responses.

## Execution

### Owner/Admin Team Boundary

- Signed in as `tsk397.pass80.sandbox.1782362697@example.local`.
- Direct `/admin/users` loaded `Team - Rental Command` with the sample-data banner.
- The grid showed the current user row as `Pass 80 Sandbox User (you)` with role `Admin` and status `Active`.
- The current user's role and status controls were disabled, preventing self-lockout.

### Tenant Role Denial

- Signed in as Blake Hayes Portal, `blake.hayes.portal.pass55@example.local`.
- Login landed at `/portal` with portal-only navigation: Dashboard, Messages, Notifications, Maintenance, Payments, Lease, and Appointments.
- Direct `/admin/users` returned `Error 403`, `Something went wrong`, and `Admin access required`.
- Direct `/admin/audit` returned the same `Error 403` / `Admin access required` state.
- Direct `/superadmin/engine` returned the app `Error 404` page because local `PLATFORM_ADMIN_EMAILS` is not configured for this user.
- Direct `/settings/security`, `/settings/accounting`, and `/audit` redirected back to `/portal`, preserving the portal-only boundary.
- Clicking the 403 page's `Back to dashboard` recovery link returned Blake to `/portal`, not the staff Dashboard.

### Stale Session Recovery

- Cleared browser cookies with Playwright `cookie-clear`.
- Navigating to `/portal/messages` redirected to `/login?redirectTo=%2Fportal%2Fmessages` with the anonymous auth shell.
- Re-signing in as Blake Hayes Portal returned to `/portal/messages`, preserving the intended local redirect target.

## Evidence

- Tenant login snapshot: `.playwright-cli/page-2026-06-25T04-53-27-016Z.yml`
- 403 recovery snapshot: `.playwright-cli/page-2026-06-25T04-54-44-090Z.yml`
- Stale-session return snapshot: `.playwright-cli/page-2026-06-25T04-55-09-720Z.yml`
- Request proof included the expected deliberate guard responses:
  - `GET /admin/users => 403`
  - `GET /admin/audit => 403`
  - `GET /superadmin/engine => 404`
  - `POST /login?redirectTo=%2Fportal%2Fmessages => 200`
  - `GET /portal/messages/__data.json?... => 200`
- Final console scan after returning to `/portal/messages`: Vite debug messages only, `Errors: 0`, `Warnings: 0`.

## Status

Pass with documentation-only verification. No product bug or code fix was found in this guard/stale-session slice.
