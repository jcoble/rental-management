# TSK-397 Real-User UI Pass

Date: 2026-06-22
Branch: `tsk-397-real-user-ui-pass`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-real-user-ui-pass`
Continuation branch: `tsk-397-ui-inventory-continuation`
Continuation worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-ui-inventory-continuation`

## Scope

Run the web app as a real new live user starting from empty portfolio data. Build sanitized, production-like local data through the product workflows, including properties, units, tenants, leases, scanned documents, receipts, payments, applications, and image uploads. Plaid banking and QuickBooks/provider accounting are deferred until sandbox credentials are available.

## Local Stack

- Web: `https://localhost:5807`
- API: `https://localhost:5806` (`http://localhost:5805`)
- Database: PostgreSQL container `rentalcommand-tsk397-full-ui-db`, database `rentalcommand_tsk397_full_ui`, host port `5754`
- Assistant provider setting: `claude-cli`, model `sonnet`
- Safe wrapper: `scripts/qa/start-scan-audit-local.sh` with local provider-backed email/SMS settings blanked.

## Synthetic Account

- Earlier broad-pass user: Riley Morgan, `tsk397.ui.1782115363@example.local`
- Current clean-start continuation user: Nora Vale, `tsk397.full.1782131040@example.local`
- Current portfolio: Nora Vale's Portfolio

## Pass Log

### Dashboard

Acceptance criteria:
- Dashboard loads the real portfolio, money snapshot, briefing, messages, work orders, appointments, and setup progress without demo rows.
- Hero actions and card links route to the intended product surfaces.
- User-facing labels do not expose enum tokens, and money prose is formatted as currency.
- Empty/current states are truthful for overdue rent and expiring leases.

Bug RC-UI-017:
- Repro: Create a `MaintenanceVisit` appointment, then open `/`.
- Observed: Upcoming Appointments displayed raw enum `MaintenanceVisit`.
- Fix: Dashboard now uses the shared appointment type label helper used by appointment list/detail.
- Regression: `web/src/routes/(protected)/appointments/calendar-utils.test.ts` already covers `MaintenanceVisit -> Maintenance`.

Bug RC-UI-018:
- Repro: Open `/` with cents-bearing monthly expense/net values.
- Observed: Money explanation text rendered `$572.9`, which looked unpolished and inconsistent with currency.
- Fix: accounting snapshot explanation formatting keeps whole-dollar values compact and formats cents-bearing values with two decimals.
- Regression: `RentalCommand.Api.Tests/Domain/AccountingServiceTests.cs`.

Evidence:
- `/` loaded `GET /api/v1/portfolios/2/dashboard => 200` and `GET /api/v1/accounting/snapshot => 200`.
- Dashboard rendered Riley Morgan's real portfolio, setup progress `6 of 8 done`, latest message/work order, no overdue rent, and no expiring leases.
- Browser proof after fixes showed `Clearline plumbing service visit · Maintenance`, `You spent $572.90`, and `You're keeping $950.55 ... after $572.90 of expenses`.
- Console error count remained zero.

Status: Pass after fixes for loaded-state dashboard display and primary hero/card link targets. Setup checklist route is covered separately.

### Registration and Setup Entry

Acceptance criteria:
- New user can register, verify email, log in, choose live setup, and land on onboarding.
- Empty live portfolio starts without seeded properties, units, tenants, or leases.

Evidence:
- Verification link was pulled from local `OutboxMessages`.
- Browser reached `/onboarding` authenticated as the synthetic user.
- Initial DB check showed zero properties and zero units for the fresh live portfolio.

Status: Pass.

### Onboarding Property and Units

Acceptance criteria:
- User can type a state abbreviation into the state combobox, tab/click away, and continue.
- A resumed onboarding session can attach a property to the existing owner created earlier.
- Property save includes required hidden API fields and persists units.
- Successful save advances to the Tenants step and creates rows in PostgreSQL.

Bug RC-UI-001:
- Repro: On the property address step, type `OH` into State, fill the rest of the address, click Next.
- Observed: The UI showed `OH`, but Next displayed `State is required`.
- Root cause: `StateSelect` visible typed text could diverge from its bound value when the combobox was not open during blur/submit.
- Fix: Added shared state-input commit logic and wired closed-combobox blur to commit a resolved state code.
- Regression: `web/src/lib/components/shared/resolve-state.test.ts`.

Bug RC-UI-002:
- Repro: Create owner, refresh or resume onboarding, fill a property and two units, then click Save & continue.
- Observed: UI jumped back to the property address step with no visible error and no property API POST.
- Root cause: Onboarding property validation omitted the required hidden `status` field. The owner id also only came from the current-session `createdOwner`, which made resume fragile.
- Fix: Added onboarding helpers for owner selection and property payload construction with `status: Active`.
- Regressions:
  - `web/src/lib/onboarding/owner-selection.test.ts`
  - `web/src/lib/onboarding/property-payload.test.ts`

Evidence after fixes:
- Browser requests showed `POST /api/v1/properties => 201` and two `POST /api/v1/units => 201`.
- DB rows:
  - Property: `Maple Grove Duplex`, state `OH`, postal code `43215`, owner entity `1`.
  - Units: `A` rent `1400.00`; `B` rent `1450.00`.
- Browser advanced to the Tenants step.

Status: Pass after fixes.

### Onboarding Tenants, Lease, and Photo Extraction

Acceptance criteria:
- User can create a tenant from onboarding and the tenant is available to the lease step.
- Lease step preselects the newly created tenant/property/unit and seeds monthly rent from the unit.
- User can upload a camera-style lease image through "Snap a photo"; the shared scan engine processes it as an image and returns reviewable extracted values.
- Applying extracted values fills the editable lease form without changing the selected tenant/property/unit.
- Creating the lease finishes onboarding and persists a usable property -> unit -> tenant -> lease spine.

Synthetic image:
- `output/qa/tsk397-ui-pass/lease-photo-maple-grove.jpg`
- Sanitized phone-camera-style JPG generated locally with tenant/property/rent/date/deposit/late-fee terms.

Evidence:
- `POST /api/v1/tenants => 201`, tenant `Avery Brooks` persisted.
- Onboarding lease step prefilled `Avery Brooks`, `Maple Grove Duplex`, `Unit A`, and rent `1400`.
- `POST /api/v1/scans => 201`; engine log: `claude-cli extraction: VISION read (image/jpeg, no extractable text)` and `Scan extraction succeeded for draft 1 ... Lease, Reviewing`.
- Review UI displayed extracted values for lease number, dates, monthly rent, security deposit, late fee, and rent due day with confidence.
- Applying extracted values filled monthly rent `1400.00`, security deposit `1400.00`, late fee `50.00`, and due day `1`.
- `POST /api/v1/leases => 201`.
- DB row: lease `MGD-A-20260622`, property `1`, unit `1`, tenant `1`, start `2026-06-22`, end `2027-06-22`, rent `1400.00`, deposit `1400.00`, late fee `50.00`, due day `1`.
- Browser reached the truthful completion state: `You're all set!`.

Status: Pass.

### Receipt Image Scan To Expense

Acceptance criteria:
- User can upload a camera-style receipt image through the scan workflow.
- The shared extraction engine treats the image as vision input and returns reviewable expense fields.
- Confirming the draft creates a usable expense detail page with preview/file, receipt fields, line items, and vendor linkage.

Synthetic image:
- `output/qa/tsk397-ui-pass/receipt-clearline-plumbing.jpg`

Bug RC-UI-003:
- Repro: Upload a receipt image whose vendor name exactly matches an existing active portfolio vendor, confirm as an expense, then open the created expense detail.
- Observed: Expense detail showed `No vendor` even though `Clearline Plumbing` already existed.
- Root cause: scan-confirm expense creation promoted vendor text but did not resolve an exact existing vendor id.
- Fix: `ScanService.ConfirmAsExpenseAsync` now uses a portfolio-scoped exact vendor lookup before creating the expense.
- Regression: `RentalCommand.Api.Tests/Scanning/ScanServiceTests.cs`.

Evidence:
- Engine log: `claude-cli extraction: VISION read (image/jpeg, no extractable text)`.
- Expense detail route `/accounting/expenses/2` rendered the scanned document preview and line items.

Status: Pass after fix.

### Maintenance, Appointments, Recurring Tasks, Inspections

Acceptance criteria:
- User can create and schedule a maintenance work order with property/unit/tenant/vendor context.
- User can create and open a calendar appointment from the work flow.
- Appointment labels are user-facing names, not enum tokens.
- Recurring maintenance tasks can be created, edited, paused, and resumed with correct date-only display.
- Inspections preserve the local scheduled date/time when submitted.

Bug RC-UI-004:
- Repro: Create a maintenance visit appointment and view the appointment list/detail.
- Observed: UI displayed raw enum `MaintenanceVisit`.
- Fix: Added appointment type label helper and used it on appointment list/detail.
- Regression: `web/src/routes/(protected)/appointments/calendar-utils.test.ts`.

Bug RC-UI-005:
- Repro: Create a recurring maintenance task with next due `09/22/2026`.
- Observed: Row displayed `Sep 21, 2026`.
- Root cause: UTC-midnight date-only value was formatted through local-time date formatting.
- Fix: recurring task UI now formats next due with `formatDateOnly`.
- Regression: `web/src/lib/utils/date.test.ts`.

Bug RC-UI-006:
- Repro: Create an inspection for `2026-06-25 10:30`.
- Observed risk: raw `datetime-local` value sent without offset, vulnerable to timezone shift.
- Fix: inspection submit now serializes via `localInputToOffsetIso`.
- Regression: `web/src/lib/utils/date.test.ts`.

Evidence:
- Work order `Kitchen sink leak under Unit A` persisted and was scheduled with vendor `Clearline Plumbing`.
- Appointment `Clearline plumbing service visit` opened from the calendar row and displayed `Maintenance`.
- Recurring task `Quarterly HVAC filter replacement` created, edited, paused, and resumed.
- Inspection detail `/maintenance/inspections/1` displayed the intended `6/25/2026 10:30 AM`.

Status: Pass after fixes for the covered workflows.

### Public Applications and Application Lifecycle

Acceptance criteria:
- Landlord can generate/use a public application link.
- Applicant can submit with image-extracted or manually entered data.
- Requested property/unit display uses names/numbers, not raw ids.
- Full-address scan output does not duplicate structured address pieces.
- Landlord can approve, decline, and withdraw separate applications.
- Approval creates a tenant and lands on a usable tenant detail page.
- Terminal application states do not offer new screening actions.

Synthetic image:
- `output/qa/tsk397-ui-pass/jordan-ellis-application-camera.jpg`

Bug RC-UI-007:
- Repro: Submit an application with property/unit selected, then open application detail.
- Observed: requested home displayed `#1` / `#2`.
- Fix: application list/get now projects property/unit display labels in the paged SQL query, and the frontend prefers names/numbers.
- Regressions:
  - `RentalCommand.Api.Tests/Domain/ApplicationServiceTests.cs`
  - `web/src/lib/applications/application-display.test.ts`

Bug RC-UI-008:
- Repro: Scan/submit an application where image extraction puts a full address in line 1 and also fills city/state/ZIP.
- Observed: detail and approved tenant notes could show duplicated address pieces.
- Fix: shared address composition deduplicates structured parts; approval notes use the same composer.
- Regressions:
  - `RentalCommand.Api.Tests/Domain/ApplicationServiceTests.cs`
  - `web/src/lib/applications/application-display.test.ts`

Bug RC-UI-009:
- Repro: Decline or withdraw an application and reopen the detail page.
- Observed: `Run screening` remained visible on terminal applications.
- Fix: screening action now uses the application lifecycle predicate and is hidden after a decision, with a terminal-state note.
- Regression: `web/src/lib/applications/application-display.test.ts`.

Evidence:
- Jordan Ellis submitted via public apply using a JPG upload. Engine log showed vision extraction; form autofilled partial image data and allowed user edits.
- Jordan approval created tenant `/tenants/2`.
- Casey Nguyen submitted through public form, was declined with a reason, and grid displayed `Declined`.
- Taylor Brooks submitted with no property preference, was withdrawn, and detail displayed `Screening can only be run before a decision` with no run button.
- Morgan Pierce submitted with scan-style full address, was approved, and tenant `/tenants/3` note showed `Prior address: 44 Cedar Bend Apt 5, Columbus, OH 43215.` once.

Status: Pass after fixes for application happy paths and core terminal states.

### Owners

Acceptance criteria:
- Owner list loads the seeded portfolio owner and supports create, detail, edit, and delete for an unrelated owner.
- Owner create/edit uses the same state/address behavior expected elsewhere in the app.
- Delete confirmation identifies the selected owner and removes only that local synthetic record.

Evidence:
- `/owners` loaded Riley Morgan from the onboarding-created portfolio owner.
- Created disposable owner `Lena Ortiz Holdings` with Ohio address/contact fields through the add-owner modal.
- Opened `/owners/2`; detail displayed address, contact, documents/history panels, and edit controls.
- Edited phone/email from detail; UI showed `Owner updated`.
- Deleted `Lena Ortiz Holdings` through the confirmation dialog; `/owners` still displayed Riley Morgan and no Lena row.

Status: Pass for covered create/detail/edit/delete workflow.

### Vendors

Acceptance criteria:
- Vendor list loads existing scan-linked vendors and supports create, detail, rating, tax edit, W-9 state toggle, and delete for an unrelated vendor.
- Vendor detail changes persist back to the list grid.
- Delete confirmation identifies the selected vendor and removes only that local synthetic record.
- Outbound W-9 text request is not exercised unless local provider behavior is confirmed safe.

Evidence:
- `/vendors` loaded scanned vendor `Clearline Plumbing`, used by the receipt/work-order workflows.
- Created disposable vendor `Summit Electric` with service type, email, phone, preferred status, 1099 eligibility, and W-9-on-file state.
- Detail `/vendors/2` displayed the performance scorecard, compliance fields, and tax controls.
- Rated `Summit Electric` 5 stars with a comment; detail and list grid updated to `5.0` / one rating.
- Toggled W-9 from `Yes` to `Missing`; the detail and list grid reflected `W-9: Missing`.
- Edited tax info and saved synthetic EIN `31-1234567`; detail displayed the saved tax id.
- Deleted only `Summit Electric` through the confirmation dialog; `/vendors` retained `Clearline Plumbing`.
- `Text W-9 request` was intentionally not clicked because the implementation enqueues outbound SMS via the outbox and may hit an SMS provider when credentials are configured.

Status: Pass for covered local CRUD/compliance workflow; outbound SMS request remains gated by provider safety.

### Messages

Acceptance criteria:
- Message center loads from the real portfolio state and supports starting a local-safe tenant conversation.
- User can select portal-only delivery, send a message, open the thread, and reply without invoking email/SMS providers.
- Outbound provider-backed channels are not exercised unless local provider behavior is confirmed safe.

Evidence:
- `/messages` loaded the empty state, then created a tenant conversation with Avery Brooks using portal-only delivery.
- Thread detail accepted a reply and retained the conversation history.
- Email/SMS channel send paths were intentionally not used during this local pass.
- Later production-scale grid proof seeded 25 local-only synthetic conversations for Nora Vale and verified the staff message list loads through `/api/v1/conversations/page?take=20`, `Load more` fetches `/api/v1/conversations/page?skip=20&take=20`, the shell badge uses `/api/v1/conversations/unread-count`, and compose tenant search uses `/api/v1/tenants/page?take=20&search=Avery...`; no legacy `/api/v1/conversations` or `/api/v1/tenants?take=500` list call occurred and console warnings/errors were zero.

Status: Pass for local portal-only messaging workflow; provider-backed email/SMS sending remains gated.

### Settings Notifications and In-App Broadcasts

Acceptance criteria:
- Getting Started can deep-link to the Settings Notifications tab and the tab state is preserved in the URL hash.
- Notification email saves against the portfolio and reloads with the saved value.
- Notification channel matrix renders current preferences and saves without clobbering hidden push settings.
- Local in-app broadcast validates title/message before enabling submit, creates an unread notification, supports the exposed severity choices, clears the form after success, and refreshes the header bell/list immediately.
- SMS test/send controls are not exercised unless local provider behavior is confirmed safe.

Bug RC-UI-019:
- Repro: Open `/settings#notifications`, send a local in-app broadcast, and watch the header notification badge.
- Observed: API returned `201` with `isRead:false`, but the header stayed at `0 unread notifications`; the page invalidated generic query keys and refetched conversations/appointments, not the notification singleton used by the shell.
- Fix: broadcast success now invalidates notification queries and calls `notificationStore.refresh()` so the shell bell refetches unread count and recent list immediately.
- Regression: browser proof plus service-level notification regression in `RentalCommand.Api.Tests/Domain/ConversationNotificationTests.cs`.

Bug RC-UI-020:
- Repro: Broadcast severity dropdown offered `Critical`, but the typed notification renderer only modeled `Info`, `Success`, `Warning`, and `Error`.
- Observed: Critical notifications could fall back to Info styling client-side, and API casing was not normalized.
- Fix: `Critical` is now a first-class notification severity in the web type/config, and `NotificationService.CreateBroadcastAsync` normalizes allowed severities case-insensitively.
- Regression: `CreateBroadcastAsync_NormalizesSeverityAndCountsAsUnreadForPortfolioUsers`.

Bug RC-UI-021:
- Repro: Protected Settings page exposed the broadcast card to roles beyond the API's `Admin,Manager,Owner` authorization.
- Observed: an Agent could reach a form that would 403 on submit.
- Fix: the card now uses the same role set as the API.

Evidence:
- Getting Started `Show me` link opened `/settings#notifications`.
- `PUT /api/v1/notifications/email => 200` saved `riley.alerts@example.local`.
- Pre-fix network proof: `POST /api/v1/notifications/broadcast => 201` returned notification id `1`, `isRead:false`, followed by no notification refetch and header `0 unread notifications`.
- Post-fix reload picked up id `1` and rendered `1 unread notification`.
- Post-fix Critical broadcast returned id `2`, `severity:"Critical"`, `isRead:false`; the app then fetched `GET /api/v1/notifications/unread-count => 200` with `{"count":2}` and `GET /api/v1/notifications?take=20 => 200` listing ids `2` and `1`.
- Header rendered `2 unread notifications`; form cleared; console warnings/errors remained zero.

Status: Pass after fixes for notification email save, local in-app broadcast validation/submit/refresh, severity handling, and role-gated broadcast UI. SMS provider test remains intentionally unsubmitted.

### Lease Agreement, Native Signing, and Public Signing

Acceptance criteria:
- Lease detail can generate/download a printable agreement PDF from persisted lease data.
- Native e-sign send creates a local signature request and signer link without using production providers.
- Public signer can open the document, consent to electronic records, sign, and reach completion.
- Completed signature stores a signed document and certificate of completion.
- Management detail for an already signed/finalized lease must not offer another send-for-signature action.

Bug RC-UI-010:
- Repro: Open a lease detail before an agreement PDF exists.
- Observed: The page probed `/api/v1/leases/{id}/document`, producing a visible 404 in console/network before the user requested a download.
- Fix: Added `GET /api/v1/leases/{id}/document-status` and switched lease detail to use metadata status instead of probing the binary download endpoint.
- Regressions:
  - `RentalCommand.Api.Tests/Domain/LeaseAgreementDocumentTests.cs`
  - lease detail browser proof: `/leases/1/document-status => 200`, no initial `/leases/1/document` 404.

Bug RC-UI-011:
- Repro: After a lease was signed and active, open Agreement & Signing.
- Observed: UI could still offer send/resend signature actions even though service state rejected finalized leases; a notice-given lease could also be pushed into signature flow.
- Fix: server-side send now only accepts Draft/PendingSignature, and the web helper hides send actions for Signed/Active/NoticeGiven/Expired/Terminated leases while preserving signed download.
- Regressions:
  - `RentalCommand.Api.Tests/Domain/LeaseEsignServiceTests.cs`
  - `web/src/lib/leases/lease-esign.test.ts`

Evidence:
- Generated agreement downloaded as `.playwright-cli/lease-agreement-1.pdf` and rendered to PNG for visual inspection.
- Local outbox contained the e-sign message; public signer route loaded from the local token.
- Public signer opened the document, consent enabled signing, typed `Avery Brooks`, submitted, and reached completion.
- DB showed the signature request completed and lease signed with a stored signed document.
- Management Agreement & Signing tab showed `Signed`, exposed `Download signed lease`, and did not show send/resend signature.
- Signed PDF downloaded as `.playwright-cli/signed-lease-1.pdf`; `pdfinfo` showed 3 A4 pages; rendered PNG inspection showed executed agreement, signature page, and certificate of completion.

Status: Pass after fixes for generated agreement, public signing happy path, signed-document download, and finalized-lease action guard.

### Tenant Notices

Acceptance criteria:
- Notices page loads current lease/payment state and does not generate drafts when no notice condition exists.
- Draft review/approval/send workflow remains gated unless a local-safe eligible notice condition exists.

Evidence:
- `/notices` loaded the empty/current state.
- `Generate drafts` produced no draft for the clean current portfolio state.

Status: Partial pass for empty/no-op state. Draft edit, fair-housing rewrite, approval, and send/dismiss remain open for an eligible synthetic notice condition.

### Reports Hub and Generic Report Viewer

Acceptance criteria:
- Reports catalog loads from the real API and lists every server-declared report.
- Each generic report route auto-generates with server defaults and renders either a populated table or a truthful empty state without console errors.
- Shared report controls support property filtering, CSV export, and print.
- External/deep-link reports still route to their existing product pages instead of trying to render in the generic viewer.

Evidence:
- `/reports` loaded `GET /api/v1/reports/catalog => 200` and rendered Accounting, Rent & Payments, Owners, and Operations report cards.
- Browser route sweep covered all generic report keys:
  - Initial tables: `income-expense-statement`, `property-pnl-summary`, `general-ledger`, `cash-flow`, `rent-roll`, `rent-ledger`, `owner-distributions`, `occupancy`, `vendor-1099`, `work-orders`.
  - Expected empty states before later data setup: `delinquency`, `lease-expirations`, `security-deposit-register`.
- Network proof showed each report endpoint returning `200`, including `GET /api/v1/reports/rent-roll?propertyIds=1 => 200`.
- Rent Roll shared controls:
  - Selected `Maple Grove Duplex` in the property popover and clicked `Update`.
  - Table stayed scoped to `Maple Grove Duplex`, `Unit A`, lease `MGD-A-20260622`, tenant `Avery Brooks`.
  - `Export CSV` downloaded `.playwright-cli/rent-roll-2026-06-22.csv`.
  - `Print` invoked `window.print` through a test stub.
- After the deposits workflow created a holding and deduction, `/reports/security-deposit-register` rendered a table row for `Maple Grove Duplex`, `Unit A · MGD-A-20260622`, tenant `Avery Brooks`, held `$1,400.00`, deductions `$125.50`, balance `$1,274.50`, and matching totals.
- Console error count remained zero during the sweep.

Status: Pass for the reports catalog, all generic report default routes, report property filtering, CSV export, and print action. External reports are covered separately by `/tax`, `/owners-report`, and `/accounting/year-end`.

### Security Deposits

Acceptance criteria:
- Deposits page distinguishes held tenant money from rent/payments and starts with a truthful empty state.
- User can create a security-deposit holding from an existing lease, defaulting amount from lease deposit when amount is blank.
- Required-form validation is visible for missing lease and missing deduction fields.
- User can add a deduction, see detail totals and net refund update, upload a camera-style image as a condition photo, download the move-out statement PDF, and open/cancel the return confirmation without changing status.

Evidence:
- `/deposits` initially showed `No security deposits on record yet. Add a holding to get started.`
- `New Holding` with no lease showed required validation.
- Created a holding for lease `MGD-A-20260622 · Avery Brooks` with amount left blank; list rendered `$1,400.00`, `Held`, held date `6/22/2026`.
- `Add deduction` with empty fields showed validation, then saved `Move-out cleaning`, `$125.50`, notes `TSK397 synthetic cleaning deduction.`
- Detail `/deposits/1` rendered amount held `$1,400.00`, total deductions `$125.50`, net refund `$1,274.50`, notes, and deduction row.
- Uploaded synthetic camera-style JPG `lease-photo-maple-grove.jpg` as a condition photo; API proof `POST /api/v1/documents => 201`, `GET /api/v1/documents/10/file => 200`.
- `Download move-out statement (PDF)` saved `.playwright-cli/move-out-statement-1.pdf`; network proof `GET /api/v1/security-deposits/1/move-out-statement => 200`.
- `Process Return` opened a confirmation message for `Return $1,274.50`; canceled it and verified status remained `Held`.
- Console error count remained zero.

Status: Pass for create, validation, deduction, detail, image attachment, move-out statement download, and return-confirm cancel path. Actual return confirmation remains intentionally unsubmitted to preserve synthetic state for later flows.

### Payment Detail

Acceptance criteria:
- Payment detail page loads a real payment with lease/tenant display labels, status, method/reference, and audit history.
- Edit mode validates required fields, can save a non-destructive update, and refreshes audit history.
- Delete remains available but is not clicked during the pass because it would remove synthetic ledger state.

Evidence:
- Opened `/accounting/payments/2` for the rent payment resolved by the past-due workflow.
- Detail rendered `Avery Brooks`, `Rent · $123.45 · Paid`, lease `MGD-A-20260622`, due date `May 1, 2026`, paid date `Jun 22, 2026`, method `Cash`, reference `TSK397-CASH-001`, and audit history.
- Edit mode with blank amount showed required validation.
- Restored amount `$123.45`, updated notes to `TSK397 browser past-due mark-paid seed; detail edit verified.`, and saved.
- Network proof: `PATCH /api/v1/payments/2 => 200`, followed by refreshed `GET /api/v1/payments/2 => 200` and payment audit requests.
- UI returned to view mode with updated notes and `Updated payment just now`.
- Console error count remained zero.

Status: Pass for detail load, validation, update, and audit refresh. Delete intentionally not submitted.

### Expense Detail

Acceptance criteria:
- Expense detail page loads a scanned receipt expense with property/vendor labels, scan preview, parsed receipt fields, line items, raw receipt disclosure, and audit history.
- Edit mode validates required fields, can repair a missing vendor on local pre-fix data, and preserves typed scan line items.
- Delete remains available but is not clicked because it would remove synthetic accounting history.

Evidence:
- Opened `/accounting/expenses/2`; detail rendered `Clearline Plumbing`, `Repairs · $286.45 · Paid`, property `Maple Grove Duplex`, scanned document preview/link, receipt subtotal `270`, tax `16.45`, card last 4 `4242`, payment method `Card`, document kind `Receipt`, vendor phone/address, and two line items.
- The existing local row initially showed `No vendor` because it was created before the scan vendor-linking fix; this was treated as local data remediation, while future scan-linking is covered by `ScanServiceTests`.
- Edit mode with blank amount showed required validation.
- Selected vendor `Clearline Plumbing`, updated notes to `Kitchen sink trap repair — Maple Grove Duplex, Unit A; detail vendor repaired after scan-link fix.`, and saved.
- Network proof: `PATCH /api/v1/expenses/2 => 200`, followed by refreshed `GET /api/v1/expenses/2 => 200` and expense audit requests.
- UI returned to view mode with `Vendor: Clearline Plumbing`, parsed line items still intact, and `Updated expense just now`.
- Console error count remained zero.

Status: Pass for detail load, validation, update, scan preview/receipt fields, line items, and audit refresh. Delete intentionally not submitted.

### Money Ledger Filters and Create Dialogs

Acceptance criteria:
- Main money ledger loads from real portfolio data with KPI cards, paged transactions, and no seeded/demo rows.
- Search, type, status, category, property, and sort controls persist in the URL and drive server requests with explicit query parameters.
- Filtered ledger rows remain openable/deletable through visible row actions, while destructive delete is not submitted during this pass.
- New Payment and New Expense dialogs validate required fields without posting incomplete rows, and Cancel returns to the same filtered ledger state.

Evidence:
- `/accounting` loaded KPI cards from `GET /api/v1/accounting/summary => 200` and report data from `GET /api/v1/accounting/reports => 200`.
- Applied filters: search `Clearline`, type `Expense`, status `Paid`, category `Repairs`, property `Maple Grove Duplex`, then sorted by `Amount`.
- URL persisted as `/accounting?q=Clearline&kind=Expense&status=Paid&category=Repairs&property=1&sort=amount`.
- Network proof: `GET /api/v1/accounting/transactions?take=20&search=Clearline&sort=amount&kind=Expense&status=Paid&category=Repairs&propertyId=1 => 200`.
- Grid rendered two matching Clearline repair expenses, both scoped to `Maple Grove Duplex`, with the scanned receipt thumbnail/link still visible on the receipt-backed row.
- `New Payment` empty save showed `Lease is required`, `Amount is required`, and `Due date is required`; Cancel closed the modal without a `POST /api/v1/payments`.
- `New Expense` empty save showed `Description is required`, `Amount is required`, and `Incurred date is required`; Cancel closed the modal without a `POST /api/v1/expenses`.
- Console error count remained zero.

Status: Pass for ledger KPI load, server-backed filter/sort URL state, create-payment validation/cancel, and create-expense validation/cancel. Row delete remains intentionally unsubmitted.

### Assistant Briefing and Data Q&A

Acceptance criteria:
- Assistant page loads the daily briefing from real portfolio state.
- A live-data question uses server-side portfolio tools instead of generic model memory or external Claude/Notion tools.
- Long-running local `claude -p` assistant requests do not hit the frontend's global short timeout.
- UI renders the answer, tools used, and no console/network errors.

Bug RC-UI-015:
- Repro: On `/ai`, ask `Show me my recent expenses` with the local `claude-cli` provider.
- Observed: Browser posted `/api/v1/ai/ask`; the server returned 200 after roughly 46-49 seconds, but the frontend aborted at the global 20-second timeout and showed `The request is taking longer than expected`.
- Fix: `ai.ask` now uses the lower-level `fetchApi` call with an assistant-specific timeout budget instead of the default API timeout.

Bug RC-UI-016:
- Repro: After extending the timeout, ask `Show me my recent expenses` again.
- Observed: API returned 200 with `toolsUsed: []` and an answer claiming it only had Notion/project-planning tools, not Rental Command database access.
- Root cause: `ClaudeCliLlmProvider.ChatWithToolsAsync` ignored the supplied application tool specs and returned best-effort prose from `claude -p`, so the portfolio assistant could not execute its DB-backed tools in local testing.
- Fix: local Claude now follows the same tool-call contract as the HTTP providers: the first pass returns compact JSON tool calls constrained to the supplied app tools, the service executes those tools, and the second pass answers from the tool-result messages only.
- Regression: `RentalCommand.Api.Tests/Scanning/ClaudeCliLlmProviderTests.cs`.

Evidence:
- `/ai` daily briefing rendered a real inspection alert: `Inspection in 3 days — Routine`.
- Browser proof after fix: `POST /api/v1/ai/ask => 200`.
- Response body: `toolsUsed:["list_recent_expenses"]`, `source:"Data"`, model `claude-cli:sonnet`.
- Rendered answer used real synthetic app data: 2 expenses in the last 90 days totaling `$572.90`, both at `Maple Grove Duplex` for `Clearline Plumbing`.
- Console error count remained zero.

Status: Pass after fixes for daily briefing and data-backed assistant Q&A. Optional answer delivery via email/SMS remains gated by provider safety.

### External Report Pages: Owner Statement and Tax

Acceptance criteria:
- Owner statement page lists owners with year-scoped net distribution, opens a selected owner statement, and exports CSV without using outbound email.
- Tax page shows Schedule E summary, vendor W-9 checklist, CSV export, and year-end packet PDF download.
- Outbound email/SMS actions remain gated unless local provider safety is confirmed.

Evidence:
- `/owners-report` loaded `GET /api/v1/accounting/owner-statements?year=2026 => 200`.
- Owner list displayed `Riley Morgan Net: $951`; selecting the owner rendered `Riley Morgan — 2026`, total income `$1,523`, total expenses `$573`, management fee `$0`, net to owner `$951`, and property row `Maple Grove Duplex`.
- `Download CSV` saved `.playwright-cli/owner-statement-1-2026.csv`.
- `/tax` loaded `GET /api/v1/accounting/schedule-e?year=2026 => 200` and `GET /api/v1/accounting/reports => 200`.
- Tax page displayed 2026 Schedule E totals: rental income `$1,523`, expenses `$573`, net income `$951`, and a 1099 checklist row for `Clearline Plumbing`.
- `Download CSV` saved `.playwright-cli/schedule-e-2026.csv`; network proof `GET /api/v1/accounting/schedule-e/export?year=2026 => 200`.
- `Download packet` saved `.playwright-cli/year-end-2025.pdf`; network proof `GET /api/v1/accounting/year-end-packet?year=2025 => 200`.
- `Email to owner` and `Text W-9 request` were intentionally not clicked because they may enqueue or send provider-backed outbound messages.
- Console error count remained zero.

Status: Pass for owner-statement list/detail/export and tax Schedule E/packet downloads. Outbound email/SMS actions remain gated.

### Past-Due Mark Paid Workflow

Acceptance criteria:
- A real user can resolve a past-due rent item from the past-due UI without client-side loading/filtering of payment pages.
- The mark-paid action opens a modal, collects received date/method/reference, posts one lease-level server action, and refreshes the list to an empty current state.

Evidence:
- Seeded local synthetic late rent payment `Payment.Id=2`, lease `1`, amount `$123.45`, due `2026-05-01`.
- `/accounting/past-due` rendered `1 rental behind, owing $123` with row `Avery Brooks`, `Maple Grove Duplex · Unit A`.
- Clicking `Mark paid` opened the confirmation modal with date, method, reference, and notes fields.
- Selected `Cash`, filled reference `TSK397-CASH-001`, submitted the modal.
- Network proof: `POST /api/v1/payments/leases/1/past-due/mark-paid => 200`, followed by `GET /api/v1/accounting/past-due => 200`.
- UI refreshed to `Everyone is current. Nice.` with zero console errors.

Status: Pass after the B015 server-action fix.

### B015 Data-Access Fixes From This Pass

Acceptance criteria:
- Reporting/accounting/banking endpoints touched during this pass do not materialize rows and then filter/group/sort/aggregate for the covered B015 defects.
- Regression tests inspect behavior and, where practical, emitted SQL shape.

Fixes:
- Past-due snapshot/list totals now derive from shared SQL grouped queries instead of materialized payment rows.
- Banking review queue now performs candidate filtering, scoring, and top-match ranking SQL-side before response mapping.
- Vendor 1099 report now filters year/vendor eligibility and totals paid SQL-side.
- Security-deposit register no longer parses deduction JSON to total report rows. A maintained `DeductionsTotal` scalar was added, backfilled from JSONB in migration `20260622100316_AddSecurityDepositDeductionsTotal`, and report totals now aggregate that column SQL-side.
- Year-end selected-property view now calls `/accounting/year-end?propertyId=...`; cash-flow, Schedule E, and rent-roll blocks are filtered server-side instead of Svelte filtering/reducing the full portfolio response.
- Past-due `Mark paid` now calls one lease-level server action that resolves eligible past-due payments in one filtered query, instead of the page loading up to 200 payments, filtering, and marking each client-side.
- Lease-expiration totals now use a SQL count/sum over the filtered lease query instead of `rows.Count`/`rows.Sum`.
- Rent-ledger running balances are projected through a correlated SQL sum, and per-lease/portfolio charged/paid totals are grouped SQL-side. The remaining in-memory work is DTO nesting from ordered SQL rows.
- True cash-flow, property P&L, occupancy, year-end packet monthly/yearly totals, and owner-distribution portfolio totals now use DB-side grouped/summed queries.
- Single-owner statement report totals now use a grouped SQL aggregate instead of summing per-property DTOs in memory.
- Schedule E raw rental income, deductible expense, and modeled interest totals are SQL-side. Depreciation remains an app-domain calculation from per-property basis data, not an aggregate over materialized transaction rows.
- Assistant/portfolio QA tools now use SQL aggregates for property totals, recent expense totals, and recent payment totals; the mixed appointments/inspections schedule tool now merges, sorts, and caps through a SQL union before formatting.
- Accounting import retry now queries parked payment and expense rows by external type before materialization, deposit account matching pushes name predicates into SQL, and active lease lookup selects the latest lease per tenant through a grouped/order projection.
- Accounting mappings and review queues now support DB-side `confirmed`, `skip`, and `take` filters. The settings loader fetches confirmed/unconfirmed mappings and review queue rows as separate capped overfetch queries instead of filtering full arrays in Svelte.
- Accounting transaction inline reconciliation suggestions now perform amount/date/name gating, scoring, and top-match ranking SQL-side before the suggested bank chip is attached to page rows.
- Audit pagination now overfetches by one row and passes an explicit `hasNext` signal, so an exactly full final page no longer enables a fake next page.
- Banking summary `LastSyncedAt` now uses a DB-side `MAX` aggregate instead of loading bank connections and aggregating in memory.
- Plaid reconnect/exchange matching now uses queryable SHA-256 lookup hashes for external item/account ids, with provider ids still encrypted at rest. The exchange flow no longer loads every Plaid connection and decrypts each row to find a match.

Regressions:
- `RentalCommand.Api.Tests/Domain/AccountingServiceTests.cs`
- `RentalCommand.Api.Tests/Domain/BankingServiceTests.cs`
- `RentalCommand.Api.Tests/Domain/ReportsServiceTests.cs`
- `RentalCommand.Api.Tests/Domain/PaymentServiceTests.cs`
- `RentalCommand.Api.Tests/Domain/ScheduleEServiceTests.cs`
- `RentalCommand.Api.Tests/Domain/OwnerStatementServiceTests.cs`
- `RentalCommand.Api.Tests/Domain/PortfolioQaServiceTests.cs`
- `RentalCommand.Api.Tests/Domain/AccountingImportServiceTests.cs`
- `RentalCommand.Api.Tests/Domain/AccountingConnectionServiceTests.cs`
- `web/src/lib/audit/pagination.test.ts`

Focused static audit:
- Read-only audit of `RentalCommand.Api/Services/Domain` and `RentalCommand.Api/Controllers` found the two remaining banking violations above after the earlier report/accounting/banking fixes.
- After the hash lookup, `MAX`, banking review-queue ranking, and accounting inline-reconciliation ranking fixes, no additional high-confidence report/accounting/banking controller/service B015 violations were identified in that audited scope.

Status: Pass for the repaired attached B015 slices in the audited report/accounting/banking controller/service scope. Do not generalize this to a full-app DB-side audit outside that scope.

### Attached Inventory Follow-Up Fixes

Acceptance criteria:
- Read-only inventory findings are either fixed with regression coverage, proven safe with browser evidence, or left explicitly open with a bounded next step.
- UI escape paths are real user actions, not only comments or dead links.
- Data-access fixes respect the hard DB-side rule for filtering, sorting, grouping, paging, and aggregation.

Fixes closed after the initial scan-heavy pass:
- `ConversationService` list/detail projections now compute message counts and message ordering through SQL projections instead of materialized navigation mapping. Regression: `ConversationNotificationTests`.
- `NoticeDraftService` now applies renewal/move-out eligibility windows DB-side for non-forced generation runs while preserving forced targeted notices. Regression: `NoticeDraftServiceTests`.
- Scan batch upload now wraps batch and draft creation in one transaction and rolls back both when any draft creation fails. Regression: `ScanBatchControllerTests`.
- Failed scan draft copy no longer tells the user to manually confirm a failed draft; it points the user to retry extraction or reject because server confirmation only accepts `Reviewing` drafts.
- Shared `FileDrop` now rejects unsupported dragged files before any selected-file state or upload callback runs, while supported PDF/image files still pass through. Regression: `web/src/lib/components/file-drop.test.ts`.
- `/properties` grid no longer fetches `take=500` and filters/sorts/pages in Svelte; it uses `/api/v1/properties/page` with SQL count, search/filter, sort, offset, and page size. Regression: `PropertyServiceTests`.
- `/tenants` grid no longer fetches `take=500` and filters/sorts/pages in Svelte; it uses `/api/v1/tenants/page` with SQL count, search, sort, offset, page size, and DB-side `ActiveLeaseCount` sorting. Regression: `TenantServiceTests`.
- `/leases` grid no longer fetches `take=500` and filters/sorts/pages in Svelte; it uses `/api/v1/leases/page` with SQL count, search, status filtering, sort, offset, page size, and DB-side tenant-name sorting. Regression: `LeaseServiceListTests`.
- `/units` health grid no longer fetches `take=500` and sorts/pages in Svelte; it uses `/api/v1/units/list-with-health/page` with SQL count, search, property filtering, sort, offset, page size, and DB-side open-repair-count sorting. Regression: `UnitServiceListTests`.
- Command Center unit search no longer eagerly loads every unit health row on login; it lazily opens a server-searched first page from `/api/v1/units/list-with-health/page`.
- `/maintenance` work-order grid no longer fetches `take=100` and filters status/priority in Svelte; it uses `/api/v1/work-orders/page` with SQL count, search, status/priority filtering, sort, offset, page size, and DB-side property-name sorting. Regression: `WorkOrderServiceListTests`.
- `/appointments?view=list` no longer fetches a single `take=20` legacy list and filters type/status in Svelte; it uses `/api/v1/appointments/page` with SQL count, search, type/status filtering, sort, offset, page size, and DB-side property/tenant-name sorting. Regression: `AppointmentServiceListTests`.
- `/vendors` grid no longer fetches `take=100` and sorts/pages in Svelte; it uses `/api/v1/vendors/page` with SQL count, search, sort, offset, and page size. Regression: `VendorServiceListTests`.
- `/owners` grid no longer fetches a capped legacy list and sorts/pages in Svelte; it uses `/api/v1/owner-entities/page` with SQL count, search, sort, offset, and page size. Regression: `OwnerEntityServiceListTests`.
- `/applications` grid no longer fetches a status-filtered list and then searches/sorts/pages in Svelte; it uses `/api/v1/applications/page` with SQL count, search, status filter, sort, offset, and page size. Regression: `ApplicationServiceTests.ListPageAsync_ReturnsSqlCountAndRequestedWindow`.
- `/deposits` grid no longer fetches an unpaged security-deposit list and sorts/pages in Svelte; it uses `/api/v1/security-deposits/page` with SQL count, optional lease filter, sort, offset, and page size. Regression: `SecurityDepositServiceListTests`.
- `/maintenance/recurring` no longer fetches a fixed `take=200` list and sorts/pages in Svelte; it uses `/api/v1/recurring-maintenance/page` with SQL count, search, active filtering, sort, offset, page size, and DB-side property-name sorting. Regression: `RecurringMaintenanceTaskServiceTests.ListPageAsync_ReturnsSqlCountAndRequestedWindow`.
- `/scan` draft grid no longer fetches a fixed legacy draft array and lets DataGrid sort/page the current window; it uses `/api/v1/scans/page` with SQL count, status filtering, sort, offset, and page size. Regression: `ScanBatchControllerTests.ListPage_ReturnsSqlCountAndRequestedWindow`.
- `/messages` staff conversation list no longer fetches the legacy unpaged conversation list; it uses `/api/v1/conversations/page` for initial/load-more windows, and the app-shell unread badge uses `/api/v1/conversations/unread-count` with a SQL `SUM`. The compose modal no longer preloads `tenants?take=500`; it uses modal-scoped `/api/v1/tenants/page` search. Regression: `ConversationNotificationTests.ListPageAsync_ReturnsSqlCountAndRequestedWindow` and `ConversationNotificationTests.GetUnreadCountAsync_SumsUnreadCountsInSql`.
- Unit detail work tabs no longer fetch broad capped child lists: Rent uses `/api/v1/payments/page`, Expenses uses `/api/v1/expenses/page`, and Maintenance uses `/api/v1/work-orders/page` plus `/api/v1/expenses/page?workOrderLinkedOnly=true`. Regression: `PaymentServiceTests.ListPageAsync_ReturnsSqlCountAndRequestedWindow`, `ExpenseServiceTests.ListPageAsync_FiltersWorkOrderReceiptsAndPagesInSql`, and `web/src/lib/api/endpoints/expense-list-path.test.ts`.
- Converted primary-grid query keys now preserve the existing entity/portfolio invalidation prefix (`['entity', portfolioId, ...]`) so create/update/delete mutations refresh both legacy picker lists and new paged grids.
- Public application submission now blocks duplicate open applications by normalized email like scan-created applications already did. Regression: `ApplicationServiceTests`.
- `/settings/security` now exposes a confirm-password show/hide toggle instead of keeping hidden `showConfirm` state unreachable.
- `/admin/users` generated-password modal now has an `I saved it manually` escape path when clipboard write fails.
- `/settings/accounting` review queue no longer hardcodes `Create it` to tenants. Payments link to `Add tenant`, purchases/bills link to `Add vendor`, skipped non-cash rows expose no fake create action, and `/tenants?create=1` plus `/vendors?create=1` now open their create modals. Regression: `web/src/lib/accounting/review-create-target.test.ts`.
- `/portal/lease` suggestion chips now pass the selected question directly into the ask mutation instead of relying on the intermediate bound input state. Regression: `web/e2e/portal-lease.spec.ts`.

Browser evidence:
- `/tenants?create=1` redirected through login as Riley Morgan and opened the `New Tenant` modal with first-name focus.
- `/vendors?create=1` redirected through login as Riley Morgan and opened the `New Vendor` modal with vendor-name focus.
- `web/e2e/portal-lease.spec.ts` captured the portal lease Q&A POST body and proved the `Can I have a pet?` chip submits that exact question, then renders the mocked answer.
- `/properties` grid browser proof as Nora Vale showed initial and Name-sort requests go to `/api/v1/properties/page?take=20...`; no `/api/v1/properties?take=500` grid fetch occurred.
- `/tenants` grid browser proof as Nora Vale showed initial, search, and Active Leases sort requests go to `/api/v1/tenants/page?take=20...`; no `/api/v1/tenants?take=500` grid fetch occurred.
- `/leases` grid browser proof as Nora Vale showed initial load, `Avery` tenant-name search, and Tenant-column sort requests go to `/api/v1/leases/page?take=20...`; no `/api/v1/leases?take=500` grid fetch occurred.
- `/units` grid and Command Center browser proof as Nora Vale showed initial unit list, `1A` search, Open Repairs sort, and command-center dropdown search go to `/api/v1/units/list-with-health/page?take=20...`; no `/api/v1/units/list-with-health?take=500` request occurred in that flow.
- `/maintenance` work-order grid browser proof as Nora Vale showed initial load, `Front` search, New status, Normal priority, and Property-column sort requests go to `/api/v1/work-orders/page?take=20...`; no `/api/v1/work-orders?take=100` grid fetch occurred.
- `/appointments?view=list` browser proof as Nora Vale created a real appointment through the UI, then showed `Nora Showing` search, Showing type, Scheduled status, and Property-column sort requests go to `/api/v1/appointments/page?take=20...`; no legacy `/api/v1/appointments?take=20` list fetch occurred.
- `/vendors` grid browser proof as Nora Vale created a real vendor through the UI, then showed `Nora Vendor` search and Category-column sort requests go to `/api/v1/vendors/page?take=20...`; no `/api/v1/vendors?take=100` grid fetch occurred.
- `/owners` grid browser proof as Nora Vale created a real owner through the UI, then showed `Nora Owner Grid` search and Type-column sort requests go to `/api/v1/owner-entities/page?take=20...`; no legacy `/api/v1/owner-entities?...` grid fetch occurred.
- `/applications` grid browser proof as Nora Vale used the scan-created `Gray Johnson` application, then showed `Gray` search, Submitted status filtering, and Income-column sort requests go to `/api/v1/applications/page?take=20...`; no legacy `/api/v1/applications?...` grid fetch occurred. Console warnings/errors: zero.
- `/deposits` grid browser proof as Nora Vale created a real $1,225 holding for the scanned Avery Ellis lease, then showed Amount-column sort requests go to `/api/v1/security-deposits/page?take=20...`; no legacy `/api/v1/security-deposits?...` grid fetch occurred. Console warnings/errors: zero.
- `/maintenance/recurring` grid browser proof as Nora Vale created `TSK-397-HVAC-Filter-1782142807739` through the UI, then showed search, Next-column sort, and Active-only filtering requests go to `/api/v1/recurring-maintenance/page?take=20...`; no legacy `/api/v1/recurring-maintenance?...` grid fetch occurred. Console warnings/errors: zero.
- `/scan` draft grid browser proof as Nora Vale showed initial load, Reviewing tab filtering, Created-column sort, and Status-column sort requests go to `/api/v1/scans/page?take=20...`; no legacy `/api/v1/scans?...` draft-list fetch occurred. The separate recent-batches call to `/api/v1/scans/batches` remained. Console warnings/errors: zero.
- `/messages` staff conversation proof as Nora Vale loaded seeded local conversations, clicked `Load more`, opened New conversation, searched `Avery`, and observed only `/api/v1/conversations/page?take=20`, `/api/v1/conversations/page?skip=20&take=20`, `/api/v1/conversations/unread-count`, `/api/v1/tenants/page?take=20&sort=name...`, and `/api/v1/tenants/page?take=20&search=Avery&sort=name...`; no legacy `/api/v1/conversations` or `/api/v1/tenants?take=500` list request occurred. Console warnings/errors: zero.
- `/units/3?tab=rent` unit-detail proof as Nora Vale visited the rent, maintenance, and expenses tabs and observed only `/api/v1/payments/page?take=20&sort=dueDate&portfolioId=2&leaseId=3`, `/api/v1/work-orders/page?take=20&sort=-requestedAt&portfolioId=2&unitId=3`, `/api/v1/expenses/page?take=20&sort=-incurredAt&portfolioId=2&unitId=3&workOrderLinkedOnly=true`, and `/api/v1/expenses/page?take=20&sort=-incurredAt&portfolioId=2&unitId=3`; no legacy `/payments`, `/expenses`, or `/work-orders` list request occurred. Console warnings/errors: zero.

Status: Pass for the attached findings listed above. Remaining broad inventory gaps are tracked below under `Open While In Progress`.

### Nora Vale Empty-Portfolio New-Rental Scan Continuation

Acceptance criteria:
- Starting from no rental rows, a live-mode user can scan a lease document and create the property, unit, tenant, lease, and attached source document.
- The new-rental wizard can link to an existing property, choose create-new for a different unit, preserve visible tenant contact fields, and save a non-overlapping second lease.
- Hidden form defaults and server-side id override semantics match what the review UI promises.
- Provider-backed outbound email/SMS is not used during local scan testing.

Safety issue RC-SAFE-001:
- Repro: Started the generic `scripts/start-dev.sh` stack directly before switching to the safe audit wrapper.
- Observed: Engine inherited configured SendGrid settings and sent a synthetic verification email to `tsk397.full.1782131040@example.local`; engine log showed `POST https://api.sendgrid.com/v3/mail/send => 202`.
- Mitigation: Stopped the stack and restarted through `scripts/qa/start-scan-audit-local.sh`, which blanks SendGrid/SMTP/SMS provider settings. Subsequent provider-safety grep found no `SendGrid`, `SMTP`, `SMS`, `Twilio`, `Telnyx`, `Vonage`, `mail/send`, or `Email sent` lines in the safe-run logs.
- Status: Process issue documented; no production or sensitive recipient was used, but future local real-user passes must start with the safe wrapper.

Bug RC-UI-022:
- Repro: Upload `output/scan-fixtures/scan-lease-excerpt.pdf` on `/scan/new-rental`, fill the missing city/state/ZIP on Property step, then click Next.
- Observed: The wizard stayed on Step 1 with no visible user-actionable error.
- Root cause: `new-rental` used the shared property schema but omitted hidden required `status`.
- Fix: `createNewRentalPropertyForm()` now initializes `status: Active` and the page uses that helper.
- Regression: `web/src/lib/scan/new-rental-state.test.ts`.

Bug RC-UI-023:
- Repro: In `/scan/new-rental`, fill visible Tenant step fields for email, phone, and emergency contact, confirm, then query/open the created tenant.
- Observed: Tenant row was created with only first/last name; visible contact fields were silently dropped.
- Root cause: The web override payload only sent `tenantName`, and `ScanService` did not parse or apply tenant contact overrides for lease confirmations.
- Fix: The web override now includes `tenantEmail`, `tenantPhone`, and `tenantEmergencyContact`; `ScanService` parses those fields and passes them to tenant creation.
- Regression: `ConfirmAndCreateAsync_LeaseDraftWithTenantContactOverrides_PersistsCreatedTenantContact`.

Bug RC-UI-024:
- Repro: After one scanned lease created Harbor View Unit 4B, rescan the same lease, select existing Harbor View property, choose `Create new from the lease` for Unit, edit unit number to `5C`, fill Lena Park contact fields, and confirm.
- Observed: Review showed `Unit 5C (new)`, but the API rejected the confirm with `This unit already has an active lease (L-4B-2024-11) overlapping these dates`.
- Evidence: Confirm request body contained `"unitId": null` and `"unitNumber": "5C"`, but server logs showed lease creation was attempted against the extracted/grounded Unit 4B id.
- Root cause: nullable id overrides ignored JSON null, so an extracted `unit_id` survived even after the reviewer explicitly chose create-new.
- Fix: Scan confirm override parsing now treats present null/empty/zero id keys as explicit clears for work-order, lease, and application id overrides. Lease unit matching also uses a DB-side unit-number predicate instead of materializing units to compare in memory.
- Regression: `ConfirmAndCreateAsync_LeaseDraftWithNullUnitOverride_CreatesReviewerEditedUnit`.

Browser and DB evidence:
- Current stack: `https://localhost:5807`, `https://localhost:5806`, DB `rentalcommand_tsk397_full_ui`.
- Registered Nora Vale, verified via local `OutboxMessages`, selected live setup, and confirmed the live portfolio initially had zero properties, units, tenants, leases, payments, expenses, work orders, and scan-created rental data.
- First scan of `scan-lease-excerpt.pdf` created `Harbor View Apartments`, Unit `4B`, Tenant `Maria Chen`, Lease `L-4B-2024-11`, and attached the scanned source document.
- Patched rerun: second scan selected existing Harbor View property, selected `Create new from the lease` for unit, changed the unit to `5C`, filled tenant `Lena Park`, `lena.park@example.local`, `555-010-3972`, and `Noah Park 555-010-3973`, changed lease number to `L-5C-2026-06`, reviewed `Unit 5C (new)`, confirmed, and landed on `/leases/2`.
- DB proof after confirm:
  - Tenants: `Maria Chen` and `Lena Park | lena.park@example.local | 555-010-3972 | Noah Park 555-010-3973`
  - Units: `4B` and `5C` with bedrooms `2.0`, bathrooms `1.5`, rent `1500.00`
  - Leases: `L-4B-2024-11 -> Unit 4B`, `L-5C-2026-06 -> Unit 5C`
  - Scan drafts: draft `5` confirmed; stale drafts `1`, `2`, and `4` remain `Reviewing` and are reserved for scan-list reject/retry coverage.
- Lease detail `/leases/2` rendered `Harbor View Apartments · Unit 5C · Lena Park`, Overview tabs/actions, financials, parties, notes, and `View scanned document (PDF)`.

Status: Pass after fixes for this scan-new-rental continuation path. The scan list, batch, retry/reject, failed-state, and stale-draft cleanup workflows remain open below.

### Nora Vale Generic Scan Hub Image Records

Acceptance criteria:
- A real user can start from `/scan`, choose a target document type, upload a camera-style JPG, review extracted fields, confirm, and open the created record.
- Payment confirmation requires an explicit lease selection and blocks creation until the user selects one.
- Created records retain the uploaded image as a viewable document/preview where the destination page supports attachments.
- Scan list reflects confirmed records with `View record` links.

Synthetic images:
- `output/qa/production-scale-scans/02-expenses-camera/expense-001.jpg`
- `output/qa/production-scale-scans/03-payments-camera/payment-001.jpg`
- `output/qa/production-scale-scans/05-work-orders-camera/work-order-001.jpg`
- `output/qa/production-scale-scans/04-applications-camera/application-001.jpg`

Evidence:
- Receipt image draft `7` processed through `claude-cli:sonnet` vision, confirmed as Expense `1`, and opened `/accounting/expenses/1`. Detail rendered `Green Thumb Landscaping`, `$63.75 · Paid`, category `Repairs`, property `Cedar Point Flats`, scanned image preview `/expense-file/1`, receipt subtotal `58.75`, tax `5.00`, card last four `4242`, payment method `Visa`, document kind `Receipt`, line items, and history. DB proof: `ScanDrafts 7 | Confirmed | Expense`; expense row linked vendor/property and retained receipt fields.
- Rent-check image draft `8` processed through vision, extracted total `1125.00`, method `Check`, bank `First QA Bank`, payer `Avery Ellis`, check `8001`, transaction date `2026-02-03`, and notes for lease `QA-2026-001-1A`. Before selecting a lease, `Create Payment` stayed disabled with `Select a lease above to enable payment creation.` After selecting `#QA-2026-001-1A — Avery Ellis · Unit 1A`, confirmation created Payment `1` and opened `/accounting/payments/1`. Detail rendered `Avery Ellis`, `Rent · $1125 · Paid`, method `Check`, reference `8001`, notes, scanned document preview `/payment-file/1`, and history. DB proof: `ScanDrafts 8 | Confirmed | Payment`; payment row `1 | LeaseId 3 | Amount 1125.00 | Method Check | ExternalReference 8001 | PaidDate 2026-02-03`.
- Maintenance request image draft `9` processed through vision, extracted property `Cedar Point Flats`, title `Front door lock sticks`, unit id `3`, tenant id `4`, priority `Normal`, and description. Confirmation created WorkOrder `1` and opened `/maintenance/1`. Detail rendered title, `New`, `Normal`, `Cedar Point Flats`, `Unit 1A`, category `General`, full description, attached scan photo, and history. DB proof: `ScanDrafts 9 | Confirmed | WorkOrder`; work order row `1 | PropertyId 2 | UnitId 3 | TenantId 4 | Front door lock sticks | General | Normal | New`.
- Rental application image draft `10` processed through vision, extracted `Gray Johnson`, email `qa.applicant.001@example.local`, phone `555-0101`, employer `QA Employer 1`, income `3850.00`, requested home `Cedar Point Flats Unit 1A`, and ID last four `1000`. Confirmation redirected to `/applications`, where the grid displayed the new row as `Submitted`; detail `/applications/1` rendered applicant data, requested property `Cedar Point Flats`, unit `1A`, income `$3,850.00`, no-consent screening disabled state, and notes `Applying for: Cedar Point Flats Unit 1A. ID last-4: 1000.` DB proof: `ScanDrafts 10 | Confirmed | Application`; application row `1 | PropertyId 2 | UnitId 3 | Gray Johnson | Submitted`.
- Console warning/error count after these flows: zero warnings, zero errors. Network proof for the application flow ended in `POST /api/v1/scans/10/confirm => 200`, `/api/v1/applications => 200`, `/api/v1/applications/1 => 200`, and `/api/v1/applications/1/screening => 200`.

Watch item:
- The scan detail page already polls while `Pending` or `Processing`. During manual testing, reloads sometimes raced worker completion; no confirmed auto-refresh defect is logged yet. Reproduce only if the engine has already logged `Scan extraction succeeded` and the open page remains in `Processing` past the next poll interval.

Status: Pass for generic scan-hub image flows covering expense, payment, work order, and application records. Retry/reject/batch/failed-state variants remain open.

### Properties Grid and Detail Labeling

Acceptance criteria:
- Property type/status displays and select triggers use landlord-facing labels while preserving API enum values in submitted payloads and filters.
- Property create/edit controls offer only valid API enum values.
- The fix applies consistently across the properties grid, type/status filters, create/edit modal, property detail inline fields, and onboarding property setup.

Bug RC-UI-025:
- Repro: Open `/properties`, inspect the Type column/filter, open New Property, or open a property detail page.
- Observed: UI exposed raw enum values such as `MultiFamily` and `UnderMaintenance`. The shared `PropertyFields` control also offered invalid `Townhouse` and `Other` values that do not match the `PropertyType` API union.
- Fix: Added shared property type/status label helpers and wired properties list/detail, shared property fields, and onboarding to use readable labels while keeping submitted values as API enum values.
- Regression: `web/src/lib/properties/property-labels.test.ts`.

Evidence:
- `rtk node --test --experimental-strip-types web/src/lib/properties/property-labels.test.ts`: 4 passed.
- Playwright browser proof on `https://localhost:5807` as Nora Vale verified `/properties`, the type filter, New Property modal, and Cedar Point Flats detail render `Multi-family`, do not leak `MultiFamily`, and no longer offer `Townhouse` or `Other` in the property type options.

Status: Pass after fix for property enum labeling across the covered property/onboarding surfaces.

### Spreadsheet Import

Acceptance criteria:
- Entity cards for Tenants, Properties, and Units select the correct import type and show the expected columns.
- Template download produces an authenticated CSV download for the selected entity.
- Unsupported drag/drop files are rejected client-side without starting an import request or setting selected-file state.
- CSV click upload runs a dry-run preview immediately, shows valid and invalid rows, and allows only valid rows to be committed.
- Partial commits create valid rows, skip invalid rows, and keep row-level errors visible.
- Result links route to the destination list, `Import another file` clears selected/result state, and switching entity clears stale preview/result/file state.
- Console warnings/errors remain zero during the workflow.

Bug RC-UI-026:
- Repro: Import one valid property row and one invalid property row from `/import`.
- Observed: The result summary rendered `Created 1 propertie.`
- Fix: Import entity metadata now carries explicit singular/plural record labels instead of stripping a trailing `s` from the destination list label.
- Regression: `web/e2e/import.spec.ts`.

Bug RC-UI-027:
- Repro: Preview a CSV with exactly one valid unit row.
- Observed: The summary rendered `1 of 1 row look good.`
- Fix: The preview summary now uses explicit `looks`/`look` grammar based on the row count.
- Regression: `web/e2e/import.spec.ts`.

Evidence:
- Browser proof on `https://localhost:5807/import` as Nora Vale downloaded `tenant-import-template.csv`.
- Dropping `not-a-spreadsheet.txt` showed `Please choose a CSV file (a spreadsheet saved as ".csv").`, left no selected-file card, and did not add any `/api/v1/import` request.
- Tenant mixed CSV preview showed `1 of 2 rows look good`, a valid row, and row-level `FirstName` validation for the invalid row; switching to Properties cleared the stale file and preview.
- Property mixed CSV committed one valid property and skipped one invalid `type` row; result summary rendered `Created 1 property. 1 row skipped`, and `View properties` routed to `/properties`.
- Unit CSV for that imported property previewed `1 of 1 row looks good` and committed `Created 1 unit`.
- Switching from the Unit result to Tenants cleared stale result/file state; a final valid tenant import rendered `Created 1 tenant`; `Import another file` returned to the empty dropzone.
- `rtk env PW_BASE_URL=https://localhost:5807 PW_EMAIL=tsk397.full.1782131040@example.local PW_PASSWORD='AuditPass!23' pnpm --dir web exec playwright test e2e/import.spec.ts --project=chromium --reporter=list`: 1 passed.
- `rtk pnpm --dir web test:unit`: 84 passed.
- `rtk pnpm --dir web check`: 0 errors, 4 existing `PageHeader.svelte` unused-selector warnings.

Status: Pass after fixes for the covered spreadsheet import workflow.

### Settings Security

Acceptance criteria:
- Security page loads for a signed-in local-password account and the Back to settings link is available.
- Current, new, and confirmation password visibility toggles change the input type and accessible label state.
- Submit stays disabled until current password is present, new password satisfies the displayed policy, and confirmation matches.
- Password policy hints and mismatch validation are visible before submit.
- Wrong-current-password submission returns a user-facing error and does not change the password.
- Successful password change clears all password fields and shows success.
- The test account password is changed back and verified before continuing the audit.

Evidence:
- Browser proof on `https://localhost:5807/settings/security` as Nora Vale exercised all three show/hide toggles, weak-password rules, mismatch validation, disabled submit, wrong-current-password API error, successful change to `AuditTemp!24`, and successful change-back to `AuditPass!23`.
- Proof script network log showed three enhanced form posts to `/settings/security?/changePassword`: wrong current, change to temporary password, and change back.
- Final API login verification with `AuditPass!23` returned success; `restored: true`.
- Browser page errors and console warnings/errors were zero.
- `rtk env PW_BASE_URL=https://localhost:5807 API_BASE_URL=https://localhost:5806 PW_EMAIL=tsk397.full.1782131040@example.local PW_PASSWORD='AuditPass!23' PW_TEMP_PASSWORD='AuditTemp!24' pnpm --dir web exec node output/playwright/security-proof.mjs`: pass with `restored: true`.

Status: Pass for the local-password account workflow. Google-only/no-local-password messaging and stale-session failure state remain open account/session variants.

### Settings Accounting

Acceptance criteria:
- Accounting settings page loads for a signed-in account and exposes Back to settings navigation.
- Configured-but-not-connected provider state renders the provider card, status, explanatory copy, and connect action without showing connected-only import, mapping, direction, or disconnect controls.
- OAuth callback error return renders a user-facing toast and strips the query parameter so refresh does not repeat the toast.
- Browser pass does not initiate external OAuth, import, disconnect, or provider mutation without sandbox authorization.

Evidence:
- API status proof for Nora Vale returned QuickBooks `configured:true`, `status:null`, `pullEnabled:true`, `pushEnabled:false`, and zero imported/review counts.
- Playwright CLI login on `https://localhost:5807/login` reached `/settings/accounting` as Nora Vale.
- Snapshot rendered `Connect your accounting`, Back to settings, QuickBooks status `Not connected`, explanatory provider copy, and one visible provider action: `Connect QuickBooks`.
- The connected-only controls were not visible in this state: pull toggle, disabled push row, date range import, mappings, review queue, and disconnect.
- Navigating to `/settings/accounting?error=access_denied` displayed `Connection cancelled - you did not grant access.` and rewrote the URL back to `/settings/accounting`.
- Back to settings clicked through to `/settings`.
- Browser-side requests stayed on local same-origin app/API routes; console warnings/errors remained zero.

Status: Partial pass for the local unconnected accounting shell and OAuth-return error banner. The actual QuickBooks connect/reconnect/disconnect, import, direction toggles, mapping confirmation, and review-queue create-target workflows remain gated until sandbox credentials are available.

### Activity History And Admin Forensic Audit

Acceptance criteria:
- Staff activity history loads portfolio-scoped rows, exposes Admin-only Advanced navigation, and supports refresh, search, action filter, entity filter, no-results state, pagination state, and row deep links.
- Following an activity row to a scanned-source record must not emit broken image/file requests when the stored file metadata row has no readable backing blob.
- Admin forensic audit loads behind the Admin role, supports refresh, search, action/entity filters, no-results state, row disclosure, raw old/new JSON panels, IP address display, entity links, and filtered CSV export.
- Browser pass must not perform any provider-backed or destructive action.

Bug RC-UI-028:
- Repro: From `/audit`, filter to `Created` + `Payment`, click the `Payment #1` row, and open `/accounting/payments/1`.
- Observed: Payment detail rendered a scanned-document preview because `hasScan` came from a `StoredFiles` metadata row, then the browser requested `/payment-file/1?thumb=true` and logged a 404 because the local blob was missing.
- Root cause: detail DTO scan flags trusted attachment metadata without verifying storage availability. The serving endpoint correctly returned 404 for unreadable blobs, so the UI could advertise a file that could not be served.
- Fix: added a shared available-file lookup that opens the blob before setting detail scan/receipt flags, and used it in payment, lease, work-order, and expense detail services. List/grid flags remain DB-side metadata only to avoid storage N+1 behavior.
- Regression: `PaymentServiceTests.GetAsync_DoesNotAdvertiseScanWhenStoredFileBlobIsMissing`.

Evidence:
- Local DB confirmed Nora Vale is an active Admin in portfolio 2.
- `/audit` loaded `Page 1 · 25 shown`; Advanced link was visible, Refresh stayed local, unmatched search `no-audit-hit-1782149519` showed `No audit entries found`, and `Created` + `Payment` filters loaded `Page 1 · 1 shown`.
- Clicking the filtered payment row reached `/accounting/payments/1`; after the fix the API returned `hasScan:false` / `scanIsImage:false`.
- Playwright proof on the payment detail returned `scannedCards:0`, `paymentFileRequests:[]`, `consoleErrors:[]`, and `pageErrors:[]`; the only relevant detail request was `GET /api/v1/payments/1`.
- `/admin/audit` loaded `Page 1 · 25 shown`; unmatched search `no-admin-audit-hit-1782149519` showed the no-results state; `Created` + `Payment` filters loaded `Page 1 · 1 shown`.
- Opening the forensic row showed two raw JSON panels, IP `::1`, and payment entity links. Filtered CSV export requested `/api/v1/admin/audit/export?sort=-timestamp&operation=Created&entityType=Payment` and downloaded `audit-2026-06-22-17-33-17.csv`; the generated local artifact was deleted after verification.
- Browser page errors and console errors were zero during the post-fix `/audit`, payment detail, and `/admin/audit` proofs.

Status: Pass for staff activity history and Admin forensic audit controls covered above. Platform Engine health and remaining admin role/error variants remain open.

### Admin Team Management

Acceptance criteria:
- Team page loads behind the Admin role through a paged API contract and shows current members with role, status, joined date, and pagination state.
- Current admin's own role/status controls are disabled to avoid self-lockout.
- Invite member modal keeps submit disabled until email is present, supports role selection and optional temporary password, and creates a local team member without external calls.
- Auto-generated temporary-password modal shows the created member email, blocks accidental close/Escape until copy or manual-save acknowledgement, and exposes a copy action.
- Role changes and active/inactive toggles persist through the API and refresh the list.
- Browser pass must not hit an unbounded team-member list endpoint or emit page/console errors.

Bug RC-UI-029:
- Repro: Create a team member without a manually supplied temporary password.
- Observed: the generated-password modal was typed as if the create response were a flat `TeamMember`, but the API returns `{ member, generatedPassword }`; the modal could read the wrong email field.
- Root cause: frontend `CreateTeamMemberResponse` did not match `AdminUsersController.Create`.
- Fix: corrected the TypeScript response shape and used `result.member.email` for the toast and generated-password modal.
- Regression: `rtk pnpm --dir web check` plus browser proof for generated email `tsk397.team.paged.1782150198228@example.local`.

Bug RC-UI-030:
- Repro: Open `/admin/users` and inspect the team-member load path.
- Observed: the UI used the legacy list endpoint, which returned the portfolio team list without an explicit paged response/count contract.
- Root cause: admin user management predated the hard DB-side paging rule.
- Fix: added `GET /api/v1/admin/users/page` with SQL-side count, search, sort, skip, and take; kept `GET /api/v1/admin/users?take=50` bounded for compatibility; changed the Team page to use the page endpoint.
- Regression: `AdminUsersControllerTests.ListPage_ReturnsSqlCountAndRequestedWindow`.

Evidence:
- Browser proof on `https://localhost:5807/admin/users` as Nora Vale loaded `/api/v1/admin/users/page?take=20&sort=-createdAt`, showed `Page 1 · 2 shown`, and confirmed the current admin's own role/status controls were disabled.
- Created `tsk397.team.paged.1782150198228@example.local` through the invite modal; submit started disabled, generated-password response returned HTTP 201, modal showed the created email, generated a 16-character required-class password, blocked Escape before acknowledgement, and allowed close after manual-save/copy acknowledgement.
- Changed the new member role from Agent to Manager, deactivated it, then reactivated it; the API returned 200 for each PATCH and the refreshed row reflected Manager/Inactive/Active states.
- Browser page errors and console errors were zero for the post-restart proof; older cumulative console artifacts included pre-restart 404s from before the new API route was live.

Status: Pass for the core Admin Team controls covered above. Role-denied, stale-session, duplicate email, tenant-linked team member, and service-error variants remain open.

### Public Docs, Application, And Signing

Acceptance criteria:
- Public `/docs` loads anonymously, supports search with count text, no-results state, start-here navigation, article breadcrumb/back, article table of contents, previous/next links, and missing-article state.
- Public `/apply/[token]` handles invalid tokens, loads the valid portfolio context, exposes property/unit selection, validates required applicant fields/consent, validates email format, accepts a synthetic camera-style image upload for extraction, keeps manual correction possible, and submits an application.
- Public `/sign/[token]` handles invalid tokens, expired tokens, active package load, PDF open/download, typed signature, drawn signature, clear behavior, ESIGN consent gating, decline modal cancel/confirm, and terminal signed/declined states.
- Expired pending signing links must reject both the signing package and direct document URL.
- Browser pass must stay on local app/API routes, use only synthetic local documents/images, and avoid outbound email/SMS/provider actions.

Bug RC-UI-031:
- Repro: Seed a pending signer with `ExpiresAtUtc` in the past, then request `GET /api/v1/sign/{token}` and `GET /api/v1/sign/{token}/document`.
- Observed: before this fix, both endpoints returned 200 for an expired-but-unacted signer, so the public page could render an active signing package and direct PDF link after expiry.
- Root cause: `NativeSigningService.GetPackageAsync` and `GetDocumentAsync` resolve tokens with `requireActive:false`; expiry was only checked in the `requireActive` block used by sign/decline mutations.
- Fix: `ResolveAsync` now rejects expired non-terminal signer/request pairs before the `requireActive` branch, while preserving already signed/declined/voided terminal reads for read-only terminal states.
- Regression: `NativeEsignTests.GetPackage_ExpiredPendingToken_IsRejected` and `NativeEsignTests.GetDocument_ExpiredPendingToken_IsRejected`.

Evidence:
- Docs proof on `https://localhost:5807/docs` loaded 20 visible category/article link entries, showed `9 of 30 articles match "lease"`, showed no-results for `zz-no-doc-match-397`, opened `/docs/welcome`, rendered a 3-item table of contents and previous/next navigation, returned to `/docs`, and showed `Article not found` for `/docs/not-a-real-article-397`.
- Public application proof showed invalid token copy, loaded `Apply to Nora Vale`, exposed the property/unit selectors, and showed required-field/consent validation plus invalid-email validation.
- Uploaded synthetic image `tmp/tsk397-public-application-id.png` through the same `accept="image/*"` scan input used for camera capture; `/api/v1/public/applications/{token}/scan-id` returned 200 and the UI reported that it filled first name, last name, date of birth, and current address.
- Completed the application with corrected synthetic applicant data; `/api/v1/public/applications/{token}` returned 201 and the page showed `Application submitted!`.
- Signing API proof after the fix returned `410 application/json` for both the expired package and expired document URL, while the active document URL returned `200 application/pdf` with the seeded local PDF.
- Browser signing proof showed invalid-link copy, expired-link copy, active package header for `PublicSign Resident`, PDF open/download affordance, initial disabled submit, drawn-signature enablement after consent, disabled submit after Clear, typed-name signing success (`Signed - all done`), decline dialog Escape cancel, reason entry, and terminal declined state.
- Browser page errors and unexpected console warnings/errors were zero for the final public-flow proof.

Status: Pass for the public docs, public application, and native public signing controls covered above. Duplicate-application handling, provider-backed email delivery, and staff-side send/resend signing workflows remain open.

## Route Inventory For Continued Pass

Core route map to exercise next:
- Dashboard/setup: `/`, `/get-started`, `/choose-setup`, `/setting-up`.
- Scan: `/scan`, `/scan/[draftId]`, `/scan/batch`, `/scan/batch/[id]`, `/scan/new-rental`.
- Rentals: `/properties`, `/properties/[id]`, `/units`, `/units/[id]`, `/tenants`, `/tenants/[id]`, `/leases`, `/leases/[id]`, `/applications`, `/applications/[id]`, `/owners`, `/owners/[id]`, `/owners-report`.
- Work: `/maintenance`, `/maintenance/[id]`, `/maintenance/inspections/[id]`, `/maintenance/recurring`, `/appointments`, `/appointments/[id]`, `/vendors`, `/vendors/[id]`.
- Inbox/comms: `/messages`, `/notices`.
- Money/reports: `/accounting`, `/accounting/expenses/[id]`, `/accounting/payments/[id]`, `/accounting/past-due`, `/accounting/year-end`, `/deposits`, `/deposits/[id]`, `/reports`, `/reports/[report]`, `/tax`.
- Settings/admin/support: `/settings`, `/settings/security`, `/settings/accounting`, `/audit`, `/admin/users`, `/admin/audit`, `/superadmin/engine`, `/ai`, `/docs`.
- Portal/public: `/portal/*`, `/apply/[token]`, `/sign/[token]`.

## Deferred External Integrations

- Plaid banking: requires sandbox login/connect flow.
- QuickBooks/accounting provider: unconnected settings shell is browser-proven; connected-provider workflows require sandbox credentials.

## Verification

- Browser: reports catalog, 13 generic report routes, report property filter, CSV export, print action, owner statement list/detail/export, tax Schedule E/export/year-end packet, year-end property filter, past-due mark-paid modal, money ledger filter/sort URL state, money create-dialog validation/cancel states, dashboard loaded state, Getting Started checklist/deep-link/reset states, settings notification email/broadcast flow, tenant/vendor create deep links, portal lease suggestion Q&A, assistant daily briefing, and assistant data Q&A were exercised with Playwright CLI against `https://localhost:5797`.
- `rtk git diff --check`: pass.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~ReportsServiceTests|FullyQualifiedName~ScheduleEServiceTests|FullyQualifiedName~OwnerStatementServiceTests|FullyQualifiedName~YearEndPacketTests|FullyQualifiedName~AccountingServiceTests|FullyQualifiedName~BankingServiceTests|FullyQualifiedName~PaymentServiceTests" --logger "console;verbosity=normal"`: 75 passed.
- `rtk pnpm --dir web test:unit`: 74 passed.
- `rtk pnpm --dir web check`: 0 errors, 4 existing `PageHeader.svelte` unused-selector warnings.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --logger "console;verbosity=minimal"`: 476 passed.
- `rtk env PW_BASE_URL=https://localhost:5797 PW_EMAIL=tsk397.ui.1782115363@example.local PW_PASSWORD='AuditPass!23' pnpm --dir web exec playwright test e2e/portal-lease.spec.ts --project=chromium`: 1 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~ClaudeCliLlmProviderTests|FullyQualifiedName~PortfolioQa" --logger "console;verbosity=normal"`: 21 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~ConversationNotificationTests"`: 4 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~NoticeDraftServiceTests"`: 2 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~ConversationNotificationTests|FullyQualifiedName~NoticeDraftServiceTests" --logger "console;verbosity=normal"`: 8 passed after the messages page/unread-count change.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~ScanBatchControllerTests"`: 14 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~ApplicationServiceTests"`: 13 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~AccountingConnectionServiceTests"`: 3 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~AccountingImportServiceTests"`: 5 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~BankingServiceTests" --logger "console;verbosity=normal"`: 18 passed.
- `rtk node --test --experimental-strip-types web/src/lib/scan/new-rental-state.test.ts`: 5 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~ScanServiceTests" --logger "console;verbosity=normal"`: 18 passed.
- Browser scan continuation on `https://localhost:5807`: `/scan/new-rental` lease PDF upload, existing property selection, create-new Unit 5C, tenant contact entry, review, confirm, `/leases/2` detail, and DB verification passed.
- `rtk node --test --experimental-strip-types web/src/lib/properties/property-labels.test.ts`: 4 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~LeaseLedgerServiceTests" --logger "console;verbosity=normal"`: 4 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~UnitDashboardServiceTests" --logger "console;verbosity=normal"`: 1 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~DailyBriefingServiceTests" --logger "console;verbosity=normal"`: 1 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~InspectionChecklistServiceTests" --logger "console;verbosity=normal"`: 6 passed.
- `rtk node --test --experimental-strip-types web/src/lib/components/file-drop.test.ts`: 3 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~PropertyServiceTests" --logger "console;verbosity=normal"`: 1 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~TenantServiceTests" --logger "console;verbosity=normal"`: 1 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~LeaseServiceListTests" --logger "console;verbosity=normal"`: 1 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~UnitServiceListTests" --logger "console;verbosity=normal"`: 1 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~WorkOrderServiceListTests" --logger "console;verbosity=normal"`: 1 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~AppointmentServiceListTests" --logger "console;verbosity=normal"`: 1 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~VendorServiceListTests" --logger "console;verbosity=normal"`: 1 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~OwnerEntityServiceListTests" --logger "console;verbosity=normal"`: 1 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~ApplicationServiceTests.ListPageAsync_ReturnsSqlCountAndRequestedWindow" --logger "console;verbosity=normal"`: 1 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~SecurityDepositServiceListTests" --logger "console;verbosity=normal"`: 1 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~RecurringMaintenanceTaskServiceTests" --logger "console;verbosity=normal"`: 7 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~ScanBatchControllerTests" --logger "console;verbosity=normal"`: 15 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~PaymentServiceTests.ListPageAsync_ReturnsSqlCountAndRequestedWindow|FullyQualifiedName~ExpenseServiceTests.ListPageAsync_FiltersWorkOrderReceiptsAndPagesInSql" --logger "console;verbosity=normal"`: 2 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~PaymentServiceTests|FullyQualifiedName~ExpenseServiceTests|FullyQualifiedName~WorkOrderServiceListTests" --logger "console;verbosity=normal"`: 18 passed.
- `rtk node --test --experimental-strip-types web/src/lib/api/endpoints/expense-list-path.test.ts`: 2 passed.
- `rtk pnpm --dir web check`: 0 errors, 4 existing `PageHeader.svelte` unused-selector warnings after the FileDrop/properties/tenants/leases/units/maintenance/appointments/vendors/owners/applications/deposits/recurring-maintenance/scan/messages UI changes.
- `rtk env PW_BASE_URL=https://localhost:5807 PW_EMAIL=tsk397.full.1782131040@example.local PW_PASSWORD='AuditPass!23' pnpm --dir web exec playwright test e2e/import.spec.ts --project=chromium --reporter=list`: 1 passed.
- `rtk pnpm --dir web test:unit`: 84 passed.
- `rtk pnpm --dir web check`: 0 errors, 4 existing `PageHeader.svelte` unused-selector warnings after the import-page copy fixes.
- `rtk env PW_BASE_URL=https://localhost:5807 API_BASE_URL=https://localhost:5806 PW_EMAIL=tsk397.full.1782131040@example.local PW_PASSWORD='AuditPass!23' PW_TEMP_PASSWORD='AuditTemp!24' pnpm --dir web exec node output/playwright/security-proof.mjs`: pass with `restored: true`.
- Browser settings-accounting proof on `https://localhost:5807`: QuickBooks configured-but-not-connected shell rendered only Back to settings and Connect QuickBooks, OAuth `access_denied` return showed a toast and stripped the query parameter, back navigation reached `/settings`, no external provider action was clicked, and console warnings/errors were zero.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~PaymentServiceTests.GetAsync_DoesNotAdvertiseScanWhenStoredFileBlobIsMissing" --logger "console;verbosity=normal"`: 1 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~PaymentServiceTests|FullyQualifiedName~ExpenseServiceTests|FullyQualifiedName~WorkOrderStatusTimelineTests|FullyQualifiedName~WorkOrderCostsTimingAndProjectionTests|FullyQualifiedName~WorkOrderTenantScheduleSmsTests|FullyQualifiedName~WorkOrderServiceListTests" --logger "console;verbosity=normal"`: 33 passed.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~AdminUsersControllerTests" --logger "console;verbosity=normal"`: 1 passed.
- `rtk pnpm --dir web check`: 0 errors, 4 existing `PageHeader.svelte` unused-selector warnings after the Admin Team page contract/type fix.
- Browser activity/admin proof on `https://localhost:5807`: `/audit` refresh, unmatched search, Created + Payment filters, payment row deep link, missing-blob payment detail, `/admin/audit` no-results/filter/disclosure/IP/raw JSON, and filtered CSV export passed with no console/page errors and no `/payment-file/1` request after the fix.
- Browser Admin Team proof on `https://localhost:5807`: `/admin/users` loaded through `/api/v1/admin/users/page?take=20&sort=-createdAt`, created `tsk397.team.paged.1782150198228@example.local`, showed the correct generated-password email and guarded close/Escape states, changed role Agent to Manager, deactivated/reactivated the row, and finished with console/page errors at zero.
- `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~NativeEsignTests.GetPackage_ExpiredPendingToken_IsRejected|FullyQualifiedName~NativeEsignTests.GetDocument_ExpiredPendingToken_IsRejected" --logger "console;verbosity=normal"`: 2 passed.
- Browser public proof on `https://localhost:5807`: `/docs` search/no-results/article/navigation/404, invalid and valid `/apply/[token]`, synthetic camera-style image scan, application validation/success, invalid/expired `/sign/[token]`, expired document 410, active PDF 200, typed/drawn/clear signature, sign terminal, decline modal Escape/reason/terminal, and zero unexpected console/page errors passed.
- Browser property-label proof on `https://localhost:5807`: `/properties`, type filter, New Property modal, and property detail use landlord-facing labels while preserving API enum values.
- Browser FileDrop proof on `https://localhost:5807/scan`: dragged unsupported CSV emitted a rejection warning, did not render as the selected file, and did not trigger `POST /api/v1/scans`.
- Browser properties grid proof on `https://localhost:5807`: initial load and Name sort used `/api/v1/properties/page?take=20...`; no `/api/v1/properties?take=500` grid fetch occurred.
- Browser tenants grid proof on `https://localhost:5807`: initial load, `Avery` search, and Active Leases sort used `/api/v1/tenants/page?take=20...`; no `/api/v1/tenants?take=500` grid fetch occurred.
- Browser leases grid proof on `https://localhost:5807`: initial load, `Avery` search, and Tenant sort used `/api/v1/leases/page?take=20...`; no `/api/v1/leases?take=500` grid fetch occurred.
- Browser units grid and Command Center proof on `https://localhost:5807`: initial load, `1A` search, Open Repairs sort, and command-center dropdown used `/api/v1/units/list-with-health/page?take=20...`; no `/api/v1/units/list-with-health?take=500` request occurred.
- Browser maintenance grid proof on `https://localhost:5807`: initial load, `Front` search, New status filter, Normal priority filter, and Property sort used `/api/v1/work-orders/page?take=20...`; no `/api/v1/work-orders?take=100` grid fetch occurred.
- Browser appointments list proof on `https://localhost:5807`: created `Nora Showing Grid Proof` via the UI, then list search/type/status/property-sort used `/api/v1/appointments/page?take=20...`; no legacy `/api/v1/appointments?take=20` list fetch occurred. Calendar still uses a bounded broad query and remains an open date-window risk.
- Browser vendors grid proof on `https://localhost:5807`: created `Nora Vendor Grid Proof` via the UI, then search/category-sort used `/api/v1/vendors/page?take=20...`; no legacy `/api/v1/vendors?take=100` grid fetch occurred.
- Browser owners grid proof on `https://localhost:5807`: created `Nora Owner Grid Proof` via the UI, then search/type-sort used `/api/v1/owner-entities/page?take=20...`; no legacy `/api/v1/owner-entities?...` grid fetch occurred.
- Browser applications grid proof on `https://localhost:5807`: used the scan-created `Gray Johnson` row, then search/status/income-sort used `/api/v1/applications/page?take=20...`; no legacy `/api/v1/applications?...` grid fetch occurred.
- Browser deposits grid proof on `https://localhost:5807`: created a `$1,225.00` holding for the scanned Avery Ellis lease, then amount-sort used `/api/v1/security-deposits/page?take=20...`; no legacy `/api/v1/security-deposits?...` grid fetch occurred.
- Browser recurring-maintenance grid proof on `https://localhost:5807`: created `TSK-397-HVAC-Filter-1782142807739`, then search/next-due-sort/active-only filtering used `/api/v1/recurring-maintenance/page?take=20...`; no legacy `/api/v1/recurring-maintenance?...` grid fetch occurred and console warnings/errors were zero.
- Browser scan grid proof on `https://localhost:5807`: initial load, Reviewing tab, Created sort, and Status sort used `/api/v1/scans/page?take=20...`; no legacy `/api/v1/scans?...` draft-list fetch occurred and console warnings/errors were zero.
- Browser messages grid/compose proof on `https://localhost:5807`: initial staff conversation load and `Load more` used `/api/v1/conversations/page?take=20...`, header unread count used `/api/v1/conversations/unread-count`, compose tenant search used `/api/v1/tenants/page?take=20...`, no legacy `/api/v1/conversations` or `/api/v1/tenants?take=500` list fetch occurred, and console warnings/errors were zero.
- Browser unit-detail tab proof on `https://localhost:5807`: rent, maintenance, and expenses tabs used paged `/api/v1/payments/page`, `/api/v1/work-orders/page`, and `/api/v1/expenses/page` requests with `take=20`; work-order receipt filtering used `workOrderLinkedOnly=true`; no legacy child-list endpoint was observed and console warnings/errors were zero.

## Pass 2 Fresh-User Scan Findings

Pass-2 local account: `tsk397.pass2.20260622.1852@example.local`. Synthetic document evidence was generated from scratch and kept under local ignored QA output at `output/qa/tsk397-pass2/`: lease PDF, lease camera images, application PDF, receipt PDF/image, rent-check image, and maintenance photo.

Fixed in this slice:

- `TSK397-B017` — `/scan/new-rental` displayed contradictory progress (`Step 1 of 4 · Property` beside `1/5`) and used receipt/invoice upload copy on lease-only drop zones. Fix: shared helper renders the full five-step sequence including review, and `FileDrop` accepts context-specific title/helper text. Regression: `web/src/lib/scan/new-rental-state.test.ts`.
- `TSK397-B018` — lease scans could visibly contain tenant email/phone, but the lease extraction schema did not ask the engine to return `tenant_email`, `tenant_phone`, or `tenant_emergency_contact`; the confirm path already knew how to persist them if present. Fix: lease schema instructions and fields now include tenant contact fields. Regression: `LeaseExtractionSchemaTests`.
- `TSK397-B019` — work-order deep links from unit maintenance cards and audit/activity rows used `/work-orders/{id}`, which is an API/mobile route shape and has no protected web detail page. The canonical web route is `/maintenance/{id}`. Fix: unit maintenance open action and audit DTO detail hrefs now emit `/maintenance/{id}`. Regression: `AuditDetailHrefTests`.

Browser retest on `https://localhost:5817` as Taylor Brooks:

- `/scan/new-rental` capture page rendered lease-specific drop zones: `Drop lease photos here` and `Drop the lease PDF here`.
- Uploaded `output/qa/tsk397-pass2/harbor-view-lease.pdf`; after extraction reached the real wizard, the stepper rendered `Step 1 of 5 · Property` and `1/5`.
- `/units/1?tab=maintenance` expanded the scan-created `Kitchen sink leak` work order; clicking `Open work order` navigated to `/maintenance/1`.
- After restarting the API with the patched DTO, the unit recent-activity rail's `Created a work order` link rendered `/maintenance/1`.
- Playwright console check after the route-link proof returned zero warnings and zero errors.

Open pass-2 product gaps:

- Application scan detail did not expose the attached scanned source document; fixed in Pass 10 by surfacing the stored `Application` file on the detail page.
- Expense receipt scan stores extracted vendor details but does not auto-create or link a Vendor entity; the categorization screen can still show "No vendor".
- Payment scan did not auto-match a clear rent check to the only matching lease; the user had to select the lease manually.
- Maintenance scan review exposes raw numeric relationship IDs in editable "Other" fields. The final work-order detail also does not show the tenant even when the scan extracted a tenant id.
- New-user live setup still starts with manual property/spreadsheet onboarding; the scan-first path is in the sidebar/scan page, not the primary onboarding path.

## Open While In Progress

- This is not yet a claim that every button/modal/grid/state in the product has been exercised. Continue real-user flow through dashboard cards/actions, settings/security account/session variants, settings/accounting connected-provider states, platform Engine health, in-app help variants, assistant delivery variants, portal, banking connection review states, scan batch/retry/reject variants, tenant-notice draft/send states, row delete confirmations, and remaining CRUD unhappy/edge states.
- Plaid and QuickBooks remain deferred until sandbox credentials are available.
- Continue static DB-side sweep outside the repaired report/accounting/banking controller/service scope; no endpoint should be marked production-scale until generated SQL is confirmed for filtering, sorting, paging, grouping, and aggregation. Read-only follow-up found additional high-confidence DB-side risks in lease ledger, unit timeline, daily briefing, inspection completion, unpaged document/deposit/opening-balance/conversation/portal lists, and broad `take:100/500` grid screens. Lease ledger, unit timeline, daily briefing, inspection completion, Command Center unit search, the scan draft grid, staff messages grid, staff messages unread count, staff messages compose tenant picker, Admin Team, and the properties/tenants/leases/units/maintenance/appointments/vendors/owners/applications/deposits/recurring-maintenance primary grids are now fixed with focused regressions; other unpaged/capped lists and shared-grid risks remain open.
- Tenant-notice draft review/approve/send still needs an eligible synthetic notice condition and provider-safe channel setup.

## Inventory Gaps For Next Browser Pass

The current pass is not a complete every-control inventory. The next new-user pass must explicitly cover:
- `/scan`, `/scan/batch`, `/scan/batch/[id]`, `/scan/new-rental`, and `/scan/[draftId]`: batch creation/detail, mixed success/failure states, retry, reject, hold, bulk operations, progress/error banners, draft edit/re-open, validation failures, and destructive confirmations.
- `/banking`, `/settings/accounting`, and `/plaid/auth`: Plaid unconfigured/configured states, disabled connect, sandbox exchange validation, manual import invalid/missing/success states, transaction status filters, match/ignore/unignore/clear/review controls, provider connect/reconnect/disconnect, pull toggles, disabled push, date range import, mapping confirm, and provider-safe review queue create-target links. `/settings/accounting` unconnected QuickBooks shell and OAuth `access_denied` return banner are browser-proven.
- `/notices` and `/tenants/[id]` notice panels: generate drafts, filter, edit/save, fair-housing acknowledgement, suggested rewrite, channel checkboxes, approve/send, dismiss, conversation link, force renewal/move-out drafts, retry, no-channel blocking, provider-safe portal-only sends, and reload persistence.
- `/properties`, `/properties/[id]`, `/units`, `/units/[id]`, `/tenants`, `/tenants/[id]`, `/leases`, and `/leases/[id]`: detail tabs, inline edits, contextual action menus, archive/delete/restore where available, remaining non-primary-grid search/filter/sort paths, empty states, validation, save/cancel, and success/error banners. Properties, tenants, leases, units, maintenance, recurring-maintenance, owners, applications, deposits, vendors, and appointment-list primary grids are server-side/page browser-proven.
- `/appointments` and `/appointments/[id]`: calendar month/week/agenda date-window loading, drag-reschedule failure snapback, create-at-slot, detail edit/delete, and calendar/list cross-invalidation. The appointments list primary grid is server-side/page browser-proven.
- Portal routes `/portal`, `/portal/messages`, `/portal/maintenance`, `/portal/payments`, `/portal/notifications`, `/portal/appointments`, and `/portal/lease`: dashboard links, compose/reply/cancel/delete, Enter vs Shift+Enter, unread invalidation, maintenance request create with photo preview/remove/failure, detail/timeline, Stripe unavailable, checkout success/cancel params, autopay on/off, notification action links/read state, appointment real workflow or placeholder defect, pagination, empty/loading/error states, and optimistic-update failures.
- `/settings/accounting`: connected-provider toggle/submit/reset/copy actions, confirmations, persistence after reload, provider-unconfigured states, review queue controls, and stale-session failure behavior. `/settings/security` still needs Google-only/no-local-password and stale-session variants; local password-change controls are browser-proven.
- `/admin/users` and `/superadmin/engine`: Admin Team core list/create/generated-password/role/status controls are browser-proven; remaining Admin Team variants include role-denied, stale-session, duplicate email, tenant-linked member, service-error, and larger-page pagination. Engine health refresh/reindex actions, exports where present, no-results states, and loading/error states remain open. `/audit` and `/admin/audit` core filters/refresh/no-results/detail/export controls are browser-proven; role-denied/error variants remain open.
- `/docs`, public `/apply/[token]`, and public `/sign/[token]`: docs search/no-results/article/navigation/404, valid/invalid application links, required/invalid-email validation, synthetic image scan success, application submit success, valid/invalid/expired signing links, expired direct document rejection, active PDF open/download, typed/drawn signature, clear, decline modal, and signed/declined terminal states are browser-proven. Remaining variants include duplicate applications, public application scan failure/no extracted fields, staff-side signing send/resend, provider-backed delivery, larger signing envelopes with multiple signers, and stale/reused token reload states after terminal actions.
- App shell/navigation: staff versus portal role menus, collapsible groups, collapsed rail, mobile drawer/overlay, command-center search/no matches, header badges, account menu/logout, theme toggle, and role-hidden route gates.
- Shared controls in composed routes: data grids, pagination, search inputs, confirm dialogs, app-shell navigation, command-center navigation, notification bell/list interactions, keyboard/focus/escape behavior, and select-all/bulk states. FileDrop unsupported-file rejection is fixed and browser-proven for drag/drop on `/scan`; upload failure states still need route-specific coverage.
- `/activity/*` and `/analytics/*`: route-level matrix with evidence for each visible control cluster and empty/error/loading state.

## DB-Side Rule Findings

Fixed in this branch:
- `ReportsService.GetRentRollAsync`: totals now use SQL aggregates over the scoped lease query.
- `ReportsService.GetRentLedgerAsync`: running balances and charged/paid totals now use SQL projection/aggregates; in-memory code only assembles nested lease DTOs.
- `ReportsService.GetCashFlowAsync`, `GetPropertyProfitAndLossAsync`, `GetOccupancyAsync`, and `GetLeaseExpirationsAsync`: total rows now come from SQL aggregates over the scoped query.
- `AccountingService.GetSnapshotAsync` / `GetPastDueAsync`: past-due count and totals now aggregate SQL-side over the grouped per-lease query.
- `AccountingService.GetYearEndPacketDataAsync`: year-end P&L, cash-flow, rent-roll, and past-due totals now use SQL-side grouping/filtering.
- `BankingService.GetReviewQueueAsync`: review queue count and rows now use the DB-side suggestion predicate before in-memory scoring.
- `BankingService.GetSummaryAsync`: `LastSyncedAt` now uses SQL `MAX`.
- `BankingService.ExchangePlaidPublicTokenAsync`: Plaid reconnect matching now queries indexed external item/account hash columns instead of filtering decrypted rows in memory.
- `OwnerStatementService.GetForOwnerAsync`, `ListOwnersWithNetAsync`, and `GetTotalNetToOwnersAsync`: owner totals and distributions now aggregate in SQL.
- `ScheduleEService.GetReportAsync`: transaction/loan income and expense totals aggregate in SQL; depreciation remains a deterministic per-property formula over property basis fields.
- `ReportsService.GetSecurityDepositRegisterAsync`: deduction JSON totals now use maintained scalar `DeductionsTotal` with migration backfill.
- `ReportsService.GetVendor1099Async`: vendor filtering, tax-year range, and total paid are SQL-side.
- `PortfolioQaService` assistant tools: property totals aggregate in SQL; recent expenses/payments use SQL totals over the full filtered window while the visible row preview stays SQL-capped; upcoming appointment/inspection events are merged, sorted, and capped through a SQL union before enum/date formatting.
- `ConversationService`: message counts and ordered message details now project through SQL instead of mapping materialized navigation collections.
- `NoticeDraftService`: non-forced renewal/move-out generation now narrows candidate leases DB-side before notice evaluation.
- `AccountingImportService`: parked retry rows, deposit account detection, and latest active lease lookup now filter/order/group in SQL before materialization.
- `AccountingConnectionService`: mapping review and review queue endpoints now filter/sort/page in SQL and the settings page fetches bounded sections instead of filtering full arrays client-side.
- Audit pages: exactly full audit pages now use server overfetch and explicit next-page state instead of `count >= take`.
- `LeaseService.GetLedgerAsync`: payment ledger rows now project `LedgerDate` and order by date/id in SQL before materialization. Regression: `LeaseLedgerServiceTests.GetLedgerAsync_OrdersPaymentRowsInSql`.
- `UnitDashboardService.GetTimelineAsync`: child entity scopes now stay as translated subqueries inside the paged audit-log query instead of preloading lease/payment/work-order/inspection/appointment/expense id lists. Regression: `UnitDashboardServiceTests.GetTimelineAsync_ScopesChildAuditRowsInSqlWithoutPreloadingIds`.
- `DailyBriefingService.ComposeAsync`: briefing candidates now come from a single translated `UNION`/`ORDER BY`/`LIMIT` query across maintenance, overdue rent, rent due, appointments, inspections, and expiring leases before bounded bullet formatting and optional LLM summarization. Regression: `DailyBriefingServiceTests.ComposeAsync_RanksAndCapsBriefingCandidatesInSql`.
- `InspectionService.CompleteAsync`: failed checklist items now create inspection follow-up work orders and initial status events in one tracked batch, then hydrate all new broadcast payloads with one SQL projection instead of revalidating/hydrating per failed item. Regression: `InspectionChecklistServiceTests.Complete_BatchesFailedItemWorkOrderCreationWithoutPerItemScopeQueries`.
- `PropertyService.ListPageAsync` and `/properties` grid: primary property table now uses a page contract with SQL count, search/filter, sort, skip, and take instead of a capped client-side `take=500` list. Regression: `PropertyServiceTests.ListPageAsync_ReturnsSqlCountAndRequestedWindow`.
- `TenantService.ListPageAsync` and `/tenants` grid: primary tenant table now uses a page contract with SQL count, search, sort, skip, take, and active-lease count ordering instead of a capped client-side `take=500` list. Regression: `TenantServiceTests.ListPageAsync_ReturnsSqlCountAndRequestedWindow`.
- `LeaseService.ListPageAsync` and `/leases` grid: primary lease table now uses a page contract with SQL count, search, status filter, sort, skip, take, and tenant-name ordering instead of a capped client-side `take=500` list. Regression: `LeaseServiceListTests.ListPageAsync_FiltersSortsAndPagesInSql`.
- `UnitService.ListWithHealthPageAsync`, `/units` grid, and Command Center unit search: unit health rows now use a page contract with SQL count, search, property filter, sort, skip, take, and open-work-order-count ordering instead of capped client-side `take=500` health lists. Regression: `UnitServiceListTests.ListWithHealthPageAsync_ReturnsSqlCountAndRequestedWindow`.
- `WorkOrderService.ListPageAsync` and `/maintenance` grid: primary work-order table now uses a page contract with SQL count, search, status/priority filters, sort, skip, take, and property-name ordering instead of a capped client-side `take=100` list. Regression: `WorkOrderServiceListTests.ListPageAsync_FiltersSortsAndPagesInSql`.
- `AppointmentService.ListPageAsync` and `/appointments?view=list` grid: primary appointment list now uses a page contract with SQL count, search, type/status filters, sort, skip, take, and property/tenant-name ordering instead of a one-window client-side filtered list. Regression: `AppointmentServiceListTests.ListPageAsync_FiltersSortsAndPagesInSql`.
- `VendorService.ListPageAsync` and `/vendors` grid: primary vendor table now uses a page contract with SQL count, search, sort, skip, and take instead of a capped client-side `take=100` list. Regression: `VendorServiceListTests.ListPageAsync_ReturnsSqlCountAndRequestedWindow`.
- `OwnerEntityService.ListPageAsync` and `/owners` grid: primary owner table now uses a page contract with SQL count, search, sort, skip, and take instead of a capped client-side list. Regression: `OwnerEntityServiceListTests.ListPageAsync_ReturnsSqlCountAndRequestedWindow`.
- `ApplicationService.ListPageAsync` and `/applications` grid: primary application table now uses a page contract with SQL count, search, status filter, sort, skip, and take instead of client-side search/sort/page over a status-filtered list. Regression: `ApplicationServiceTests.ListPageAsync_ReturnsSqlCountAndRequestedWindow`.
- `SecurityDepositService.ListPageAsync` and `/deposits` grid: primary security-deposit table now uses a page contract with SQL count, optional lease filter, sort, skip, and take instead of an unpaged list with client-side sort/page. Regression: `SecurityDepositServiceListTests.ListPageAsync_ReturnsSqlCountAndRequestedWindow`.
- `ConversationService.ListPageAsync`, `GetUnreadCountAsync`, `/messages`, and the app-shell message badge: staff conversation rows now use SQL count/sort/skip/take for initial/load-more windows, unread badge totals use a SQL `SUM` instead of loading all conversations and reducing client-side, and the compose modal uses the existing tenant page query instead of preloading `tenants?take=500`. Regression: `ConversationNotificationTests.ListPageAsync_ReturnsSqlCountAndRequestedWindow` and `ConversationNotificationTests.GetUnreadCountAsync_SumsUnreadCountsInSql`.
- `PaymentService.ListPageAsync`, `ExpenseService.ListPageAsync`, and unit detail Rent/Expenses/Maintenance tabs: child payments, expenses, work orders, and work-order receipt rows now use page contracts with SQL count/filter/sort/skip/take instead of unit-tab `take=500` lists and client-side receipt filtering. Regression: `PaymentServiceTests.ListPageAsync_ReturnsSqlCountAndRequestedWindow`, `ExpenseServiceTests.ListPageAsync_FiltersWorkOrderReceiptsAndPagesInSql`, and `web/src/lib/api/endpoints/expense-list-path.test.ts`.
- `AdminUsersController.ListPage` and `/admin/users`: team-member management now uses a page contract with SQL count, search, sort, skip, and take instead of an unpaged list. Regression: `AdminUsersControllerTests.ListPage_ReturnsSqlCountAndRequestedWindow`.

Remaining:
- Unpaged list endpoints still need tightening or SQL proof: documents, opening balances, portal conversation/payment/work-order lists, appointment calendar date-window loading, and remaining capped `take:100/500` screens other than the fixed properties/tenants/leases/units/maintenance/appointments-list/vendors/owners/applications/deposits/recurring-maintenance/scan/messages/Admin Team primary grids and unit detail work tabs.
- Shared `DataGrid` defaults to client-side sort/page, and several DB-backed list screens still fetch broad capped lists before Svelte filtering/sorting. Treat these remaining screens as unresolved production-scale risks until converted to server-side paging/filtering/sorting or proven bounded by design.
- `FileDrop` unsupported drag/drop now rejects before upload; route-specific upload failure states remain open where they depend on each page's mutation/error handling.
- Banking/accounting match suggestions have been moved to SQL-ranked candidate queries for the audited review-queue and accounting-grid surfaces. Continue the remaining static sweep outside this repaired controller/service scope before making a full-app DB-side claim.

## Pass 3 Fresh-User Full-UI Continuation

Date: 2026-06-22
Branch: `tsk-397-full-ui-pass-3`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-3`

Local stack:
- Web: `https://localhost:5827`
- API: `https://localhost:5826` (`http://localhost:5825`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass3-db`, database `rentalcommand_tsk397_pass3`, host port `5757`
- Assistant provider: `claude-cli`, model `sonnet`
- Safe wrapper: `scripts/qa/start-scan-audit-local.sh`; demo data disabled and outbound email/SMS provider credentials blanked.

Synthetic account:
- Mina Alvarez, `tsk397.pass3.20260622153516@example.local`
- Chose "Set up my real portfolio" from `/choose-setup`; DB proof before scan showed zero properties, units, tenants, leases, and scan drafts.

Baseline artifacts:
- `scripts/qa/inventory-web-surfaces.mjs` wrote `output/qa/web-surface-inventory.{json,md}` with 85 classified routes and the current acceptance matrix.
- `scripts/qa/generate-production-scale-scan-fixtures.py` wrote 480 sanitized local scan files under `output/qa/production-scale-scans`: 40 lease PDFs, 40 lease camera JPEGs, 80 expense PDFs, 80 expense camera JPEGs, 40 payment PDFs, 40 payment camera JPEGs, 40 application PDFs, 40 application camera JPEGs, 40 work-order PDFs, and 40 work-order camera JPEGs.
- Baseline checks: `rtk pnpm --dir web test:unit` passed 85/85; `rtk pnpm --dir web check` passed with the known four unused-selector warnings in `web/src/lib/components/m3/PageHeader.svelte`.

### Fresh Scan-First Lease Spine

Acceptance criteria:
- A brand-new verified user can choose live setup without demo data.
- The user can reach a scan-first path from the app shell and create the first property, unit, tenant, and lease from a lease PDF without direct DB seeding.
- Generated property/unit/tenant/lease records preserve user-reviewed edits and expose the scanned source document on the resulting lease detail.

Evidence:
- Registered through `/register`, verified via the local outbox link, logged in, chose live setup, and confirmed zero portfolio records before scanning.
- `/onboarding` still lands on the manual property step; scan-first setup is discoverable from the sidebar `/scan`, not primary onboarding. This remains a product gap unless fixed in this pass.
- `/scan` empty state showed no drafts, a visible `New rental from your lease` CTA, the generic scan type selector, and the `Bulk import leases` link.
- Uploaded `output/qa/production-scale-scans/01-leases/lease-001-1a.pdf` through `/scan/new-rental`.
- Engine proof: `claude-cli extraction: TEXT-FIRST (born-digital PDF, 442 chars of text - no PDF/vision sent)` and `Scan extraction succeeded for draft 1 ... Lease, Reviewing`.
- Browser proof:
  - Step 1 showed `Step 1 of 5 · Property` and populated Cedar Point Flats, 742 Evergreen St, Columbus, OH 43200.
  - Step 2 showed Unit 1A and rent `$1125.00`; beds/baths were not present in the lease fixture and were edited to `2` / `1`.
  - Step 3 showed tenant Avery Ellis; email/phone/emergency contact were manually entered because the fixture omitted contact fields.
  - Step 4 showed lease `QA-2026-001-1A`, Jan 1 2026 to Jan 1 2027, rent/deposit `$1125.00`, due day `1`, status Active.
  - Step 5 review listed one pending create operation; `Confirm & create` navigated to `/leases/1`.
- Lease detail rendered `Cedar Point Flats · Unit 1A · Avery Ellis`, active status, overview tabs, ledger link, property/unit/tenant links, and `View scanned document (PDF)` at `/lease-file/1`.
- DB proof: one property, one unit, one tenant, and one lease persisted with lease `QA-2026-001-1A`, monthly rent `1125.00`, unit beds/baths `2/1`, and tenant email `avery.ellis.tsk397@example.local`.

New pass-3 findings:
- `TSK397-B020` — scan-new-rental review loads broad support lists (`GET /api/v1/properties?take=200` and `GET /api/v1/tenants?take=200`) after extraction. This is better than the former `take=500` grid pattern but still violates the hard production-scale rule for unbounded support lists once portfolios grow. Needs a bounded search/page contract or a route-specific lookup strategy before claiming full DB-side compliance for this workflow.
- `TSK397-B021` — lease ledger opening-balance explanation formatted cents-bearing amounts with a trimmed decimal (`$225.3`) while the summary cards showed `$225.30`. Repro: create an opening balance of `225.30` from `/leases/1` → Ledger. Root cause: `LedgerExplanation.Money` used `$#,0.##`, which drops insignificant trailing zeros. Fix: whole-dollar values remain compact, cent-bearing values now render with exactly two decimals. Regression: `OpeningBalanceServiceTests.GetLedgerAsync_FormatsOpeningBalanceCentsInPlainEnglishExplanation`.

Additional lease-detail evidence:
- Agreement tab: active lease blocks sending for signature with explanatory copy, `Regenerate lease agreement (PDF)` showed success toast, and `Download agreement` downloaded `.playwright-cli/lease-agreement-1.pdf` (`80.0 KB`).
- Ledger tab: `Set opening balance` modal saved synthetic balance `$225.30` with note `TSK397 pass3 starting balance`; after API restart with the fix, browser proof showed charged `$225.30`, balance `$225.30`, and explanation `Opening balance carried over from before Rental Command — $225.30 as of Jan 1, 2026.`

Status: Pass for fresh-user PDF scan-first creation of the initial rental spine, lease agreement generation/download, and opening-balance happy path after `TSK397-B021` fix. Camera-image lease, batch/retry/reject, and onboarding scan-first discoverability remain open in this pass.

## Pass 4 Fresh-User Image-First Continuation

Date: 2026-06-22
Branch: `tsk-397-full-ui-pass-4`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-4`

Local stack:
- Web: `https://localhost:5837`
- API: `https://localhost:5836` (`http://localhost:5835`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass4-db`, database `rentalcommand_tsk397_pass4`, host port `5758`
- Assistant provider: `claude-cli`, model `sonnet`, `Assistant__ImageDetail=low`, `Assistant__UseImageOcr=false`
- Safe wrapper: `scripts/qa/start-scan-audit-local.sh`; demo data disabled.

Synthetic account:
- Mina Alvarez Pass 4, `tsk397.pass4.20260622160800@example.local`
- Chose "Set up my real portfolio" from `/choose-setup`; DB proof before scan showed zero properties, units, tenants, leases, and scan drafts.

Baseline artifacts:
- `scripts/qa/inventory-web-surfaces.mjs` regenerated `output/qa/web-surface-inventory.{json,md}` with 85 classified routes and no unclassified routes.
- `scripts/qa/generate-production-scale-scan-fixtures.py` regenerated 480 sanitized PDFs/JPEGs under `output/qa/production-scale-scans`.
- Baseline checks: `rtk pnpm --dir web test:unit` passed 85/85; `rtk pnpm --dir web check` passed with the known four unused-selector warnings in `web/src/lib/components/m3/PageHeader.svelte`.

### Camera-Image Scan-First Lease Spine

Acceptance criteria:
- A brand-new verified live user can create the first rental spine from a camera-style lease image, not only a born-digital PDF.
- The shared scan engine processes the uploaded image through the same extraction flow used by mobile camera capture.
- User-reviewed edits persist across property, unit, tenant, and lease creation.
- The resulting lease exposes the scanned source document and accepts downstream agreement, ledger, document, lifecycle, and history actions without breaking the page.

Evidence:
- `/onboarding` still landed on the manual property step; scan-first remains discoverable from the sidebar `/scan`, not primary onboarding.
- `/scan` empty state showed `New rental from your lease`, `Bulk import leases`, document type controls, upload dropzone, voice note, status tabs, and no drafts.
- Uploaded `output/qa/production-scale-scans/01-leases-camera/lease-002-2b.jpg` through `/scan/new-rental`.
- Engine proof: `claude-cli extraction: VISION read (application/pdf, no extractable text)` and `Scan extraction succeeded for draft 1 (model claude-cli:sonnet): 14 field(s) with a value -> Lease, Reviewing`.
- Browser proof:
  - Step 1 populated Riverside Flats, 1188 Maple Ave, Columbus, OH 43201, Type Single-family.
  - Step 2 populated Unit 2B and rent `$1200.00`; beds/baths were added manually because the fixture omitted them.
  - Step 3 populated tenant Blake Hayes; email, phone, and emergency contact were manually supplied.
  - Step 4 populated lease `QA-2026-002-2B`, Feb 1 2026 to Feb 1 2027, rent/deposit `$1200.00`, due day `1`, status Active.
  - Step 5 review listed Property/Unit/Tenant/Lease; `Confirm & create` navigated to `/leases/1`.
- DB proof: lease `QA-2026-002-2B`, rent `1200.00`, property Riverside Flats, unit 2B, beds/baths `2/1`, tenant Blake Hayes email `blake.hayes.tsk397@example.local`.
- Source document proof: `/lease-file/1` rendered the generated camera-image content; lease overview later rendered an image preview and `Open full size`.

Status: Pass for image-first creation of the initial property/unit/tenant/lease spine. The upload path stores the camera image through the lease-file proxy as a normalized scan file; continue watching wording/preview consistency when more camera sources are tested.

### Lease Detail Continuation

Acceptance criteria:
- Active lease agreement actions show safe states and download generated PDFs.
- Ledger opening-balance cents remain correctly formatted.
- Lease document upload/download works for camera-style JPEG attachments.
- Lifecycle actions require confirmation and render user-facing status labels in header, cards, and audit history.
- Edit mode must expose editable fields no matter which tab the user started from.
- Destructive actions can be opened to their confirmation dialog and cancelled without mutation.

Evidence:
- Agreement tab blocked active-lease send-for-signature with explanatory copy, generated a lease agreement, and downloaded `.playwright-cli/lease-agreement-1.pdf` (`81K`).
- Ledger `Set opening balance` saved `$300.15`; summary cards, entry text, and the "Why this is here" popover all rendered `$300.15`.
- Uploaded `output/qa/production-scale-scans/01-leases-camera/lease-003-3c.jpg` from lease Documents; detail listed `lease-003-3c.jpg`, `lease-1-agreement.pdf`, and the original scan. Downloaded document remained a real JPEG (`1800x2400`, 145.1K).
- Document delete opened a confirmation and was cancelled.
- Give Notice required a dialog with optional move-out date; Set Active required a separate confirmation. Both actions saved and refreshed the lease.
- Lease Delete opened `Delete lease QA-2026-002-2B? This cannot be undone.` and was cancelled.

New pass-4 findings:
- `TSK397-B022` — Lease History says "Every recorded change to this lease", but opening-balance save, agreement generation, and document upload did not appear in the lease history. Only direct lease updates were visible. This remains open because the intended cross-entity history scope needs a product decision.
- `TSK397-B023` — Give Notice and History diff leaked raw `NoticeGiven` enum text. Fix: shared status label helper formats status badges, inline fields, select labels, and audit status diffs as user-facing labels. Regressions: `web/src/lib/utils/status-labels.test.ts`; browser proof showed `Notice given -> Active` in the expanded audit diff.
- `TSK397-B024` — Pressing `Edit` while on the History tab switched the page into edit mode but left History visible, so no editable fields appeared until the user manually changed tabs. Fix: lease edit now moves to Overview before entering edit mode. Regression: `web/src/lib/leases/lease-detail-state.test.ts`; browser proof from History showed Overview selected with editable fields visible.

Verification after fixes:
- `rtk pnpm --dir web test:unit -- status-labels lease-detail-state` passed 91/91.
- `rtk pnpm --dir web check` passed with the known four unused-selector warnings in `web/src/lib/components/m3/PageHeader.svelte`.
- Browser reload on `/leases/1` showed the lease header and overview status as `Active`, History expanded row as `Notice given -> Active`, and Edit-from-History landing on Overview with editable fields visible.

Status: Pass after fixes for the covered lease-detail workflows. Lease-related child history scope remains open as `TSK397-B022`.

### Unit Command Center Start

Acceptance criteria:
- Lease detail links land on the correct unit workspace tab and preserve deep links across reloads.
- The unit workspace supports normal property-manager actions from the lease, rent, and maintenance tabs without leaving the user at a dead end.
- Payment and work-order creation expose validation before save, persist the intended record, and reflect the result in unit header chips, tab content, and recent activity.

Evidence:
- Navigated from lease overview to Unit 2B at `/units/1?tab=lease`; Lease tab showed current lease `QA-2026-002-2B`, tenant Blake Hayes, rent `$1200.00`, `Open lease`, and `Scan/upload lease`.
- Header chips updated from the live unit state: rent current, `0/1 open repairs` after the work order was added, lease ends in 224 days, 3 docs, and Blake Hayes.
- Rent tab:
  - Empty state rendered outstanding balance `$0.00` and no payments.
  - `Post payment` opened the form; empty submit produced `Amount is required`.
  - Saved synthetic rent payment `$300.15`, date 2026-06-22, status Paid, type Rent.
  - The row appeared as `Jun 22, 2026 · Rent`, `Paid`, `$300.15`; recent activity linked to `/accounting/payments/1`.
  - Inline payment detail expanded and edit mode saved harmless method `Cash`.
- Maintenance tab:
  - Empty state rendered no work orders and no receipts.
  - `New work order` opened the form; empty submit produced `Title is required` and `Description is required`.
  - Created `Bathroom sink drain leak` with synthetic P-trap description, Normal priority, New status, General category.
  - Unit header updated to `1 open repair`; Maintenance tab row expanded with description/category/requested/cost and `Open work order`, which navigated to `/maintenance/1`.

New pass-4 findings:
- `TSK397-B025` — Same-route Unit Command Center deep links changed the URL but not the visible tab. Repro: from `/units/1?tab=lease`, click the `Rent on track` overview link; URL became `?tab=rent` while the Lease tab remained selected. Fix: shared `resolveUnitTab` helper initializes and reacts to `page.url.searchParams.get('tab')`, and tab changes normalize arbitrary values before writing the URL. Regression: `web/src/lib/components/unit/unit-tabs.test.ts`; browser proof showed `/units/1?tab=rent` selecting the Rent tab after reload and same-route navigation.

Verification after Unit Command Center fix:
- `rtk pnpm --dir web test:unit -- unit-tabs status-labels lease-detail-state` passed 93/93.
- `rtk pnpm --dir web check` passed with the known four unused-selector warnings in `web/src/lib/components/m3/PageHeader.svelte`.

Status: Pass for the covered lease, rent, and maintenance unit workflows after the tab-sync fix. Remaining unit tabs still need the continuation pass: work-order detail actions, Documents, Expenses, Timeline, and lifecycle actions beyond opening/cancelling destructive confirmations.

## Pass 5 Fresh-User Unit, Property, and Money Continuation

Date: 2026-06-22
Branch: `tsk-397-full-ui-pass-5`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-5`

Local stack:
- Web: `https://localhost:5847`
- API: `https://localhost:5846` (`http://localhost:5845`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass5-db`, database `rentalcommand_tsk397_pass5`, host port `5760`
- Assistant provider: `claude-cli`, model `sonnet`, `Assistant__ImageDetail=low`, `Assistant__UseImageOcr=false`
- Safe wrapper: `scripts/qa/start-scan-audit-local.sh`; demo data disabled.

Synthetic account:
- Mina Alvarez Pass 5, `tsk397.pass5.20260622165100@example.local`
- Registered, verified through the local outbox email link, logged in, chose "Set up my real portfolio", and confirmed zero live properties, units, tenants, leases, and scan drafts before the first scan.

### Image Scan-First Lease, Property, Tenant, and Unit Corrections

Acceptance criteria:
- A new live user can create the first property/unit/tenant/lease from a camera-style lease image.
- The resulting object pages expose enough controls to correct extraction omissions and continue normal property-manager work.
- Related links from lease, property, tenant, and unit pages route to the intended records.

Evidence:
- Uploaded `output/qa/production-scale-scans/01-leases-camera/lease-006-2b.jpg` through `/scan/new-rental`.
- Engine proof: `claude-cli extraction: VISION read (application/pdf, no extractable text)` and `Scan extraction succeeded for draft 1 (model claude-cli:sonnet): 14 field(s) with a value -> Lease, Reviewing`.
- The scan draft became reviewable from `/scan` and created lease `QA-2026-006-2B` for Harbor View Homes, Unit 2B, tenant Finley Vega, rent `$1500.00`.
- Corrected extracted omissions through normal UI:
  - Unit beds/baths updated to `2 / 1` from the parent property Units grid.
  - Property purchase price, land value, and in-service date updated from property Edit.
  - Tenant email, phone, and emergency contact updated from tenant Edit.
- Unit Command Center reflected the corrected `2 bd · 1 ba`, active lease, tenant, document count, and unit links.

New pass-5 findings:
- `TSK397-B026` — `/scan/new-rental` processing did not transition to the completed review when extraction finished. Reload returned to the upload page, while `/scan` correctly showed the draft as `Ready to review`. Recoverable, but the in-flight processing page leaves the user at a dead end.
- `TSK397-B027` — Camera-image scan records still use PDF-facing copy in multiple places. Examples: lease "View scanned document (PDF)", lease note "Imported from scanned lease PDF", property/tenant notes "Created from scanned lease PDF". The engine log also reported the JPEG as `application/pdf`, so this needs a MIME/copy audit before declaring image parity complete.
- `TSK397-B028` — Unit Command Center has no direct Edit unit action. Users can correct beds/baths/status only by navigating back to the parent property Units grid.
- `TSK397-B029` — Clean live setup still lands on manual Add Property onboarding, while the flagship scan-first path is only discoverable through the sidebar Scan / Add route.

Status: Pass for creating and correcting the first image-scanned rental spine, with the listed scan UX/copy gaps remaining open.

### Property Loans and Recurring Expenses

Acceptance criteria:
- Property detail supports creating, editing, expanding, and safely cancelling destructive actions for mortgage/loan records.
- Property recurring expenses support create, edit, and delete confirmation using user-facing category labels.

Evidence:
- Added and edited loan `First Local Bank` with original principal `$240,000.00`, current principal `$237,900.00`, `6.25%`, 360-month term, start date `2026-06-01`, due day `1`, monthly P&I `$1,477.29`, and escrow `$390.00`.
- Expanded amortization schedule. Current user-facing state: `No payments generated yet. The debt-service worker fills this in monthly.`
- Delete loan opened `Remove loan` confirmation and was cancelled.
- Added and edited recurring expense `Property insurance premium`, category Insurance, monthly amount `$192.75`, start date `2026-07-01`.
- Delete recurring expense opened confirmation and was cancelled.

Fixed in this pass:
- `TSK397-B030` — Recurring expense and other expense category controls leaked raw Schedule E enum tokens such as `AutoTravel`, `CleaningMaintenance`, `LegalProfessional`, and `MortgageInterest`. Fix: added shared `expense-categories.ts` labels/options and wired property recurring expenses, unit expenses, accounting grid/filter/new-expense modal, and expense detail to the same readable labels. Regression: `web/src/lib/accounting/expense-categories.test.ts`.

Status: Pass after category-label fix for covered loan and recurring-expense workflows.

### Unit Expense Scan and Accounting Expense Detail

Acceptance criteria:
- Unit Command Center expense scan/upload path should let a user attach a camera-style receipt, review extracted fields, assign context, and create a usable expense.
- Accounting expense list/detail should preserve category labels, receipt preview/details, edit state, history, and destructive confirmation behavior.

Evidence:
- From Unit Command Center, `Scan / Upload` opened generic `/scan` with Receipt/Bill selected.
- Uploaded `output/qa/production-scale-scans/02-expenses-camera/expense-032.jpg`.
- Draft `/scan/2` initially showed Processing with an image preview, then review fields from `claude-cli:sonnet`: vendor `Apex Plumbing`, receipt `RCPT-0032`, subtotal `$77.50`, tax `$5.00`, total `$82.50`, payment method Visa, card last4 `4242`, and transaction date `2026-09-08`.
- The scan review category dropdown already used readable labels. Because unit/property context was not retained, Harbor View Homes had to be selected manually before confirming.
- Confirming created an expense, and expense detail rendered the scanned receipt image preview and receipt details. Edit saved updated notes and billable-to-owner state, with history updated.

New pass-5 findings:
- `TSK397-B031` — Unit Command Center `Scan / Upload` loses unit/property context and drops the user into generic `/scan`; the receipt review defaulted to `-- No property --` and required manual reassignment.
- `TSK397-B032` — Receipt scan confirmation promoted vendor text into the description while the resulting expense still showed `No vendor` when the vendor did not already exist. This may need vendor creation/linking behavior, not just display formatting.
- `TSK397-B033` — Receipt line-item extraction produced descriptions with blank amounts for this camera fixture, and confirmation still allowed the expense. Decide whether line items are optional hints or should block/warn when incomplete.

Fixed in this pass:
- `TSK397-B034` — Expense detail `Delete` immediately deleted the record and navigated to `/accounting` with no confirmation. Fix: expense detail now opens a shared `ConfirmDialog` with `Delete "<description>"? This cannot be undone.` and only calls the delete mutation on confirmation. Regression: `web/src/lib/accounting/expense-detail-actions.test.ts`.

Browser proof after fixes:
- Created replacement expense `Retest faucet repair parts`, amount `$82.50`, category `Repairs & maintenance`, property Harbor View Homes, date `2026-09-08`.
- Accounting ledger rendered row category `Repairs & maintenance` and property Harbor View Homes.
- Expense detail `/accounting/expenses/2` rendered subtitle `Repairs & maintenance · $82.5 · Pending`, category `Repairs & maintenance`, notes, and history.
- Clicking Delete opened `Delete expense` confirmation and stayed on `/accounting/expenses/2`; Cancel closed the dialog without deleting.

Verification:
- `rtk pnpm --dir web test:unit` passed 98/98.
- `rtk pnpm --dir web check` passed with 0 errors and the known four unused-selector warnings in `web/src/lib/components/m3/PageHeader.svelte`.

Status: Pass after fixes for covered accounting category labels and expense-detail delete confirmation. Unit scan context and vendor/line-item promotion remain open.

### Deferred User-Reported Item

- `TSK-399` — User reported that the Unit Command Center `Send renewal` link does nothing when pressed. This was captured for later and intentionally not fixed in this pass.

## Pass 8 Fresh-User Scan Spine and Vendor-Link Regression

Date: 2026-06-22
Branch: `tsk-397-full-ui-pass-8`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-8`

Local stack:
- Web: `https://localhost:5872`
- API: `https://localhost:5871` (`http://localhost:5870`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass8-db`, database `rentalcommand_tsk397_pass8_clean`, host port `5548`
- Assistant provider: `claude-cli`, model `sonnet`

Synthetic account:
- Jordan Harper, `tsk397.pass8.20260622b@example.local`
- Registered through the public welcome page, verified by opening the local `OutboxMessages` verification URL, logged in, selected "Set up my real portfolio", and confirmed the starting DB state had one seeded portfolio and zero properties, units, tenants, leases, and scan drafts.

Generated sanitized scan inputs:
- Lease photo: `output/qa/pass8-docs/harbor-unit-1-lease-2026-2027-photo.jpg`
- Receipt photo: `output/qa/pass8-docs/harbor-hardware-receipt-photo.jpg`
- Contractor invoice photo: `output/qa/pass8-docs/clearwater-plumbing-invoice-photo.jpg`

### Lease Photo to First Rental Spine

Acceptance criteria:
- A clean live user can choose the scan-first path, upload a camera-style lease image, review extracted values, edit missing fields, and create a property, unit, tenant, and lease in one confirmation.
- The confirmed lease detail links to the created property/unit/tenant, shows the financial terms, and preserves the uploaded scan as a viewable document.

Evidence:
- `/scan` exposed `New rental from your lease`; `/scan/new-rental` accepted the camera-style lease image and transitioned from `Reading your lease...` to the five-step review form.
- Review prefilled property `Harbor Test Duplex`, address `2100 Harbor Test Ave`, state `OH`, unit `1`, rent `$1125.00`, tenant `Morgan Lee`, lease `L-2026-101`, start `2026-07-01`, end `2027-06-30`, security deposit `$1125.00`, late fee `$50.00`, and due day `1`.
- User-corrected fields in the review UI: city `Columbus`, ZIP `43215`, property type `Multi-family`, beds `2`, baths `1`, tenant email `morgan.lee@example.local`, phone `614-555-0132`, emergency contact `Casey Lee, 614-555-0144`.
- Confirm landed on `/leases/1`; lease detail showed `Harbor Test Duplex · Unit 1 · Morgan Lee`, `$1,125.00` rent, active status, linked tenant/property/unit, and scanned document link.
- Database proof after confirm: one property, one unit, one tenant, one lease, scan draft `1` status `Confirmed`, stored file `EntityType=Lease`, `EntityId=1`.
- The `/lease-file/1` tab rendered the converted one-page PDF in the browser. Console warnings/errors for the proof path: zero user-facing app warnings/errors; only the known `/favicon.ico` 404 appeared.

Notes:
- The current pass8 lease fixture did not visibly include city/ZIP or beds/baths, so the missing city/ZIP/bed/bath extraction values are fixture coverage gaps, not evidence of a mapper failure.
- `TSK397-B029` remains open: the live setup wizard still starts at manual Add Property while the scan-first path is discoverable through the sidebar Scan / Add route.

### Receipt and Invoice Scans to Expenses

Acceptance criteria:
- A receipt photo can create a paid expense with extracted vendor text, property association, category, receipt metadata, and line items.
- An invoice photo can create an unpaid bill with extracted due date and a linked vendor even when that vendor does not already exist.
- Confirmed scan drafts route to their created expense records and the records remain usable from accounting detail pages.

Evidence:
- Uploaded `harbor-hardware-receipt-photo.jpg` through `/scan` with Receipt/Bill selected.
- Draft `/scan/2?type=Expense` extracted vendor `Franklin Hardware Supply`, receipt `FH-88219`, subtotal `$56.69`, tax `$4.25`, total `$60.94`, payment method Visa, card last4 `4242`, date `2026-07-03`, notes, and four balanced line items.
- Assigned property `Harbor Test Duplex`, category `Repairs & maintenance`, confirmed as paid, and opened `/accounting/expenses/1`.
- Expense detail showed title/description `Franklin Hardware Supply`, category `Repairs & maintenance`, property `Harbor Test Duplex`, receipt preview, card/payment fields, notes, and all line items.
- Browser reproduction of `TSK397-B032`: before the fix, that scanned expense still showed `Vendor: No vendor` because no matching vendor existed.

Fixed in this pass:
- `TSK397-B032` — Receipt/invoice scan confirmation promoted vendor text into the expense description but left `VendorId` null when the vendor did not already exist. Fix: expense scan confirm now exact-matches an active vendor by normalized name, creates a lightweight `General` vendor from the scanned vendor name/contact fields when there are zero matches, and leaves ambiguous duplicate matches unlinked instead of guessing.
- Regression: `RentalCommand.Api.Tests/Scanning/ScanServiceTests.cs` now covers new scanned vendor creation/linking and the existing exact-match behavior.
- Browser proof after restarting the API: uploaded `clearwater-plumbing-invoice-photo.jpg`; draft `/scan/3?type=Expense` extracted `Clearwater Plumbing LLC`, invoice `CP-2026-447`, subtotal/total `$268.75`, due date `2026-07-20`, invoice date `2026-07-05`, notes, and three line items. After selecting `Unpaid bill`, property `Harbor Test Duplex`, and category `Repairs & maintenance`, confirm created `/accounting/expenses/2`.
- `/accounting/expenses/2` showed `Status: Pending`, due date `Jul 20, 2026`, property `Harbor Test Duplex`, and `Vendor: Clearwater Plumbing LLC`. DB proof showed `Expenses.VendorId=1` joined to `Vendors.Name=Clearwater Plumbing LLC`, `ServiceType=General`.

Verification:
- RED: `MSBUILDDISABLENODEREUSE=1 rtk dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~ScanServiceTests.ConfirmAndCreateAsync_ReviewingExpenseDraft_WithNewVendorName_CreatesAndLinksVendor"` failed before the fix with no vendor row.
- GREEN: same targeted test passed after the fix.
- Focused regression suite: `MSBUILDDISABLENODEREUSE=1 rtk dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~ScanServiceTests"` passed 20/20 with the known `SQLitePCLRaw.lib.e_sqlite3` vulnerability warnings.
- Browser console check after the invoice retest: zero warning-or-higher messages.

Status: Pass after fix for the covered scan-first rental spine, paid receipt scan, unpaid invoice scan, and scanned-vendor linking workflow. Continue next with Unit Command Center remaining tabs/actions and other non-banking/non-QuickBooks workflows.

## Pass 9 Unit Command Center Deep Pass

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-9`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-9`

Local stack:
- Web: `https://localhost:5872`
- API: `https://localhost:5871` (`http://localhost:5870`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass8-db`, database `rentalcommand_tsk397_pass8_clean`, host port `5548`
- Assistant provider: `claude-cli`, model `sonnet`

Synthetic account:
- Jordan Harper, `tsk397.pass8.20260622b@example.local`

Acceptance criteria:
- Unit Command Center tabs and shortcuts support a property manager's normal unit workflow: lease review, rent posting/editing, maintenance creation, receipt scanning, documents, expenses, and timeline audit review.
- Unit-scoped scan flows carry enough context to minimize duplicate user entry, attach the source file to the created record, and return the user back to the originating unit workflow.
- Unit document counts and document lists include files attached to the unit and its child records using DB-side filtering.
- Rent activity is listed newest-first with server-side sort/paging, matching the overview card.

Evidence:
- Lease tab rendered the current lease, tenant, rent, deposit, dates, Open lease link, and Scan/upload lease action.
- Rent tab validation blocked an empty payment submit with `Amount is required`. Posting `$1,125.00` created Payment `3`, updated recent activity, and expanding/editing the row saved `$1,120.00` plus method `ACH`. The audit feed expanded the update diff for amount and method.
- Maintenance tab validation blocked incomplete work-order creation. Creating `Kitchen sink leak` updated the header chip and recent activity. Opening the work-order detail and returning through the unit link preserved the Maintenance tab.
- Maintenance receipt scan uploaded `output/scan-fixtures/scan-maintenance-invoice.pdf`, extracted `Harborline Maintenance LLC`, category Repairs & maintenance, amount `$420.68`, and created Expense `3` linked to Unit `1` and WorkOrder `1`.
- Expenses tab created and edited a manual paid expense, then displayed the update diff in the full timeline.
- Overview shortcuts `Open rent`, `Open maintenance`, and `Open documents` landed on the intended tabs. The current next-action link `Confirm move-in / collect deposit` landed on the Lease tab. Lifecycle `Move-Out` landed on Maintenance.
- Documents tab and header now show `4 docs`, including the work-order expense scan `scan-20260623001412` grouped under Expense, plus the lease and two payment scans.
- Browser console check after the pass returned 0 warning-or-higher messages.

Fixed in this pass:
- `TSK397-B035` - Unit-scoped payment scans did not infer a lease unless `leaseId` was explicitly present, so the rent-check review required manual lease selection. Fix: scan context now resolves a payment lease from an explicit lease or unambiguous unit lease context, with preferred Draft/PendingSignature/Active disambiguation. Regression: `web/src/lib/scan/scan-context.test.ts`.
- `TSK397-B036` - Confirmed unit-origin scans left users on the scan success screen with no return-to-source action. Fix: confirmed scan success cards now offer `View/Edit Record`, context-aware `Back to unit/property/maintenance/lease/workflow`, and `Scan another`, only for safe local return targets.
- `TSK397-B037` - Unit Documents omitted expense-attached files even when the expense belonged to the unit or one of its work orders. Fix: `UnitDashboardService.BuildUnitDocumentsQuery` now includes `Expense` stored files through DB-side expense and work-order subqueries. Regression: `UnitDashboardServiceTests.GetDashboardAsync_IncludesExpenseDocumentsLinkedToTheUnitOrItsWorkOrders`.
- `TSK397-B038` - Unit Rent tab requested `sort=dueDate`, so the full list showed older payments before newly posted payments while Overview showed newest-first. Fix: Rent tab now requests `sort=-dueDate` server-side. Regression: `PaymentServiceTests.ListPageAsync_SortsDueDateDescendingWhenRequested`.

Verification:
- `pnpm --dir web test:unit -- src/lib/scan/scan-context.test.ts` passed 105/105.
- `pnpm --dir web check` passed with 0 errors and the known four unused-selector warnings in `web/src/lib/components/m3/PageHeader.svelte`.
- `MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.sln --filter "FullyQualifiedName~UnitDashboardServiceTests|FullyQualifiedName~PaymentServiceTests" --verbosity minimal` passed 11/11 in `RentalCommand.Api.Tests`, with only known NuGet vulnerability warnings.

Deferred:
- `TSK-399` - User-reported Unit Command Center `Send renewal` no-op remains captured and intentionally deferred.

Status: Pass after fixes for unit-scoped payment scan context/return, expense document rollup, and rent-list newest-first ordering. Continue next with the remaining non-banking/non-QuickBooks app surfaces after this checkpoint.

## Pass 10 Fresh-User Camera Scan Continuation

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-10`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-10`

Local stack:
- Web: `https://localhost:5882`
- API: `https://localhost:5881` (`http://localhost:5880`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass8-db`, database `rentalcommand_tsk397_pass10_clean`, host port `5548`
- Assistant provider: `claude-cli`, model `sonnet`

Synthetic account:
- Riley Morgan Pass 10, `tsk397.pass10.202606230052@example.local`
- Registered through `/welcome`, verified through the local outbox email link, logged in, selected "Set up my real portfolio", and confirmed the live portfolio had zero properties, units, tenants, leases, and scan drafts before the first scan.

Generated sanitized scan inputs:
- `scripts/qa/generate-production-scale-scan-fixtures.py` wrote 480 local fixtures under `output/qa/production-scale-scans`: 40 lease PDFs, 40 lease camera JPEGs, 80 expense PDFs, 80 expense camera JPEGs, 40 payment PDFs, 40 payment camera JPEGs, 40 application PDFs, 40 application camera JPEGs, 40 work-order PDFs, and 40 work-order camera JPEGs.

Acceptance criteria:
- A clean live user can create the first rental spine from a camera-style lease image using the shared scan extraction engine.
- A rental application camera image can be scanned, reviewed, confirmed, and opened as an application record.
- The application detail page exposes the original attached scan source when the backing blob is available.
- Application scan file availability must be portfolio-scoped and must not add list/grid N+1 storage checks.

Evidence:
- Uploaded `output/qa/production-scale-scans/01-leases-camera/lease-001-1a.jpg` through `/scan/new-rental`; the engine logged `VISION read` and created a reviewable Lease draft.
- The review flow created `Cedar Point Flats`, Unit `1A`, tenant `Avery Ellis`, and lease `QA-2026-001-1A`; lease detail `/leases/1` rendered the linked property/unit/tenant, rent `$1,125.00`, and the scanned source document.
- DB proof after lease confirm: `properties=1`, `units=1`, `tenants=1`, `leases=1`, `scan_drafts=1`, `stored_files=1`.
- Uploaded `output/qa/production-scale-scans/04-applications-camera/application-001.jpg` with Rental Application selected; the engine logged `VISION read (image/jpeg...)` and draft `2` became `Reviewing`.
- Review extracted applicant `Gray Johnson`, email `qa.applicant.001@example.local`, phone `555-0101`, employer `QA Employer 1`, income `$3,850.00`, requested home `Cedar Point Flats Unit 1A`, and ID last four `1000`.
- Confirming created application `1`, redirected to `/applications`, and detail `/applications/1` rendered applicant data, requested property/unit labels, no-consent screening disabled state, and notes.
- DB proof after application confirm: `ScanDrafts 2 | Confirmed | Application`; `StoredFiles` included `Application EntityId=1 image/jpeg` plus its thumbnail.
- Browser proof after the fix: `/applications/1` rendered `Scanned application`, preview image `/application-file/1?thumb=true` with natural size `375x500`, and the proxy returned `200 image/jpeg` with a 12.7 KB thumbnail. Screenshot: `output/playwright/pass10-application-scan-card.png`.
- Browser console check after the application detail proof returned zero error-level messages.

New pass-10 findings:
- `TSK397-B039` - Application scan upload/review still uses receipt-specific copy in places. Examples: the upload dropzone said "Drop a receipt, invoice, or maintenance photo here" while Rental Application was active, and the processing page said it was pulling out vendor, amounts, and dates.
- `TSK397-B040` - Application scan processing stayed on the Processing page after the engine had marked the draft `Reviewing`; reloading showed the review screen. This reproduces the same scan transition family as `TSK397-B026` outside the new-rental flow.

Fixed in this pass:
- `TSK397-B041` - Application detail did not expose the attached scanned source image even though scan confirmation re-keyed the `StoredFile` to `EntityType=Application` and `EntityId=1`. Fix: application detail now returns `hasScan`/`scanIsImage` only after the stored blob can be opened, `/api/v1/applications/{id}/scan` streams the portfolio-scoped source file, and `/application-file/{id}` proxies it for the Svelte app. Regression: `ApplicationServiceTests.GetAsync_ExposesAttachedScannedApplicationImage`.

Verification:
- RED: `MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~ApplicationServiceTests.GetAsync_ExposesAttachedScannedApplicationImage" --verbosity minimal` failed before the fix because `ApplicationResponse` had no `HasScan`/`ScanIsImage` contract.
- GREEN: same targeted test passed after the fix.
- Focused regression suite: `MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~ApplicationServiceTests" --verbosity minimal` passed 15/15 with only the known `SQLitePCLRaw.lib.e_sqlite3` vulnerability warnings.
- Frontend check: `pnpm --dir web check` passed with 0 errors and the known four unused-selector warnings in `web/src/lib/components/m3/PageHeader.svelte`.

Deferred:
- `TSK-399` - User-reported Unit Command Center `Send renewal` no-op remains captured and intentionally deferred.

Status: Pass after fix for the covered fresh-user lease camera scan, application camera scan, and application scanned-source detail proof. Continue next with the remaining scan transition/copy defects and non-banking/non-QuickBooks surfaces.

## Pass 11 Fresh-User Scan Copy, Application Decision, and Lease Source Continuation

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-11`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-11`

Local stack:
- Web: `https://localhost:5892`
- API: `https://localhost:5891` (`http://localhost:5890`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass8-db`, database `rentalcommand_tsk397_pass11_clean`, host port `5548`
- Assistant provider: `claude-cli`, model `sonnet`

Synthetic account:
- Parker Lane Pass 11, `tsk397.pass11.202606230124@example.local`
- Registered through `/register`, verified through the local outbox email link, logged in, selected "Set up my real portfolio", and confirmed the live portfolio started with zero properties, units, tenants, leases, payments, expenses, work orders, and scan drafts.

Generated sanitized scan inputs:
- Reused `scripts/qa/generate-production-scale-scan-fixtures.py` output under `output/qa/production-scale-scans`: 480 synthetic files across lease, expense, payment, application, and work-order PDF/photo fixtures.

Acceptance criteria:
- Application scan should use application-specific upload and processing copy, transition from processing to review without a reload, create an applicant, and support the application approval path into a tenant.
- New-rental lease scan should create a property, unit, tenant, lease, and viewable source document from a camera-style lease image with human edits preserved.
- Filtered application lists should distinguish "no matches" from true first-run empty state.
- Scanned-source UI and persisted scan-created notes should not label camera-photo imports as PDFs.

Evidence:
- `/scan` with Rental Application selected now says `Drop a rental application here`; default Receipt/Bill copy remains receipt-specific.
- Uploaded `output/qa/production-scale-scans/04-applications-camera/application-001.jpg`; draft `/scan/1?type=Application` auto-transitioned from Processing to `Ready to review` without reload.
- Review extracted applicant `Gray Johnson`, email `qa.applicant.001@example.local`, phone `555-0101`, employer `QA Employer 1`, income `$3,850.00`, requested home `Cedar Point Flats Unit 1A`, and ID last four `1000`.
- Confirming created application `1`; Applications list showed the submitted row. `Get application link` opened the public link modal and Copy showed `Link copied to clipboard.`
- Application detail rendered the scanned application preview. Approve opened a confirmation modal, then created tenant `Gray Johnson` and exposed `View tenant`; tenant detail showed the application-derived provenance notes.
- Uploaded `output/qa/production-scale-scans/01-leases-camera/lease-001-1a.jpg` through `/scan/new-rental`; it transitioned to the five-step review wizard.
- Review prefilled property `Cedar Point Flats`, address `742 Evergreen St`, city `Columbus`, state `OH`, ZIP `43200`, unit `1A`, rent `$1125.00`, tenant `Avery Ellis`, lease `QA-2026-001-1A`, dates `2026-01-01` to `2027-01-01`, deposit `$1125.00`, and due day `1`.
- Human edits in the review UI set unit beds `2`, baths `1`, tenant email `avery.ellis@example.local`, phone `555-1101`, and emergency contact `Morgan Ellis 555-1102`; confirmation landed on `/leases/1`.
- DB proof after confirm: `properties=1`, `units=1`, `tenants=2`, `leases=1`, `applications=1`, `scan_drafts=2`; unit `1A` retained `Bedrooms=2.0`, `Bathrooms=1.0`; lease `QA-2026-001-1A` linked tenant `2` and unit `1`.
- `/lease-file/1` rendered the camera lease source document in-browser; screenshot proof: `output/playwright/pass11-lease-file-1.png`.
- Lease Agreement, Ledger, and History tabs rendered usable states. Ledger showed zero balance, opening-balance CTA, no payments yet, and the attached scan document.

Fixed in this pass:
- `TSK397-B039` - Application scan upload/review used receipt-specific copy. Fix: added `scan-copy.ts` and wired upload/processing copy by target entity type. Regression: `web/src/lib/scan/scan-copy.test.ts`.
- `TSK397-B042` - Applications search/status filters showed first-run empty copy (`No applications yet...`) when rows existed but no rows matched the active filters. Fix: added `formatApplicationsEmptyMessage` and wired the Applications grid empty message to active filters. Regression: `web/src/lib/applications/application-display.test.ts`.
- `TSK397-B043` - Scanned-source labels and scan-created notes assumed `PDF` even for camera-photo imports. Fix: lease, payment, and expense scanned-source links now use format-neutral `View scanned document`; lease scan confirmation now persists `Imported/Created from scanned lease document.` for lease/property/unit/tenant notes. Regressions: `web/src/lib/leases/lease-detail-state.test.ts` and `RentalCommand.Api.Tests/Scanning/ScanServiceTests.cs`.

Browser proof after fixes:
- `/applications?status=Declined` now shows `No applications match your filters.`
- `/leases/1` scanned-source link now reads `View scanned document`. The existing Pass 11 lease row was created before the backend note text change, so its persisted note still says `Imported from scanned lease PDF`; focused API tests cover the corrected note for newly confirmed scans.

Verification:
- `pnpm --dir web test:unit -- src/lib/applications/application-display.test.ts src/lib/leases/lease-detail-state.test.ts src/lib/scan/scan-copy.test.ts` passed 110/110.
- `MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter FullyQualifiedName~ScanServiceTests --logger "trx;LogFileName=scan-service-pass11.trx" --logger "console;verbosity=minimal"` passed 20/20 with the known `SQLitePCLRaw.lib.e_sqlite3` vulnerability warnings.
- `pnpm --dir web check` passed with 0 errors and the known four unused-selector warnings in `web/src/lib/components/m3/PageHeader.svelte`.

Deferred:
- `TSK397-B040` - Application scan processing stuck state did not reproduce on Pass 11 after the copy fix; the draft auto-transitioned to review and confirmed normally. Keep watching in later scan passes.
- `TSK-399` - User reported again that the Unit Command Center `Send renewal` link does nothing. Existing task `TSK-399` was updated with the 2026-06-23 repro note and screenshot; intentionally not fixed in this pass.

Status: Pass after fixes for application scan copy, application filtered-empty copy, and scanned-source PDF-specific wording. Pause after this checkpoint commit, then continue the remaining non-banking/non-QuickBooks UI inventory.

## Pass 24 Fresh-User True Image Lease Scan Continuation

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-24`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-24`

Local stack:
- Web: `https://localhost:6022`
- API: `https://localhost:6021` (`http://localhost:6020`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass24-db`, database `rentalcommand_tsk397_pass24_clean`, host port `5581`
- Assistant provider: `claude-cli`, model `sonnet`

Synthetic account:
- Casey Rowan Pass 24, `tsk397.pass24.202606230947@example.local`
- Registered through `/register`, verified through the local outbox email link, logged in, selected `Set up my real portfolio`, and confirmed the live portfolio started with zero properties, units, tenants, leases, payments, expenses, applications, work orders, and scan drafts.

Acceptance criteria:
- A clean live user can create rental records from a camera-style lease image using the shared scan engine, without demo data.
- New-rental single-photo uploads must preserve the original image file and content type so web testing exercises the same image extraction path expected from mobile camera capture.
- The lease review wizard must transition from processing to review without reload, allow landlord corrections, create property/unit/tenant/lease records, and surface the scanned source document on the resulting lease detail.
- Browser evidence and SQL proof must show persisted reviewed values, stored source-file content type, and no warning-or-higher console messages for the retested path.

Evidence:
- `/choose-setup` showed separate sample-data and real-portfolio paths. Selecting the real portfolio landed on `/onboarding`; core record counts stayed `0|0|0|0|0|0|0|0|0` before scanning.
- `/onboarding` still starts at manual property setup; scan-first setup remains discoverable through the app shell `/scan` path, not the primary onboarding path.
- `/scan` clean empty state showed `New rental from your lease`, `Bulk import leases`, document-type buttons, voice-note control, status tabs, and no scan drafts.
- Uploaded `output/qa/production-scale-scans/01-leases-camera/lease-007-3c.jpg` through `/scan/new-rental`; the pre-fix path created draft `1`, transitioned to review without reload, and created lease `QA-2026-007-3C` for Oak Terrace, Unit `3C`, tenant Gray Chen.
- Landlord edits during review set Unit `3C` to `2` beds / `1` bath, tenant email `gray.chen.pass24@example.local`, phone `614-555-2407`, emergency contact `Maya Chen, 614-555-2408`, and late fee `$50.00`.
- Lease detail `/leases/1` rendered Active status, linked property/unit/tenant, `$1,575.00` rent, `$1,575.00` deposit, `$50.00` late fee, format-neutral note `Imported from scanned lease document.`, and `View scanned document`.
- SQL proof after first confirm: Oak Terrace / Unit `3C` / Gray Chen / lease `QA-2026-007-3C` persisted with reviewed beds/baths, rent, deposit, and late fee.
- Root-cause proof for the new image finding: draft `1` and its `StoredFiles` row persisted as `ContentType=application/pdf`; engine log said `claude-cli extraction: VISION read (application/pdf, no extractable text)` even though the browser selected a `.jpg`.
- After the fix, uploaded `output/qa/production-scale-scans/01-leases-camera/lease-008-4d.jpg` through the same photo upload control. Draft `2` immediately persisted as `ContentType=image/jpeg` and `FileSize=152696`.
- Engine proof after the fix: `claude-cli extraction: VISION read (image/jpeg, no extractable text)` and `Scan extraction succeeded for draft 2 ... Lease, Reviewing`.
- Confirming draft `2` created lease `QA-2026-008-4D` for Summit Row, Unit `4D`, tenant Harper Foster. Landlord edits set beds/baths `3 / 2`, contact fields, and late fee `$50.00`.
- Lease detail `/leases/2` rendered an inline scanned-document image preview and `Open full size`; SQL proof showed draft `2` confirmed with `StoredFiles.ContentType=image/jpeg`, while draft `1` remained the pre-fix `application/pdf` comparison row.
- Browser console check after the fixed path returned zero warning-or-higher messages.

Fixed in this pass:
- `TSK397-B044` - New-rental single-photo lease uploads were converted to a generated PDF before upload, so web camera-image testing never actually exercised or preserved an image source file. Root cause: `onPhotos()` always called `stitchImagesToPdf(files)` even when exactly one photo was selected. Fix: added `prepareNewRentalPhotoUpload`; single-photo uploads now keep the original `File`, while multiple lease photos still stitch into one PDF. Regression: `web/src/lib/scan/new-rental-upload.test.ts`.

Verification:
- RED: `pnpm --dir web test:unit -- src/lib/scan/new-rental-upload.test.ts` failed before the helper existed.
- GREEN: `pnpm --dir web test:unit -- src/lib/scan/new-rental-upload.test.ts` passed 136/136 after the fix.
- Frontend check: `pnpm --dir web check` passed with 0 errors and the known four unused-selector warnings in `web/src/lib/components/m3/PageHeader.svelte`.
- Browser/SQL regression: draft `2` persisted as `image/jpeg`, engine logged `VISION read (image/jpeg...)`, `/leases/2` rendered an image preview, and console warnings/errors were zero.

Status: Pass after fix for true single-image lease scan preservation and review/confirm. Continue next with application scans and the remaining non-banking/non-QuickBooks UI inventory.

## Pass 24 Application Scan Requested-Home Continuation

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-24`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-24`

Acceptance criteria:
- A rental application camera image can be scanned, reviewed, confirmed, and opened as an application record.
- The reviewer's `Applying for` correction should preserve the human-readable note and, when it unambiguously names an existing property/unit, link the created application to the queryable requested home.
- Application requested-home matching must stay portfolio-scoped and DB-side; ambiguous or non-matching free text should remain unlinked rather than guessing.
- The application detail page must show applicant fields, requested-home labels, the attached image scan, no-consent screening disabled state, and notes.

Evidence:
- Uploaded `output/qa/production-scale-scans/04-applications-camera/application-002.jpg` with `Rental Application` selected. Draft `3` processed as `image/jpeg` and became `Reviewing`.
- Review extracted Harper Kim, email `qa.applicant.002@example.local`, phone `555-0102`, income `$4,100.00`, and requested home text. Landlord edits set DOB `1991-04-12`, current address, desired move-in `2026-08-01`, and `Applying for` to `Summit Row Unit 4D`.
- Pre-fix result: `/applications/1` showed the note `Applying for: Summit Row Unit 4D. ID last-4: 1000.`, but the Requested home card still showed Property `No preference`, Unit `No preference`. SQL confirmed `PropertyId`/`UnitId` were null.
- Root cause: `ScanService.ConfirmAsApplicationAsync` validated explicit extracted/override IDs but never resolved `ApplyingFor` text to an existing property/unit, so reviewer-entered requested-home text was only folded into Notes.
- After the fix and API restart, confirmed draft `4` from `output/qa/production-scale-scans/04-applications-camera/application-003.jpg` as applicant Lena Park with `Applying for = Summit Row Unit 4D`.
- Post-fix SQL proof: application `2` persisted `PropertyId=2`, `UnitId=2`, property `Summit Row`, unit `4D`, notes `Applying for: Summit Row Unit 4D. ID last-4: 1003.`
- Browser proof: `/applications/2` rendered Requested home `Summit Row` / `4D`, applicant contact/income fields, no-consent screening disabled state, scanned application card, and notes. Screenshot: `output/qa/playwright/pass24-application-2-requested-home-fixed.png`.
- Console observation: the login/navigation probe produced transient SignalR negotiation warnings from aborted requests, while the negotiate endpoint also returned `200` after the page settled. Treat as a watch item, not a blocker for this requested-home fix.

Fixed in this pass:
- `TSK397-B045` - Scanned rental applications did not link the requested home when the reviewer supplied only `Applying for` text. Fix: application scan confirm now resolves exact property-name + unit-number text such as `Summit Row Unit 4D`, or a unique unit-only reference, through portfolio-scoped DB queries before creating the application. Ambiguous text remains unlinked.

Verification:
- RED: `MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~ConfirmAndCreateAsync_ReviewingApplicationDraft_WithApplyingForText_LinksRequestedHome" --verbosity minimal` failed before the fix because `CreateApplicationRequest.PropertyId` was null.
- GREEN: same targeted test passed after the fix.
- Focused scan regression suite: `MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~ScanServiceTests" --verbosity minimal` passed 22/22 with only known `SQLitePCLRaw.lib.e_sqlite3` vulnerability warnings.
- Browser/SQL regression: application `2` created from a camera JPEG scan linked to Summit Row / Unit 4D and rendered those labels on `/applications/2`.

Status: Pass after fix for application requested-home linking from reviewed scan text. Continue next with payment, expense, work-order, maintenance, documents, timeline, and remaining non-banking/non-QuickBooks app surfaces.

## Pass 24 Payment, Expense, and Work-Order Camera Scan Continuation

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-24`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-24`

Acceptance criteria:
- A rent-check camera image can be scanned, reviewed, manually associated to the correct lease when extracted text is ambiguous, confirmed, and opened as a payment record.
- A receipt/invoice camera image can be scanned, reviewed, linked to a property/unit, confirmed with line items, and opened as an expense record.
- A maintenance-request camera image can be scanned, reviewed, linked to a property, confirmed as a work order, and opened with the attached source image.
- Reviewer-selected associations must win over conflicting extracted prose, but the extracted prose remains visible as provenance notes.
- Unit inference from scan text must stay portfolio/property scoped and DB-side; ambiguous or non-matching unit references must remain unlinked rather than guessing.
- Detail pages must expose the attached image source through the normal document/download path and complete with zero warning-or-higher browser console messages.

Evidence:
- Uploaded `output/qa/production-scale-scans/03-payments-camera/payment-003.jpg` as a Payment scan. Draft `5` processed as `image/jpeg`, extracted total `$1,275.00`, method `Check`, payer `Casey Kim`, bank `First QA Bank`, check/reference `8003`, transaction date `2026-04-03`, and notes for lease `QA-2026-003-3C`.
- Before lease selection, the Payment review required an explicit lease. Selecting `#QA-2026-007-3C — Gray Chen · Unit 3C` and confirming created Payment `1`.
- Payment SQL proof: `Payment 1 | LeaseId 1 | Amount 1275.00 | Status Paid | Method Check | ExternalReference 8003 | PayerName Casey Kim | CheckNumber 8003 | BankName First QA Bank | LeaseNumber QA-2026-007-3C`; source file row persisted as `Payment|1|image/jpeg`.
- `/accounting/payments/1` rendered Gray Chen, Rent `$1,275.00`, Paid, lease `QA-2026-007-3C`, due/paid `Apr 3, 2026`, method Check, reference `8003`, provenance notes, scanned-document image link, and history. Screenshot: `output/qa/playwright/pass24-payment-1-detail.png`.
- `/leases/1` Ledger tab rendered Charged `$1,275.00`, Paid `$1,275.00`, Balance `$0.00`, the rent line item, and the payment row. Screenshot: `output/qa/playwright/pass24-lease-1-ledger-after-payment-clicked.png`.
- Uploaded `output/qa/production-scale-scans/02-expenses-camera/expense-004.jpg` as a Receipt/Bill scan. Draft `6` extracted vendor `Summit Roofing`, receipt `RCPT-0004`, subtotal `$115.00`, tax `$5.00`, total `$120.00`, Visa `4242`, transaction date `2026-05-05`, and notes that mentioned a different property/unit.
- Review selected property `Summit Row`, category `Repairs & maintenance`, and line item amounts for materials `$55.00`, labor `$50.00`, and service fee `$10.00`. Confirming created Expense `1`.
- Expense SQL proof: `Expense 1 | Summit Roofing | 120.00 | PropertyId 2 | UnitId 2 | Summit Row | 4D | Visa | 4242 | Receipt | Category Repairs & maintenance`; line items persisted with amounts and line numbers; source file row persisted as `Expense|1|image/jpeg`.
- `/accounting/expenses/1` rendered Summit Row, Unit 4D, scanned document, receipt fields, line items, card/payment metadata, document kind, and history. Screenshot: `output/qa/playwright/pass24-expense-1-detail.png`.
- Uploaded `output/qa/production-scale-scans/05-work-orders-camera/work-order-004.jpg` as a Work Order scan. Draft `7` extracted title `Toilet runs continuously`, priority `Normal`, and description `Tenant reports the toilet runs continuously without stopping. Issue located at Unit 4D, 91 Summit Dr. Tenant requests weekday afternoon entry window.`
- Pre-fix review state required property selection but exposed no unit selector. The extracted notes said Unit `4D` matched a known unit but IDs were not assigned because the document property text did not match a known property.
- After the fix and API restart, selecting `Summit Row`, category `Plumbing`, and estimated cost `$185.00` created WorkOrder `1` from draft `7` without page errors. Screenshot: `output/qa/playwright/pass24-workorder-scan-confirm-success.png`.
- WorkOrder SQL proof: `WorkOrder 1 | Toilet runs continuously | PropertyId 2 | Summit Row | UnitId 2 | 4D | Priority Normal | Category Plumbing | EstimatedCost 185.00`.
- `/maintenance/1` rendered title, New status, Normal priority, `Summit Row`, `Unit 4D`, category Plumbing, estimated cost `$185.00`, status history, Photos & documents, and history. Screenshot: `output/qa/playwright/pass24-workorder-1-detail.png`.
- Opening the work-order document downloaded `scan-20260623101951.jpeg`; local file proof showed a real JPEG image, `1800x2400`, 136,547 bytes.
- Browser console check after the work-order flow returned zero warning-or-higher messages. Network proof for the final flow showed `POST /api/v1/scans/7/confirm => 200`, `/api/v1/work-orders/1 => 200`, `/api/v1/documents?entityType=WorkOrder&entityId=1 => 200`, and `/api/v1/documents/12/file => 200`.

Fixed in this pass:
- `TSK397-B046` - Scanned work-order review let the reviewer select a property but had no unit selector, and backend confirmation did not ground `Unit 4D` from the description under the selected property. Fix: work-order scan confirmation now resolves unit references from description/notes only within the selected portfolio and property before creating the work order. Ambiguous or missing references remain unlinked.

Verification:
- RED: `MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~ConfirmAndCreateAsync_ReviewingWorkOrderDraft_WithSelectedPropertyAndUnitInDescription_GroundsUnit" --verbosity minimal` failed before the fix because `CreateWorkOrderRequest.UnitId` was null.
- GREEN: same targeted test passed after the fix.
- Focused scan regression suite: `MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~ScanServiceTests" --verbosity minimal` passed 23/23 with only known `SQLitePCLRaw.lib.e_sqlite3` vulnerability warnings.
- Browser/SQL regression: draft `7` confirmed from a camera JPEG scan, created WorkOrder `1`, linked Summit Row / Unit 4D, retained the attached source JPEG, and completed with zero warning-or-higher console messages.

Watch items:
- Scan-upload thumbnails create separate `StoredFiles` rows with `EntityType` set and `EntityId` null; the user-facing document lists filter by `EntityId` and correctly show only the original source image. Keep this as a storage hygiene watch item unless it becomes visible in document counts or grids.
- `TSK-404` - User-reported Unit Command Center `Send renewal` no-op remains captured with the 2026-06-23 screenshot and is intentionally deferred from this scan/data-spine lane.

Status: Pass after fix for work-order unit grounding and browser proof for payment, expense, and work-order camera scan workflows. Continue next with maintenance actions, document/timeline surfaces, and the remaining non-banking/non-QuickBooks app inventory.

## Pass 27 Scan Front-Door Controls and Reject Reason Continuation

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-27`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`
- Assistant provider: `claude-cli`, model `sonnet`

Synthetic account:
- Avery Rowan Pass 26, `tsk397.pass26.202606231127@example.local`
- Continued from the fresh live portfolio created in the current production-scale scan pass.

Acceptance criteria:
- The `/scan` front door must expose usable document-type selection, contextual help, upload/history tabs, and DB-backed status/sort controls.
- Rejecting a reviewable draft with a typed reason must persist that reason for the user's records and show it on the rejected scan detail.
- Blank reject reasons should not erase an existing extraction failure reason.
- Rejected scans must be finalized: review fields/actions disabled, scan history updated, and no confirm/retry action available from the rejected detail.
- Permission-denied voice capture should give the user visible recovery/status feedback instead of silently doing nothing.

Evidence:
- `/scan` rendered `New rental from your lease`, `Bulk import leases`, document-type buttons, `Record voice note`, scan history tabs, and the current batch import summary.
- Help popover for `Record voice note` rendered the expected title and guidance, then dismissed cleanly with Escape.
- Status tabs used server-side list calls: Pending and Reviewing showed empty filtered states, Confirmed listed confirmed captures, and sorting by Type/Created updated the URL plus `/api/v1/scans/page?take=20&sort=...&status=Confirmed`.
- Browser microphone capability probe showed `navigator.mediaDevices`, `getUserMedia`, and `MediaRecorder` present, but permission state `denied`. Clicking `Record voice note` produced no visible inline status, recovery copy, or durable error message, and no useful network request.
- Pre-fix reject reproduction: uploaded `output/qa/production-scale-scans/02-expenses-camera/expense-002.jpg`, created draft `8`, rejected it with reason `QA pass: reject disposable expense scan`; browser POST body contained the reason, but SQL showed `ScanDrafts 8 | Rejected | FailureReason NULL/blank`.
- Post-fix reject regression: uploaded `output/qa/production-scale-scans/02-expenses-camera/expense-003.jpg`, created draft `9`, waited until Ready to review, and rejected with reason `QA pass: verify rejection reason persistence`.
- `/scan` history showed draft `9` as `Rejected Expense`; opening `/scan/9` showed disabled rejected-state controls and `This scan has been rejected: QA pass: verify rejection reason persistence`.
- SQL proof after the fix: `ScanDrafts 9 | Rejected | QA pass: verify rejection reason persistence`; draft `8` remains the pre-fix comparison row with a blank reason.

Fixed in this pass:
- `TSK397-B047` - Scan reject modal promised a reason "for your records", but `RejectDraftAsync` only sent it to the audit log and did not persist it on the draft or display it on the rejected detail. Fix: rejection now stores the trimmed reason in `ScanDraft.FailureReason`, preserves an existing failure reason when the user submits a blank reason, and the rejected review footer displays the stored reason.

Watch items:
- `TSK397-B048` - With browser microphone permission denied, `Record voice note` gives no visible recovery/status in the scan front door. This is a user-facing dead click in the denied-permission state and should be fixed in a later voice-intake slice.

Verification:
- RED: `dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~ScanServiceTests.RejectDraftAsync_ReviewingDraft_SetsRejectedAndLogsAudit"` failed before the fix because `FailureReason` was null.
- GREEN: `rtk dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~ScanServiceTests.RejectDraftAsync_ReviewingDraft_SetsRejectedAndLogsAudit"` passed after the fix with only known `SQLitePCLRaw.lib.e_sqlite3` vulnerability warnings.
- Frontend check: `rtk pnpm --dir web check` passed with 0 errors and the known four unused-selector warnings in `web/src/lib/components/m3/PageHeader.svelte`.
- Browser/SQL regression: draft `9` rejected from a camera JPEG scan, displayed the stored rejection reason on `/scan/9`, and persisted the same reason in PostgreSQL.

Status: Pass after fix for scan rejection reason persistence/display and DB-backed history tab/sort checks. Continue next with invalid upload handling, adjacent created-record pages, maintenance actions, document/timeline surfaces, and the remaining non-banking/non-QuickBooks app inventory.

## Pass 28 Scan Front-Door Invalid Upload and Terminal Filters

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-28`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Acceptance criteria:
- Unsupported files dropped on scan upload controls must be rejected before upload, must not create a draft, and must give durable visible feedback.
- Every scan lifecycle state visible in history rows must also be filterable from the scan history tabs.
- Filtered-empty scan history views must use status-specific copy rather than first-run empty copy.
- Scan history filtering must continue to use the DB-backed `/api/v1/scans/page` endpoint.

Evidence:
- Pre-fix unsupported upload reproduction: dropped `output/qa/production-scale-scans/MANIFEST.txt` on the `/scan` drop zone. SQL count stayed at `9`, no upload request was sent, but the page had no visible `not supported` feedback.
- Post-fix unsupported upload proof: dropping the same file rendered inline `role="alert"` text `File type "text/plain" is not supported. Upload a PDF or image.` and SQL count remained `9`.
- Pre-fix terminal filter gap: `/scan` showed rejected rows in the All tab, but available tabs were only All, Pending, Reviewing, and Confirmed.
- Post-fix filter proof: `/scan` rendered All, Pending, Reviewing, Confirmed, Rejected, and Failed tabs.
- Clicking Rejected navigated to `/scan?status=Rejected`, rendered the two rejected rows, and network proof showed `GET /api/v1/scans/page?take=20&status=Rejected => 200`.
- Clicking Failed navigated to `/scan?status=Failed`, rendered the status-specific empty state `No failed scans.`, and network proof showed `GET /api/v1/scans/page?take=20&status=Failed => 200`.
- Browser console check after the terminal-filter pass returned zero warning-or-higher messages.

Fixed in this pass:
- `TSK397-B049` - Unsupported files dropped on the shared scan upload control were rejected without creating drafts, but there was no durable visible feedback in the page. Fix: `FileDrop` now renders the first unsupported-file message inline as a `role="alert"` while preserving the toast.
- `TSK397-B050` - Scan history could show Rejected/Failed states but did not expose Rejected or Failed filter tabs, and filtered terminal empty states used generic first-run copy. Fix: added a tested scan-history filter helper, exposed Rejected/Failed tabs, and added status-specific empty messages.

Verification:
- `rtk pnpm --dir web test:unit -- src/lib/components/file-drop.test.ts src/lib/scans/scan-history-filters.test.ts` passed 141/141.
- `rtk pnpm --dir web check` passed with 0 errors and the known four unused-selector warnings in `web/src/lib/components/m3/PageHeader.svelte`.
- Browser/SQL regression: unsupported text-file drop showed inline alert and did not increase `ScanDrafts` count; Rejected and Failed filters used DB-backed `/scans/page` requests and rendered the expected row/empty states.

Status: Pass after fixes for invalid upload feedback and terminal scan history filters. Continue next with confirmed-record detail pages from scan history, maintenance actions, document/timeline surfaces, and the remaining non-banking/non-QuickBooks inventory.

## Pass 29 Lease Detail Controls and Scanned Document Proof

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-29`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Acceptance criteria:
- Lease detail tabs must expose Overview, Agreement & Signing, Ledger, and History without console warnings.
- Destructive or lifecycle-changing lease controls must require an explicit confirmation step and must be cancellable without changing data.
- Giving notice must capture the expected move-out date before the lease is marked `NoticeGiven`, because the date feeds later move-out steps.
- The scanned lease document link must open the original uploaded/generated document and preserve the detail page.

Evidence:
- Opened `/leases/3` from the scan-created lease row for `QA-2026-004-4D`. Overview rendered Active status, linked tenant Dana Nguyen, linked property Sunset Ridge, linked Unit 4D, term Apr 1 2026 to Apr 1 2027, monthly rent `$1,350.00`, security deposit `$1,350.00`, and scan provenance notes.
- Agreement & Signing rendered `Regenerate lease agreement (PDF)`, `Download agreement`, `Not sent`, and blocked sending because the lease is already active. The Send for signature help popover opened and closed cleanly.
- Ledger rendered Charged/Paid/Balance `$0.00`, `All caught up — nothing owed.`, `Set opening balance`, `View deposits`, and the scanned document list. History rendered `Added lease`.
- Edit from the non-editable History tab moved safely to the Overview edit surface; Cancel returned to the read-only lease without mutation.
- Delete opened a confirmation modal with focus on Cancel and copy `Delete lease QA-2026-004-4D? This cannot be undone.` Cancelling closed it without deleting.
- Scanned document link opened `/lease-file/3` in a new tab and rendered the synthetic lease PDF. Screenshot: `output/playwright/lease-file-3-20260623.png`.
- Pre-fix Give Notice reproduction: the dialog copy said the date feeds move-out steps, but the field label was `Move-out date (optional)` and the `Give notice` button was enabled with a blank date.
- Post-fix proof: the dialog labels the field `Move-out date`, shows required workflow copy, disables `Give notice` while blank, and enables it after a typed date commits on blur. Cancel closed the dialog without changing the lease.
- Browser console check after the lease-detail pass returned zero warning-or-higher messages.

Fixed in this pass:
- `TSK397-B051` - Lease detail Give Notice allowed marking an active lease as `NoticeGiven` without the expected move-out date even though the dialog said the date feeds the move-out workflow. Fix: the Give Notice dialog now treats move-out date as required, disables submission until the date exists, and documents the state rule in a focused helper test.

Verification:
- `rtk pnpm --dir web test:unit -- src/lib/leases/lease-detail-state.test.ts` passed 142/142.
- `rtk pnpm --dir web check` passed with 0 errors and the known four unused-selector warnings in `web/src/lib/components/m3/PageHeader.svelte`.
- Browser regression: blank Give Notice dialog showed disabled submit; entering `07/15/2026` and blurring enabled submit; Cancel left `/leases/3` unchanged.

Status: Pass after fix for lease notice date gating and scanned document rendering. Continue next with application, maintenance, expense, and payment detail pages created from scan history, then remaining non-banking/non-QuickBooks inventory.

## Pass 30 Application and Work-Order Detail Continuation

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-30`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Acceptance criteria:
- Application detail should hide decision actions after approval, show the created-tenant handoff, preserve scan provenance, and expose the scanned camera image.
- Applications list search/status/sort/page controls must call the DB-backed paged endpoint, not filter only the current client page.
- Application-link generation must create a local apply URL, allow manual copy, and show copy feedback.
- Work-order detail should expose valid status transitions, status note history, edit/cancel, delete confirmation, document download, and vendor dispatch controls.
- Vendor dispatch should not allow a known-invalid text attempt when the selected vendor has no phone on file.

Evidence:
- `/applications/1` rendered approved applicant Gray Johnson, email `qa.applicant.001@example.local`, phone `555-0101`, requested Cedar Point Flats Unit 1A, scan notes, no decision buttons, and a banner saying a tenant record was created.
- `View tenant` navigated to `/tenants/2`; the tenant page rendered Gray Johnson contact info and notes `Created from rental application #1...`.
- The scanned application link opened `/application-file/1` as an image tab. Screenshot: `output/playwright/application-file-1-20260623.png`.
- Applications list search for `Johnson` made `GET /api/v1/applications/page?take=20&search=Johnson => 200`. Selecting Approved added `status=Approved`, proving the current list search/filter path is DB-backed.
- `Get application link` generated a local `https://localhost:6042/apply/...` URL; Copy changed the button to `Copied` and rendered toast `Link copied to clipboard.`
- `/maintenance/1` rendered scan-created work order `Front door lock sticks`, property Cedar Point Flats, Unit 1A, status New, priority Normal, request description, status history, attached source scan, and record history.
- Pre-fix dispatch reproduction: opening `Text a vendor`, selecting `Green Thumb Landscaping` (`no phone on file`) enabled `Text the job`; clicking it sent `POST /api/v1/work-orders/1/dispatch => 400`, showed toast `Vendor has no phone number on file; add one before dispatching.`, and produced a browser console error for the 400.
- Post-fix dispatch proof: selecting the same no-phone vendor shows inline `Add a phone number before texting this vendor the job.` and keeps `Text the job` disabled, so no new dispatch request is sent.
- Status transition proof: changed work order from New to Scheduled with note `QA pass scheduled from work-order detail`; header updated to Scheduled, available transitions became In progress / Waiting on parts / Cancelled, status history showed the note, and record history showed an update.
- Edit mode exposed title, description, property, priority, category, requested/scheduled/completed dates, estimated cost, and actual cost; Cancel returned to read-only without mutation.
- Delete opened a confirmation modal with copy `Delete "Front door lock sticks"?`; Cancel closed it without deleting.
- Clicking attached document `scan-20260623113552` downloaded `.playwright-cli/scan-20260623113552.jpeg`; `file` identified it as a JPEG, `1800x2400`, 130.1 KB. Network proof showed `GET /api/v1/documents/7/file => 200`.

Fixed in this pass:
- `TSK397-B052` - Work-order dispatch picker let users select a vendor explicitly marked `no phone on file` and then press `Text the job`, causing an avoidable API 400 and console error. Fix: dispatch eligibility is now tested in a helper, the dialog shows an inline block reason, and the confirm button remains disabled until the selected vendor has a non-blank phone number.

Verification:
- `rtk pnpm --dir web test:unit -- src/lib/maintenance/work-order-dispatch.test.ts` passed 144/144.
- `rtk pnpm --dir web check` passed with 0 errors and the known four unused-selector warnings in `web/src/lib/components/m3/PageHeader.svelte`.
- Browser regression: no-phone vendor selection kept `Text the job` disabled and showed the inline phone-number requirement; status transition, edit cancel, delete cancel, and document download paths worked.

Status: Pass after fix for work-order no-phone dispatch gating. Continue next with payment and expense detail pages created from scan history, then remaining non-banking/non-QuickBooks inventory.

## Pass 31 Payment Detail Delete Confirmation

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-31`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Acceptance criteria:
- Payment detail must render charge, tracking, scanned-document provenance, and record history for scan-created payments.
- Edit must be cancellable without mutation.
- Delete must require an explicit, cancellable confirmation step before any destructive request.
- Canceling the delete confirmation must keep the user on the payment detail page and preserve the payment.

Evidence:
- `/accounting/payments/1` rendered scan-created payment Avery Ellis, `Rent · $1125 · Paid`, lease `QA-2026-001-1A`, due date Feb 3 2026, paid date Feb 3 2026, method Check, reference `8001`, scan notes, and history `Recorded a payment`.
- The scanned payment link opened `/payment-file/1` as the generated camera check image. Screenshot: `output/playwright/payment-file-1-20260623.png`.
- Edit exposed lease, amount, payment type, due date, status, paid date, method, reference, and notes. Cancel returned to read-only without mutation.
- Pre-fix destructive repro: pressing Delete on `/accounting/payments/1` immediately navigated back to `/accounting`, removed the payment row, and dropped collected totals to zero. There was no in-app confirmation dialog to cancel.
- Post-fix browser proof used a replacement local UI-created payment, `/accounting/payments/2`, for Avery Ellis, `$10.00`, Scheduled, due Jun 15 2026.
- Pressing Delete on `/accounting/payments/2` kept the URL on `/accounting/payments/2` and opened a `Delete payment` confirmation dialog with copy `Delete this $10.00 rent payment? This cannot be undone.`
- Pressing Cancel closed the dialog, stayed on `/accounting/payments/2`, and the payment detail still rendered the `$10.00` scheduled payment and history.

Fixed in this pass:
- `TSK397-B053` - Payment detail Delete bypassed the shared confirmation pattern and called the delete mutation directly from the header button. Fix: payment detail now tracks a pending delete target, renders the shared `ConfirmDialog`, and only deletes from the dialog confirm action.

Verification:
- RED: `rtk pnpm --dir web test:unit -- src/lib/accounting/payment-detail-delete.test.ts` failed before the fix because payment detail had no `ConfirmDialog` and still wired `onclick={() => deleteMutation.mutate()}`.
- GREEN: `rtk pnpm --dir web test:unit -- src/lib/accounting/payment-detail-delete.test.ts` passed 145/145 after the fix.
- Frontend check: `rtk pnpm --dir web check` passed with 0 errors and the known four unused-selector warnings in `web/src/lib/components/m3/PageHeader.svelte`.
- Browser regression: payment detail Delete opened a cancellable dialog; Cancel preserved `/accounting/payments/2` and the record.

Status: Pass after fix for payment-detail destructive confirmation. Continue next with expense detail pages, then remaining non-banking/non-QuickBooks inventory.

## Pass 32 Unit Expense/Rent Workflow and Quick-Post Payment Tracking

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-32`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Acceptance criteria:
- Unit Expenses tab should create a unit-scoped expense, show it in the unit list, support inline expansion/edit/cancel/save, and deep-link to expense detail.
- Expense detail should persist paid date and itemized receipt rows, expose delete confirmation, and keep history/audit expansion useful.
- Unit Rent tab should clear an overdue scheduled payment when marked paid, update header/next-best action/outstanding balance, and support posting an additional rent payment.
- Unit Rent tab quick Post payment must capture method, reference, and notes before save, not require a second detail edit.

Evidence:
- `/units/1?tab=expenses` created `QA filter replacement` for `$12.34`; row appeared first with Pending and activity linked to `/accounting/expenses/2`.
- Inline expense edit changed amount to `$14.99` and status to Paid; toast `Expense updated.` and row/detail reflected the update.
- `/accounting/expenses/2` saved paid date Jun 23, 2026 and line item Air filter, qty 1, unit price/amount `$14.99`; detail showed paid date and line item table total.
- Expense detail Delete opened cancellable `Delete expense` dialog and Cancel preserved the record.
- Unit expense/payment update history buttons expanded audit metadata in place; recorded activity rows deep-linked correctly.
- `/units/1?tab=rent` followed `Collect $10.00`, expanded scheduled rent payment, changed status to Paid, paid date Jun 23 2026, method Check; header changed to `Rent current`, outstanding `$0.00`, and next-best action changed to `Rent on track`.
- Pre-fix quick-post reproduction: posting `$15` rent from unit created `/accounting/payments/3`, but detail showed Method `-` and Reference `-` because the create form had no method/reference/notes fields.
- Post-fix browser proof: quick Post payment form rendered Method, Reference, and Notes. Posting `$16` with Method Check, Reference `QA-REF-004`, Notes `Quick post captured method and reference` created `/accounting/payments/4`, whose detail showed all three values.

Fixed in this pass:
- `TSK397-B054` - Unit Rent tab Post payment created valid payments but omitted method/reference/notes capture, forcing check/cash/manual payments through a second edit. Fix: quick-post state/form now includes method, external reference, and notes, using the shared `PAYMENT_METHODS` list so web/mobile method values stay aligned.

Verification:
- RED: `rtk pnpm --dir web test:unit -- src/lib/components/unit/rent-tab-create.test.ts` failed before the fix because create state/form lacked method/reference/notes.
- GREEN: `rtk pnpm --dir web test:unit -- src/lib/components/unit/rent-tab-create.test.ts` passed 146/146 after the fix.
- Frontend check: `rtk pnpm --dir web check` passed with 0 errors and the known four unused-selector warnings in `web/src/lib/components/m3/PageHeader.svelte`.
- Browser regression: `/accounting/payments/4` showed Method Check, Reference `QA-REF-004`, and Notes `Quick post captured method and reference` from the unit quick-post form.

Status: Pass after fix for unit rent quick-post payment tracking. Continue next with remaining unit tabs: Lease, Maintenance, Documents, Timeline, Unit Edit, and Scan/Upload; then fold explorer inventory gaps into implementation slices.

## Pass 33 Unit Command Center Remaining Workflows

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-33`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Acceptance criteria:
- Unit Lease tab should show the current lease, expose a real lease-detail handoff, and preserve unit/property context when scanning a replacement or supporting lease document.
- Unit Maintenance tab should support empty-submit validation, cancellable creation, happy-path work-order creation, work-order detail navigation, and work-order receipt scan context.
- Unit Documents tab should show unit-related documents across lease/work-order/expense sources, open stored files through the app proxy, and preserve unit context when scanning a new document.
- Unit Timeline tab and Recent activity sidebar should link record-creation activities to their target records and expand update activities in place with before/after values.
- Unit Edit should seed existing values, block invalid blank unit numbers, and allow cancel without mutation.
- The top-level Unit Scan / Upload action should enter scan capture with property/unit context and a return target to the current tab.

Evidence:
- Landed the prior checkpoint first: PR `#254` merged to `main` at `67320d1`, remote branch `tsk-397-full-ui-pass-32` deleted, and the worktree moved to `tsk-397-full-ui-pass-33` from updated `origin/main`.
- `/units/1?tab=lease` rendered lease `QA-2026-001-1A`, status Active, tenant Avery Ellis, rent `$1,125.00`, deposit `$1,125.00`, and term Jan 1 2026 to Jan 1 2027.
- Lease-tab `Scan / upload lease` navigated to `/scan?type=Lease&propertyId=1&unitId=1&returnTo=%2Funits%2F1%3Ftab%3Dlease`, with Lease Agreement selected and upload copy `Drop a lease agreement here`.
- Overview `View lease` switched to the Lease tab; Lease-tab `Open lease` navigated to `/leases/1`, which rendered the lease detail overview and linked back to `Unit 1A`.
- `/units/1?tab=maintenance` empty-submit on `New work order` showed inline `Title is required` and `Description is required` without leaving the unit page.
- Maintenance happy path created `QA hallway light flickers` with description `Hallway light outside Unit 1A flickers during evening walkthrough; please inspect fixture and switch.` Toast showed `Work order created.`, the header changed from `1 open repair` to `2 open repairs`, the new work order appeared first with New/Normal status, and Recent activity linked to `/maintenance/2`.
- New work-order receipt scan navigated to `/scan?type=Expense&propertyId=1&unitId=1&workOrderId=2&returnTo=%2Funits%2F1%3Ftab%3Dmaintenance`, proving the receipt would attach back to the job.
- Maintenance `Open work order` navigated to `/maintenance/2`; the detail page rendered title, New/Normal status, property Cedar Point Flats, `Unit 1A` back-link, status transition buttons, vendor dispatch, edit/delete, empty documents, status history, and record history.
- `/units/1?tab=documents` grouped stored documents by Expense, Lease, and WorkOrder and exposed app-proxy view links such as `/document-file/1`.
- Documents `Scan / upload` navigated to `/scan?type=Expense&propertyId=1&unitId=1&returnTo=%2Funits%2F1%3Ftab%3Ddocuments`, preserving the unit context.
- Opening `/document-file/1` in a new tab rendered the generated scanned image as `1 (1800x2400)`. The only console issue was a standalone image-document `/favicon.ico` 404, not an app route failure.
- `/units/1?tab=timeline` rendered full unit activity with the newly created work order first; linked creation rows navigated to their records, and expanding `Updated payment` showed before/after Method, Status, and Paid date values.
- Unit edit modal seeded Unit number `1A`, Beds `2`, Baths `1`, and Rent `1125`. Clearing Unit number and saving showed `Unit number is required`; Cancel closed the modal and preserved `Unit 1A`.
- Top-level Unit `Scan / Upload` navigated to `/scan?type=Expense&propertyId=1&unitId=1&returnTo=%2Funits%2F1%3Ftab%3Dtimeline`, preserving the current tab in `returnTo`.

Fixed in this pass:
- No production defect required a code patch in this slice. Existing known no-op lifecycle tasks remain tracked separately: `TSK-401` for List this unit and `TSK-404` for Send renewal.

Verification:
- Browser proof through Playwright CLI on the live local stack covered the Lease, Maintenance, Documents, Timeline, Edit, top-level Scan / Upload, stored-image document, and lease-detail handoff paths above.
- Current app-page console check after the unit-to-lease detail handoff returned zero warning-or-error messages.
- No code changed in this pass; no unit test rerun was required beyond the prior landed Pass 32 verification.

Status: Pass for the remaining occupied-unit command-center workflows exercised here. Continue next with property detail/list workflows, tenant detail/list workflows, lease list/detail gaps, and the tracked lifecycle no-op tasks.

## Pass 34 Properties List and Detail Workflows

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-34`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Acceptance criteria:
- Properties list search, type filter, status filter, sorting, paging, row open, row edit, row delete, create, and filtered empty states must operate through the DB-backed paged endpoint and preserve URL state.
- Creating from a filtered empty list must not create a record that immediately disappears because the modal defaults conflict with the active filters.
- Property detail must support identity/cost-basis edit/cancel/save, cancellable delete, unit create/edit/open/delete, loan create/edit/schedule/delete, recurring expense create/edit/delete, and activity history expansion.
- Property detail changes must recalculate visible summary cards and preserve navigation to related Unit Command Center records.

Evidence:
- `/properties` rendered Cedar Point Flats, Riverside Flats, and Sunset Ridge. Search for `Sunset` made `GET /api/v1/properties/page?take=20&search=Sunset&portfolioId=2 => 200`; selecting Multi-family added `type=MultiFamily`, proving the primary search/type filters are DB-backed.
- Status filter options rendered All statuses, Active, Under maintenance, and Inactive. Selecting Inactive navigated to `/properties?status=Inactive` and rendered a status-filtered empty state.
- Created disposable property `QA Maple Annex`, then opened `/properties/4`; detail rendered identity, address, details, cost basis, units, loans, recurring expenses, leases, and history.
- Add Unit empty submit showed `Unit number is required`; creating `QA-1`, editing rent to `$1,400`, opening `/units/4`, returning by breadcrumb, and deleting the unit all updated the property summary and occupancy counts correctly.
- Add Loan empty submit showed required validation for lender, original amount, interest rate, start date, and monthly P&I. Creating `QA Mutual Bank`, expanding `Amortization schedule`, editing escrow settings, and cancelling/confirming delete all behaved correctly.
- Add Recurring Expense empty submit showed required validation for description, amount, and start date. Creating annual tax true-up, editing amount, cancelling delete, and confirming delete all behaved correctly.
- Property edit seeded existing fields; changing name, purchase price, land value, and in-service date updated the detail page and produced an expandable history row with before/after values.
- Property delete opened cancellable confirmation `Delete "QA Maple Annex Updated"? This also removes its units.`; confirming removed the disposable property and returned to `/properties`.
- Row edit/delete on seeded Cedar Point Flats opened seeded edit and delete confirmation surfaces; Cancel preserved the row.
- Pre-fix filtered-empty reproduction: `/properties?status=Inactive` showed first-run copy `No rentals yet` / `Add your first property`; pressing the empty-state action opened New Property with Status `Active`, so saving would create a property hidden by the current Inactive filter.
- Post-fix proof: `/properties?status=Inactive` now renders `No properties match your filters`, description `Try adjusting search or filters, or add a property that matches this view.`, and action `Add property`; opening it seeds Status `Inactive`.
- Toolbar create proof: `/properties?type=Commercial&status=Inactive` then `New Property` opened the modal with Type `Commercial` and Status `Inactive`.
- No backend/data-access code changed in this slice; the existing properties page continues to call the paged endpoint for search/filter/sort/page.

Fixed in this pass:
- `TSK397-B055` - Filtered properties empty states used first-run copy and the New Property draft ignored active type/status filters. Fix: property list state now has a tested draft builder and filter-aware empty copy helper, and the page seeds create modals from valid active filters.

Verification:
- RED: `rtk bash -lc 'cd web && node --test --experimental-strip-types src/lib/properties/property-list-state.test.ts'` failed before the helper existed with `ERR_MODULE_NOT_FOUND`.
- GREEN: `rtk bash -lc 'cd web && node --test --experimental-strip-types src/lib/properties/property-list-state.test.ts src/lib/properties/property-labels.test.ts'` passed 9/9.
- Frontend check: `rtk bash -lc 'pnpm --dir web check'` passed with 0 errors and the known four unused-selector warnings in `web/src/lib/components/m3/PageHeader.svelte`.
- Browser regression: Playwright CLI confirmed `/properties?status=Inactive` filter-aware empty copy, empty-state modal Status `Inactive`, and `/properties?type=Commercial&status=Inactive` toolbar modal Type `Commercial` plus Status `Inactive`.

Status: Pass after fix for properties filtered-create defaults. Continue next with tenant detail/list workflows, lease list gaps, and the tracked lifecycle no-op tasks (`TSK-401`, `TSK-404`) after the main inventory lanes.
