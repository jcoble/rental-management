# TSK-397 Pass 88 - Command Center Unit Detail Navigation

Date: 2026-06-25
Branch: `tsk-397-428-real-user-pass-88-unit-detail-nav`
Scope: Real-user verification for Notion `TSK-428` / prior bug `TSK397-B085`.

## Acceptance Criteria

- Command Center opens from the app shell on a clean browser session.
- Unit search filters to matching units without losing the unit result link.
- Clicking `Short North Condo - Unit 4B` navigates to `/units/18`.
- The unit detail route renders `Unit 4B - Rental Command` and the Overview tab instead of `This unit could not be loaded.`
- The transient-query retry policy still retries request timeouts/network/server failures while keeping deterministic 400/404 responses fast-fail.

## Edge Cases Checked

- Fresh Playwright CLI browser after closing the existing automation session.
- Guarded-route login through the visible local dev-admin helper.
- Command Center search term `Short` with a single matching unit.
- Navigation from a sidebar app-shell result into a protected dynamic unit route.
- Regression coverage for status `408`, network/server errors, and deterministic `400`/`404` failures.

## Evidence

- Filtered Command Center proof: `output/playwright/pass88-command-center-short-search.png`
- Unit detail proof: `output/playwright/pass88-command-center-unit-4b-detail.png`

Browser findings:

- Opened `https://localhost:6042/login`, signed in as `admin@rentalcommand.local`, and landed on `https://localhost:6042/`.
- Expanded Command Center and searched `Short`; the list narrowed to `Short North Condo - Unit 4B` linked to `/units/18`.
- Clicking the filtered unit result landed on `https://localhost:6042/units/18` with page title `Unit 4B - Rental Command`.
- The page rendered the unit header, lifecycle rail, Overview tab, rent card, tenant/lease card, and recent activity instead of the prior `This unit could not be loaded.` state.

Regression test:

```bash
pnpm --dir web test:unit -- src/lib/api/query-retry.test.ts
```

Result: Passed. The command ran the web unit suite under the project script and reported 235/235 passing tests, including the query retry policy tests.

## Result

Pass. No product code change was needed for this slice because the current app-shell query client already uses `shouldRetryQuery`, and the real Command Center to unit-detail workflow no longer reproduces the transient hard-error.
