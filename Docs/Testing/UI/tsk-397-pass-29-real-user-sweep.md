# TSK-397 Pass 29 Real-User Sweep

Date: 2026-06-25

Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-real-user-pass-29`

Branch: `tsk-397-real-user-pass-29`

Stack:
- Web: `https://localhost:5707`
- API: `https://localhost:5706`
- Database: `rentalcommand_tsk397_pass29_clean` in local container `rentalcommand-tsk397-pass29-db` on port `5585`
- Seed: enabled for roles/default admin only; demo data disabled
- LLM: `claude-cli` / `sonnet`
- Upload base path: worktree `uploads/`

Baseline:
- API health returned `{"status":"ok"}`.
- Database started with one seeded admin user and zero properties, units, tenants, leases, and stored files.
- Engine acquired its advisory lock and started scan/rent/accounting/notice workers.
- No Playwright, remote-debugging, Node, Chrome, or Claude listeners were left from the old session before this stack was started.

## Acceptance Criteria

- A new landlord can start with no business data and create/manage a rental portfolio without relying on demo data.
- Document/photo scan flows accept PDFs and images, extract into editable drafts, and create the intended records only after user confirmation.
- Each user-facing route, role surface, list, grid, tab, modal, button, input, empty state, loading state, and primary workflow is either verified, fixed, or explicitly logged as blocked/deferred.
- All list filtering, sorting, paging, joining, grouping, and aggregation remain DB-side; any in-memory data operation discovered during implementation is treated as a defect.
- External production/sensitive/destructive actions are not performed without explicit user approval. QuickBooks and Plaid remain blocked until sandbox/login access is available.
- Evidence includes browser screenshots, reproduction steps for bugs, local verification, and regression tests for fixes.

## Coverage Log

| Area | Status | Evidence | Notes |
| --- | --- | --- | --- |
| Public welcome | Pass | Playwright snapshot `page-2026-06-25T21-09-18-680Z.yml` | Sign-in and registration entry points visible. |
| New user / empty portfolio bootstrap | Pass | Registered `avery.pass29.20260625@rentalcommand.local`; API health OK; SQL baseline zero business rows before scan | Selected live portfolio, not demo data. |
| Synthetic production-scale scan corpus | Pass | `output/qa/production-scale-scans/MANIFEST.txt`; rendered generated lease PDF locally; camera image visual check | 480 sanitized files: PDFs and camera-style images for leases, expenses, payments, applications, and work orders. |
| Lease camera image to rental spine | Pass after fix | `/scan/new-rental` uploaded `01-leases-camera/lease-001-1a.jpg`; created `/leases/1`; SQL verified one property/unit/tenant/lease and two stored files; draft `8` uploaded `01-leases-camera/lease-002-2b.jpg` and showed blank Beds/Baths inputs | Extraction used `claude-cli:sonnet` vision path. Missing bed/bath now stays blank for reviewer instead of defaulting to zero. |
| Scanned lease source document preview | Pass | `/lease-file/1` rendered the uploaded 1800x2400 image; only favicon 404 in console | Confirms image scans remain available after confirm. |
| Receipt camera image to expense | Pass | `/scan` uploaded `02-expenses-camera/expense-001.jpg`; created `/accounting/expenses/1`; SQL verified vendor, expense, receipt details, line items, stored files | Review matched Green Thumb Landscaping receipt, total `$63.75`, paid Visa `4242`. |
| Rent check camera image to payment | Pass | `/scan` uploaded `03-payments-camera/payment-001.jpg`; created `/accounting/payments/1`; SQL verified paid rent payment linked to lease `1` | Check number `8001`, bank `First QA Bank`, payer `Avery Ellis`. |
| Rental application camera image | Pass | `/scan` uploaded `04-applications-camera/application-001.jpg`; created `/applications/1`; SQL verified applicant, requested property/unit, extracted fields | Detail page showed approve/decline/withdraw actions and scanned application link. |
| Maintenance request camera image | Pass after fix | Initial `/scan` upload of `05-work-orders-camera/work-order-001.jpg` created `/maintenance/1` with `LeaseId` null; post-fix rerun draft `6` created `/maintenance/2`; SQL verified `WorkOrders.Id=2` has `LeaseId=1` | Root cause was scan grounding/schema excluding leases. Regression tests now require leases in worker grounding and schema contract. |

## Bugs And Fixes

| ID | Bug | Reproduction evidence | Fix | Regression |
| --- | --- | --- | --- | --- |
| P29-001 | `/scan/new-rental` converted missing lease-scan bed/bath values into real `0` values. | Lease camera fixture omitted bed/bath; review seeded Beds `0`, Baths `0`, requiring manual correction before confirm. Post-fix draft `8` showed empty Beds/Baths inputs and SQL showed empty extracted values. | Changed guided lease prefill to leave bed/bath blank unless extracted. | `pnpm --dir web test:unit -- src/lib/scan/new-rental-state.test.ts` |
| P29-002 | Work-order scan could not link a lease from a document that named the lease number. | Work order draft extracted property/unit/tenant but `lease_id` empty; confirmed row `/maintenance/1` had `LeaseId` null. Post-fix draft `6` extracted `lease_id=1` and confirmed `/maintenance/2` with `LeaseId=1`. | Added non-deleted leases to worker grounding JSON and updated work-order extraction schema to allow `lease_id` copied from `leases[].id`. | `dotnet test RentalCommand.Engine.Tests/RentalCommand.Engine.Tests.csproj --filter "FullyQualifiedName~ScanProcessingWorker"`; `dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~ScanServiceTests"` |

## Verification

- `MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Engine.Tests/RentalCommand.Engine.Tests.csproj --filter "FullyQualifiedName~ScanProcessingWorker"`: passed, 12 tests.
- `pnpm --dir web test:unit -- src/lib/scan/new-rental-state.test.ts`: passed, 269 tests.
- `MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~ScanServiceTests"`: passed, 25 tests.

## Blocked / Deferred

- QuickBooks/accounting provider OAuth: blocked until user provides sandbox/login access.
- Plaid sandbox/link flow: blocked until user provides sandbox access.
