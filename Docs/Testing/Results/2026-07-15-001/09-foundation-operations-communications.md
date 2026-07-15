# Scenario 09 Result — Foundation Operations & Communications

**Run:** `2026-07-15-001`  
**Scenario:** `Docs/Testing/Scenarios/09-foundation-operations-communications.md`  
**Preview:** `https://rental-command.chimp-map.ts.net`  
**Initial source SHA:** `9a20158a4dbc0e3ac177ddd75d369e5c65ab55ec`  
**Status:** In progress — waiting for the refreshed preview before continuing real-browser journeys

## Implementation read before exploration

The tester traced the role shells, client routes, endpoint modules, controller routes, and primary
query services before opening the browser. The reviewed surfaces include:

- Web navigation and authorization: `AppShell.svelte` and `experience-policy.ts`
- Mobile navigation and authorization: `app_router.dart`, `mobile_access_policy.dart`,
  `mobile_role_shell.dart`, `mobile_shell_actions.dart`, and `settings_screen.dart`
- Money: accounting, banking, expenses, payments, deposits, and owner reports
- Work: work orders, responsibility, vendors, recurring maintenance, and inspections
- Communications: portal messages, personal alerts, team routing, tenant notice policies,
  templates, drafts, and delivery status
- Relationship experiences: owner and tenant web/mobile shells and their API controllers

The reviewed web endpoint paths align with their controller route sets. The role policy separates
management, leasing, maintenance, owner, and tenant experiences; relationship identities are denied
management destinations in the client policy. Property Managers receive property-scoped operational
money/reporting capabilities without workspace bank-connection, billing, integration, team, or
security administration.

Focused query-shape inspection of `NotificationFoundationService`, `OwnerPortalService`, and
`WorkOrderService` found filtering, sorting, and paging composed on `IQueryable` before async
materialization. No client-side grouping, load-then-filter, or per-row query defect has been proven in
those reviewed paths. This is a focused check, not a repository-wide SQL certification.

## Release gate found before the refreshed preview

### OPS-001 — Password form cannot establish the web session

- **Severity:** Critical / release blocker
- **Role/route:** Seeded Workspace Administrator at `/login`
- **Reproduction:** Open the stable preview, enter the seeded administrator credentials, and submit
  the sign-in form.
- **Expected:** The web session is established and the user lands in the authenticated management
  experience.
- **Actual:** The button briefly enters its pending state, the browser remains at `/login`, and no
  useful inline error is rendered.
- **Evidence:** A direct `POST /api/v1/auth/login` with the same credentials returned HTTP 200 and a
  Workspace Administrator/Management access envelope. The real Svelte form instead issued
  `POST https://rental-command.chimp-map.ts.net/login`, which returned HTTP 403 in Playwright's
  network/console output.
- **Code boundary:** Svelte login action/proxy/origin handling, rather than credential validation in
  the API, because the API login succeeds independently.
- **Suggested fix:** Correct the Svelte server action's preview origin/proxy request handling and
  surface its server error in the form. Re-verify by establishing a cookie-backed browser session.
- **Landlord impact:** Nobody can enter the product, so no operations, communication, role, or
  mobile-parity journey can be certified.

The root runner is updating the preview configuration. Browser mutation is intentionally paused
until the refreshed SHA is confirmed live, so evidence is not mixed between revisions.

## Browser journeys

Pending refreshed preview:

- Management money loop
- Work order/vendor/recurring maintenance loop
- Messages, My alerts, Team routing, and Tenant notices
- Owner relationship shell and direct-route denials
- Tenant portal shell and direct-route denials
- Mobile route/action parity and physical-phone follow-up

## Fixes made during this scenario

None yet. Obvious in-scope failures found after the refreshed preview will be fixed directly in the
shared foundation worktree and handed to the root runner for serialized build, test, and preview
refresh.

## Screenshots and external effects

No screenshots, videos, or traces were captured. No real bank was connected, no provider-backed
payment was attempted, and no Email/SMS delivery was sent.
