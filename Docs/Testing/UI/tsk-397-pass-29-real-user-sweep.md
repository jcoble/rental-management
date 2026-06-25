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
- Continuation process check found only the active `pass29` Playwright daemon and its Chrome children; no stale Claude/Playwright sessions were killed.

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
| Work order detail | Pass | `/maintenance/2`; downloaded `.playwright-cli/scan-20260625213615.jpeg` and verified JPEG `1800x2400`; edit form opened/cancelled; vendor SMS dialog opened/cancelled; delete confirmations opened/cancelled | Vendor text action disabled because Green Thumb Landscaping has no phone on file. |
| Unit command center | Pass with finding | `/units/1`; Maintenance/Documents/Expenses/Timeline tabs exercised; associated lease/payment/expense/work-order files rendered; expense row expanded and edit/cancel worked | Unit header showed `6 docs` but `/units` list showed Docs `0`; logged as P29-003. |
| Expense detail | Pass | `/accounting/expenses/1`; edit/cancel, delete confirm/cancel, raw receipt JSON accordion, scanned image full-size link `/expense-file/1` | Full-size receipt opened as a raw `1800x2400` image. |
| Application detail/list | Pass | `/applications/1` and `/applications`; scanned application image `/application-file/1`; get-link modal, copy toast, search empty state, status filter | Screening button correctly disabled because applicant consent is required. |
| Properties | Pass with validation risk | `/properties`; New/Edit/Delete modals opened and cancelled; search/type/status filters present | Blank New Property save button enabled; included in P29-004 risk bucket. |
| Units list | Pass with finding | `/units`; search/property filter, sort headers, row actions visible | No New Unit button visible on the list; docs count mismatch logged as P29-003. |
| Tenants | Pass with validation risk | `/tenants`; New/Edit/Delete modals opened and cancelled; active-lease delete dialog disabled destructive delete | Blank New Tenant save button enabled; included in P29-004 risk bucket. |
| Leases | Pass with validation risk | `/leases`; New Lease modal opened/cancelled; search/status filters and sortable columns visible | Blank New Lease save button enabled; included in P29-004 risk bucket. |
| Maintenance list and recurring | Pass with validation risk | `/maintenance` and `/maintenance/recurring`; New Work Order, Inspection, Recurring Task modals opened/cancelled; list filters and empty states visible | Blank primary actions enabled in several create dialogs; included in P29-004 risk bucket. |
| Appointments | Pass with validation risk | `/appointments`; calendar/list tabs, month/week/agenda controls, type legend, New Appointment modal | Blank New Appointment save button enabled; included in P29-004 risk bucket. |
| Vendors | Pass with validation risk | `/vendors`; Add/Edit/Delete modals opened/cancelled; scanned receipt-created vendor displayed | Blank Add Vendor save button enabled; address fields still absent per existing backlog. |
| Accounting ledger | Pass with validation risk | `/accounting`; ledger filters/sort headers, New Payment and New Expense modals opened/cancelled; receipt thumbnail link visible | Blank New Payment/New Expense save buttons enabled; included in P29-004 risk bucket. |
| Accounting reports/overview | Pass | `/accounting?tab=reports` and `/accounting?tab=overview`; report groups expanded; why-this-is-here toast; Schedule E CSV downloaded; Money Snapshot help popover opened | CSV contents matched visible `$1,125.00` income, `$63.75` expenses, `$1,061.25` net. |
| Reports catalog and generated report viewer | Pass with data-rule risk | `/reports` and `/reports/income-expense-statement`; report cards, date range picker, property selector, update, Actions menu, CSV export | Viewer correctly updated Last 30 Days totals to `$0.00`; reporting data-access scale risk remains deferred. |
| Tax and year-end packet | Pass | `/tax`; Schedule E CSV downloaded; year-end PDF downloaded and verified as PDF 1.4, 2 pages, A4, not encrypted | Tax UI rounds display dollars but CSV preserves cents. |
| Owner reports | Pass | `/owners-report`; owner selected; statement rendered; CSV export downloaded and matched totals | `Email to owner` intentionally not fired because it is an external-message style action. |
| Banking | Pass with data-rule risk | `/banking`; manual JSON import created Sample Bank connection and one transaction; All/Unmatched/Ignored filters; Ignore and Un-ignore actions verified | Plaid Link/connect/exchange actions intentionally not fired. Transaction list cap/no-paging risk remains deferred. |

## Bugs And Fixes

| ID | Bug | Reproduction evidence | Fix | Regression |
| --- | --- | --- | --- | --- |
| P29-001 | `/scan/new-rental` converted missing lease-scan bed/bath values into real `0` values. | Lease camera fixture omitted bed/bath; review seeded Beds `0`, Baths `0`, requiring manual correction before confirm. Post-fix draft `8` showed empty Beds/Baths inputs and SQL showed empty extracted values. | Changed guided lease prefill to leave bed/bath blank unless extracted. | `pnpm --dir web test:unit -- src/lib/scan/new-rental-state.test.ts` |
| P29-002 | Work-order scan could not link a lease from a document that named the lease number. | Work order draft extracted property/unit/tenant but `lease_id` empty; confirmed row `/maintenance/1` had `LeaseId` null. Post-fix draft `6` extracted `lease_id=1` and confirmed `/maintenance/2` with `LeaseId=1`. | Added non-deleted leases to worker grounding JSON and updated work-order extraction schema to allow `lease_id` copied from `leases[].id`. | `dotnet test RentalCommand.Engine.Tests/RentalCommand.Engine.Tests.csproj --filter "FullyQualifiedName~ScanProcessingWorker"`; `dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~ScanServiceTests"` |

## Open Findings / Risks

| ID | Finding | Evidence | Status |
| --- | --- | --- | --- |
| P29-003 | Unit list document count disagrees with unit detail. | `/units/1` header showed `6 docs` and Documents tab listed lease, payment, expense, and work-order files; `/units` row showed Docs `0`. | Open. Likely list-summary query/count issue. |
| P29-004 | Several create dialogs enable primary save/schedule actions while empty. | Observed in New Property, New Tenant, New Lease, New Work Order, Inspection, Recurring Task, New Appointment, Add Vendor, New Payment, New Expense, and unit-level post/payment/expense/work-order forms. | Open risk. Bad-save submission not run yet to avoid intentionally creating invalid local rows during this pass. |
| P29-005 | Reports/accounting/banking still have known scale/data-rule risk areas. | Reports viewer/export and banking transaction list remain part of the broader TSK-397 server-side data-access refactor lane. | Deferred; not a safe small patch in this checkpoint. |
| P29-006 | External-provider and outbound-message actions not fired. | Plaid Link/connect/exchange, QuickBooks/OAuth, owner email, and vendor/tenant outbound SMS/email actions were not clicked unless the UI was demonstrably disabled or confirmation was cancelled. | Intentional safety boundary. |

## Verification

- `MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Engine.Tests/RentalCommand.Engine.Tests.csproj --filter "FullyQualifiedName~ScanProcessingWorker"`: passed, 12 tests.
- `pnpm --dir web test:unit -- src/lib/scan/new-rental-state.test.ts`: passed, 269 tests.
- `MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~ScanServiceTests"`: passed, 25 tests.
- Browser evidence retained under `.playwright-cli/` for this worktree, including downloaded CSV/PDF/image artifacts:
  - `schedule-e-2026.csv`
  - `income-expense-statement-2026-06-25.csv`
  - `owner-statement-1-2026.csv`
  - `year-end-2026.pdf`
  - `scan-20260625213615.jpeg`

## Blocked / Deferred

- QuickBooks/accounting provider OAuth: blocked until user provides sandbox/login access.
- Plaid sandbox/link flow: blocked until user provides sandbox access.
- Production/outbound owner email, SMS, and provider actions: blocked unless the user explicitly approves external effects.
- Broad DB-side refactor for reports/accounting/banking/client-side portal data handling: deferred to a dedicated lane because it crosses multiple endpoints and report semantics.
