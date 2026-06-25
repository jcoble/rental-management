# TSK-397 Pass 91 - Tenant Maintenance Empty Submit Validation

Date: 2026-06-25
Branch: `tsk-397-426-real-user-pass-91-tenant-maint-empty-submit`
Scope: Real-user verification for Notion `TSK-426` / prior bug `TSK397-B085`.

## Acceptance Criteria

- A tenant-only user can open `/portal/maintenance` in local sanitized data.
- Clicking `Submit Request` with both the title and description blank stays on the maintenance page.
- The title field shows a visible validation message.
- The description field shows a visible validation message.
- Both invalid fields expose invalid state to assistive technology.
- The empty submit does not call the create-work-order endpoint.
- Existing focused regression tests still cover the tenant maintenance page and dashboard maintenance form.

## Edge Cases Checked

- Used a fresh local-only tenant portal account linked to tenant `11` / Marcus Williams.
- Browser login verified the tenant landed on `/portal` before opening `/portal/maintenance`.
- The form was submitted with title and description both blank while category and priority retained their defaults.
- The request list already contained an existing maintenance item, so the validation path was checked on a non-empty page as well as the form itself.
- Network evidence was checked after submit to confirm no `POST /api/v1/portal/tenant/work-orders` was sent.

## Evidence

- Before submit: `output/playwright/pass91-tenant-maintenance-before-empty-submit.png`
- After submit: `output/playwright/pass91-tenant-maintenance-empty-submit-validation.png`

Browser findings:

- Created synthetic tenant portal user `tsk397.pass91.tenant.1782378547050@example.local` through the local API setup path, linked to tenant `11` / Marcus Williams.
- Signed in through the real web login form at `https://localhost:6042/login`.
- Tenant login landed on `https://localhost:6042/portal` with tenant portal navigation.
- Opened `https://localhost:6042/portal/maintenance`.
- Clicked `Submit Request` with the title and description fields blank.
- The page stayed on `https://localhost:6042/portal/maintenance`.
- The title field became invalid and showed `Issue title is required.`
- The description field became invalid and showed `Describe the issue before submitting.`
- Playwright network log after submit showed only auth refresh, notification reads, SignalR negotiation, and `GET /api/v1/portal/work-orders`; it did not show a create-work-order POST.

Regression test:

```bash
pnpm --dir web exec node --test --experimental-strip-types src/lib/portal/maintenance-page.test.ts src/lib/portal/dashboard-page.test.ts
```

Result: Passed. The focused command reported 3/3 passing tests.

## Result

Pass. No product code change was needed for this slice because the current tenant maintenance form already tracks attempted submit, renders inline validation for both required fields, marks both controls invalid, and returns before mutating when either required field is blank.
