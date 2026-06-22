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

- Application scan confirm creates the `RentalApplication`, but the application detail page does not expose the original scanned PDF; there is no persisted source-file relationship on the application record yet.
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
