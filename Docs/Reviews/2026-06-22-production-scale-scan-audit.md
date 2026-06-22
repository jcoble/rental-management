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

## Bug Log

| ID | Severity | Area | Finding | Evidence | Status |
| --- | --- | --- | --- | --- | --- |
| TSK397-B001 | P0 | Audit recovery | Previous uncommitted TSK-397 worktree disappeared before it was committed. Recreated compliant worktree from `main` and rebuilt the audit tooling slice first. | `git worktree list` showed no TSK-397 worktree; branch recreated at `b6d80c2`; tooling slice committed as `52e4968`. | Recovered |
| TSK397-B002 | P1 | Local stack | `scripts/start-dev.sh` could reuse an existing Postgres volume/container without ensuring the configured `PG_DB` existed. The API exited with `3D000: database "rentalcommand_tsk397" does not exist` while the web server still started and produced API connection errors. | Red: first TSK-397 startup failed in `/tmp/rentalcommand-api.log`. Green: restart printed `Creating database 'rentalcommand_tsk397'...`; `curl -ks https://localhost:5696/health` returned `{"status":"ok"}`; listeners were present on 5696 and 5697. | Fixed |
| TSK397-B005 | P1 | Scan batch detail | `/api/v1/scans/batches/{id}` displayed correct counts only because it folded loaded draft rows in memory. This violated the DB-side aggregation rule and would scale poorly for production-sized review queues. | Red: `dotnet test ... --filter "FullyQualifiedName~ScanBatchControllerTests.GetBatch_ComputesCountsWithGroupedSql"` failed with no `GROUP BY`/`COUNT` SQL. Green after fix: focused test passed; full `ScanBatchControllerTests` passed 8/8. | Fixed |

## Regression Expectations

- Add focused tests for each coherent fix.
- For DB-side report/accounting fixes, tests must include unrelated out-of-range and cross-property data that would incorrectly affect results if filtering happens after materialization.
- For scan batch counts, a regression must assert count correctness without requiring all draft rows to be loaded.
- For scan matching, regressions must prove lookup candidate selection is DB-side when scale matters.
- For browser proof, evidence must include screenshots or traces for registration, empty live setup, lease batch upload/review, `/scan/new-rental` photo upload, generic JPEG upload for at least one dependent document, at least one confirmed dependent document, and representative route inventory after scan-created data exists.
