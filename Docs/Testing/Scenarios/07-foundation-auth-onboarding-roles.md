# Scenario 07 — Foundation Authentication, Onboarding & Roles

**Run:** `2026-07-15-001`  
**Domain:** Authentication, first-run setup, sample data, guided setup, Team and role/account entry  
**Suggested tester session:** `foundation-auth`

## Mission

Prove that a new landlord can create or enter an account, make the first-run sample/live choice,
finish setup, and return later without getting trapped or losing work. Then prove that a management
company can add and scope its real job presets while owners and tenants remain relationship-scoped
identities rather than fake Team roles.

This is a real UI walkthrough through visible controls. Do not bypass authentication with an API
cookie and do not call a flow passed merely because its route exists. Every mutation must be reopened
from its canonical UI. Do not create screenshots, recordings, or traces; record observable text,
URLs, request/status behavior, console errors, persisted identifiers, and relevant code references.

## Foundation invariants under test

- The browser-facing public origin is exactly `https://rental-command.chimp-map.ts.net`. Enhanced
  form actions, secure cookies, Google OAuth redirects, and absolute links use that public HTTPS
  origin, never the VPS's private host/port.
- Password and Google login use the same canonical identity/access-context result. Google is shown
  only when both server and web runtime configuration are complete.
- Unknown accounts and wrong passwords render the same accessible `Invalid email or password`
  message. A runtime/origin/API transport failure renders a different actionable error; it never
  looks like a successful submit or silently stops its spinner.
- A new Management workspace makes exactly one explicit first-run choice: explore sample data or
  set up real rentals. The choice is resumable and cannot be bypassed through a protected deep link.
- Sample records are visibly labeled and isolated. Leaving sample mode permanently removes only
  sandbox-owned operational data and cannot dispatch real money or tenant delivery.
- Guided setup resumes from durable server state. One-rental setup creates one Property plus its one
  explicit Unit atomically; multi-rental setup uses the same model with multiple Units.
- Team job presets are Workspace Administrator, Property Manager, Leasing Agent, and Maintenance
  Technician. A person can have multiple separately scoped assignments. Owner and Tenant access is
  granted from relationship records and never appears as a Team job.
- Workspace Administrator controls workspace billing/security/integrations. Property Manager can
  run assigned-property money, expenses, deposits, reports, and operations but not workspace-level
  administration. Leasing and Maintenance receive narrower purpose-built experiences.
- All identity, assignment, property-scope, and onboarding queries filter/join/page DB-side. Lists
  must not materialize a workspace then filter it in memory or issue per-row follow-up queries.

## Implementation map to read before browser work

- Login and registration:
  - `web/src/routes/login/`, `web/src/routes/register/`, `web/src/routes/auth/google/`
  - `web/src/lib/server/auth-cookies.ts` and `web/src/lib/auth/experience-policy.ts`
  - `AuthController`, `GoogleAuthService`, registration/login services, and access-context resolver
- First-run choice and sample data:
  - `web/src/routes/choose-setup/`, `web/src/routes/setting-up/`, and
    `web/src/routes/(protected)/get-started/`
  - protected layout onboarding gate and `web/src/lib/api/endpoints/portfolios.ts`
  - `SandboxController`, `SandboxService`, `IdentitySeeder`, and `DemoDataSeeder`
- Guided setup:
  - `web/src/routes/(protected)/onboarding/+page.svelte`
  - owner, Property/Unit setup, scan/import, tenant, Agreement, and notification clients/controllers
- Team and relationship access:
  - `web/src/routes/(admin)/admin/users/` and `web/src/routes/activate-team/`
  - team users/assignments API and role-profile/capability services
  - Owner access and LeaseManagement party/tenant-access commands
- Account entry and role shells:
  - `web/src/lib/components/AppShell.svelte`, protected/portal layouts, Settings, Profile, Security,
    Owner routes, and Tenant portal routes
  - `mobile/lib/core/router/app_router.dart`, `mobile_access_policy.dart`, and role landing shells

## Preconditions and data hygiene

1. Record the exact preview source SHA and confirm the stable public URL is healthy.
2. Confirm the preview's persistent integration status without printing values. Google requires a
   client ID and secret, Google Places requires its API key, and the assistant requires provider,
   model, and key. Email/SMS/push dispatch remains suppressed for this run.
3. Confirm Google Cloud authorizes exactly
   `https://rental-command.chimp-map.ts.net/auth/google/callback` before invoking Google login.
4. Establish the actual database state. A clean reset may contain only the seeded administrator;
   do not assume an old “test customer” still exists.
5. Use marker `QA-AOR-<HHMMSS>` in new workspace, member, property, owner, tenant, and assignment
   names. Record every created ID. Never edit another tester's marked data.
6. Use the seeded administrator only where a clean-preview bootstrap requires it. Create separate
   identities for role checks rather than repeatedly mutating one person's primary assignment.

## Journey A — Login failures, recovery, and valid password login

1. Open `/login` in a clean browser context. Verify email/password, Forgot password, Create account,
   and Sign In are keyboard reachable and correctly labeled.
2. Submit an unknown email with a valid-looking password. Verify an accessible `role="alert"`
   renders exactly `Invalid email or password`, the page remains `/login`, the password is not echoed,
   and the button returns from `Signing in…` to `Sign In`.
3. Submit a real email with the wrong password. Verify the same message and timing. No response may
   reveal whether the account exists, is locked, or has a different provider.
4. Exercise an unavailable API/origin/form transport path in an isolated automated test. Verify a
   distinct actionable alert appears, focus remains usable, and the spinner resets. Do not break the
   shared live stack merely to induce this condition.
5. Sign in with valid seeded-admin credentials through the visible form. Verify the POST succeeds,
   secure first-party access/refresh cookies are set, `/login` is left, and the user reaches the
   first-run choice or its authorized landing page.
6. Refresh and deep-link to an authorized protected page. Then sign out and verify the same deep
   link returns to login without leaving usable authenticated data in browser state.
7. Exercise Forgot password with an unknown and a real test account. Both initial responses must be
   neutral. Use only a safe local/test delivery path for the actual reset token and verify expired,
   reused, and mismatched tokens fail clearly.

## Journey B — Google sign-in and access-context selection

1. Verify the Google button is visible when the preview reports complete Google configuration. If
   configuration is intentionally empty, the run must explicitly say Google is disabled; absence
   must not be misreported as a UI pass.
2. Start Google login and verify the authorization request uses the configured client ID and exact
   stable callback URL. Do not record tokens or secret query values.
3. Cancel once and verify the login page explains that Google is unavailable/cancelled without a
   crash or redirect loop. Then complete with a designated test Google account.
4. Verify a matching existing identity signs into the same user rather than creating a duplicate.
   Verify an allowed new identity follows the intended registration/linking flow.
5. For a person with more than one access context, verify a clear workspace selector is shown after
   credential validation; choose each context and confirm its workspace name, experience, scope, and
   landing page. Refresh and switch context using the visible account control.

## Journey C — First-run sample/live decision

Use separate marked workspaces or reset only a disposable marked workspace so both branches can be
tested without destroying another tester's data.

1. Sign into a new Management workspace and deep-link to Dashboard, Rentals, Money, and Settings.
   Each must redirect to `/choose-setup` until a choice is complete.
2. Verify the choice page plainly distinguishes **Explore with sample data** from **Set up my own
   rentals**, including what is created, whether messages/providers can still deliver, and whether
   the decision can later be changed.
3. Choose sample data. Verify `/setting-up` communicates real progress, survives refresh/retry, and
   lands only after the idempotent server-side seed completes.
4. Inspect Dashboard, Rentals, leases/account balances, Work, Messages, notices, and Settings. Sample
   records and the portfolio must be visibly labeled; realistic records must use the replacement
   LeaseManagement/TenantAccount/Agreement model rather than legacy rows.
5. Trigger the sample choice again with the same operation/retry. Confirm no duplicate properties,
   Units, people, Agreements, ledger entries, work orders, templates, or messages are created.
6. Confirm sandbox guards suppress Stripe/payment/provider delivery even when a machine has keys.
7. Choose **Use my own rentals / Go live**. Read and confirm the destructive warning. Verify only
   sandbox-owned operational records are deleted atomically, the identity/workspace survives, and
   the workspace can never silently return to sample mode.
8. In a second marked workspace choose live setup immediately. Verify no sample data is created and
   the user enters Guided Setup with an empty, understandable real workspace.

## Journey D — Guided setup, scan-first and manual paths

1. Verify Guided Setup groups work into understandable Setup, Property, People, and Notify sections;
   it must not dump every table into one form.
2. Complete portfolio/business and primary owner details. Refresh and navigate away/back after each
   save; completed state must come from the server, not only local storage.
3. Exercise the preferred scan/import path from Guided Setup with an available provider. Upload a
   representative lease, review extracted fields and relationship matches, correct at least one
   value, and confirm. Provider/key absence must be an explicit blocked state with a manual route.
4. Exercise manual **one rental** setup. One submit must atomically create the Property and exactly
   one explicit Unit; validation/conflict/double-submit must not leave an orphan Property or zeroed
   Unit. The UI may hide meaningless unit hierarchy but the canonical Unit remains navigable.
5. Exercise manual **multiple rentals** setup and add at least two Units using the same model and
   commands. Confirm the presentation flag does not fork leases, calculations, or APIs.
6. Add tenant/household and initial Agreement data or follow the scan-created records. Verify the
   next guided step recognizes existing canonical records without duplicate creation.
7. Configure the initial notification choice/template. Supplied editable templates must already
   contain useful default content; do not require writing every notice from a blank page.
8. Reload mid-step, use browser Back/Forward, and open Guided Setup in a second tab. Confirm one
   coherent durable position, no stale local-only completion, and no duplicate submissions.
9. Finish and verify the normal management landing page no longer forces onboarding. Guided Setup
   remains available as a checklist/reference rather than a destructive rerun.

## Journey E — Team presets, activation, multiple assignments, and scope

1. As Workspace Administrator open Team. Verify the page explains the four real job presets and
   explicitly says Owner/Tenant access comes from relationship records.
2. Add one Property Manager scoped to exactly one marked Property. Verify the activation email is
   queued or, with delivery suppressed, a clear test activation path/status is available.
3. Open the activation link in a signed-out context. Exercise invalid, expired, reused, password
   mismatch, and valid activation. Valid activation must establish the same identity created by the
   invitation and navigate to its authorized landing experience.
4. Verify the Property Manager can manage rent receipts, charges, expenses, deposits, operational
   reports/reconciliation, work, leasing, and communications only for assigned properties. Verify
   workspace billing, integrations, bank connections/payouts, Team, and security administration are
   denied even through direct URLs/API IDs.
5. Add a Leasing Agent with selected-property scope. Verify its purpose-built leasing experience and
   intended applicant/tenant/Agreement actions; management money and Team administration stay hidden
   and server-denied.
6. Add a Maintenance Technician. The only valid preset scope is assigned work orders. Verify My Work,
   permitted work updates/messages/time/materials, and denial of broad Property/tenant/lease/money
   lists and unassigned work.
7. Give one person a second separately scoped assignment. Switch or resolve effective contexts and
   confirm each assignment retains its own scope; capability union must not accidentally broaden a
   selected-property or assigned-work context.
8. Replace a selected-property scope and confirm it is a complete atomic replacement: removed
   properties disappear and new properties appear together. Retry must not duplicate assignments.
9. Suspend and reactivate a member. Active sessions must lose access at the intended access-revision
   boundary; reactivation restores only current assignments, not stale capabilities.
10. Search/page the Team list and property picker. Confirm filtering, sorting, joins, counts, and
    paging are server-side and another workspace's people/properties never appear.

## Journey F — Sole landlord, Owner, Tenant, account and security entry

1. In a sole-landlord workspace verify one human can be the Workspace Administrator identity and
   also be the business/property Owner relationship. The UI must not force choosing one identity or
   duplicate the account; Owner remains a property relationship, not a Team role.
2. Grant Owner portal access from the marked Owner relationship and Tenant portal access from the
   marked LeaseManagement party. Activate/sign in each identity through the intended flow.
3. Verify the Owner lands in the Owner experience and the Tenant in the Tenant portal; neither sees
   management navigation, even if a stale client cache contains old capability data.
4. From every persona—Administrator, Property Manager, Leasing, Maintenance, Owner, Tenant—open the
   account menu, Profile/My account, My alerts, Security/password, Sign out, and any workspace switch
   control that should be available. Labels and available destinations must match the active persona.
5. Paste representative unauthorized URLs for each persona. Verify safe 403/authorized landing,
   not a partially rendered page, leaked navigation title, or another workspace's data.
6. Repeat the landing shell, discoverability, and direct-route denial matrix in mobile. Mobile may
   arrange actions differently but must expose the same authorized outcomes and no broader access.

## Cross-cutting release checks

- Run password success, unknown account, wrong password, and transport-failure regression tests.
- Verify Google enabled/disabled status during preview start without logging any secret value.
- Inspect failed requests and browser console output throughout. A spinner that stops without an
  alert is always a bug.
- Refresh/deep-link every important landing/setup/team route. Hidden modal/tab state is not a valid
  source of truth.
- Retry each safe first-run/assignment/setup command with the same operation key. Confirm the stored
  receipt returns the same result and no duplicate relationship is created.
- Probe one cross-workspace user, Property, and assignment ID using authorized fixtures. UI pickers
  and server mutations must both reject them.
- Inspect generated SQL for Team/property pickers and onboarding summaries. No in-memory filtering,
  grouping, joins, sorting, paging, or N+1 queries are acceptable.

## Report

Write the result to:

`Docs/Testing/Results/2026-07-15-001/07-foundation-auth-onboarding-roles.md`

For every section record **Pass**, **Fail**, or **Blocked**, exact visible actions, URLs, request/status
evidence, created IDs, console errors, and code references. Use `BUG-N` from
`Docs/Testing/exploratory-tester.md`. Separate a proven product/configuration bug from a flow that was
not executed because the shared preview had no matching identity/provider state.
