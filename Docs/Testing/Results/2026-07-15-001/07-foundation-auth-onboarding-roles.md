# Exploratory Test Report: Foundation Authentication, Onboarding & Roles

Date: 2026-07-15
Tester/session: `foundation-auth`
Source SHA exercised: `8419a55ff9ae850ef833431ac1c5cff65fd85bd7`
Environment: `https://redacted-host.example.invalid`
Method: visible Chrome controls and in-memory accessibility/DOM snapshots; no screenshots, video, trace, or retained browser artifacts
Overall status: **Partial pass — password auth, Administrator route guards, Team presets, activation status, and Guided Setup resume work; the full persona matrix remains incomplete**

## Scenario

`Docs/Testing/Scenarios/07-foundation-auth-onboarding-roles.md`

## Summary

The refreshed preview keeps the original release-blocking origin fix. The password form now
reaches the API, displays the same accessible error for unknown and existing accounts, accepts the
seeded administrator, preserves protected navigation across refresh, and signs out cleanly. Forgot
password also returns the same neutral confirmation for unknown and existing addresses.

An already-open browser tab initially lacked the Google button because it still held the pre-refresh
login document. After sign-out and a fresh `/login` navigation, `Sign in with Google` and
`data-testid="login-google-button"` were present. The current deploy therefore has the web-side
public Google client configuration. Full OAuth was not repeated in this checkpoint; the earlier
Google `redirect_uri_mismatch` remains an external configuration blocker until the exact callback
`https://redacted-host.example.invalid/auth/google/callback` is registered and retested.

The existing sample workspace still exposes its Example-data banner and live-data choice. On a cold
`/onboarding` load, the route temporarily exposed editable Step 1 while five existing-record queries
were still settling; it then resolved to `You're all set!` once the canonical portfolio, owner,
property, tenant, and LeaseManagement reads completed. A focused source fix now gates the editable
wizard behind explicit loading/error states so a user cannot resave setup during that misleading
intermediate state. The destructive go-live path, a brand-new empty workspace, and the complete
scan/manual setup flows were intentionally not exercised in this shared run.

Team exposes the four approved job presets and relationship-role explanation. The existing selected-
property Property Manager is now visibly distinguished from membership status with `Activation
required — this person cannot sign in until the queued email is delivered and its link is used.` The
narrow `requiresAccountActivation` source fix is therefore deployed and proven in the web preview.
The invitation dialog also visibly lists exactly Workspace Administrator, Property Manager, Leasing
Agent, and Maintenance Technician. A live Administrator direct-route check denied Leasing,
Maintenance, and Owner shells with a generic 403; the Tenant route returned the authorized Management
landing rather than leaking Tenant content. Other persona sessions remain unavailable without
activated fixtures.

## Section status

| Section | Status | Evidence / boundary |
|---|---|---|
| A. Login failures, recovery, valid login | **Pass / Partial** | Unknown and real wrong-password attempts showed `Invalid email or password`; valid admin login, refresh, sign-out, protected deep-link redirect, and neutral forgot-password responses passed. Reset-token expiry/reuse was not run. |
| B. Google and access contexts | **Partial / Blocked** | Fresh `/login` proves the current web runtime exposes the Google button. Full OAuth was not repeated; the earlier callback mismatch and multi-context selection remain unresolved. |
| C. First-run sample/live decision | **Partial pass** | Both choices are visible. Existing sample setup and repeated sample entry passed visibly. Destructive go-live and a separate empty workspace were not run. |
| D. Guided setup | **Partial pass / source UX fix pending deployment** | The populated sample workspace eventually resolves to `You're all set!`, but a cold load exposes editable Step 1 while detection is pending. Source now shows an explicit loading/error gate. Scan import and fresh single/multiple rental creation remain outside this checkpoint. |
| E. Team presets, activation, scope | **Partial pass** | Four presets, deployed activation-required copy, and Administrator direct-route denials passed. Full live role authorization matrix and reused/expired activation tests remain unexecuted. |
| F. Sole landlord, Owner, Tenant, account/mobile | **Blocked / code-grounded only** | Admin account menu, Security, sign-out, and signed-out denial passed. No activated Owner/Tenant fixtures or installed-mobile persona session were available. Web/mobile route policies were inspected but are not reported as live UI proof. |

## Live actions and evidence

### A — Password login, recovery, and sign-out

- A fresh `/login` visibly exposes Email, Password, Forgot password, Create account, Sign In, and
  Google. The previously open tab was stale across the preview refresh and was not accepted as current
  evidence.
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

- Fresh navigation rendered `Sign in with Google`; the button's test-id count was exactly one.
- This checkpoint did not repeat the external Google redirect. The previous run reached Google and
  received `redirect_uri_mismatch` for
  `https://redacted-host.example.invalid/auth/google/callback`.
- Cancel/success, identity linking, new-Google-account behavior, and context selection remain blocked
  until the callback is registered in the Google Cloud OAuth client and the flow is rerun.

### C/D — Setup and Guided Setup

- The authenticated sample workspace visibly identifies itself with the Example data banner.
- `/get-started` showed both `Explore with sample data` and `Set up my real portfolio`, including the
  data-removal warning for the live choice.
- Re-entering `Start exploring` completed without a visible error or duplicate setup prompt and
  returned the completed Getting Started checklist.
- `/onboarding` visibly groups Setup, Property, People, and optional Notify work.
- On a cold refreshed load, the page first exposed Step 1 with server-populated `Default Portfolio`,
  `Rental Command`, and time-zone fields while existing-record detection was still pending. No save
  was submitted.
- After the remaining reads settled, the same route resolved to `You're all set!` and offered Import
  more records, Add security deposits, Configure my alerts, and Go to my dashboard.
- This shared run did not press the destructive live-data choice. It also did not create another clean
  workspace merely to duplicate Scenario 08's scan/single/multiple-rental coverage.

### E — Team creation, scope, and activation

- `/admin/users` explains that Owners and Tenants are relationship-managed experiences, not Team
  jobs.
- The Add-team-member dialog visibly offers exactly Workspace Administrator, Property Manager,
  Leasing Agent, and Maintenance Technician.
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
- In the earlier activation checkpoint, using only this designated QA invitation from its queued
  outbox payload without logging the token:
  - mismatched passwords rendered `Passwords do not match.`;
  - matching valid passwords rendered `Your account is ready`;
  - the activation updated the same invited identity rather than creating another user.
- Reused and expired token behavior was not executed before the checkpoint.
- On the refreshed SHA the Team row now explicitly renders `Activation required — this person cannot
  sign in until the queued email is delivered and its link is used.` This proves the deployed web
  projection/copy fix; it also means no current Property Manager password session was available.
- As Workspace Administrator, direct navigation to `/leasing`, `/my-work`, and `/owner` produced a
  generic Error 403 page with `This page is not available for your current workspace access.` Direct
  `/portal` returned the authorized Management landing, without rendering Tenant navigation or data.

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

### BUG-2 — Resolved for web Google-button configuration; other providers not proven

- A fresh `/login` shows the Google button, proving the current web process received the public client
  configuration. The stale pre-refresh browser document did not.
- Google Places and assistant provider behavior were not exercised in this scenario. Outbound
  delivery remains deliberately suppressed.
- Remaining OAuth registration/success proof is tracked separately as BUG-5.

### BUG-3 — Resolved in refreshed source — login transport failure was invisible

- The login form now has explicit transport-error handling and always resets submission state.
- Unknown and wrong credentials were visibly verified; the destructive transport-outage induction
  was not repeated against the shared stack.

### BUG-4 — Resolved for this run — no usable role fixture after the clean reset

- A selected-property Property Manager identity was created and activated through the product's
  canonical membership/invitation flow.
- Owner, Tenant, Leasing, and Maintenance live fixtures still need deliberate creation for the final
  persona walkthrough.

### BUG-5 — High / external configuration retest required — Google callback registration

- **Route:** `/auth/google`
- **Expected:** Google accepts the stable callback and returns to Rental Command.
- **Earlier actual:** Google displayed `redirect_uri_mismatch`; this refreshed checkpoint did not
  repeat the external redirect.
- **Required configuration:** add
  `https://redacted-host.example.invalid/auth/google/callback` to the authorized redirect URIs of
  the configured Google OAuth web client.
- **Impact:** Google sign-in cannot be completed even though the button and application config exist.

### BUG-6 — Resolved in refreshed preview — activation status is distinct from membership status

- **Route:** `/admin/users` and the mobile Team screen.
- **Expected:** membership status and account activation status are distinct. An administrator can
  see that the assigned member cannot sign in until the queued link is delivered and used.
- **Earlier actual:** the member row showed only `Active`, even though the account could not sign in.
- **Narrow deployed fix:**
  - add `RequiresAccountActivation` to `TeamMemberSummaryDto`;
  - project it from `ApplicationUser.PasswordHash` inside the existing paged SQL query;
  - display explicit activation-required copy on both web and mobile;
  - preserve the secure design: only the invitation hash is durable and no token/readback API is
    added.
- **Refreshed verification:** `/admin/users` now shows the explicit activation-required sentence for
  the pending QA member while retaining its separate `Active` membership control.

### BUG-7 — Resolved in protected preview — setup waits for resume detection

- **Route:** `/onboarding`
- **Expected:** existing-record detection finishes before an editable wizard step is shown; a
  populated workspace opens the completed/checklist state.
- **Actual:** the page initializes `stepIndex = 0` and renders Step 1 while its five detection queries
  are pending. It later resolves correctly to `You're all set!`, but the intermediate form looks
  authoritative and allows a user to resubmit existing setup.
- **Root cause:** the resolver already waits for `detectionReady`, but the template did not. It
  rendered the wizard whenever `finished` was still false, including the entire query-loading window.
- **Narrow source fix:** render `Checking your existing setup…` until both detection and initial
  positioning complete; show a retryable, non-destructive error state if any detection query fails;
  render the editable wizard only afterward.
- **Focused contract:** `onboarding-flow-state.test.ts` asserts the loading/error gate remains ahead
  of editable setup.
- **Live proof:** a cold `/onboarding` load first showed `Checking your existing setup…`, did not
  expose editable setup fields, and then resolved to `You're all set!` after the existing records
  completed loading.

## Not yet proven end to end

- Google success/cancel/linking and multi-context selection.
- Password-reset invalid/expired/reused token handling.
- Destructive sample-to-live transition and an empty direct-live workspace.
- Full scan-first and manual one/multiple-rental Guided Setup journeys (Scenario 08 ownership).
- Reused/expired Team invitation behavior.
- Live Property Manager capability/record-scope denials after activation; the current fixture visibly
  requires activation.
- Live Leasing, Maintenance, Owner, and Tenant shells, account menus, and server/API ID probes.
- Installed-mobile login, bottom navigation, role shells, and unauthorized deep links.

## Source files and artifacts

This refreshed verification added the bounded Guided Setup loading/error gate and its focused source
contract. The already-deployed BUG-6 fix plus this BUG-7 follow-up are backed by:

- `RentalCommand.Api/Controllers/TeamController.cs`
- `RentalCommand.Api/DTOs/TeamAuthorityDtos.cs`
- `web/src/lib/api/endpoints/team.ts`
- `web/src/routes/(admin)/admin/users/+page.svelte`
- `mobile/lib/features/team/team_repository.dart`
- `mobile/lib/features/team/team_screen.dart`
- `web/src/routes/(protected)/onboarding/+page.svelte`
- `web/src/lib/onboarding/onboarding-flow-state.test.ts`
- `Docs/Testing/Results/2026-07-15-001/07-foundation-auth-onboarding-roles.md`

No temporary screenshots, videos, traces, tokens, or copied outbox payloads were retained.
