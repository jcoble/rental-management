# TSK-397 Pass 89 - Tenant Security Menu Link

Date: 2026-06-25
Branch: `tsk-397-427-real-user-pass-89-tenant-security-link`
Scope: Real-user verification for Notion `TSK-427` / prior bug `TSK397-B084`.

## Acceptance Criteria

- A tenant-only account can be created through the visible admin Team UI against local sanitized data.
- Tenant login lands on `/portal` and exposes only tenant portal navigation.
- From `/portal/maintenance`, opening the tenant account menu shows `Security` linked to `/portal/security`, not `/settings/security`.
- Clicking `Security` lands on `/portal/security` and does not bounce back to `/portal`.
- The tenant security page renders the shared account security form with `Back to portal`.
- Invalid password input shows validation locally and keeps `Change password` disabled.
- Staff/admin account menus still point Security at `/settings/security`.

## Edge Cases Checked

- Fresh synthetic tenant account created from the real Team modal rather than a seed shortcut.
- Tenant role selected with a linked tenant record before login.
- Original repro route `/portal/maintenance` used before opening the account menu.
- Menu href inspected before click to verify the target route.
- Weak new password plus mismatched confirmation blocks submit without changing the password.
- Focused regression tests cover tenant-vs-staff menu hrefs, portal route title/back-link wiring, and the shared change-password action.

## Evidence

- Team create proof: `output/playwright/pass89-team-tenant-created.png`
- Tenant menu proof: `output/playwright/pass89-tenant-security-menu.png`
- Tenant security route proof: `output/playwright/pass89-tenant-security-route.png`
- Tenant validation proof: `output/playwright/pass89-tenant-security-validation.png`

Browser findings:

- Signed in as `admin@rentalcommand.local`, opened `Team`, and created `Pass89 Security Tenant` with email `tsk397.pass89.security.20260625.0815@example.local`, role `Tenant`, and tenant link `Pass85 Tenant0610`.
- The Team page showed a success toast and placed the new tenant user at the top of the table.
- The admin account menu still showed `Security` linked to `/settings/security`.
- Signed out through the account menu, then signed in as the new tenant-only account with the supplied local temporary password.
- Tenant login reached `https://localhost:6042/portal` with page title `Tenant Dashboard - Rental Command` and tenant-only sidebar navigation.
- Navigated to `https://localhost:6042/portal/maintenance`, opened the tenant account menu, and confirmed `Security` linked to `/portal/security`.
- Clicking `Security` landed on `https://localhost:6042/portal/security` with page title `Security - Rental Command`.
- The page rendered `Back to portal`, `Security`, and the shared `Change password` form.
- Entering invalid new password values showed password rule text and `Passwords do not match.`
- `Change password` remained disabled; no password change was submitted.

Regression test:

```bash
pnpm --dir web exec node --test --experimental-strip-types src/lib/components/app-shell-security-menu.test.ts src/lib/account/security-page-routes.test.ts
```

Result: Passed. The focused command reported 5/5 passing tests.

## Result

Pass. No product code change was needed for this slice because the current tenant account menu already routes tenant-only users to `/portal/security`, and the focused regression tests cover the staff and tenant route split.
