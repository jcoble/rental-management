# Exploratory Test Report: Authentication, Sessions, Recovery, and Invitations

Date: 2026-08-22
Duration: ~30 minutes
Environment: Local dev stack (web https://localhost:5667, API https://localhost:5666, fresh seeded DB rentalcommand_qa20260822)

## Scenario

Test scenario 10 covering login, logout, registration, email verification, forgot/reset password, team invitation activation, Google OAuth, session guards, and mobile responsiveness. Regression pass after 138 write operations were migrated from a legacy atomic-command kernel to TransactionalWrite / IRequestWriteExecutor.

## Summary

The core authentication flows (login, logout, registration, forgot-password, reset-password, email verification, team activation) all function correctly through the UI. Session guards work: protected routes redirect unauthenticated visitors to /login with a redirectTo parameter, back-button after logout redirects properly, and visiting /login while authenticated bounces to the dashboard. Three bugs were found: one critical (Engine process completely dead due to missing DI registration), one medium (account enumeration via registration), and one low (register page accessible while logged in). Mobile layout checks passed on all auth pages.

## Bugs Found

### BUG-1: Engine process fails to start — IRequestWriteExecutor not registered in Engine DI container

**Severity:** Critical
**Location:** RentalCommand.Engine startup (Program.cs:255)
**Expected:** The Engine should start successfully and process background jobs (email delivery, notifications, e-sign, LLM credential management).
**Actual:** The Engine crashes immediately on startup with an `AggregateException` containing five `InvalidOperationException` errors, all reporting `Unable to resolve service for type 'RentalCommand.Api.Writes.IRequestWriteExecutor'`. Affected services: WorkspaceLlmCredentialService, NativeEsignExecutionService, ConversationService, NotificationFoundationService, NoticeDraftGenerationService.
**Evidence:** `/tmp/rentalcommand-engine.log` line 1: `Unhandled exception. System.AggregateException: Some services are not able to be constructed (Error while validating the service descriptor 'ServiceType: RentalCommand.Api.Services.Domain.IWorkspaceLlmCredentialService ... Unable to resolve service for type 'RentalCommand.Api.Writes.IRequestWriteExecutor' ...)` (verified)
**Code Reference:** `RentalCommand.Engine/Program.cs:255` — the Engine's DI container does not register `IRequestWriteExecutor`, which was introduced by the TransactionalWrite migration that touched all 138 write operations.
**Why This Matters:** The Engine is the background processor. Without it: verification emails are enqueued but never delivered (users cannot complete registration), password-reset emails are never sent, notifications do not fire, e-sign workflows are broken, and LLM credential operations fail. This is a complete regression of all Engine-dependent functionality.
**Suggested Fix:** Register `IRequestWriteExecutor` (and its implementation `RequestWriteExecutor` or equivalent) in the Engine's service collection, mirroring how the API project registers it. The Engine shares service types from `RentalCommand.Api` but has its own composition root.

### BUG-2: Registration reveals whether an email is already registered (account enumeration)

**Severity:** Medium
**Location:** /register page
**Expected:** Registration for an already-used email should show a neutral message that does not reveal whether the account exists (consistent with how /forgot-password handles this: "If an account exists for that email, we've sent a password reset link.").
**Actual:** Submitting the registration form with an email that is already registered returns the error: "Email is already registered". This explicitly confirms the email has an account.
**Evidence:** Registered with email `admin@rentalcommand.local` (the seeded admin), received alert text "Email is already registered" (verified via snapshot). The /forgot-password endpoint correctly returns a neutral "If an account exists..." message, but /register does not follow the same pattern.
**Code Reference:** `RentalCommand.Api/Controllers/AuthController.cs:128-166` (Register action) returns a 401 with the raw `result.Error` from `IAuthService.RegisterAsync`, which includes the enumeration-revealing message. The SvelteKit action at `web/src/routes/register/+page.server.ts:76-89` passes this error through to the UI unchanged.
**Why This Matters:** An attacker can probe the registration endpoint to build a list of valid email addresses. This undermines the anti-enumeration measures already in place on the login page (which shows "Invalid email or password" for both wrong-password and unknown-account) and the forgot-password page.
**Suggested Fix:** Have the API's RegisterAsync return a neutral success response even when the email is taken (e.g., "Registration successful. Please check your email to verify your account." — identical to the real success message). Alternatively, the SvelteKit action can map known duplicate-email errors to the same "check your email" success state, so the user sees no difference between a new registration and a duplicate.

### BUG-3: Register page accessible and fully functional while already logged in

**Severity:** Low
**Location:** /register page
**Expected:** Visiting /register while already authenticated should redirect to the dashboard (like /login does).
**Actual:** The /register page renders fully while logged in. The user can see the registration form and potentially submit it, creating a second account from within an active session.
**Evidence:** Logged in as admin@rentalcommand.local, navigated to https://localhost:5667/register, page rendered with heading "Create your account" and all form fields visible and interactive (verified via snapshot). By contrast, navigating to /login while logged in correctly redirects to / (dashboard).
**Code Reference:** `web/src/routes/register/+page.server.ts:16-21` — the load function does `void locals; return { googleEnabled: ... }` with a comment "Already logged in — nothing to expose; layout redirect handles the guard." However, /register is NOT inside the (protected) layout group, so no guard runs. The login page at `web/src/routes/login/+page.server.ts:44-48` has an explicit `if (locals.user) throw redirect(...)` that register lacks.
**Why This Matters:** Minor UX issue — a logged-in user should not see a registration form. It could also lead to confusion or accidental duplicate account creation.
**Suggested Fix:** Add the same redirect guard to the register load function: `if (locals.user) throw redirect(303, '/');`

## Potential Issues (Need Investigation)

### PI-1: Onboarding gate overrides redirectTo parameter

When a user who has not completed onboarding logs in with a redirectTo parameter (e.g., /properties), they land on /choose-setup instead of their intended destination. After onboarding completes, the original redirectTo is lost. This is by design (the code explicitly gates onboarding-pending users), but it means bookmarked deep links are silently dropped for first-time users. The original redirectTo could be preserved through the onboarding flow and honored once it completes.

### PI-2: Emails are enqueued but never delivered (Engine is down)

Related to BUG-1. Registration succeeded and the API logged "Enqueued email-confirmation email for testuser@example.com" but the Engine that would deliver it is crashed. This means the user is stuck: they registered, got the "check your email" success screen, but no email will ever arrive. The resend-verification button works (the API endpoint returns 200) but the email is again only enqueued, not delivered. This is a consequence of BUG-1, not a separate bug, but worth flagging because it means the full registration-to-verified-login flow is currently broken end to end.

## Observations

- **Anti-enumeration is mostly consistent.** Login shows "Invalid email or password" for both wrong password and unknown account. Forgot-password shows a neutral success message regardless of whether the account exists. The only leak is the registration endpoint (BUG-2).

- **Password validation is thorough.** Both client-side (live hints for length and mismatch) and server-side (rejects <8 chars, mismatch). The register page shows a green checkmark when passwords match, and an amber hint for short passwords.

- **Session cookies are properly httpOnly.** `document.cookie` returns empty string while authenticated, confirming all auth cookies are httpOnly and not accessible to client JavaScript.

- **Google OAuth is configured and functional.** The Google Sign-In button appears on both login and register pages. Clicking it redirects to Google's consent screen with proper CSRF state parameter. The /auth/google endpoint correctly guards against unconfigured state (returns redirect to /login?error=google_unavailable when client ID is missing).

- **Mobile layout is solid.** All auth pages (login, register, forgot-password, reset-password, verify-email, activate-team) render without horizontal overflow at 390x844. The brand panel hides on mobile, replaced by a compact brand mark. Form elements are properly sized.

- **Error messages are clear and helpful.** "Invalid reset link" includes links to request a new one and sign in. "Invalid activation link" advises contacting the workspace administrator. "Verification failed" links back to sign in.

- **The "Forgot password?" link on the login page is well-placed** between the password label and the input field, making it easy to find.

- **Dev convenience features are properly gated.** The "Fill dev login (admin)" button only appears in development mode (gated by `import.meta.env.DEV`). The Auth:ExposeDevTokens flag that would reveal email confirmation tokens in API responses defaults to off.

- **Terms/Privacy checkbox placement is unusual.** The checkbox appears between "Your name" and "Email" rather than at the bottom near the submit button, which is where users typically expect it. Not a bug, but slightly unconventional.

## What Was Tested

1. **Login page rendering:** Brand panel, form elements, Google button, dev fill button, "Create one" link, "Forgot password?" link — all present and functional at 1710x990. (verified)
2. **Empty form submission:** Browser-level required validation blocks submission, email field gets focus. (verified)
3. **Wrong password:** Generic "Invalid email or password" error, email preserved. (verified)
4. **Non-existent account login:** Same generic error, no account enumeration. (verified)
5. **Mixed-case email login:** Login with "Admin@RentalCommand.LOCAL" succeeds, case-insensitive. (verified)
6. **Logout and back button:** /logout clears session and redirects to /login. Browser back after logout redirects to /login with redirectTo. (verified)
7. **Protected deep link while logged out:** /properties redirects to /login?redirectTo=%2Fproperties. (verified)
8. **Login with redirectTo:** After onboarding gate, login redirect lands on dashboard. (verified)
9. **Google Sign-In redirect:** Clicking Google button redirects to accounts.google.com with proper OAuth parameters. (verified)
10. **Registration — mismatched passwords:** Server-side "Passwords do not match" error. (verified)
11. **Registration — short password:** Server-side "Password must be at least 8 characters" error. (verified)
12. **Registration — successful:** Shows "Check your email" success state with the submitted email address. (verified)
13. **Registration — duplicate email:** Shows "Email is already registered" (account enumeration — BUG-2). (verified)
14. **Login with unverified email:** Shows amber verification notice with "Resend verification email" button. (verified)
15. **Resend verification email:** Shows "Verification email sent!" green success message. (verified)
16. **Forgot password — existing account:** Generic "If an account exists..." success message. (verified)
17. **Forgot password — non-existent account:** Same generic success message. (verified)
18. **Forgot password — email pre-fill:** ?email= query parameter pre-fills the email field. (verified)
19. **Verify email — no params:** "Verification failed" with "Invalid verification link." (verified)
20. **Verify email — garbage tokens:** "Verification failed" with API error message. (verified)
21. **Reset password — no params:** "Invalid reset link" with helpful links. (verified)
22. **Reset password — garbage tokens:** Form renders (tokens present in URL), submission returns "Invalid or expired reset link." (verified)
23. **Activate team — no token:** "Invalid activation link" with guidance. (verified)
24. **Activate team — fake token:** Form renders, submission returns "This activation link is invalid, expired, or has already been used." (verified)
25. **Login while already authenticated:** Redirects to / (dashboard). (verified)
26. **Register while authenticated:** Renders fully — BUG-3. (verified)
27. **Whitespace-only credentials:** Browser email validation blocks submission. (verified)
28. **Very long credentials (300+ char email, 1000 char password):** Graceful "Invalid email or password" error, no crash. (verified)
29. **Root / while logged out:** Redirects to /welcome. (verified)
30. **Cookie security:** document.cookie returns empty string while authenticated (all httpOnly). (verified)
31. **Refresh endpoint without token:** Returns 401 with "No refresh token". (verified)
32. **Terms/Privacy pages:** Both /terms and /privacy return 200. (verified)
33. **Register without accepting terms:** Browser required validation blocks checkbox. (verified)
34. **Mobile viewport (390x844):** Login, register, forgot-password, reset-password, verify-email, activate-team all render without horizontal overflow. (verified)
35. **Engine startup:** Crashes with IRequestWriteExecutor DI resolution failure — BUG-1. (verified via /tmp/rentalcommand-engine.log)

Browser cleanup: stopped e2e-s10 (daemon + Chrome helper tree)
