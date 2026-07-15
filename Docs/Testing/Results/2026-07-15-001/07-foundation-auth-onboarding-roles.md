# Exploratory Test Report: Foundation Authentication, Onboarding & Roles

Date: 2026-07-15
Tester/session: `foundation-auth`
Source SHA exercised: `2404ec7abc802477569d0dfe7d4fec60b2760636`
Environment: `https://redacted-host.example.invalid`
Method: headed/headless Playwright CLI snapshots and visible controls; no screenshots, video, or trace
Overall status: **Partial pass — password auth and one Team activation work; Google OAuth and the full persona matrix remain incomplete**

## Scenario

`Docs/Testing/Scenarios/07-foundation-auth-onboarding-roles.md`

## Summary

The refreshed preview fixed the original release-blocking origin problem. The password form now
reaches the API, displays the same accessible error for unknown and existing accounts, accepts the
seeded administrator, preserves protected navigation across refresh, and signs out cleanly. Forgot
password also returns the same neutral confirmation for unknown and existing addresses.

Google is now visible and starts the configured OAuth request, but Google rejects the stable Rental
Command callback with `redirect_uri_mismatch`. The callback that must be registered is
`https://redacted-host.example.invalid/auth/google/callback`.

The existing sample workspace exposes both sample and live choices, sample setup can be safely
re-entered without visible duplication, and Guided Setup recognizes its existing canonical records.
The destructive go-live path, a brand-new empty workspace, and the complete scan/manual setup flows
were intentionally not exercised in this shared run.

Team exposes the four approved job presets and relationship-role explanation. A Property Manager was
created with exactly one selected Property and its queued test invitation successfully activated the
same identity. The live Team list nevertheless labeled that person `Active` before activation and did
not explain that sign-in was impossible until delivery/use of the link. A narrow source fix now adds
`requiresAccountActivation` to the existing DB-side Team projection and shows explicit web/mobile
activation-required copy; that fix was not yet deployed during this walkthrough.

## Section status

| Section | Status | Evidence / boundary |
|---|---|---|
| A. Login failures, recovery, valid login | **Pass / Partial** | Unknown and real wrong-password attempts showed `Invalid email or password`; valid admin login, refresh, sign-out, protected deep-link redirect, and neutral forgot-password responses passed. Reset-token expiry/reuse was not run. |
| B. Google and access contexts | **Fail / Blocked** | Button is visible and reaches Google, but Google rejects the stable callback as unregistered. Multi-context selection could not be completed. |
| C. First-run sample/live decision | **Partial pass** | Both choices are visible. Existing sample setup and repeated sample entry passed visibly. Destructive go-live and a separate empty workspace were not run. |
| D. Guided setup | **Partial pass** | Existing-record summary loaded from the server and resolved to `You're all set!` after refresh. Scan import and fresh single/multiple rental creation belong to Scenario 08 and were not duplicated here. |
| E. Team presets, activation, scope | **Partial pass** | Four presets, selected-property PM creation, password mismatch validation, and valid activation passed. Full live role authorization matrix and reused/expired activation tests remain unexecuted. |
| F. Sole landlord, Owner, Tenant, account/mobile | **Blocked / code-grounded only** | Admin account menu, Security, sign-out, and signed-out denial passed. No activated Owner/Tenant fixtures or installed-mobile persona session were available. Web/mobile route policies were inspected but are not reported as live UI proof. |

## Live actions and evidence

### A — Password login, recovery, and sign-out

- `/login` visibly exposes Email, Password, Forgot password, Create account, Sign In, and Google.
- Unknown account `qa-aor-unknown@example.invalid` with a wrong password:
  - remained on `/login`;
  - rendered accessible alert `Invalid email or password`;
  - returned the button from `Signing in…` to `Sign In`.
- Existing account `admin@rentalcommand.local` with a wrong password produced the identical alert.
- Valid seeded-admin credentials left `/login` and opened the authorized Management dashboard shell.
- `/admin/users` was reachable while authenticated. After sign-out, the same direct URL redirected to
  `/login?redirectTo=%2Fadmin%2Fusers`.
- `/forgot-password` returned exactly the same visible confirmation for the unknown address and the
  existing administrator: `If an account exists for that email, we've sent a password reset link.`
- The original raw-transport dead-button problem is covered by the refreshed source fix, but this run
  did not deliberately break the shared stack to reproduce a transport outage.

### B — Google OAuth

- `Sign in with Google` is visible after preview integration configuration was restored.
- Clicking it reached Google OAuth rather than failing in Rental Command.
- Google displayed `redirect_uri_mismatch` for the exact callback
  `https://redacted-host.example.invalid/auth/google/callback`.
- Cancel/success, identity linking, new-Google-account behavior, and context selection remain blocked
  until the callback is registered in the Google Cloud OAuth client.

### C/D — Setup and Guided Setup

- The authenticated sample workspace visibly identifies itself with the Example data banner.
- `/get-started` showed both `Explore with sample data` and `Set up my real portfolio`, including the
  data-removal warning for the live choice.
- Re-entering `Start exploring` completed without a visible error or duplicate setup prompt and
  returned the completed Getting Started checklist.
- `/onboarding` rendered Step 1 while the canonical server summaries loaded, then resolved to
  `You're all set!` with Setup, Property, People, and optional Notify groups.
- Refresh repeated the server load and resolved to the same completed state, rather than losing local
  progress or asking for duplicate records.
- This shared run did not press the destructive live-data choice. It also did not create another clean
  workspace merely to duplicate Scenario 08's scan/single/multiple-rental coverage.

### E — Team creation, scope, and activation

- `/admin/users` explains that Owners and Tenants are relationship-managed experiences, not Team
  jobs.
- The visible job presets are Workspace Administrator, Property Manager, Leasing Agent, and
  Maintenance Technician, with their descriptions and allowed scope choices.
- Created `QA-AOR-0928 Property Manager` / `qa-aor-0928-pm@example.invalid` as Property Manager,
  `SelectedProperties`, scoped only to `Dublin Single Family`.
- Durable IDs read back from the preview database in one server-side join:
  - User `2`
  - Access context `2`
  - Workspace membership `2`
  - Role assignment `2`
  - Selected Property `7`
- The assignment sheet showed one active Property Manager assignment and the complete selected scope.
- A signed-out activation visit with no token showed `Invalid activation link` and instructed the user
  to ask the administrator for a new invitation.
- Using only this designated QA invitation from its queued outbox payload, without logging the token:
  - mismatched passwords rendered `Passwords do not match.`;
  - matching valid passwords rendered `Your account is ready`;
  - the activation updated the same invited identity rather than creating another user.
- Reused and expired token behavior was not executed before the checkpoint.

## Code-grounded role route matrix (not live persona proof)

The route policies below establish the intended boundary, but the missing live identities mean this
section must not be treated as end-to-end acceptance.

| Persona | Intended visible/allowed area | Intended direct-route denials | Evidence |
|---|---|---|---|
| Workspace Administrator | Management shell, Guided Setup, Team, workspace security/integrations/billing by capability | Leasing/Maintenance/Owner/Tenant shells | `web/src/lib/auth/experience-policy.ts`; server `(admin)`/`(protected)` layout guards |
| Property Manager | Management operations, scoped rentals/work/money/reconciliation by capability | Team/security, workspace billing/integrations, bank connections/payout setup | management experience plus capability checks in shared web/mobile policies |
| Leasing Agent | `/leasing/*`, permitted scan/messages/notices | broad `/properties`, `/applications`, accounting, Team | purpose-built Leasing rules in both policies |
| Maintenance Technician | assigned work/schedule/conversation/update/scan | broad rental, money, lease, and unassigned work screens | assignment-scoped Maintenance rules in both policies |
| Owner | `/owner/*` relationship projections and personal alerts | Management root/settings/data lists | Owner-only web shell and mobile `OwnerLandingScreen` |
| Tenant | `/portal/*` relationship projections and personal alerts | Management and Owner shells | Tenant-only web route and mobile tenant role shell |

Web uses one `ROUTE_ACCESS_RULES` catalog for both navigation visibility and direct-route layout
guards. Unknown routes fail closed. `web/src/lib/auth/experience-policy.test.ts` contains explicit
representative assertions for all six personas and stale-capability denial. Mobile's
`canOpenMobilePath` similarly checks the active experience before capability-specific routes, and
`app_router.dart` redirects unauthorized deep links to `/access-denied`. No test command was run in
this lane; this is source inspection only.

## Bugs and disposition

### BUG-1 — Resolved in refreshed preview — public login POST rejected by SvelteKit origin check

- Original behavior: `POST /login` returned 403 before reaching the API.
- Refreshed behavior: unknown/wrong/valid password submissions all reach the intended form action.
- Fix boundary: stable public HTTPS origin is now used by the preview web runtime.

### BUG-2 — Partially resolved — preview integration configuration was discarded

- Google/Places/assistant values are now propagated by protected preview configuration; outbound
  delivery remains deliberately suppressed.
- Google is visible, proving the web client received enabled configuration.
- Remaining OAuth registration failure is tracked separately as BUG-5.

### BUG-3 — Resolved in refreshed source — login transport failure was invisible

- The login form now has explicit transport-error handling and always resets submission state.
- Unknown and wrong credentials were visibly verified; the destructive transport-outage induction
  was not repeated against the shared stack.

### BUG-4 — Resolved for this run — no usable role fixture after the clean reset

- A selected-property Property Manager identity was created and activated through the product's
  canonical membership/invitation flow.
- Owner, Tenant, Leasing, and Maintenance live fixtures still need deliberate creation for the final
  persona walkthrough.

### BUG-5 — High / open configuration defect — Google callback is not registered

- **Route:** `/auth/google`
- **Expected:** Google accepts the stable callback and returns to Rental Command.
- **Actual:** Google displays `redirect_uri_mismatch`.
- **Required configuration:** add
  `https://redacted-host.example.invalid/auth/google/callback` to the authorized redirect URIs of
  the configured Google OAuth web client.
- **Impact:** Google sign-in cannot be completed even though the button and application config exist.

### BUG-6 — Medium / source fix pending deployment — invited member looked usable before activation

- **Route:** `/admin/users` and the mobile Team screen.
- **Expected:** membership status and account activation status are distinct. An administrator can
  see that the assigned member cannot sign in until the queued link is delivered and used.
- **Actual in live preview:** the new member row showed `Active` with no activation-required state;
  the success toast only said the email was queued. Delivery is suppressed in this preview.
- **Narrow fix in worktree:**
  - add `RequiresAccountActivation` to `TeamMemberSummaryDto`;
  - project it from `ApplicationUser.PasswordHash` inside the existing paged SQL query;
  - display explicit activation-required copy on both web and mobile;
  - preserve the secure design: only the invitation hash is durable and no token/readback API is
    added.
- **Verification:** diff check passed. Build/tests and refreshed preview proof are intentionally left
  to the orchestrator's serialized verification step.

## Not yet proven end to end

- Google success/cancel/linking and multi-context selection.
- Password-reset invalid/expired/reused token handling.
- Destructive sample-to-live transition and an empty direct-live workspace.
- Full scan-first and manual one/multiple-rental Guided Setup journeys (Scenario 08 ownership).
- Reused/expired Team invitation behavior.
- Live Property Manager capability/record-scope denials after activation.
- Live Leasing, Maintenance, Owner, and Tenant shells, account menus, and server/API ID probes.
- Installed-mobile login, bottom navigation, role shells, and unauthorized deep links.

## Source files changed by this lane

- `RentalCommand.Api/Controllers/TeamController.cs`
- `RentalCommand.Api/DTOs/TeamAuthorityDtos.cs`
- `web/src/lib/api/endpoints/team.ts`
- `web/src/routes/(admin)/admin/users/+page.svelte`
- `mobile/lib/features/team/team_repository.dart`
- `mobile/lib/features/team/team_screen.dart`
- this result file

No temporary screenshots, videos, traces, tokens, or copied outbox payloads were retained.
