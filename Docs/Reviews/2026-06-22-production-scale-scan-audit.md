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

## Pass 18 Continuation Evidence

Date: 2026-06-23
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-18`
Local stack: `https://localhost:5962`, API `https://localhost:5961`, Postgres `localhost:5566/rentalcommand_tsk397_pass17_clean`

Fresh local landlord account:

- Name: Noah Carter
- Email: `tsk397.pass17.202606230455@example.local`
- Setup choice: live portfolio from zero domain data, not sandbox/demo

Additional scan-created data confirmed in this checkpoint:

- Application camera image: `output/qa/production-scale-scans/04-applications-camera/application-001.jpg`
- Work order camera image: `output/qa/production-scale-scans/05-work-orders-camera/work-order-001.jpg`

Verified local database counts after browser confirmation:

- 1 property, 1 unit, 1 tenant, 1 lease, 1 expense, 1 payment, 1 rental application, 1 work order, 5 scan drafts.

Browser states exercised:

- `/scan` application camera upload to `/scan/4?type=Application`, review/confirm to `/applications`, and populated application row for Gray Johnson.
- `/scan` maintenance-request camera upload to `/scan/5?type=WorkOrder`, review/confirm to `/maintenance/1`.
- `/maintenance/1` status modals, edit fields, vendor text disabled state for a vendor without a phone, document upload, document download, and unit cross-link to `/units/1?tab=maintenance`.
- `/units/1` command-center overview, lease tab, lease detail agreement/download/regenerate controls, ledger opening-balance validation, rent tab payment validation, maintenance tab new-work-order validation, documents tab file links, and scan shortcuts from rent/maintenance/documents/expenses tabs.

Bug fixed during this pass:

- Unit Documents grouped several stored files by parent entity, but linked every lease file to `/lease-file/{leaseId}` and every work-order file to `/workorder-file/{workOrderId}`. After a generated lease agreement or extra work-order attachment existed, rows for the original scan opened the wrong parent-level file.
- Fix: Unit document rows now link by stored file id through `/document-file/{id}`, a same-origin authenticated proxy to `GET /documents/{id}/file`. The proxy preserves safe inline rendering for images/PDFs and forces unsafe content to download.
- Browser/API proof after fix: Unit Documents showed distinct links for `lease-1-agreement.pdf` (`/document-file/11`), original lease scan (`/document-file/1`), payment scan (`/document-file/4`), uploaded work-order image (`/document-file/10`), and original work-order scan (`/document-file/8`). Authenticated fetches returned `200` with the expected MIME types and byte sizes for all five stored-file IDs.
- Regression coverage: `web/src/lib/components/unit/document-actions.test.ts`; inventory classifier now includes `/document-file/[id]` in the file-proxy surface group.
- Verification commands: `pnpm --dir web exec node --test --experimental-strip-types src/lib/components/unit/document-actions.test.ts`; `node scripts/qa/inventory-web-surfaces.mjs`.

## Pass 19 Continuation Evidence

Date: 2026-06-23
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-19`
Local stack: `https://localhost:5972`, API `https://localhost:5971`, Postgres `localhost:5567/rentalcommand_tsk397_pass19_clean`

Fresh local landlord account:

- Name: Ivy Morgan
- Email: `tsk397.pass19.202606230604@example.local`
- Setup choice: live portfolio from zero domain data, not sandbox/demo

Camera-style scan fixtures confirmed in this pass:

- Lease image: `output/qa/production-scale-scans/01-leases-camera/lease-001-1a.jpg`
- Expense image: `output/qa/production-scale-scans/02-expenses-camera/expense-001.jpg`
- Payment image: `output/qa/production-scale-scans/03-payments-camera/payment-001.jpg`
- Application image: `output/qa/production-scale-scans/04-applications-camera/application-001.jpg`
- Work order image: `output/qa/production-scale-scans/05-work-orders-camera/work-order-001.jpg`

Verified local database counts after browser confirmation:

- 1 property, 1 unit, 1 tenant, 1 lease, 1 payment, 2 expenses, 1 rental application, 1 work order, 6 scan drafts.
- The second expense was created after the B018 fix as a real browser regression proof. The first expense remains the pre-fix evidence row with `UnitId` null.

Browser states exercised:

- `/register`, local email verification, `/login`, `/choose-setup`, `/welcome`, and the live empty portfolio setup path.
- `/scan/new-rental` camera upload through property/unit/tenant/lease/review confirmation, including manual correction of missing beds/baths before create.
- `/scan/[draftId]` target-specific review for Expense, Payment, Application, and WorkOrder camera JPEGs.
- `/maintenance/1` source document image download via the file proxy; the downloaded file was verified as a real 1800x2400 JPEG.
- `/units/1` command-center overview, edit validation, lifecycle rail display, lease tab, rent tab payment validation, maintenance tab work-order validation, documents tab file proxy links, expenses tab, recent activity links, and scan shortcuts.

Bug fixed during this pass:

- Generic receipt scan confirmation persisted the selected `PropertyId` but left `UnitId` null when the extracted notes contained the unit reference and the review form had no unit selector. The Unit Expenses tab was filtering correctly; the scan-created expense was not grounded to the unit.
- Fix: `ScanService` now resolves finite unit references from expense notes (`Unit 1A`, `Unit: 1A`, `Apt #2B`, `Apartment 12-B`) only when the reviewer selected a property. It then performs one DB-side exact unit lookup under that property and portfolio. Explicit reviewer `unitId` overrides still win.
- Red/green regression: `ConfirmAndCreateAsync_ExpenseDraft_WithSelectedPropertyAndUnitInNotes_GroundsUnit` failed with `Expected UnitId to be 20, but found <null>`, then passed after the fix.
- Browser proof after restart: scanned `expense-001.jpg` from generic `/scan`, selected only `Cedar Point Flats`, confirmed expense #2, and verified `Expenses.Id=2` has `PropertyId=1`, `UnitId=1`. `/units/1?tab=expenses` then showed `Green Thumb Landscaping - Repairs & maintenance - Paid - $63.75` and recent activity linked to `/accounting/expenses/2`.
- Verification commands: `dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~ScanServiceTests.ConfirmAndCreateAsync_ExpenseDraft_WithSelectedPropertyAndUnitInNotes_GroundsUnit"`; `dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~ScanServiceTests"`.

Deferred findings captured outside this fix:

- Unit Command Center `Send renewal` no-op is tracked as TSK-400 and was not fixed in this pass.

## Pass 20 Continuation Evidence

Date: 2026-06-23
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-20`
Local stack: `https://localhost:5982`, API `https://localhost:5981`, Postgres `localhost:5568/rentalcommand_tsk397_pass20_clean`

Fresh local landlord account:

- Name: Olivia Hart
- Email: `tsk397.pass20.202606230650@example.local`
- Setup choice: live portfolio from zero domain data, not sandbox/demo

Camera-style scan fixtures confirmed in this pass:

- Lease image: `output/qa/production-scale-scans/01-leases-camera/lease-001-1a.jpg`
- Expense image: `output/qa/production-scale-scans/02-expenses-camera/expense-001.jpg`
- Payment image: `output/qa/production-scale-scans/03-payments-camera/payment-001.jpg`
- Application image: `output/qa/production-scale-scans/04-applications-camera/application-001.jpg`
- Work order image: `output/qa/production-scale-scans/05-work-orders-camera/work-order-001.jpg`

Verified local database counts after browser confirmation:

- 1 property, 1 unit, 1 tenant, 1 lease, 1 payment, 1 expense, 1 rental application, 1 work order, 5 scan drafts.

Browser states exercised:

- `/register`, local email verification, `/login`, `/choose-setup`, and the live empty portfolio setup path.
- `/scan/new-rental` camera upload through property/unit/tenant/lease/review confirmation, including manual correction of missing beds/baths before create.
- `/scan/2?type=Expense` generic receipt review, selected only `Cedar Point Flats`, confirmed to `/accounting/expenses/1`, and verified `Expenses.Id=1` has `PropertyId=1`, `UnitId=1`.
- `/units/1?tab=expenses` showed the scanned Green Thumb Landscaping expense, and `/accounting/expenses/1` now shows `Unit 1A` in Categorization & references.
- `/scan/3?type=Payment` camera rent-check review, manual lease selection, confirmation to `/accounting/payments/1`, and `/units/1?tab=rent` showing the paid rent row.
- `/scan/4?type=Application` camera application review/confirm to `/applications/1`, with `RentalApplications.PropertyId=1` and `UnitId=1`.
- `/scan/5?type=WorkOrder` camera maintenance request review/confirm to `/maintenance/1`, with raw internal linkage fields still hidden and `WorkOrders.PropertyId=1`, `UnitId=1`, `TenantId=1`.
- `/units/1?tab=maintenance` showed the scanned work order and recent activity.

Bug fixed during this pass:

- Expense detail pages hid the unit reference even when the expense was unit-grounded by the scan flow. The API already returned `unitId` and `unitNumber`; the page rendered only property and vendor in the reference card.
- Fix: expense detail now renders a `Unit` reference row and links unit-grounded expenses to `/units/{id}?tab=expenses`.
- Regression coverage: `web/src/lib/accounting/expense-detail-actions.test.ts`.
- Browser proof after fix: `/accounting/expenses/1` showed `Unit 1A` in the `Categorization & references` card and linked it to `/units/1?tab=expenses`.
- Verification commands: `pnpm --dir web exec node --test --experimental-strip-types src/lib/accounting/expense-detail-actions.test.ts`; `pnpm --dir web check`; `pnpm --dir web test:unit`.

Deferred findings captured outside this fix:

- Unit Command Center `Send renewal` no-op is tracked as TSK-400; the user provided an FYI screenshot, which was attached to the existing task. It was not fixed in this pass.

## Pass 21 Continuation Evidence

Date: 2026-06-23
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-21`
Local stack: `https://localhost:5992`, API `https://localhost:5991`, Postgres `localhost:5569/rentalcommand_tsk397_pass21_clean`

Fresh local landlord account:

- Name: Maya Patel
- Email: `tsk397.pass21.202606230721@example.local`
- Setup choice: live portfolio from zero domain data, not sandbox/demo

Camera-style scan fixtures confirmed in this pass:

- Lease image: `output/qa/production-scale-scans/01-leases-camera/lease-001-1a.jpg`
- Expense image: `output/qa/production-scale-scans/02-expenses-camera/expense-001.jpg`
- Payment image: `output/qa/production-scale-scans/03-payments-camera/payment-001.jpg`
- Application image: `output/qa/production-scale-scans/04-applications-camera/application-001.jpg`
- Work order image: `output/qa/production-scale-scans/05-work-orders-camera/work-order-001.jpg`

Verified local database counts after browser confirmation:

- 1 property, 1 unit, 1 tenant, 1 lease, 1 payment, 1 expense, 1 rental application, 1 work order, 5 scan drafts.

Browser states exercised:

- `/register`, local email verification, `/login`, `/choose-setup`, and the live empty portfolio setup path.
- `/scan/new-rental` camera upload through property/unit/tenant/lease/review confirmation, including manual correction of missing beds/baths before create.
- `/leases/1` overview links, scanned source link, Agreement & Signing tab download/regenerate, Ledger tab opening-balance validation/save, History tab, edit/save, Give Notice modal cancel, and Delete confirmation cancel.
- `/scan/2?type=Expense` generic receipt image review, manual property/category selection, confirmation to `/accounting/expenses/1`, scanned-image preview, receipt details, unit reference, and history.
- `/scan/3?type=Payment` rent-check image review, manual lease selection, confirmation to `/accounting/payments/1`, scanned-image preview, lease link, check reference, and history.
- `/scan/4?type=Application` application image review/confirm to `/applications`, applicant list row, `/applications/1` detail, requested-home parsing, scanned-image preview, consent/screening disabled state, and decision buttons.
- `/scan/5?type=WorkOrder` maintenance image review, property/category confirmation, `/maintenance/1` detail, unit link, status buttons, attachment list, scanned-image document, and history.
- `/units/1` overview, lifecycle rail, lease/rent/maintenance/documents/expenses/timeline tabs, manual post-payment inline form cancel, unit-level scan shortcut with preserved `propertyId`, `unitId`, and `returnTo`, document-file proxy image proof (`/document-file/9` rendered 1800x2400), and recent activity links.
- `/scan` confirmed-draft table showing all five image-created drafts with view-record links.
- `/accounting` ledger cards, filters, payment/expense rows, receipt thumbnail, and transaction actions.
- `/reports`, `/reports/income-expense-statement`, report actions menu, CSV export download, and exported P&L values matching the on-screen February totals.

Bug fixed during this pass:

- Lease detail edit mode reused fresh empty `['properties', portfolioId]` and `['tenants', portfolioId]` lookup caches that were populated on the scan review page before an empty-portfolio lease confirm created the first property and tenant. The overview still displayed linked names from `/leases/1`, but the edit selectors rendered blank and their dropdowns contained only `Select property` / `Select tenant`.
- Fix: scan-confirm success now invalidates the cache families for the created entity type. Lease confirmations invalidate scans, leases, properties, tenants, units, units-for-lease, and dashboard queries before navigating to the new lease detail page.
- Regression coverage: `web/src/lib/scans/scan-confirm-invalidation.test.ts`.
- Browser proof after fix: after reloading `/leases/1`, edit mode showed `Cedar Point Flats`, `Unit 1A (Occupied)`, and `Avery Ellis`; the property dropdown contained `Cedar Point Flats`, and the tenant dropdown contained `Avery Ellis`.
- Artifacts: red `.playwright-cli/page-2026-06-23T07-34-06-150Z.yml`; green `.playwright-cli/page-2026-06-23T07-37-11-464Z.yml` and `.playwright-cli/page-2026-06-23T07-37-52-200Z.yml`.
- Verification commands: `node --test --experimental-strip-types web/src/lib/scans/scan-confirm-invalidation.test.ts`; `pnpm --dir web test:unit`; `pnpm --dir web check`.

Additional pass 21 evidence artifacts:

- Lease edit/save after B020: `.playwright-cli/page-2026-06-23T07-41-38-074Z.yml`.
- Lease Give Notice modal: `.playwright-cli/page-2026-06-23T07-41-53-161Z.yml`.
- Lease delete confirmation: `.playwright-cli/page-2026-06-23T07-42-16-660Z.yml`.
- Expense review/confirmed/detail: `.playwright-cli/page-2026-06-23T07-43-59-127Z.yml`, `.playwright-cli/page-2026-06-23T07-44-33-645Z.yml`, `.playwright-cli/page-2026-06-23T07-44-59-162Z.yml`.
- Payment review/confirmed/detail: `.playwright-cli/page-2026-06-23T07-46-02-557Z.yml`, `.playwright-cli/page-2026-06-23T07-46-19-543Z.yml`, `.playwright-cli/page-2026-06-23T07-46-44-664Z.yml`.
- Application processing/list/detail: `.playwright-cli/page-2026-06-23T07-47-15-158Z.yml`, `.playwright-cli/page-2026-06-23T07-48-07-710Z.yml`, `.playwright-cli/page-2026-06-23T07-48-27-997Z.yml`.
- Work-order review/confirmed/detail: `.playwright-cli/page-2026-06-23T07-49-46-353Z.yml`, `.playwright-cli/page-2026-06-23T07-50-06-728Z.yml`, `.playwright-cli/page-2026-06-23T07-50-29-585Z.yml`.
- Unit tab sweep: `.playwright-cli/page-2026-06-23T07-50-49-843Z.yml`, `.playwright-cli/page-2026-06-23T07-51-02-733Z.yml`, `.playwright-cli/page-2026-06-23T07-51-16-005Z.yml`, `.playwright-cli/page-2026-06-23T07-51-33-085Z.yml`, `.playwright-cli/page-2026-06-23T07-52-00-686Z.yml`, `.playwright-cli/page-2026-06-23T07-52-18-139Z.yml`, `.playwright-cli/page-2026-06-23T07-52-58-218Z.yml`, `.playwright-cli/page-2026-06-23T07-53-15-780Z.yml`.
- Unit document-file proof: `/document-file/9` rendered a complete image with `naturalWidth=1800` and `naturalHeight=2400`.
- Scan list, Money ledger, and P&L report: `.playwright-cli/page-2026-06-23T07-53-54-176Z.yml`, `.playwright-cli/page-2026-06-23T07-54-13-327Z.yml`, `.playwright-cli/page-2026-06-23T07-54-48-323Z.yml`, `.playwright-cli/page-2026-06-23T07-55-01-461Z.yml`, `.playwright-cli/page-2026-06-23T07-55-12-260Z.yml`, CSV `.playwright-cli/income-expense-statement-2026-06-23.csv`.

Deferred findings captured outside this fix:

- Unit Command Center `Send renewal` no-op remains tracked as TSK-400; the user explicitly said not to fix it in this slice.
- Scan review can still require manual category/relationship confirmation even when extracted notes contain a likely match: the expense review required selecting `Cedar Point Flats` and `Repairs & maintenance`, the payment review required selecting lease `QA-2026-001-1A`, and the work-order review exposed accounting-style categories before creating a maintenance record. These did not block record creation because the review UI made the required choices available, but they remain product-fit candidates for a later matching/taxonomy pass.

## Pass 22 Continuation Evidence

Date: 2026-06-23
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-22`
Local stack: `https://localhost:6002`, API `https://localhost:6001`, Postgres `localhost:5570/rentalcommand_tsk397_pass22_clean`

Fresh local landlord account:

- Name: Sofia Reed
- Email: `tsk397.pass22.202606230810@example.local`
- Setup choice: live portfolio from zero domain data, not sandbox/demo

Camera-style scan fixtures confirmed in this pass:

- Fixture generator: `scripts/qa/generate-production-scale-scan-fixtures.py`
- Fixture output: `output/qa/production-scale-scans/` with 480 generated PDFs/images.
- Lease image: `output/qa/production-scale-scans/01-leases-camera/lease-001-1a.jpg`
- Expense image: `output/qa/production-scale-scans/02-expenses-camera/expense-001.jpg`
- Payment image: `output/qa/production-scale-scans/03-payments-camera/payment-001.jpg`
- Application image: `output/qa/production-scale-scans/04-applications-camera/application-001.jpg`
- Work order image: `output/qa/production-scale-scans/05-work-orders-camera/work-order-001.jpg`

Verified local database counts:

- Before scans: 0 properties, 0 units, 0 tenants, 0 leases, 0 payments, 0 expenses, 0 rental applications, 0 work orders, 0 scan drafts.
- After browser confirmation: 1 property, 1 unit, 1 tenant, 1 lease, 1 payment, 1 expense, 1 rental application, 1 work order, 5 scan drafts.

Browser states exercised:

- `/register`, local email verification, `/login`, `/choose-setup`, and live empty portfolio setup.
- `/scan/new-rental` camera lease upload through property/unit/tenant/lease/review confirmation for Cedar Point Flats, Unit 1A, Avery Ellis, and lease `QA-2026-001-1A`.
- `/scan/[draftId]` target-specific review for Expense, Payment, Application, and WorkOrder camera JPEGs.
- `/dashboard`, `/properties`, `/properties/1`, `/units/1`, and every Unit tab: Overview, Lease, Rent, Maintenance, Documents, Expenses, and Timeline.
- Work order detail `/maintenance/1`: attachment image, vendor-text modal disabled state for no vendor phone, status change modals, status notes, and timeline/status-history updates from New -> Scheduled -> In progress.
- Unit Documents tab file proxy proof: `/document-file/8` opened the scan-created work-order image and rendered a single 1800x2400 image.

Bug fixed during this pass:

- Unit Timeline expanded audit diffs displayed numeric enum values for work-order status changes (`Status 1 -> 2`) because the generic audit interceptor stored enum scalars as JSON numbers and the landlord-facing `AuditDiffBuilder` formatted those numbers without entity/property context.
- Fix: `AuditDiffBuilder` now builds a one-time map of enum-valued Core entity properties and formats both numeric JSON values and enum-name strings into user-facing labels before the generic number/date/string fallback.
- Regression coverage: `Build_Updated_FormatsNumericEnumAuditValuesForKnownEntityFields` in `RentalCommand.Api.Tests/Domain/AuditDiffBuilderTests.cs`.
- Browser proof after API restart: `/units/1?tab=timeline`, expanded first `Updated work order` row, now shows `Status Scheduled -> In progress` instead of `1 -> 2`.
- Verification commands: `dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~AuditDiffBuilderTests"`; `dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~AuditDiffBuilderTests|FullyQualifiedName~AuditTrailTests|FullyQualifiedName~UnitDashboardServiceTests"` passed 23/23. Restore/build emitted existing package vulnerability warnings for `SQLitePCLRaw.lib.e_sqlite3` and `MailKit`.

Deferred findings captured outside this fix:

- Unit Command Center `Send renewal` no-op remains tracked as TSK-400; the user explicitly said not to fix it in this slice.
- Unit lifecycle `List this unit` no-op remains tracked as TSK-401 and was not fixed in this slice.
- Rent tab payment type menu still exposes raw enum-ish labels such as `SecurityDeposit` and `LateFee`.
- Work-order scan category review still uses accounting-style categories before creating a maintenance record.

## Pass 23 Continuation Evidence

Date: 2026-06-23
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-23`
Local stack: `https://localhost:6012`, API `https://localhost:6011`, Postgres `localhost:5571/rentalcommand_tsk397_pass23_clean`

Fresh local landlord account:

- Name: Jordan Blake
- Email: `tsk397.pass23.202606230850@example.local`
- Setup choice: live portfolio from zero domain data, not sandbox/demo

Camera-style scan fixtures confirmed in this pass:

- Lease image: `output/qa/production-scale-scans/01-leases-camera/lease-001-1a.jpg`
- Expense image: `output/qa/production-scale-scans/02-expenses-camera/expense-001.jpg`
- Payment image: `output/qa/production-scale-scans/03-payments-camera/payment-001.jpg`
- Application image: `output/qa/production-scale-scans/04-applications-camera/application-001.jpg`
- Work order image: `output/qa/production-scale-scans/05-work-orders-camera/work-order-001.jpg`

Verified local database counts:

- Before scans: no domain data in the live portfolio.
- After browser confirmation: 1 property, 1 unit, 1 tenant, 1 lease, 1 payment, 1 expense, 1 rental application, 1 work order, 7 scan drafts.
- Drafts 1-5 are confirmed Lease, Expense, Payment, Application, and WorkOrder scans. Draft 6 is an unconfirmed Lease review draft used for guided-flow resume proof. Draft 7 is an unconfirmed WorkOrder review draft used for category-option proof.

Browser states exercised:

- `/scan/new-rental` camera lease upload from the empty portfolio to Cedar Point Flats, Unit 1A, Avery Ellis, and lease `QA-2026-001-1A`.
- `/leases/1`, scanned document proxy preview, `/accounting/expenses/1`, `/accounting/payments/1`, `/applications/1`, `/maintenance/1`, and `/units/1?tab=maintenance` for records created from image scans.
- `/scan/new-rental?draftId=6` upload/reload proof: the URL stayed draft-specific and returned to the guided stepper instead of the blank capture screen. Screenshot: `output/playwright/pass23-new-rental-resume-after-fix-final.png`.
- `/scan/6?type=Lease` generic recovery proof: switching the property selector to create-new showed editable `unit_bedrooms`, `unit_bathrooms`, and `unit_square_feet` inputs. Screenshot: `output/playwright/pass23-generic-lease-unit-fields-after-fix.png`.
- `/scan/7?type=WorkOrder` maintenance-request review proof: category options were General, Plumbing, Electrical, HVAC, Appliance, Repairs, Roofing, Pest, Safety, Landscaping, Cleaning, and Other; accounting-only categories such as Mortgage interest were absent. Screenshot: `output/playwright/pass23-workorder-category-options-after-fix.png`.

Bug fixes during this checkpoint:

- `/scan/new-rental` did not persist the newly uploaded draft id in the URL, so refresh/reopen dropped the landlord back to the capture screen even while the draft was still processing/reviewable.
- The generic lease recovery route could create a new unit from a lease scan but exposed only Unit number, leaving no visible way to correct bedrooms, bathrooms, or square feet before confirmation.
- Work-order scan category review reused Schedule E expense categories, including accounting/tax categories such as Mortgage interest, before creating a maintenance record.

Fixes:

- Guided new-rental uploads now replace the URL with `/scan/new-rental?draftId={id}` and initialize/resume processing state from that query parameter.
- Generic lease review now exposes editable unit detail override fields using the backend-supported `unit_bedrooms`, `unit_bathrooms`, and `unit_square_feet` keys.
- Scan review category options are target-specific: expenses retain Schedule E categories, while work orders use maintenance categories.

Regression coverage and verification:

- `pnpm --dir web exec node --test --experimental-strip-types src/lib/scan/new-rental-state.test.ts`
- `pnpm --dir web exec node --test --experimental-strip-types src/lib/scans/lease-review-state.test.ts`
- `pnpm --dir web exec node --test --experimental-strip-types src/lib/scans/scan-review-fields.test.ts`
- `pnpm --dir web check` passed with 0 errors and the pre-existing PageHeader unused CSS selector warnings.

Deferred findings captured outside this fix:

- Unit Command Center `Send renewal` no-op is tracked as TSK-404; the user explicitly said not to fix it now.
- Rent tab payment type menu still exposes raw enum-ish labels such as `SecurityDeposit` and `LateFee`.

## Classified Inventory Matrix

The route/control inventory now has a finite closure matrix instead of a raw tag-count table. `scripts/qa/inventory-web-surfaces.mjs` includes layout guards, error surfaces, redirects, server routes, file proxies, custom component controls, `data-testid` coverage, route scopes, route kinds, roles, acceptance criteria, and finite edge cases.

Latest output:

- `output/qa/web-surface-inventory.json`
- `output/qa/web-surface-inventory.md`
- Route count: 87
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
| TSK397-B017 | P1 | Unit documents | Fixed in this branch. Unit Documents linked rows by parent entity route instead of stored file id, so multiple files attached to the same lease or work order could open the same parent-level file rather than the clicked file. | Browser red: generated lease agreement and original lease scan both pointed at `/lease-file/1`; original work-order scan and uploaded work-order image both pointed at `/workorder-file/1`. Green: rows now point to `/document-file/{storedFileId}`; authenticated fetches for ids 1, 4, 8, 10, and 11 returned the expected original PDFs/images/agreement. Focused `unit-document-actions` test passed and inventory classified 87 routes with no unclassified routes. | Fixed |
| TSK397-B018 | P1 | Expense scan unit grounding | Fixed in this branch. Generic receipt scans could capture a unit reference in notes but persist only `PropertyId`, leaving `UnitId` null and making the Unit Expenses tab look empty. | Browser red: first camera receipt scan created expense #1 with `PropertyId=1`, `UnitId=NULL`, and `/units/1?tab=expenses` showed no expenses. Red/green: `ConfirmAndCreateAsync_ExpenseDraft_WithSelectedPropertyAndUnitInNotes_GroundsUnit` failed, then passed. Browser green: second generic camera receipt scan selected only `Cedar Point Flats`, created expense #2 with `UnitId=1`, and the Unit Expenses tab displayed the paid Green Thumb Landscaping expense. | Fixed |
| TSK397-B019 | P2 | Expense detail references | Fixed in this branch. Unit-grounded expenses showed Property and Vendor on the expense detail page but omitted Unit, forcing users to infer the unit from notes or navigate through the unit Expenses tab. | Browser red in pass 20: `/accounting/expenses/1` for a scan-created receipt had `Expenses.UnitId=1` and appeared in `/units/1?tab=expenses`, but the detail reference card lacked `Unit`. Green: `formatExpenseUnitReference` regression passed, `/accounting/expenses/1` now shows `Unit 1A` linked to `/units/1?tab=expenses`, `pnpm --dir web check` passed with only pre-existing PageHeader warnings, and `pnpm --dir web test:unit` passed 129/129. | Fixed |
| TSK397-B020 | P1 | Lease scan cache invalidation | Fixed in this branch. Empty-portfolio lease scan confirmation left pre-confirm empty property/tenant lookup caches fresh, so the new lease detail page's edit selectors showed blank property and tenant values even though `/leases/1`, `/properties`, and `/tenants` all had the records. | Browser red: `/leases/1` edit mode showed blank Property and Tenant triggers and dropdowns with only `Select property` / `Select tenant`. Root cause: scan confirm invalidated only `['leases']`. Green: `invalidateQueriesAfterScanConfirm` regression passed; browser edit mode showed `Cedar Point Flats` and `Avery Ellis`, and both dropdowns contained the scan-created records. Full `pnpm --dir web test:unit` passed 130/130; `pnpm --dir web check` passed with only pre-existing PageHeader warnings. | Fixed |
| TSK397-B021 | P2 | Unit timeline audit diff | Fixed in this branch. Expanded unit timeline audit rows leaked numeric enum values for work-order status transitions instead of user-facing labels. | Browser red in pass 22: `/units/1?tab=timeline`, expand first `Updated work order`, showed `Status 1 -> 2`. Root cause: generic audit JSON stored enum scalars as numbers and `AuditDiffBuilder` lacked entity-property enum context. Green: `Build_Updated_FormatsNumericEnumAuditValuesForKnownEntityFields` passed; broader audit/unit-dashboard focused tests passed 23/23; browser retest showed `Status Scheduled -> In progress`. | Fixed |

## Regression Expectations

- Add focused tests for each coherent fix.
- For DB-side report/accounting fixes, tests must include unrelated out-of-range and cross-property data that would incorrectly affect results if filtering happens after materialization.
- For scan batch counts, a regression must assert count correctness without requiring all draft rows to be loaded.
- For scan matching, regressions must prove lookup candidate selection is DB-side when scale matters.
- For browser proof, evidence must include screenshots or traces for registration, empty live setup, lease batch upload/review, `/scan/new-rental` photo upload, generic JPEG upload for at least one dependent document, at least one confirmed dependent document, and representative route inventory after scan-created data exists.
