# TSK-397 Pass 79 Tenant Portal Regression

Date: 2026-06-25
Branch: `tsk-397-real-user-pass-79`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

## Scope

Verify the tenant portal as a real tenant user against local sanitized data, with emphasis on the fixes from the tenant portal Notion cluster:
dashboard notifications, messages, payments/autopay disabled state, lease Q&A text cleanup, appointments, maintenance validation, maintenance image attachments, tenant account security, and tenant sign-out.

## Local Stack

- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`
- Data mode: local sanitized/example data only
- Browser: Playwright CLI with installed Chrome channel

## Tenant Account

- Tenant: Blake Hayes Portal
- Email: `blake.hayes.portal.pass55@example.local`
- Password: `TenantPass!23`
- Tenant id: `10`
- Portfolio id: `4`

## Test Data

Local-only notification rows were inserted before the browser pass so read-state could be tested from a real unread state:

- Notification `14`: `TSK-397 Pass 79 message notification`, action `/portal/messages?conversation=2`
- Notification `15`: `TSK-397 Pass 79 payments notification`, action `/portal/payments`

Camera-style image fixture:

- `output/qa/production-scale-scans/05-work-orders-camera/work-order-002.jpg`
- JPEG, `1800x2400`, `133263` bytes

## Acceptance Criteria

- Tenant login lands on `/portal` and exposes only tenant portal navigation: Dashboard, Messages, Notifications, Maintenance, Payments, Lease, and Appointments.
- Dashboard notification buttons mark unread notifications read before navigating, and the header unread count refreshes.
- Full Notifications page buttons mark unread notifications read before navigating, and the header unread count refreshes.
- Header notification dropdown opens from tenant routes, lists tenant notifications, preserves `0 unread`, and its `View notifications` link routes to `/portal/notifications`.
- Payments must show online payments unavailable when Stripe is off, with disabled autopay and disabled per-row payment buttons.
- Payment explanation buttons must expose readable rent-charge context without enabling checkout.
- Lease Q&A suggestion buttons must return safe visible text with raw Markdown markers stripped.
- Appointments must show real tenant-scoped appointment title, time range, unit label, type label, and status.
- Maintenance full-page submit must show inline title/description validation on empty submit.
- Maintenance valid submit must create a tenant work order, reset the form, show a success toast, and list the new request first.
- Tenant-created maintenance camera image must be stored as an image attachment, listed in the detail dialog, and downloadable/openable without byte changes.
- Messages must open an existing thread, enable send only after text entry, persist a tenant reply, show it in the thread and list, and clear the input.
- Tenant Security must route to `/portal/security`, not staff settings, show validation for weak/mismatched password input, keep submit disabled, and toggle password reveal controls.
- Tenant Sign Out must end at `/login` and clear auth cookies.
- Console errors/warnings and failed browser requests must remain at zero for the covered flows.

## Risk-Based Edge Cases Covered

- Read-state race: notification is marked read and the app-shell badge is refreshed before route navigation.
- Provider-disabled payments: no checkout/autopay POST is offered or triggered when online payments are unavailable.
- Empty maintenance submit: invalid field state is visible instead of a silent no-op.
- Camera image persistence: upload, work-order attachment listing, download, file type, dimensions, size, and SHA-256 are checked.
- Tenant document ownership path: attachment is reachable only through the tenant-owned work-order surface exercised by the portal.
- Password validation: weak length plus confirmation mismatch blocks submit; reveal controls do not alter values.
- Markdown cleanup: lease Q&A answer text contains no `**` or `__` markers.
- Tenant session cleanup: sign-out clears cookies.

## Browser Results

Login and dashboard:

- Tenant login reached `https://localhost:6042/portal` with page title `Tenant Dashboard - Rental Command`.
- Dashboard showed Blake Hayes Portal, portal-only nav, `0 unread notifications` after the read-state checks, Payments, Lease, Maintenance Requests, Appointments, and Latest Messages cards.
- Earlier unread setup showed the badge at `2`; after clicking the dashboard message notification, the app navigated to `/portal/messages?conversation=2` and the unread count dropped to `1`. After clicking the full notifications page payment notification, the app navigated to `/portal/payments` and the unread count dropped to `0`.

Notifications:

- DB proof after clicks:
  - Notification `14` was read at `2026-06-25 04:14:55.181548+00`.
  - Notification `15` was read at `2026-06-25 04:15:23.326722+00`.
- Header notification dropdown opened from `/portal/payments`, showed the Pass 79 notifications, preserved `0 unread notifications`, and `View notifications` routed to `/portal/notifications`.
- `/portal/notifications` rendered the Pass 79 entries with page title `Notifications - Rental Command`.

Payments:

- `/portal/payments` rendered the unavailable banner: `Online payments aren't set up yet.`
- Autopay `Set up autopay` was disabled.
- Three rent rows showed disabled `Pay unavailable` buttons.
- The first `What is this charge?` button opened a visible tooltip: `This is your $22.25 monthly rent, due Jun 25, 2026.`
- Browser request proof included `GET /api/v1/portal/payments => 200` and `GET /api/v1/portal/autopay => 200`; no checkout/autopay enrollment POST was triggered.

Lease:

- `/portal/lease` rendered lease `QA-2026-002-2B`, `Riverside Flats Unit 2B`, rent `$1,200.00`, and end `Feb 1, 2027`.
- Clicking suggestion `When is rent due?` returned `Rent is due on the 1st of each month.`
- Structured assertion returned `hasLeaseAnswer: true` and `hasRawMarkdown: false`.
- Request proof: `POST /api/v1/portal/lease/ask?leaseId=8 => 200`.

Appointments:

- `/portal/appointments` rendered `Pass 51 service visit date fixed`.
- Time range: `Sat, Jun 27, 9:30 AM - 10:15 AM`.
- Unit: `Riverside Flats Unit 2B`.
- Type/status: `Maintenance`, `Scheduled`.
- Structured assertion returned all expected appointment fields as true.

Maintenance:

- Empty submit on `/portal/maintenance` showed:
  - `Issue title is required.`
  - `Describe the issue before submitting.`
- Valid submit created `TSK-397 Pass 79 tenant portal photo regression`, showed `Maintenance request submitted.`, reset the form, and listed the new request first as `Normal` / `New`.
- Attachment upload used `output/qa/production-scale-scans/05-work-orders-camera/work-order-002.jpg`.
- Detail dialog showed the description, `Photos & documents`, `work-order-002.jpg`, `130.1 KB`, and Progress `New` / `Created`.
- Clicking `work-order-002.jpg` downloaded `.playwright-cli/work-order-002.jpg`.
- SHA-256 matched the source fixture:
  - `f8ab417ce87918bd21905488a252bd17be4c4d1a19d5ea77444f77323e99dc6c`
- Downloaded image remained JPEG `1800x2400`.
- Request proof:
  - `GET /api/v1/portal/work-orders => 200`
  - `POST /api/v1/portal/tenant/work-orders => 201`
  - `POST /api/v1/documents => 201`
  - `GET /api/v1/portal/work-orders/24 => 200`
  - `GET /api/v1/documents?entityType=WorkOrder&entityId=24 => 200`
  - `GET /api/v1/documents/45/file => 200`
- DB proof:
  - Work order `24`, title `TSK-397 Pass 79 tenant portal photo regression`, status `0`, priority `1`
  - Stored file `45`, `work-order-002.jpg`, `image/jpeg`, `133263` bytes, `EntityType = WorkOrder`, `EntityId = 24`, not deleted

Messages:

- `/portal/messages` loaded the conversation list and selected conversation `2`.
- Existing thread `TSK-397 Pass 65 lease portal question` rendered tenant and management messages.
- Reply composer started with disabled send; after typing, `Send message` became enabled.
- Sent reply: `TSK-397 Pass 79 tenant reply proof: I can still view the thread and respond from the portal after reading notifications.`
- Reply appeared in the thread and conversation list; input cleared and send returned disabled.
- DB proof: `ConversationMessages.Id = 8`, `ConversationId = 2`, `SenderRole = Tenant`, body starts with the Pass 79 reply.

Security:

- Tenant user menu opened on tenant routes with `Security` and `Sign Out`.
- `Security` routed to `https://localhost:6042/portal/security`, title `Security - Rental Command`, with `Back to portal`.
- Invalid values `Short1!` / `Different1!` showed password rule text and `Passwords do not match.`
- `Change password` stayed disabled; no password change was submitted.
- Password reveal controls toggled:
  - current password: `password -> text -> password`
  - new password: `password -> text -> password`
  - confirmation: `password -> text -> password`

Sign out:

- Tenant `Sign Out` returned to `https://localhost:6042/login`, page title `Sign in - Rental Command`.
- `cookie-list` returned `No cookies found`.

## Evidence Artifacts

- Final screenshot: `output/playwright/pass79-tenant-portal-messages-final.png`
- Downloaded image proof: `.playwright-cli/work-order-002.jpg`
- Representative snapshots:
  - `.playwright-cli/page-2026-06-25T04-18-21-737Z.yml` login to portal
  - `.playwright-cli/page-2026-06-25T04-18-45-591Z.yml` lease Q&A answer
  - `.playwright-cli/page-2026-06-25T04-19-14-234Z.yml` appointments
  - `.playwright-cli/page-2026-06-25T04-19-29-245Z.yml` maintenance validation
  - `.playwright-cli/page-2026-06-25T04-20-49-665Z.yml` maintenance create
  - `.playwright-cli/page-2026-06-25T04-21-02-045Z.yml` maintenance detail with document
  - `.playwright-cli/page-2026-06-25T04-22-45-779Z.yml` message reply
  - `.playwright-cli/page-2026-06-25T04-24-30-883Z.yml` payments unavailable
  - `.playwright-cli/page-2026-06-25T04-24-54-172Z.yml` tenant security
  - `.playwright-cli/page-2026-06-25T04-26-32-965Z.yml` full notifications page
  - `.playwright-cli/page-2026-06-25T04-26-49-055Z.yml` signed-out login page

## Console And Network

- Final Playwright `console error`: `Total messages: 2 (Errors: 0, Warnings: 0)`, returned 0 error messages.
- Final Playwright `console warning`: `Total messages: 2 (Errors: 0, Warnings: 0)`, returned 0 warning messages.
- Final failed-request scan found no `4xx` or `5xx` responses in Playwright request history for the covered pass.

## Status

Pass. No new product bug was found in this tenant portal regression slice, so no code fix or regression test was added in Pass 79. Remaining TSK-397 deferred boundaries are unchanged: Plaid banking, connected QuickBooks/accounting provider workflows, production/sensitive data, real SMS, and final Go Live.
