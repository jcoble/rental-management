# Scenario 10 — Authentication, sessions, recovery, and invitations

## Purpose

Cover every entry and exit point for a landlord, team member, invited user, and tenant-facing identity: login, logout, refresh, expiry, registration, email verification, forgotten/reset passwords, Google handoff when configured, and invitation activation. Confirm that the user is never stranded between a public page and the correct protected experience.

## Preconditions and login

Use the seeded Rental Command administrator locally (admin@rentalcommand.local / Admin123!) or the approved preview account. For invitation and tenant cases, use an additive QA identity or an existing test identity only; never change another user’s credentials on the shared preview. Start from a clean browser context for each identity, then repeat safe checks with a session that is near expiry.

## Read the implementation first

Before opening a browser, read these named source files and the relevant controller/service code they call:

- web/src/routes/login/+page.svelte and web/src/routes/login/+page.server.ts
- web/src/routes/logout/+page.server.ts
- web/src/routes/register/+page.svelte and web/src/routes/register/+page.server.ts
- web/src/routes/forgot-password/+page.svelte and web/src/routes/forgot-password/+page.server.ts
- web/src/routes/reset-password/+page.svelte and web/src/routes/reset-password/+page.server.ts
- web/src/routes/verify-email/+page.svelte and web/src/routes/verify-email/+page.server.ts
- web/src/routes/activate-team/+page.svelte and web/src/routes/activate-team/+page.server.ts
- web/src/routes/auth/google/+server.ts and web/src/routes/auth/google/callback/+server.ts
- web/src/routes/api/auth/refresh/+server.ts and web/src/hooks.server.ts
- web/src/lib/stores/auth.svelte.ts and web/src/lib/server/token-refresh.ts
- RentalCommand.Api/Controllers/AuthController.cs, WorkspaceInvitationsController.cs, TeamController.cs, and DevicesController.cs

## Rough exploration areas

Use these as exploration goals, not a step-by-step script:

- Compare invalid email, wrong password, unknown account, locked/disabled account, and API transport failure; expected authentication copy should be clear and should not reveal which account exists.
- Exercise logout from the shell and direct /logout navigation, then use browser Back and a protected deep link to confirm cookies are actually cleared and the server guard redirects safely.
- Let an access token expire or simulate a 401 through the normal UI; inspect whether one refresh occurs, the original action retries once, and refresh-token reuse does not create a loop or duplicate request.
- Explore registration, terms, password-strength feedback, email verification, forgot-password request, and reset-password return paths without treating a route render as proof of a completed identity mutation.
- Open an invitation/activation link with valid, expired, already-used, malformed, and wrong-account tokens; verify the resulting team role and portfolio context are explicit.
- If Google is configured, compare the redirect origin and failure return with password login; if it is not configured, verify the UI explains unavailability instead of exposing a dead button.

## Specific edge cases worth trying

- Blank and whitespace-only email/password, mixed-case email, Unicode local-part, pasted password, very long credentials, and password-manager autofill.
- Expired, reused, URL-encoded, tampered, or missing reset/invitation token; refresh or open the link in a second browser context.
- Session expiry while a form has unsaved values, simultaneous tabs refreshing the same session, and closing the browser during a redirect.
- Already-verified email, already-active invitation, invitation for a different portfolio, and a user with no linked tenant/owner relationship.
- Rate-limit or offline/API failure during login, reset request, verification, or logout; confirm the message is actionable and not a generic success.

## What to verify visually

- Input labels, focus/error association, password visibility affordance, strength indicators, disabled/submitting states, and keyboard order.
- Consistent public-page shell, loading/redirect transitions, non-leaking error copy, and a clear route back to login or support.
- No protected content flashes before redirect; stale session banners and toast messages remain readable at 390px.
- Invitation role/scope summary, terms text, confirmation state, and password-reset success state are aligned and not truncated.

## Data safety and evidence

Do not attempt destructive account deletion or change the seeded administrator password on preview. QA invitation and registration values must begin with QA-YYYYMMDD; use disposable local accounts for flows that require consuming a token.

Record actual-versus-expected behavior, URLs, response/status evidence, persistence after reload, and console errors. Report confirmed bugs separately from potential issues and observations using the BUG-N format from the exploratory tester definition.
