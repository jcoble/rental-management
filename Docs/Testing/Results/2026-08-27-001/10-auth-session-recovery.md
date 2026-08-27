# Exploratory Test Report: Authentication, sessions, recovery, and invitations
Date: 2026-08-27
Tester: e2e-s10
Duration: about 55 minutes

## Scenario
Exercise landlord, team-member, registration, verification, recovery, Google, session, logout, and invitation entry and exit paths.

## Summary
The normal password, email verification, reset, Google handoff, logout, protected-route guard, invitation activation, and single-refresh retry paths worked. Four confirmed issues remain: authentication errors disclose known-account state, a failed forgot-password request strands the user on a generic 500 page, registration does not explain the server’s uppercase/lowercase/digit policy, and invitation activation does not show the assigned role or portfolio scope. No seeded account or seeded portfolio data was changed; the QA team member used for the disabled-account check was reactivated.

## Bugs Found

### BUG-1: Use one non-enumerating message for locked and disabled accounts
**Severity:** Medium  
**Location:** `/login` password sign-in

**Expected:** Unknown email, wrong password, locked account, and an account with no active workspace access should use the same public authentication failure copy. The scenario explicitly requires that login copy not reveal whether an account exists or what state it is in.

**Actual:** Unknown email and wrong password showed `Invalid email or password`. After the fifth failed password attempt on the QA account, login showed `Account is locked. Try again later.`. After an administrator suspended the QA team member, login showed `This account has no active workspace access.`. The QA member was then reactivated.

**Evidence:** UI-only login attempts in the headless browser; the lockout occurred on the fifth total failed attempt, matching the configured five-attempt threshold. The disabled-account attempt stayed on `/login` with the account-specific message. The final Team UI row showed the QA member reactivated as `Active`.

**Code Reference:** `RentalCommand.Api/Services/Auth/AuthService.cs:169-185` returns a distinct locked-account message; `RentalCommand.Api/Services/Auth/AuthService.cs:219-225` returns a distinct no-active-workspace message; the threshold is configured at `RentalCommand.Api/Program.cs:224-226`.

**Suggested Fix:** Normalize both lockout and no-active-workspace failures to `Invalid email or password` at the login boundary while retaining the detailed state only in internal logs or security telemetry.

**Why This Matters:** An attacker can use the differences to enumerate registered users and identify locked or disabled accounts, while a landlord receives inconsistent security messaging.

### BUG-2: Keep forgot-password recovery usable when the form request fails
**Severity:** Medium  
**Location:** `/forgot-password`

**Expected:** When the password-recovery request cannot reach the server, the user should remain on the recovery form, see an actionable retry message, and be able to submit again. The server action already defines this intended message as `Unable to connect to Rental Command. Please try again.`.

**Actual:** After the fully loaded page submitted a deliberately aborted normal form request, the browser showed the global page `ERROR 500`, `Something went wrong`, `Failed to fetch`, and `Back to dashboard`. The forgot-password form and its `Send reset link` action were gone.

**Evidence:** Browser transport-fault reproduction: `POST https://localhost:5667/forgot-password net::ERR_FAILED`; page remained at `https://localhost:5667/forgot-password` but rendered the generic 500 page. Screenshot: `/home/blackcolours/Workbox/screenshots/e2e-s10-forgot-transport-failure.png`.

**Code Reference:** `web/src/routes/forgot-password/+page.svelte:52-61` uses `enhance` but only awaits `update()` and does not handle an enhanced `error` result or guarantee the submitting state is cleared; the intended server-side retry response is in `web/src/routes/forgot-password/+page.server.ts:39-59`.

**Suggested Fix:** Add an explicit enhanced-form error branch with `try/finally` that keeps the page on the form, clears `submitting`, and renders the server/network retry message instead of allowing the global error page.

**Why This Matters:** A non-technical landlord who loses connectivity while requesting a reset is sent away from the recovery task and may believe the account or reset process is broken.

### BUG-3: Explain all password requirements during registration
**Severity:** Medium  
**Location:** `/register`

**Expected:** Registration should either prevent a password that fails the actual policy or explain the complete policy inline. The current Identity policy requires at least eight characters plus one digit, one lowercase letter, and one uppercase letter.

**Actual:** With matching `abcdefgh` and the terms checkbox selected, the form showed no live warning because the length was eight. Submission then returned only `Registration failed`; no uppercase/digit guidance or field detail was shown. A read-only DB query found no account for the QA validation email, so the failed state did not create a partial account.

**Evidence:** UI reproduction on `/register`; the page retained the entered values and displayed `Registration failed`. Screenshot: `/home/blackcolours/Workbox/screenshots/e2e-s10-register-weak-password.png`. The test email `qa-20260827-s10-validation@example.local` returned no row from `AspNetUsers` in a SELECT-only check.

**Code Reference:** `web/src/routes/register/+page.svelte:27-30` and `web/src/routes/register/+page.svelte:147-165` provide only a minimum-length live check; `RentalCommand.Api/Program.cs:219-223` enforces digit, lowercase, uppercase, and length requirements; `web/src/routes/register/+page.server.ts:94-104` only renders API details when they are a string.

**Suggested Fix:** Add a live registration password checklist that mirrors the server’s digit/lowercase/uppercase/length policy and blocks or clearly explains a failing submission before the user receives the generic error.

**Why This Matters:** A landlord can enter a password that visibly appears valid, receive no useful correction, and be unable to create an account without knowing what “Registration failed” means.

### BUG-4: Show invitation role and portfolio scope before activation
**Severity:** Medium  
**Location:** `/activate-team?token=...`

**Expected:** Before setting a password, an invited team member should see the workspace/portfolio and the exact job role and access scope they are accepting. The admin invitation flow selected `Property Manager` with `All properties` and persisted those values.

**Actual:** The valid activation page showed only `Choose your password. Your workspace and job access are already prepared.`. It displayed neither `Property Manager` nor `All properties`, and no workspace or portfolio name. The invitation email included the role but not the scope; the activation page had no role/scope summary.

**Evidence:** Valid QA invitation UI snapshot and screenshot: `/home/blackcolours/Workbox/screenshots/e2e-s10-invite-activation-before.png`; the page text checks returned `HAS_PROPERTY_MANAGER_TEXT: false` and `HAS_ALL_PROPERTIES_TEXT: false`. The SELECT-only DB proof for the activated QA invitation was `Property Manager` / `AllProperties` on portfolio `1`.

**Code Reference:** `web/src/routes/activate-team/+page.svelte:31-43` renders only generic success/instructions and no invitation metadata; `web/src/routes/activate-team/+page.server.ts:5-7` loads only the raw token state. The admin-side role and scope controls are present at `web/src/routes/(admin)/admin/users/+page.svelte:416-444`.

**Suggested Fix:** Add a server-side invitation preview that resolves the token without exposing it and render the invitation’s workspace, role, and scope above the activation form.

**Why This Matters:** A team member may accept broad property access without knowing what they are being granted, creating avoidable authorization and trust risk for the landlord.

## Potential Issues (need investigation)

- A consumed or tampered invitation token renders the activation password form on the initial GET because `activate-team/+page.server.ts:5-7` checks only whether a token is present. Submitting the form is safely rejected with `This activation link is invalid, expired, or has already been used.`; decide whether the initial page should prevalidate and show an invalid-link state. No expired invitation row was available in the local DB, so an actual expired-token link was not directly opened.
- Reopening the already-used email-verification link still reports `Email verified`. This appears intentionally idempotent and caused no unsafe state change, but product/security owners should confirm that replay success wording is desired.
- Google access denial and tampered callback both return `google_unavailable`, which is safe and routes back to login but is not precise copy for a user who canceled Google sign-in.

## Observations

- Login labels and error association were readable at the required 1710×990 desktop viewport. The Google button was shown only because Google was configured locally.
- The Google redirect used `https://localhost:5667/auth/google/callback` and included state. Access-denied and tampered callbacks left no Rental Command auth cookies.
- Registration successfully queued verification for `qa-20260827-s10-628228@example.local`; pre-verification login showed a clear resend action, verification succeeded, and the second verification-link use was idempotent.
- Forgot-password requests for both known and unknown emails showed the same neutral `If an account exists for that email...` message. A valid reset link updated the password, rejected the old password, and a second reset submission was rejected.
- Direct `/logout` and the shell account-menu `Sign Out` both redirected to `/login` and cleared `rc_access_token`, `rc_access_token_expiration`, and `rc_refresh_token`. Browser Back and a protected deep link did not reveal protected content.
- A simulated 401 on the Team list produced exactly two Team requests and one refresh request, then rendered the list successfully. No refresh loop was observed.
- Anonymous `/portal` and `/portal/security` redirected to login with a safe `redirectTo` value.
- Normal flows had no console errors. The deliberate transport and 401 fault injections produced only the expected browser/network error entries.

## What Was Tested

- Read the current auth source chain, Identity configuration, current master spec/phase-0 intent, and prior auth/team UI reports before browser work.
- Opened `/login`, `/register`, `/terms`, `/privacy`, `/forgot-password`, `/reset-password`, `/verify-email`, and `/activate-team` anonymously, including missing-token states.
- Registered a disposable `QA-20260827-s10` account, exercised terms/password validation, queued/resubmitted verification, verified the email, retried the verification link, and signed in to the current `/choose-setup` boundary without selecting sample or real-rental setup.
- Compared unknown-account and wrong-password failures, induced the five-attempt lockout on the QA account, suspended that QA team member through the Team UI, checked the disabled-account login response, and reactivated the member.
- Requested reset links for known and unknown emails, extracted the disposable reset link from `OutboxMessages` using SELECT-only SQL, reset the password, checked old/new credentials, and submitted the used link again.
- Simulated a normal browser transport failure on login and forgot-password forms; verified the login form recovered and documented the forgot-password 500-page failure.
- Followed the configured Google handoff, checked callback origin/state, and exercised access-denied and tampered callback returns.
- Created a disposable Property Manager invitation with `All properties`, extracted its activation link from the local outbox, activated it, signed in as that user, checked the persisted role/scope, replayed the used link, and submitted a tampered token.
- Injected one handled 401 into the normal Team list request and counted the resulting single refresh and single retry.
- Browser cleanup: stopped e2e-s10
