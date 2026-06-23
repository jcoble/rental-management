# Production-Scale Empty-Portfolio Scan Audit

Date: 2026-06-22
Task: TSK-397
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-production-scale-audit`

## Scope

Build sanitized, production-scale local data under production-like local settings by starting from an empty live portfolio like a real landlord. Domain records must come through scan workflows, not direct database seeding. Synthetic leases create the base properties, units, tenants, and leases. Synthetic receipts, rent checks, applications, and maintenance requests are scanned after lease confirmation creates the base records.

The local extraction provider for this audit is:

- `Assistant:Provider=claude-cli`
- `Assistant:ModelId=sonnet`
- backed by `claude -p`

Do not use production systems, production data, sensitive data, or destructive database actions without explicit approval.

## Local Artifacts

- Fixture generator: `scripts/qa/generate-production-scale-scan-fixtures.py`
- Local stack wrapper: `scripts/qa/start-scan-audit-local.sh`
- Static route/control inventory: `scripts/qa/inventory-web-surfaces.mjs`
- Generated fixture output: `output/qa/production-scale-scans/`
- Browser evidence output: `output/playwright/tsk397/`
- Inventory output: `output/qa/web-surface-inventory.*`

Default generated scale:

- 40 lease PDFs and 40 lease camera JPEGs
- 80 expense receipt/invoice PDFs and 80 expense camera JPEGs
- 40 rent-check PDFs and 40 rent-check camera JPEGs
- 40 completed application PDFs and 40 application camera JPEGs
- 40 work-order PDFs and 40 work-order camera JPEGs

The PDF and image sets are both required. Mobile and web share the same extraction engine; the web audit must still prove camera-style JPEGs are extractable and confirmable through the same review path.

## Runbook

1. Generate synthetic documents:

   ```bash
   /Users/blackcolours/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/bin/python3 scripts/qa/generate-production-scale-scan-fixtures.py
   ```

2. Start the isolated local stack:

   ```bash
   scripts/qa/start-scan-audit-local.sh
   ```

3. Verify listeners before browser conclusions:

   ```bash
   lsof -nP -iTCP:5696 -sTCP:LISTEN
   lsof -nP -iTCP:5697 -sTCP:LISTEN
   curl -ks https://localhost:5696/health
   ```

4. Register a new local landlord from `/register`. Confirm email through local development behavior only. Choose Live setup, not Sandbox, so the portfolio begins without demo domain data.

5. Open `/scan/batch` and upload all PDFs under `output/qa/production-scale-scans/01-leases`. Wait until drafts are in `Reviewing`. Confirm enough lease drafts to build a representative populated portfolio, then continue until the full batch is confirmed or a blocking product defect is logged.

6. Open `/scan/new-rental` and upload camera-style JPEG lease photos from `output/qa/production-scale-scans/01-leases-camera`. Confirm at least one successful image-derived lease and intentionally dedupe or reject duplicates.

7. Upload dependent PDF and JPEG documents:

   - `02-expenses` as `Expense`
   - `02-expenses-camera` as `Expense`
   - `03-payments` as `Payment`
   - `03-payments-camera` as `Payment`
   - `04-applications` as `Application`
   - `04-applications-camera` as `Application`
   - `05-work-orders` as `WorkOrder`
   - `05-work-orders-camera` as `WorkOrder`

8. Run the static route/control inventory:

   ```bash
   /Users/blackcolours/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node scripts/qa/inventory-web-surfaces.mjs
   ```

9. Test as a real user. For every bug, log role, route, data state, reproduction steps, expected result, observed result, screenshot path, and suspected shared cause.

10. Review findings for shared causes and dependencies. Implement coherent fixes with regression tests, then rerun the relevant inventory and browser proof.

## User-Facing Inventory

### Public And Auth

Roles: anonymous visitor, new landlord.

Routes and workflows:

- `/welcome`: enter app, login/register navigation.
- `/login`: email/password login, dev-fill button in development, Google button when configured, errors, loading/disabled submit.
- `/register`: account creation, validation, existing email, email-confirmation gate.
- `/forgot-password`: request reset, neutral response for unknown email.
- `/reset-password`: valid reset, invalid/expired token, password validation.
- `/verify-email`: valid confirmation, invalid token, already verified.
- `/choose-setup`: choose live setup versus sandbox/demo.
- `/setting-up`: setup progress and navigation to first workspace.
- `/apply/[token]`: public rental application form.
- `/sign/[token]`: public lease signing flow.
- `/docs`: public documentation.
- `/logout`: clears app-scoped cookies.

Acceptance criteria:

- Anonymous users can access only public/auth routes and are redirected from protected routes.
- New landlords can register, verify email locally, log in, and reach a live empty portfolio without seeded properties, units, tenants, leases, payments, expenses, or work orders.
- Form validation is visible, specific, and preserves non-secret user input.
- Auth cookies are app-namespaced and logout clears them.

Finite risk edge cases:

- Duplicate registration email.
- Expired/invalid confirmation and reset tokens.
- Refresh-token expiry during navigation.
- Browser back after logout.
- Localhost cookie collision with another app.
- Mobile-width auth forms.

### Landlord And Staff Core

Roles: owner/admin, property manager/staff.

Routes and workflows:

- `/`: dashboard cards, empty state, populated state after scans.
- `/properties`: list, search/filter, create/edit/deactivate, owner assignment, drilldown.
- `/units`: list, filter by property/status, create/edit, vacant/occupied status.
- `/tenants`: list, create/edit, contact details, portal invite context.
- `/leases`: list, lease detail, import-created leases, active/expired status.
- `/applications`: list, scanned applications, status changes.
- `/maintenance`: work orders, priority/status, create/edit.
- `/appointments`: schedule/list/update.
- `/messages`: conversations and message history.
- `/notices`: notice drafts and send/preview states.
- `/accounting`: summary, transactions, reports links, filters.
- `/reports`: income/expense, P&L, general ledger, cash flow, rent roll, rent ledger, delinquency, occupancy, lease expirations, security deposits, vendor 1099, work orders.
- `/settings`: profile, company, notifications, integrations, account.
- `/owners`: owner entities and assignments.
- `/vendors`: vendor list/detail.
- `/deposits`: security deposit register/workflows.
- `/banking`: Plaid/manual connection status, import/review queue, matching.
- `/tax`: Schedule E and tax summaries.
- `/audit`: audit trail search/filter.
- `/ai`: assistant and daily briefing.
- `/onboarding`, `/import`, `/plaid`: guided setup/import/integration paths.

Acceptance criteria:

- Every list uses server-side filtering, sorting, aggregation, joins, and paging.
- Empty states invite a real next action and do not imply demo data exists.
- After scan confirmation, affected routes update without reload or with clear refresh behavior.
- Cross-portfolio IDs cannot be linked or viewed.
- Staff can do only role-appropriate actions.
- Destructive or irreversible actions require explicit user intent and show a result.

Finite risk edge cases:

- 0, 1, 20, and 200+ records.
- Filters with no results.
- Duplicate names across properties, tenants, vendors, and leases.
- Deactivated records in dropdowns.
- Stale route after entity deletion or status change.
- Concurrent scan confirmations.
- Mobile navigation and narrow tables.

### Scan And Document Intake

Roles: owner/admin, property manager/staff.

Routes and workflows:

- `/scan`: target selector, file upload, recent drafts.
- `/scan/new-rental`: guided single-lease import, photo-to-PDF path, lease prefill, confirm.
- `/scan/batch`: lease batch upload, max 100 PDFs, upload progress, errors.
- `/scan/batch/[id]`: batch counts, draft rows, polling, confirmed links.
- `/scan/[draftId]`: preview, status badge, field review, target-specific overrides, confirm, reject, success card.

Acceptance criteria:

- Lease scans can bootstrap properties, units, tenants, and leases from an empty portfolio.
- Camera-style JPEG uploads extract through the same engine and reach the same review/confirm behavior as PDFs.
- The review page shows required fields and refuses confirm until required data is present.
- Failed extraction has an actionable message and does not create records.
- Batch counts match draft statuses and are computed DB-side.
- Unsupported file types and oversized files fail with user-visible errors.
- Payment review lets the user select the matching lease when extraction cannot ground one.

Finite risk edge cases:

- Blank or unreadable PDF.
- Unsupported picker-allowed image type that backend rejects.
- Multi-page lease.
- Camera image with slight skew/noise.
- Duplicate lease rescan.
- Two leases for the same property/unit.
- Missing unit on a single-family lease.
- Rent check before lease exists.
- Batch with 0, 1, 40, and 101 files.
- Draft stuck in `Pending` or `Processing`.

### Admin, Superadmin, And Portal

Roles: platform admin, superadmin, tenant.

Routes and workflows:

- `/admin/users`: user list, role changes, invites, deactivate.
- `/admin/audit`: platform audit search/filter.
- `/superadmin/engine`: engine health, provider status, worker state.
- `/portal`: tenant home.
- `/portal/messages`: tenant conversation.
- `/portal/notifications`: notification preferences.
- `/portal/maintenance`: tenant work-order request/list.
- `/portal/payments`: balance/payment/autopay states.
- `/portal/lease`: lease view and signature/download states.
- `/portal/appointments`: appointment list/confirm/cancel.

Acceptance criteria:

- Admin routes are inaccessible to non-admin users.
- Superadmin route is inaccessible to platform admins without superadmin permission.
- Tenant portal sees only the tenant's own lease, payments, maintenance, messages, and appointments.
- Portal payment/signature actions degrade cleanly when external providers are not configured locally.

Finite risk edge cases:

- Staff user attempts admin routes.
- Tenant attempts landlord routes.
- Tenant with multiple leases.
- Portal with no active lease.
- Provider not configured.
- Notification preferences invalid phone/email.

## Browser Evidence From This Scan-Focused Pass

Synthetic live portfolio `TSK397 Landlord Two's Portfolio` was created from no domain data and populated through scan workflows. Current local database counts after browser confirmation:

- 8 properties, 8 units, 7 tenants, 8 active leases from lease scans.
- 2 expenses: PDF receipt draft #46 and camera JPEG receipt draft #47.
- 2 payments: PDF rent-check draft #48 and camera JPEG rent-check draft #49, both confirmed after manually selecting lease `QA-2026-001-1A`.
- 2 applications: PDF application draft #50 and camera JPEG application draft #51.
- 2 work orders: PDF maintenance draft #52 and camera JPEG maintenance draft #53.
- Remaining review queue includes duplicate/edge lease drafts and one extra expense draft; duplicate lease confirms now return actionable overlap messages instead of creating records.

Representative browser states exercised:

- `/scan/batch` upload and `/scan/batch/1` status review for 40 lease PDFs.
- `/scan/[draftId]` retry, failure reason, document preview proxy, field review, confirm, reject controls, and confirmed success cards.
- `/scan/new-rental` camera-photo path through property, unit, tenant, lease, review, duplicate conflict toast.
- `/scan` target selector for receipt, payment, application, work order, file upload, tabs, table pagination, review links, and recent batch import card.
- `/applications` populated list after scan-created applicants.

## Pass 16 Continuation Evidence

Date: 2026-06-23
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-16`
Local stack: `https://localhost:5942`, API `https://localhost:5941`, Postgres `localhost:5565/rentalcommand_tsk397_pass16_clean`

Fresh local landlord account:

- Name: Mia Rivera
- Email: `tsk397.pass16.202606230428@example.local`
- Setup choice: live portfolio from zero domain data, not sandbox/demo

Camera-style scan fixtures confirmed in this pass:

- Lease image: `output/qa/production-scale-scans/01-leases-camera/lease-001-1a.jpg`
- Expense image: `output/qa/production-scale-scans/02-expenses-camera/expense-001.jpg`
- Payment image: `output/qa/production-scale-scans/03-payments-camera/payment-001.jpg`
- Application image: `output/qa/production-scale-scans/04-applications-camera/application-001.jpg`
- Work order image: `output/qa/production-scale-scans/05-work-orders-camera/work-order-001.jpg`

Verified local database counts after browser confirmation:

- 1 property, 1 unit, 1 lease, 1 payment, 1 expense, 1 rental application, 1 work order, 5 scan drafts.
- 2 tenants: one lease tenant created from the lease scan and one applicant tenant created after approving the scanned application.

Browser states exercised:

- `/register`, local email verification, `/login`, `/choose-setup`, `/onboarding`, and scan-first navigation from an empty live portfolio.
- `/scan/new-rental` camera upload through property/unit/tenant/lease/review confirmation, including manual correction of missing beds/baths before create.
- `/scan/[draftId]` target-specific review for Expense, Payment, Application, and WorkOrder camera JPEGs.
- `/accounting/expenses/1`, `/accounting/payments/1`, `/applications/1`, `/tenants/2`, and `/maintenance/1` record drilldowns created from scan confirmations.

Bug fixed during this pass:

- Work Order scan review exposed raw internal linkage fields (`unit_id`, `tenant_id`, `property_id`, `target_entity_type`) as editable landlord fields.
- Fix: extracted review grouping now hides internal linkage fields from all generic scan review forms, renders Work Order scans as `Work order` and `Notes` sections, and keeps the underlying extracted values available to the confirm payload.
- Regression coverage: `web/src/lib/scans/scan-review-fields.test.ts`.
- Browser proof after fix: `/scan/5?type=WorkOrder` showed Title, Priority, Description, Category, Estimated cost, and Notes; raw ID fields were absent. Screenshot: `.playwright-cli/page-2026-06-23T04-48-24-806Z.png`.
- Verification commands: `pnpm --dir web exec node --test --experimental-strip-types src/lib/scans/scan-review-fields.test.ts`; `pnpm --dir web check`; `pnpm --dir web test:unit`.

## Pass 17 Continuation Evidence

Date: 2026-06-23
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-17`
Local stack: `https://localhost:5962`, API `https://localhost:5961`, Postgres `localhost:5566/rentalcommand_tsk397_pass17_clean`

Fresh local landlord account:

- Name: Noah Carter
- Email: `tsk397.pass17.202606230455@example.local`
- Setup choice: live portfolio from zero domain data, not sandbox/demo

Scan-created data confirmed in this checkpoint:

- Lease PDF: `output/qa/production-scale-scans/01-leases/lease-001-1a.pdf`
- Expense camera image: `output/qa/production-scale-scans/02-expenses-camera/expense-001.jpg`
- Payment camera image: `output/qa/production-scale-scans/03-payments-camera/payment-001.jpg`

Verified local database counts after browser confirmation:

- 1 property, 1 unit, 1 tenant, 1 lease, 1 expense, 1 payment, 3 scan drafts.
- Applications and work orders were not yet scanned in pass 17 at this checkpoint.

Browser states exercised:

- `/register`, local email verification, `/login`, `/choose-setup`, and the live empty portfolio setup path.
- `/scan/batch` lease PDF upload and `/scan/1` review/confirm from no domain data to `/leases/1`.
- `/scan` receipt camera upload and `/scan/2?type=Expense` review/confirm to `/accounting/expenses/1`, with scanned image preview.
- `/scan` rent-check camera upload and `/scan/3?type=Payment` review/confirm to `/accounting/payments/1`, including manual lease selection.
- Confirmed scan read-only views for Expense and Payment after fresh navigation.

Bug fixed during this checkpoint:

- Payment scan review reused the generic expense field grouping, showing empty Vendor, receipt, tax, category, and other expense-only controls for a rent check.
- Expense scan review put payment-only bank, payer, and check-number fields in `Other`.
- Confirmed scan drafts claimed to be read-only but still exposed editable fields, selectors, line-item add/remove controls, and payment-status toggles; fresh confirmed views could also show stale/unset linkage selectors.
- Fix: target-specific scan review grouping now has separate Payment, Expense, and WorkOrder groups; terminal scan states hide transient linkage selectors and disable all remaining review controls. Confirmed read-only copy now uses the correct article (`an Expense`).
- Regression coverage: `web/src/lib/scans/scan-review-fields.test.ts` and `web/src/lib/scans/scan-review-state.test.ts`.
- Browser proof after fix: `/scan/3?type=Payment` showed only `Payment` and `Details` groups with disabled total/method/payer/bank/check/date fields; `/scan/2?type=Expense` showed no bank/payer/check fields, no property selector, no paid toggle, and disabled fields/line-item controls. Artifacts: `.playwright-cli/page-2026-06-23T05-16-14-488Z.yml`, `.playwright-cli/page-2026-06-23T05-16-15-131Z.png`, `.playwright-cli/page-2026-06-23T05-15-59-002Z.yml`, `.playwright-cli/page-2026-06-23T05-16-07-873Z.png`.
- Verification commands: `pnpm --dir web exec node --test --experimental-strip-types src/lib/scans/scan-review-fields.test.ts`; `pnpm --dir web exec node --test --experimental-strip-types src/lib/scans/scan-review-state.test.ts`; `pnpm --dir web check`; `pnpm --dir web test:unit`.

## Classified Inventory Matrix

The route/control inventory now has a finite closure matrix instead of a raw tag-count table. `scripts/qa/inventory-web-surfaces.mjs` includes layout guards, error surfaces, redirects, server routes, file proxies, custom component controls, `data-testid` coverage, route scopes, route kinds, roles, acceptance criteria, and finite edge cases.

Latest output:

- `output/qa/web-surface-inventory.json`
- `output/qa/web-surface-inventory.md`
- Route count: 85
- Unclassified routes: none
- Surface groups: shell/guards/errors, public auth/OAuth, public docs/apply/sign, core staff app, scan intake, file proxies, accounting/reports, settings/setup/import, maintenance detail, portal, admin/superadmin, compatibility redirects.

The generated acceptance matrix is the canonical checklist for the next full real-user UI pass. Every route row is classified as page, guard/layout, redirect, server route, file proxy, or error surface and is tied to one or more surface groups with documented roles, acceptance criteria, and risk-based edge cases. This scan audit did not manually click every control in the application; it created the finite inventory, deeply exercised the scan/import path from empty data, and fixed the defects found there.

## DB-Side Rule Findings From Sweep

Read-only data-access audit found broad violations of the hard SQL-side rule. These block a clean pass until fixed or explicitly split into a follow-up implementation lane:

- `ReportsService.GetGeneralLedgerAsync`: fixed in this branch. Payments and expenses are combined with `UNION`, range-filtered, ordered, and summed in SQL; running balances are assigned after ordered rows return.
- `ReportsService.GetPropertyProfitAndLossAsync`: fixed in this branch. Paid payments and expenses now apply property/date filters and `GROUP BY`/`SUM` in SQL, with regression coverage.
- `ReportsService.GetRentLedgerAsync`: fixed in this branch. Charge and receipt activity is combined, scoped, ordered, grouped, and summed in SQL; per-entry running balances are assigned after ordered rows return.
- `BankingService.MapTransactionsWithSuggestionsAsync` and review queue paths: fixed in this branch. Candidate payments/expenses are now bounded in SQL by amount and cash-date windows before name/date scoring runs in memory.
- `AccountingService.GetReportsAsync` and vendor report paths: fixed in this branch. The report ledger is built with a SQL `UNION` over payments, expenses, and unmatched bank transactions, ordered in SQL; property rollups and vendor 1099 filtering are projected SQL-side before DTO formatting.
- `AccountingService.GetTransactionsAsync`: fixed in this branch. The main grid is DB-side, and inline reconciliation suggestions now bound unmatched bank candidates in SQL by the current page's amount and date windows before name/date scoring runs in memory.
- `ReportsService.GetCashFlowAsync`, `GetDelinquencyAsync`, and `GetWorkOrdersAsync`: fixed in this branch. Cash-flow grand totals, delinquency ordering/totals, and work-order counts/cost rollups are computed with translated SQL queries; DTO month/row formatting remains post-query.
- `OwnerStatementService`: fixed in this branch. Owner statement property lines and owner net summaries now filter/order/group/sum from SQL projections instead of dictionary-joining property aggregates in memory.
- `ScheduleEService`: fixed/reviewed in this branch. Raw payment, expense, and loan-payment sums are SQL-side, property ordering is SQL-side, and the remaining post-query work is finite tax DTO assembly for pre-aggregated category rows plus computed depreciation.

## Bug Log

| ID | Severity | Area | Finding | Evidence | Status |
| --- | --- | --- | --- | --- | --- |
| TSK397-B001 | P0 | Audit recovery | Previous uncommitted TSK-397 worktree disappeared before it was committed. Recreated compliant worktree from `main` and rebuilt the audit tooling slice first. | `git worktree list` showed no TSK-397 worktree; branch recreated at `b6d80c2`; tooling slice committed as `52e4968`. | Recovered |
| TSK397-B002 | P1 | Local stack | `scripts/start-dev.sh` could reuse an existing Postgres volume/container without ensuring the configured `PG_DB` existed. The API exited with `3D000: database "rentalcommand_tsk397" does not exist` while the web server still started and produced API connection errors. | Red: first TSK-397 startup failed in `/tmp/rentalcommand-api.log`. Green: restart printed `Creating database 'rentalcommand_tsk397'...`; `curl -ks https://localhost:5696/health` returned `{"status":"ok"}`; listeners were present on 5696 and 5697. | Fixed |
| TSK397-B003 | P1 | Local auth links | Auth emails used the default `App:WebBaseUrl` (`https://localhost:5667`) even when the local stack ran on a non-default web port. The TSK-397 verification email pointed at the wrong port, blocking a real user from confirming email in this isolated stack. | Red: local `OutboxMessages.Payload` for `tsk397.landlord.20260622@example.local` contained `https://localhost:5667/verify-email...` while the verified listener was `https://localhost:5697`. Green: after exporting `App__WebBaseUrl`, `tsk397.landlord.2.20260622@example.local` received a `https://localhost:5697/verify-email...` link and the browser reached `Email verified`. | Fixed |
| TSK397-B004 | P2 | Portfolio scope | Fixed in this branch. The protected layout now resolves the authenticated portfolio before child route queries are created, and stale `localStorage` portfolio ids are replaced by the claim-scoped portfolio for Phase 0 accounts. | Red: Playwright console on `/onboarding` showed `/api/v1/portfolios/1` 404 and mixed `portfolioId=1`/`portfolioId=3`. Green: fresh browser proof with `localStorage.rental:currentPortfolioId=1` before login landed on `/onboarding` as portfolio 4, persisted `rental:currentPortfolioId=4`, and `output/playwright/tsk397/b004-portfolio-requests-after-fix.txt` contains no `/portfolios/1` or `portfolioId=1` hits. Screenshot: `output/playwright/tsk397/b004-onboarding-after-fix.png`. Regression: `resolveInitialPortfolioId`; `pnpm --dir web test:unit` passed 53/53; `pnpm --dir web check` passed with 0 errors and existing PageHeader CSS warnings only. | Fixed |
| TSK397-B005 | P1 | Scan batch detail | `/api/v1/scans/batches/{id}` displayed correct counts only because it folded loaded draft rows in memory. This violated the DB-side aggregation rule and would scale poorly for production-sized review queues. | Red: `dotnet test ... --filter "FullyQualifiedName~ScanBatchControllerTests.GetBatch_ComputesCountsWithGroupedSql"` failed with no `GROUP BY`/`COUNT` SQL. Green after fix: focused test passed; full `ScanBatchControllerTests` passed 8/8. | Fixed |
| TSK397-B006 | P1 | Local outbound safety | The isolated audit stack inherited configured SendGrid secrets and sent the synthetic verification email through SendGrid. The address and payload were synthetic, but local audit runs must not call real outbound providers. | Engine log: `[Email sent via SendGrid] To=tsk397.landlord.2.20260622@example.local Subject=Confirm your Rental Command email Status=202`. Wrapper now clears SendGrid/SMTP env vars for the audit process. | Fixed for future audit starts |
| TSK397-B007 | P2 | Scan batch detail | The batch review table left `Unit` blank for live lease extractions because the summary helper read `unit_id`/`unitId`, while the shared extraction engine returned `unit_number`. The review page itself had the unit and could confirm correctly. | Browser: `/scan/batch/1` showed ready rows with unit `—`; `/scan/1` showed Unit `1A` and confirmed successfully. Red: `GetBatch_SummarizesUnitNumberFromLeaseExtraction` failed with `Unit <null>`. Green: focused test passed; full `ScanBatchControllerTests` passed 9/9. Browser retest after restart: batch page showed Unit `1A`, `2B`, `3C`, etc. | Fixed |
| TSK397-B008 | P1 | Scan failure recovery | Normal lease PDFs in a 40-file production-scale batch could time out or be interrupted, then remain terminal `Failed` with no retry action and no failure reason in the scan DTO. The batch page also gave no route from a failed row to recovery. | Browser/DB: batch #1 ended with 2 `Failed` drafts and failure reason `extraction interrupted (timeout or shutdown)`. Red: focused tests failed because `ScanDraftResponse.FailureReason` and `ScanController.Retry` did not exist. Green: `ScanBatchControllerTests` passed 13/13 after adding failure reason exposure, portfolio-scoped `POST /api/v1/scans/{id}/retry`, and UI retry links. Browser retest: draft #27 showed the reason, `Try extraction again` moved it to `Processing`, then `Reviewing` via `claude-cli:sonnet`; page 2 showed failed draft #37 with a `Retry` link. | Fixed |
| TSK397-B009 | P1 | Scan retry UI state | After retrying a failed lease draft, the worker produced valid extracted fields, but the review page kept create-new property/unit inputs blank because the failed/processing state had already tripped the one-shot lease seeding guards. This left `Create Lease` disabled until a full page reload. | Browser: after retry #27 reached `Ready to review`, extracted fields were present but property/unit inputs stayed blank and the button remained disabled. Green: added `shouldSeedLeaseReviewState` regression, gated lease seeding to `Reviewing`/`Confirmed` with fields, reset lease state on retry, and hid field controls while failed/processing. Browser retest: reloaded #27 showed Maple Court, 302 Cedar Rd, Unit 3C, tenant Gray Chen, enabled `Create Lease`, and confirmed to `/leases/2`. | Fixed |
| TSK397-B010 | P1 | Scan lease review | Lease drafts extracted by the shared engine with `unit_number` but no `unit_id` could propose linking an existing unit while the unit selector still stayed blank. That disabled `Create Lease` even though the UI said it would link to that unit. | Browser red: draft #38 listed `Link to Harbor View Homes` and `Link to Unit 2B`, but the unit selector was blank and confirm was disabled. Green: `seedLeaseUnitId` regression added; `pnpm --dir web test:unit -- src/lib/scans/lease-review-state.test.ts` passed 44/44; browser retest after restart showed Unit 2B selected and `Create Lease` enabled. | Fixed |
| TSK397-B011 | P1 | Scan domain errors | When a scan-confirm hit safe domain validation, such as an overlapping active lease for the same unit, the scan API returned a generic `Lease creation failed` message. Users could not tell whether to reject, end the old lease, or edit dates. | Browser/API red: draft #38 threw `DomainValidationException` for overlapping `QA-2026-006-2B` but showed generic failure. Red/green: `ConfirmAndCreateAsync_LeaseDomainValidation_ReturnsSpecificUserMessage` failed, then passed; full `ScanServiceTests` passed 15/15. Browser retest: confirm returned HTTP 400 with the overlap message and the toast displayed the same actionable text. | Fixed |
| TSK397-B012 | P1 | Camera lease wizard | `/scan/new-rental` used the shared extraction engine for camera JPEGs and linked an existing property, but the Unit step still defaulted to `Create new from the lease` even when the selected property already had an exact matching unit. A user could create duplicate units from duplicate photos. | Browser red: camera draft #41 extracted Cedar Point Flats / Unit 1A, the dropdown contained `Unit 1A (Occupied)`, but the selected value was `Create new from the lease`. Green: `findNewRentalExistingUnitId` regression added; draft #44 reached review with `Property: Cedar Point Flats (existing)` and `Unit: Unit 1A (existing)`. | Fixed |
| TSK397-B013 | P1 | Camera lease wizard | A camera-derived lease with no late fee could not advance from the Lease step because `lateFeeAmount` is required by the shared lease schema, but the field stayed blank and the shared term component did not render a late-fee error. | Browser red: draft #43 stayed on Step 4 after `Next` with no visible validation message; late fee was blank. Green: `seedNewRentalLateFeeAmount` defaults missing values to `0`, `LeaseTermFields` renders `lateFeeAmount` errors, focused web unit tests passed 48/48, `web check` passed, and draft #44 advanced to review. | Fixed |
| TSK397-B014 | P2 | Application scan dedupe | Fixed in this branch. Scan-created applications now block a second non-terminal application in the same portfolio with the same applicant email, ignoring trim/case, while allowing terminal `Declined`/`Withdrawn` applications to re-apply. | Browser red: PDF draft #50 created application #1; camera draft #51 from the same synthetic application created application #2. `/applications` showed two `Gray Johnson` rows with `qa.applicant.001@example.local`, both `Submitted`. Green: `CreateFromScanAsync_OpenApplicationWithSameEmail_ThrowsAndDoesNotDuplicate` and `CreateFromScanAsync_TerminalApplicationWithSameEmail_CreatesNewApplication`; focused `ApplicationServiceTests` passed 10/10; full `RentalCommand.Api.Tests` passed 447/447. Existing local duplicate rows were left intact as evidence, not rewritten. | Fixed |
| TSK397-B015 | P0 | DB-side data rule | Fixed in this branch. The original audit found reports/accounting/banking endpoints materializing rows and then filtering/grouping/sorting/aggregating/scoring in memory, violating the project hard rule. | Read-only data sweep found definite violations in `ReportsService`, `AccountingService`, and `BankingService`, including general ledger, property P&L, rent ledger, banking suggestions, accounting reports, and reconciliation suggestions. Fixed slices: property P&L regression `GetPropertyProfitAndLossAsync_FiltersGroupsAndSumsInSql`; general ledger regression `GetGeneralLedgerAsync_FiltersOrdersAndTotalsInSql`; rent ledger regression `GetRentLedgerAsync_FiltersOrdersAndTotalsInSql`; cash-flow totals regression `GetCashFlowAsync_TotalsAreSummedInSql`; delinquency regression `GetDelinquencyAsync_OrdersAndTotalsInSql`; work-order regression `GetWorkOrdersAsync_CountsAndSumsInSql`; banking review candidate regression `ReviewQueue_PrefiltersPaymentSuggestionCandidatesInSql`; accounting inline suggestion regression `GetTransactionsAsync_PrefiltersInlineBankSuggestionsInSql`; accounting reports regression `GetReportsAsync_BuildsLedgerWithSqlUnionAndOrdering`; owner statement SQL-shape regressions in `OwnerStatementServiceTests`; Schedule E SQL-shape assertions in `ScheduleEServiceTests`. Full `ReportsServiceTests` passed 35/35; full `BankingServiceTests` passed 16/16; `AccountingTransactionsViewTests` passed 9/9; `AccountingServiceTests` passed 7/7; `OwnerStatementServiceTests` passed 2/2; `ScheduleEServiceTests` passed 2/2. No B015 sub-items remain from the sweep. | Fixed |
| TSK397-B016 | P1 | Inventory completeness | Fixed in this branch. The inventory script now emits a classified acceptance matrix and route inventory instead of a raw tag-count table. | Read-only route/control sweep found missing coverage for public apply/sign flows, settings subroutes, accounting/report detail routes, maintenance/detail workflows, portal helper role states, admin modals, and file proxy routes. Green: `node scripts/qa/inventory-web-surfaces.mjs` produced 85 classified route rows, 12 surface groups, and `Unclassified routes: none` in `output/qa/web-surface-inventory.md`/`.json`. | Fixed |

## Regression Expectations

- Add focused tests for each coherent fix.
- For DB-side report/accounting fixes, tests must include unrelated out-of-range and cross-property data that would incorrectly affect results if filtering happens after materialization.
- For scan batch counts, a regression must assert count correctness without requiring all draft rows to be loaded.
- For scan matching, regressions must prove lookup candidate selection is DB-side when scale matters.
- For browser proof, evidence must include screenshots or traces for registration, empty live setup, lease batch upload/review, `/scan/new-rental` photo upload, generic JPEG upload for at least one dependent document, at least one confirmed dependent document, and representative route inventory after scan-created data exists.
