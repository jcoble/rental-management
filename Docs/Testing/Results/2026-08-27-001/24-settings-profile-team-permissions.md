# Exploratory Test Report: Settings, profile, portfolio, users, roles, permissions, integrations, and billing boundaries
Date: 2026-08-27  
Tester: e2e-s24  
Duration: About 50 minutes

## Scenario
Validate administrative settings, personal profile and security, portfolio configuration, team invitations and scopes, role/capability boundaries, notification and provider integrations, and exposed billing surfaces.

## Summary
I exercised the administrator Settings, Notifications, Security, Team, audit, accounting, AI, banking, and Plaid boundaries, then activated an additive QA Leasing Agent and verified its scoped navigation and rental data. One confirmed Medium bug blocks administrators from following the My Alerts link that promises a way to add a mobile number. Team invite validation, assignment persistence, suspend/reactivate transitions, self-protection, role gates, and safe provider-unavailable states behaved as expected; no unexpected console errors occurred in the normal flows.

## Bugs Found

### BUG-1: Route the My Alerts mobile-number setup to an editable profile
**Severity:** Medium  
**Location:** `/settings/notifications` → My alerts; target `/profile`  
**Expected:** When an administrator has no phone number, the visible “Add a mobile number in Profile” action should open a reachable personal profile/contact form where the number can be entered and saved. This is also required by the scenario’s personal-profile and notification-preference flow.  
**Actual:** The administrator saw `Mobile number: No mobile number on this account` and one link with `href="/profile"`. Clicking it navigated to `https://localhost:5667/`, titled `Dashboard - Rental Command`, instead of Profile. Direct navigation to `/profile` produced the same Dashboard redirect. The Profile page itself only renders display name/email and a Security link; it has no phone editor.  
**Evidence:** Browser run at `1710x990`: `BEFORE_URL https://localhost:5667/settings/notifications`, `PROFILE_LINK_COUNT 1`, `AFTER_URL https://localhost:5667/`, `AFTER_TITLE Dashboard - Rental Command`, `CONSOLE_ERRORS []`. Screenshots: [My Alerts before click](/home/blackcolours/Workbox/screenshots/e2e-s24-my-alerts-profile-link.png) and [redirect result](/home/blackcolours/Workbox/screenshots/e2e-s24-profile-link-result.png).  
**Code Reference:** `web/src/lib/components/notifications/MyAlertsSection.svelte:114-116` renders the `/profile` CTA; `web/src/lib/auth/experience-policy.ts:130` permits `/profile` only for Leasing and Maintenance experiences; `web/src/routes/(protected)/profile/+page.svelte:9-16` renders a read-only identity card and security link.  
**Suggested Fix:** Make `/profile` an authenticated, editable staff profile route for Management as well, including a mobile-number field and save path, so this existing My Alerts link reaches the promised action.  
**Why This Matters:** A landlord who wants text alerts has no path to supply a phone number and is silently sent to an unrelated dashboard, causing confusion and preventing an important alert destination from being configured.

## Potential Issues (need investigation)

- The account-versus-portfolio boundary for personal settings needs a product decision beyond BUG-1. The current Profile route is limited to Leasing/Maintenance and is display-only, while timezone appears in portfolio Settings; personal timezone, locale, and contact editing were not available in the administrator’s Profile path.
- Provider error and callback branches were not exercised because the run explicitly prohibited connecting a bank, entering AI credentials, enabling external messaging, or purchasing billing. QuickBooks and AI showed safe not-connected states, and Plaid auth showed an expired-session state; configured-provider fixtures would be needed to test retry and callback failures.
- The requested 390px layout and keyboard-only checks were not run because this run was constrained to the required 1710×990 desktop viewport.

## Observations

- General Settings clearly identified the sandbox/example-data context and exposed Portfolio, Notifications, Automations, Messaging, Team, Owners, Setup & import, and Activity history sections. Portfolio settings showed the expected name, timezone, currency, rent-collection, proration, and messaging configuration without changing shared seed values.
- Notifications loaded personal alert choices and the Team routing section successfully. Toggling the QA user’s email alert preference saved and survived reload, then was restored. Team routing loaded saved responsibility summaries and the daily-summary controls with successful API responses and no console errors.
- The Add member dialog offered exactly four role profiles—Workspace Administrator, Property Manager, Leasing Agent, and Maintenance Technician—with descriptions. It kept Add disabled until role and property scope were present, rejected a duplicate active invite with the expected `400` and message, and kept the administrator’s own status and assignment actions disabled.
- Additive QA data used the marker `QA-20260827-s24-INVITE-01`. The invitation was created, its selected-property assignment was expanded from Clintonville Townhome to Clintonville Townhome plus Dublin Single Family, and the member was suspended and reactivated. Read-only SQL later confirmed user `2`, active Leasing membership/context/assignment, access revision `4`, and the two selected properties.
- After activation, the QA Leasing Agent landed at `/leasing`, saw only leasing-oriented navigation, and could see the two assigned rentals but not Maple Ridge or other out-of-scope properties. Direct attempts to open Management settings, Team, banking, AI/accounting, and audit routes returned to the Leasing landing page. This matched the role-route policy.
- `/admin/audit` redirected to `/audit` for the seeded administrator, consistent with the separate platform-admin gate. The normal audit page remained available; no authorization bypass was observed.
- No landlord billing/subscription route was exposed by the current web route tree. `rg --files web/src/routes | rg -i 'billing|subscription|stripe|plan'` returned no route, while `billing.manage` remains a capability in `web/src/lib/auth/experience-policy.ts:30,181-184` and `StripeWebhookController.cs:10-16` is only a Stripe webhook boundary. No billing purchase or provider connection was attempted.

## What Was Tested

- Logged in as the seeded administrator through the real login UI and verified the required `1710x990` viewport.
- Read and navigated General Settings, Notifications/My alerts, Team routing, Security, Accounting/QuickBooks, AI integration, Banking, Plaid authorization, Profile, Team, and audit routes.
- Tested personal notification preference save/reload and restored the QA value; tested weak/mismatched password client blocking and an incorrect-current-password response (`400`) without changing the password.
- Created and activated one additive QA invitation, selected the Leasing Agent role and properties, verified role descriptions and no-property blocking, tested duplicate invite handling, replaced property scope, suspended/reactivated the member, and verified the resulting Leasing shell and property scope.
- Tested administrator self-protection, direct forbidden URLs, no-credential provider states, and the broken My Alerts → Profile link. Captured the cited screenshots and monitored console and HTTP responses; the non-success responses were from deliberately exercised duplicate/password validation and protected-route probes.
- Every browser script used the isolated persistent e2e-s24 profile, headless Chromium, and closed its context. A final process check found no Chrome process using the e2e-s24 profile.

Browser cleanup: stopped e2e-s24
