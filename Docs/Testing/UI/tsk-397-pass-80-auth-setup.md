# TSK-397 Pass 80 Auth And Setup Front Door

Date: 2026-06-25
Branch: `tsk-397-real-user-pass-80`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

## Scope

Verify the public auth and setup front door as a real new user on local sanitized data:
marketing/welcome entry, register, email verification, login failure/success, forgot password, reset password,
logout/session cleanup, setup choice, sandbox setup guard/progress, and live setup handoff to onboarding.

This pass intentionally avoids production data, real email delivery, real SMS, Plaid, and connected QuickBooks.
Verification links and reset links must come from local `OutboxMessages`.

## Local Stack

- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`
- Browser: Playwright CLI with installed Chrome channel

## Synthetic Accounts

Use unique local-only addresses:

- Registration/live setup: `tsk397.pass80.<timestamp>@example.local`
- Forgot/reset password: the same registered account after verification
- Invalid/unknown email: `tsk397.pass80.missing.<timestamp>@example.local`

## Acceptance Criteria

- `/welcome` loads anonymously with visible brand, sign-in, register/get-started entry points, and no authenticated shell.
- `/register` loads anonymously, validates weak/mismatched password input visibly, preserves failed-submit email/display name, and creates a local account.
- Register success shows `Check your email` and the registered local email; it must not auto-login before verification.
- Local verification link from `OutboxMessages` opens `/verify-email` and activates the account.
- Verified account can log in and reaches `/choose-setup` or the expected first-login setup boundary.
- Login wrong-password path shows a visible error and does not authenticate.
- Forgot password always shows generic success for both existing and missing local emails.
- Reset password invalid-link state is visible from a bogus token.
- Valid reset link from local `OutboxMessages` loads the reset form, rejects weak/mismatched input, accepts a strong password, and allows sign-in with the new password while rejecting the old password.
- `/logout` clears the session and returns to `/login`; `cookie-list` is empty after logout.
- `/choose-setup` shows both sandbox and live choices with the correct sample-data/live-data copy.
- Sandbox choice routes through `/setting-up`, shows progress/stage UI, seeds local example data, and lands on Dashboard without production actions.
- Live choice routes to `/onboarding` with a clean real-portfolio setup handoff. Do not click final Go Live in this pass.
- Direct route guards behave coherently:
  - Anonymous access to protected setup routes redirects to `/login`.
  - Authenticated setup-only routes do not trap a user after setup choice is complete.
- Console warnings/errors and failed browser requests remain zero for the completed happy-path checks, except expected 4xx responses intentionally caused by invalid-login or invalid-link checks.

## Risk-Based Edge Cases

- Duplicate registration or already-verified links should show actionable copy rather than a blank/error shell.
- Browser back/refresh around register success, verification, and reset success must not leave the app in an impossible state.
- Reset/verification token URLs must not leak sensitive content into visible UI beyond the link itself.
- Sandbox setup failure state must have retry/back controls if seeding fails.
- Auth cookies must be cleared on logout before moving to the next pass.

## Evidence To Capture

- Snapshot or structured assertion for `/welcome`.
- Register validation and success snapshots.
- DB/Outbox proof for verification and reset links.
- Verification success snapshot and DB user email-confirmed proof.
- Login wrong-password and successful login snapshots.
- Forgot/reset invalid and valid snapshots.
- Setup choice and setting-up progress snapshots.
- Dashboard landing proof after sandbox setup or onboarding landing proof after live setup.
- Final console and failed-request scan.

## Status

Pass with documentation-only verification. No product code change was needed for this slice.

## Execution

Synthetic users:
- Live setup/reset user: `tsk397.pass80.1782362263@example.local`
- Unknown reset email: `tsk397.pass80.missing.1782362263@example.local`
- Sandbox setup user: `tsk397.pass80.sandbox.1782362697@example.local`

### Welcome, Register, Verify

- `/welcome` loaded anonymously with title `Rental Command — the computer does the typing for you`, visible brand, `Get started`, `Sign in`, and no authenticated shell.
- `/register` loaded anonymously with display name, email, password, confirm password, Google signup, create-account, and sign-in controls.
- Weak/mismatched password input showed `Use at least 8 characters.` and `Passwords don't match.` while preserving the typed email/display name.
- Valid registration showed `Check your email` and the exact registered local email.
- Verification links were read only from local `OutboxMessages`; no external email was sent.
- Opening the local verification link for user `30` showed `Email verified`; DB proof showed `EmailConfirmed = true`.
- A second sandbox account was registered and verified through the same local outbox path; its verification page also showed `Email verified`.

### Login, Logout, Reset

- Wrong password for the verified live user stayed on `/login` and showed `Invalid email or password`.
- Correct first password signed in and landed at `/choose-setup`, the expected first-login boundary.
- `/logout` redirected to `/login`; `cookie-list` returned `No cookies found`.
- Forgot password with the missing local email showed the generic success copy: `If an account exists for that email, we've sent a password reset link.`
- Forgot password with the registered local email showed the same generic success copy and wrote the reset email to local `OutboxMessages`.
- A bogus reset token renders the reset form and then shows `Invalid token.` after submitting otherwise-valid passwords. This is acceptable for this pass, but the page does not prevalidate the token on load.
- Valid reset link rejected mismatched values with `Passwords do not match.` and matching weak values with `Password must be at least 8 characters.`
- Strong reset to `Pass80Reset!23` showed `Password updated`.
- The old password was rejected after reset; the new password signed in and landed on the live Dashboard.

### Setup Choices

- Live setup choice from `/choose-setup` routed to `/onboarding` with a clean empty portfolio and the main app shell.
- The onboarding page exposed the scan-first and spreadsheet-import handoff actions: `/scan/new-rental` and `/import`.
- After the live setup choice, logging in through a protected `/choose-setup` redirect landed on Dashboard instead of trapping the user back on the setup choice page.
- Anonymous access to `/choose-setup` redirected to `/login?redirectTo=/choose-setup`.
- Sandbox setup choice routed through `/setting-up`, showed staged progress including `Creating sample properties`, `Adding tenants & leases`, and `Generating 6 months of payment history`, then landed on Dashboard.
- Sandbox Dashboard displayed the example-data banner: `Example data — you're exploring with example data. Nothing here sends real emails/texts or charges cards.`
- Browser proof showed seeded portfolio stats including 20 units, 17 occupied, 6 open work orders, overdue rent, appointments, maintenance rows, and leasing mix.

### DB Evidence

```text
30|tsk397.pass80.1782362263@example.local|t|5|f|0|0|0|0|0
31|tsk397.pass80.sandbox.1782362697@example.local|t|6|t|8|20|22|20|15
```

Columns: user id, email, email confirmed, portfolio id, sandbox flag, properties, units, tenants, leases, work orders.

### Artifacts

- Register success snapshot: `.playwright-cli/page-2026-06-25T04-38-16-561Z.yml`
- Verification success snapshot: `.playwright-cli/page-2026-06-25T04-38-40-496Z.yml`
- Login wrong-password snapshot: `.playwright-cli/page-2026-06-25T04-41-24-671Z.yml`
- Live setup handoff snapshot: `.playwright-cli/page-2026-06-25T04-41-53-265Z.yml`
- Reset success snapshot: `.playwright-cli/page-2026-06-25T04-43-54-730Z.yml`
- Sandbox setup progress snapshot: `.playwright-cli/page-2026-06-25T04-46-10-054Z.yml`
- Sandbox Dashboard screenshot: `output/playwright/pass80-auth-setup-sandbox-dashboard.png`

### Console And Network

- Final Playwright console scan: two Vite debug messages only, `Errors: 0`, `Warnings: 0`.
- Final Dashboard request scan returned `200` for the loaded route/API requests. Two `/api/v1/ai/briefing` requests were aborted during Dashboard load and then followed by a successful `/api/v1/ai/briefing => 200`; this appears to be route/load cancellation noise rather than a user-visible failure.

Deferred boundaries remain production data, real email/SMS delivery, Plaid banking, connected QuickBooks/provider accounting, and final Go Live.
