# TSK-397 Pass 78 Activity And Audit Guide

## Scope

Verify the Activity history and audit surfaces as a real landlord using local example data:
`/audit`, `/admin/audit`, legacy `/activity`, legacy `/analytics`, visible filters, refresh,
pagination, row/detail links, admin forensic details, and CSV export.

## Setup

- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- Login: visible dev-admin helper on `/login`
- Account: `admin@rentalcommand.local`
- Data mode: local sample/example data only

## Safety Boundaries

- Read-only pass except for any incidental audit reads/export downloads.
- Do not press final Go Live.
- Do not use production or sensitive data.
- Do not mutate QuickBooks/Plaid/provider connections.

## Acceptance Criteria

- `/audit` loads as Activity history with the normal landlord-facing audit rows, no raw JSON, and no hard-error state.
- Search filters debounce and call `/api/v1/audit` with `search=...`, reset pagination, and show either matching rows or the empty state.
- Action and entity filters call `/api/v1/audit` with `operation=...` and `entityType=...`.
- Refresh invalidates and reloads the audit query without changing the selected filters.
- Pagination uses overfetch paging (`take=51` for a 50-row page), and Prev/Next states match available rows.
- Rows with detail links navigate to the intended record and can be returned from without losing browser stability.
- Admin users see the `Advanced` button on `/audit`; `/admin/audit` loads forensic rows with IP address and collapsible old/new value panels.
- Admin forensic search/action/entity filters use `/api/v1/admin/audit` with matching query parameters.
- Admin CSV export honors the current filters and downloads a CSV from `/api/v1/admin/audit/export`; no full result set is buffered in UI code.
- `/activity` redirects to `/audit`.
- `/analytics` redirects to Dashboard (`/`), because analytics is merged into the dashboard.
- Console has no unexpected app errors or warnings after the pass.

## Browser Steps

1. Sign in as the dev admin and open `/audit`.
2. Verify the page title, heading, list, filter controls, refresh, pagination, and admin `Advanced` button.
3. Use search with a term expected to match at least one row, then a no-match term; verify request URLs and empty state.
4. Select an action filter and an entity filter; verify request URLs, visible trigger labels, and list/empty state.
5. Use Refresh; verify the filters remain selected.
6. If a row has a detail link, click it and verify the target route loads, then return to `/audit`.
7. Open `/admin/audit` via `Advanced`; verify title, export, filters, rows, IP display, and collapsible old/new value panels.
8. In `/admin/audit`, run a search/filter combination and export CSV. Verify a downloaded `.csv` artifact exists and has the expected header.
9. Open `/activity`; verify redirect to `/audit`.
10. Open `/analytics`; verify redirect to Dashboard (`/`).
11. Check Playwright console warnings/errors and network requests.

## Evidence To Capture

- Playwright result object for `/audit` initial state, filters, pagination, row-link navigation, and empty state.
- Playwright result object for `/admin/audit` forensic details and CSV export.
- Request evidence for `/api/v1/audit`, `/api/v1/admin/audit`, and `/api/v1/admin/audit/export`.
- Console error/warning counts.

## Results - 2026-06-25

Environment:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`
- Account: local dev admin `admin@rentalcommand.local`
- Data mode: local example/sample data only

Bug `TSK397-B086` / Notion `Fix audit inspection row detail links`:
- Repro before fix: from `/audit`, the first inspection row rendered href `/inspections/1`. Clicking it loaded `https://localhost:6042/inspections/1` with page title `Page not found - Rental Command`.
- Root cause: `AuditEntryResponse.BuildDetailHref("Inspection", id)` emitted the API/mobile-shaped `/inspections/{id}` path, but the web inspection detail page lives at `/maintenance/inspections/{id}`.
- Fix: the shared audit detail-href mapper now emits `/maintenance/inspections/{id}` for `Inspection`.

Regression:
- RED before fix: `MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter FullyQualifiedName~AuditDetailHrefTests --no-restore` failed because inspection hrefs returned `/inspections/7`.
- GREEN after fix: the same focused test passed 2/2. The existing `SQLitePCLRaw.lib.e_sqlite3` NU1903 warning is unrelated.
- API build after fix: `MSBUILDDISABLENODEREUSE=1 dotnet build RentalCommand.Api/RentalCommand.Api.csproj --no-restore` succeeded with 0 warnings and 0 errors.

Browser proof:
- `/audit` loaded `Activity history - Rental Command`, showed the example-data banner, Admin `Advanced`, `Refresh`, search, action/entity filters, `Page 1 · 50 shown`, disabled Prev, enabled Next, and 50 rows.
- Post-fix row proof: first inspection row rendered `/maintenance/inspections/1`; clicking it loaded `https://localhost:6042/maintenance/inspections/1` with page title `Inspection - Rental Command` / `MoveIn inspection - Rental Command`, not a 404.
- Public audit does not expose forensic labels (`Old values`, `New values`, `IP`) in row content.
- Search proof: `Inspection` narrowed the list to six inspection rows and disabled Next. No-match `qx-empty-never-match-omega` showed `No audit entries found.` with `Page 1` and disabled Prev/Next. Earlier `zz-no-audit-pass78` intentionally matched `Payment #78`, proving ID/description search behavior.
- Filter proof: `Created` + `Expense` narrowed to 35 expense rows; clicking `Refresh` preserved `Created`, `Expense`, and `Page 1 · 35 shown`.
- Pagination proof: unfiltered `/audit` moved from `Page 1 · 50 shown` to `Page 2 · 50 shown` and back to `Page 1 · 50 shown`; Prev/Next disabled states matched the page.
- `/admin/audit` loaded `Audit (forensic) - Rental Command` with `Export CSV`, `Refresh`, search, filters, IP display, entity links, and `Page 1 · 50 shown`.
- Forensic row disclosure proof: opening the first row showed `Old values`, `New values`, raw JSON, and `Open record -> /maintenance/inspections/1`.
- Admin filters proof: `Created` + `Inspection` narrowed to six rows, and the first entity link remained `/maintenance/inspections/1`.
- CSV export proof: filtered export requested `https://localhost:6042/api/v1/admin/audit/export?sort=-timestamp&operation=Created&entityType=Inspection`, returned `200`, downloaded `audit-2026-06-25-03-45-50.csv`, and was saved as `output/playwright/pass78-audit-export.csv`. The CSV has header `Timestamp,Action,Description,Actor,UserId,EntityType,EntityId,IpAddress,ChangeReason` plus six Created/Inspection rows.
- Redirect proof: `/activity` redirected to `/audit` with title `Activity history - Rental Command`; `/analytics` redirected to `/` with title `Dashboard - Rental Command` and no 404.
- Screenshot artifact: `output/playwright/pass78-admin-audit-proof.png`.
- Console proof: final Playwright `console error` returned `Total messages: 2 (Errors: 0, Warnings: 0)`.

Status: Pass after fixing `TSK397-B086`. Remaining related variants are admin/role-denied and service-error states; provider-bound QuickBooks/Plaid work stays deferred.
