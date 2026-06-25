# TSK-397 Pass 77 Settings Hub Guide

## Scope

Verify `/settings` as a real landlord using local example data: account mode guard, tab/hash navigation, portfolio defaults, notification delivery, automations, messaging defaults, SMS-provider UI, team/owner/setup/activity links, and the accounting connection entry point.

## Setup

- Web: `https://localhost:6042`
- Login: visible dev-admin helper on `/login`
- Account: `admin@rentalcommand.local`
- Data mode: local example/sample data only

## Safety Boundaries

- Do not press the final `Yes, switch to my real rentals` Go Live action.
- Do not enter real SMS/accounting credentials.
- Do not send a live SMS.
- Do not connect, import from, disconnect, or mutate QuickBooks/accounting providers until sandbox credentials are provided.

## Acceptance Criteria

- `/settings` loads the sandbox/account-mode card and all tabs: Portfolio, Notifications, Automations, Messaging, Team, Owners, Setup & import, Activity history.
- Tab clicks and hash deep links render the correct tab without page hard errors.
- Go Live opens a type-to-confirm destructive dialog, keeps final confirmation disabled until `GO LIVE`, and cancels cleanly without clearing sample data.
- Portfolio basics can be edited and saved through the typed form, including time zone/status/rent collection day, and can be restored.
- Advanced settings show/add/remove controls work without exposing raw JSON.
- Notification email saves a local example address, can be restored, and refreshes without losing other portfolio settings.
- Notification channel matrix, daily briefing fields, and automation toggles save through the delivery settings endpoint and reload coherently.
- Broadcast is disabled until title and message are present; sending a local example-data in-app announcement refreshes the bell list/unread count without external delivery.
- Messaging defaults save through portfolio settings and reload coherently.
- SMS provider selector reveals provider-specific credential fields; `Send test SMS` stays disabled until a phone number is present. Do not submit a live test SMS.
- Team, Owners, spreadsheet import, Activity history, and Accounting link cards route to their intended pages.
- In sample/example-data mode, Setup wizard activates the sandbox guard and returns to Dashboard with live-account-only copy instead of starting live setup.
- `/settings/accounting` loads the provider list and safely stops at the not-configured/disconnected boundary unless sandbox credentials are provided.

## Browser Steps

1. From signed-out state, use the dev-admin helper and sign in.
2. Open `/settings`; snapshot the sandbox card and tab list.
3. Open the Go Live dialog, type a wrong phrase, confirm the final action remains disabled, then type `GO LIVE`, confirm the final action enables, and cancel.
4. Cycle each Settings tab and verify the URL hash and intro panel match the clicked tab.
5. In Portfolio, edit safe local fields, save, reload, verify persistence, then restore original values.
6. Use Advanced show/add/remove without saving a permanent row.
7. In Notifications, save a local example notification email, save at least one channel/daily-briefing change, then restore original values where practical.
8. Verify Broadcast disabled state, then send one local example-data announcement and confirm it appears in the notification dropdown.
9. In Automations, save a small local settings change and verify reload state.
10. In Messaging, toggle email/SMS defaults, save, reload, then restore if changed. Open the SMS provider select, verify provider-specific fields for Twilio/SignalWire, and do not send a test SMS.
11. Follow Team, Owners, Setup wizard, Import, Accounting, and Activity links; confirm each route loads and return to `/settings`.
12. On `/settings/accounting`, verify provider cards/status and stop before external connect/import/disconnect actions.
13. Check request log and console output.

## Evidence To Capture

- Snapshots or Playwright command outputs for Settings tabs, Go Live dialog, portfolio save, notification save, broadcast result, messaging/SMS provider UI, `/settings/accounting`, and signed-in shell after returning.
- Request log for settings/portfolio/notification/accounting endpoints and route loads.
- Console log showing no unexpected errors or warnings.

## Results - 2026-06-25

Environment:
- Branch: `tsk-397-real-user-pass-77`
- Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- Account: Rental Command Admin, `admin@rentalcommand.local`
- Data mode: local sample/example data only

Account mode and tabs:
- `/settings` loaded with page title `Settings - Rental Command`, the top example-data banner, and `settings-sandbox-section` visible.
- Go Live dialog opened from `Use my real rentals`; wrong phrase kept final confirm disabled, exact `GO LIVE` enabled final confirm, and Cancel returned to Settings without clearing example data.
- Tab clicks selected all eight tabs and mirrored the URL hashes: `#portfolio`, `#notifications`, `#automations`, `#messaging`, `#team`, `#owners`, `#setup`, and `#activity`.
- Deep link `https://localhost:6042/settings#messaging` selected Messaging and rendered `settings-messaging`.

Portfolio:
- Baseline values were `Default Portfolio`, management company `Rental Command`, time zone `Eastern Time - New York (America/New_York)`, status `Active`, and rent collection day `1`.
- Browser edited safe local fields to `Default Portfolio Pass 77`, description `Local example-data portfolio save test for Pass 77.`, company `Rental Command QA`, Central time, status `Onboarding`, and collection day `2`.
- `PATCH /api/v1/portfolios/1` returned `200`; reload showed the changed values persisted.
- The same form restored the original values and reload confirmed the baseline was back in place.
- Advanced settings Show/Add/Remove produced one row and then zero rows again; no permanent advanced row was saved.

Notifications:
- Notification email was saved to a local example address during the run and restored to blank with `PUT /api/v1/notifications/email => 200`.
- Channel/daily-briefing fields were changed through the UI, saved with `PUT /api/v1/notifications/settings => 200`, reloaded, and then restored to the original disabled/blank state.
- Broadcast stayed disabled with title-only input, enabled after title plus message, and `POST /api/v1/notifications/broadcast => 201` created one local in-app announcement visible from the bell.
- No external email or SMS delivery was attempted.

Automations:
- Interrupted browser work left temporary automation values in the form; the run explicitly normalized them back to service defaults.
- Final defaults verified in UI: rent charges disabled, late fees disabled, lease reminders enabled, tenant notifications disabled, rent charge lead days `5`, late fee grace days `5`, and lease expiry reminder days `60`.
- Save used `PUT /api/v1/notifications/settings => 200`.

Messaging and SMS provider UI:
- Message email/SMS defaults were toggled true/true, saved through `PATCH /api/v1/portfolios/1 => 200`, reloaded, then restored false/false with another successful portfolio patch.
- Twilio selection showed Account SID/Auth Token fields and hid the third credential slot.
- `Send test SMS` was disabled with no test number and enabled after entering `+15555550177`; the test send was intentionally not clicked.
- SignalWire selection showed Project ID/API Token/Space URL fields.
- Reload without saving returned provider selection to `None (use platform default)`.

Routes and safe boundaries:
- Card routes passed after waiting for each visible link:
  - `settings-security-open` -> `/settings/security`, title `Security - Rental Command`.
  - `settings-team-open` -> `/admin/users`, title `Team - Rental Command`.
  - `settings-owners-open` -> `/owners`, title `Owners - Rental Command`.
  - `settings-setup-import` -> `/import`, title `Import from a spreadsheet - Rental Command`.
  - `settings-accounting-open` -> `/settings/accounting`, title `Connect your accounting - Rental Command`.
  - `settings-activity-open` -> `/audit`, title `Activity history - Rental Command`.
- Guided setup in example-data mode clicked `/onboarding?from=settings`, bounced to `/`, and displayed `Setup runs on a live account. Go live first to set up your real portfolio.` This matches the sandbox guard in `web/src/routes/(protected)/onboarding/+page.svelte`.
- `/settings/accounting` showed QuickBooks and `Not connected`, with no hard-error state. `Connect QuickBooks` was visible but not pressed.

Console and recovery notes:
- Final Playwright CLI `console error` and `console warning` checks returned 0 errors and 0 warnings.
- Mid-pass Vite stopped and the browser briefly showed stale partial state. The web process was restarted with the local API certificate trusted through `NODE_EXTRA_CA_CERTS=/tmp/rentalcommand-api-6041.pem`; after re-login, Settings again showed the example-data banner/card. This was a local test-harness outage, not an app defect.

Status:
- Pass with documentation-only evidence. No code changes were needed for this Settings Hub slice.
- Deferred by boundary: final Go Live confirmation, real SMS delivery, accounting provider connect/import/disconnect, and any real/sensitive data.
