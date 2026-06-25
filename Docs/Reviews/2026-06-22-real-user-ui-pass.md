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

## Findings Index

This is the consolidated ledger for the pass. The detailed repro, fix, test, and browser evidence remains in the pass sections below.

Fixed and verified:
- `RC-UI-001` through `RC-UI-031`: dashboard, onboarding, photo extraction, receipt scan, application flows, owner/vendor/portal/notification/signing/reporting/security/admin/public-route controls covered in the first broad pass.
- `B015` DB-side rule slices: audited report/accounting/banking/list endpoints now push the covered filtering, grouping, sorting, paging, and aggregation work to SQL. This is not a full-app DB-side certification; remaining broad grid/DataGrid risk is listed below.
- `TSK397-B017`, `B018`, `B019`: scan-new-rental progress/copy, lease extraction contact fields, and work-order detail links.
- `TSK397-B021`, `B023`, `B024`, `B025`: lease ledger currency, status enum labels, lease edit tab switching, and Unit Command Center tab URL sync.
- `TSK397-B027`, `B028`, `B029`, `B030`, `B031`, `B032`, `B034`: image/PDF wording and single-image preservation, Unit edit, scan-first onboarding, expense category labels, unit scan context, scanned-vendor linking, and expense delete confirmation.
- `TSK397-B035` through `B038`: unit-scoped payment scan lease inference, post-scan return actions, unit document rollup for expense files, and newest-first rent rows.
- `TSK397-B039`, `B041` through `B047`, `B049` through `B069`: application scan copy/detail, neutral scanned-source labels, image preservation, application requested-home linking, work-order unit grounding, scan rejection persistence, invalid upload feedback, terminal scan filters, lease notice gating, work-order no-phone dispatch gating, payment delete confirmation, unit quick-post payment method/reference/notes, filtered empty states, tenant notice behavior, document delete accessibility, duplicate confirm guarding, lease agreement/source separation, lease history note diffs, no-reload signature state, scan-first setup shortcut, lease-prefill contact fields, and payment detail currency formatting.
- `TSK397-B071` through `B075`: work-order status modal enum labels, stale completed-work-order dispatch hints/no-vendor dispatch recovery, scheduled/completed date ordering, reset-password lockout recovery, and dashboard work-order status labels.
- `TSK397-B076` and `B077`: Unit Documents direct unit-file uploads/page-level receipt labeling, and audit amount diff currency formatting.
- `TSK397-B078`: tenant delete dialogs now block active-lease deletes up front with dependency copy instead of sending users into a known server-side rejection.
- `TSK397-B079`: active lease delete confirmations now explain that the action terminates the lease record, releases the unit from active occupancy, and should be used only for duplicate or mistaken leases.
- `TSK397-B080`: completed native signing now keeps lease detail header/actions/status cards in sync with the fresh signed/active signature status while the main lease cache catches up.
- `TSK397-B081`: lease detail tabs now honor and maintain `?tab=` deep links through reload/login redirect, direct tab clicks, and edit-mode redirection back to Overview.
- `TSK397-B082` and `B083`: typed DatePicker dates now immediately update modal-gated actions, and Give Notice keeps lease/signature lifecycle state coherent so Notice given status, actions, and move-out date render together.
- `TSK397-B084`: cancelling a Notice given lease back to Active now clears the expected move-out date so the active lease no longer shows stale move-out workflow data.
- `TSK-401`: Unit Command Center `List this unit` now continues into a unit-scoped public application link with property/unit preselection.
- `TSK-404`: Unit Command Center `Send renewal` current-state retest no longer reproduces the no-op; both sample renewal units link into the tenant notice workflow and open a renewal-offer draft without sending.
- `TSK-406`: local API and Engine scan workers now share one upload directory by default and in local Docker Compose, so camera-image scans created by the web app are readable by the Engine.
- `TSK-407`: appointment edit modals now preserve a typed replacement date when the user edits the time afterward, so rescheduling does not mix the old date with the new time.
- `TSK-408`: appointment detail edit mode now seeds native datetime-local fields from local wall-clock time and converts them back to UTC on save, so an unchanged detail save does not shift the appointment.
- `TSK-409`: appointment no-show detail now uses human status labels, treats no-show as a terminal status for active transition actions, and appointment mutations refresh the app-shell upcoming badge.
- `TSK-410`: Admin Team invite now collects the required tenant link for Tenant-role users, creates a real tenant portal login, and leaves staff invites free of stale tenant ids.
- `TSK-411`: portal and staff message unread state now clears again when a live reply arrives in an already-open thread, keeping list badges and the staff message header in sync with the server read counters.
- `TSK-412`: tenant portal maintenance detail now shows the photos/documents attached to a tenant-created work order, and the tenant can open the camera-image attachment from the detail dialog.
- `TSK-413`: tenant portal lease cards now show the real property and unit labels from a single DB-side lease projection instead of falling back to `Linked property`.
- `TSK-414`: tenant portal appointments now load the signed-in tenant's upcoming scheduled/confirmed appointments from a tenant-scoped, limited DB-side projection instead of showing placeholder-only copy.
- `TSK-415`: clicking notifications from the full tenant portal notifications page now marks them read and refreshes the app-shell unread count before navigating to the target workflow.
- `TSK-416`: tenant portal payments now learn online-payment availability from `/portal/autopay` and show a disabled upfront autopay-unavailable state when Stripe is off, instead of inviting a setup click that only then fails with 503.
- `TSK-417` and `TSK-418`: tenant portal Pay now rows now respect online-payment availability before checkout, and the Unit Rent tab shows a just-posted payment immediately while shared payment/unit caches refresh.
- `TSK-419`: tenant portal lease Q&A answers now strip raw Markdown emphasis markers before rendering safe visible text.
- `TSK-420`: staff and tenant Messages now keep the selected conversation URL in sync when a user clicks a thread or returns to the list, so stale notification/deep-link query strings no longer survive after visible thread changes.
- `TSK-421`: tenant portal dashboard notification links now mark unread notifications read, refresh the app-shell unread badge, and then navigate to the target workflow.
- `TSK-422`, `TSK-423`, and `TSK-424`: tenant portal dashboard now shows upcoming tenant appointments, treats due-today rent as due today instead of overdue, and fills tenant-scoped appointment unit labels from the active/latest lease when the appointment itself has no direct unit.
- `TSK-425`: tenant dashboard quick maintenance submit now shows inline title/description validation instead of silently doing nothing on an empty submit.
- `TSK-426`: tenant portal maintenance page submit now shows inline title/description validation instead of silently doing nothing on an empty submit, while preserving valid tenant work-order creation.
- `TSK-427`: tenant-only users now land on a tenant-accessible `/portal/security` account security route from the user menu instead of bouncing away from the protected staff `/settings/security` route.

Open, watch, or deferred:
- `TSK397-B020`: scan-new-rental review still uses broad support lookups (`take=200`) for property/tenant choices. Needs bounded lookup/search contracts before production-scale DB-side compliance can be claimed for that workflow.
- `TSK397-B022` and `TSK397-B070`: lease/property history copy promises every change, but child records such as opening balances, generated agreements, document uploads, loans, and recurring expenses do not all appear in the parent history. This needs a product decision on parent-history scope.
- `TSK397-B026` / `B040`: scan processing stuck-state family. It did not reproduce in later application-scan proof after scan-review cleanup, but stays on the watch list until more scan types are rerun.
- `TSK397-B033`: receipt line-item extraction can produce incomplete line items; product policy is still needed on whether incomplete line items are optional hints or must block/warn.
- `TSK397-B048`: scan front-door voice-note permission denied state has no visible recovery/status.
- Plaid banking and connected QuickBooks/accounting provider workflows remain deferred until sandbox credentials are available. The unconnected accounting shell and OAuth-return error state are browser-proven.

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
- `/admin/users` and `/superadmin/engine`: Admin Team list/create/generated-password/role/status controls, duplicate email recovery, tenant-linked invite, self-row lockout, and one-time-password guards are browser-proven. Remaining Admin Team variants include role-denied, stale-session, service-error, and larger-page pagination. `/superadmin/engine` is verified gated by `PLATFORM_ADMIN_EMAILS` when unset; Engine health happy/error states still need a platform-admin allowlist session. `/audit` and `/admin/audit` core filters/refresh/no-results/detail/export controls are browser-proven; role-denied/error variants remain open.
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
- `TSK397-B029` was fixed in Pass 39: live onboarding now surfaces `Scan a lease` as the primary setup shortcut before spreadsheet import.

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

## Pass 35 Tenant List, Detail, Documents, and Notice Workflows

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-35`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Acceptance criteria:
- Tenants list search/filter/sort/page controls must preserve URL state and use the DB-backed tenant page endpoint.
- Filtered tenant empty states must explain the active search/filter context and keep create actions useful.
- Tenant detail must render contact, active lease count, leases, documents, history, edit/delete actions, and link active lease rows to lease detail.
- Tenant document upload must accept image files from the same scan/camera-style fixture path used by the broader scan engine, refresh the document list, and expose named view/delete controls.
- Tenant notice generation must be fast enough for the dialog, must fall back to deterministic copy if LLM copy generation is slow, and must surface any existing open tenant draft created by an earlier request.
- Forced notice generation with no eligible lease must return a specific no-eligible-record state rather than generic "nothing due" copy.
- Shared destructive confirmations must not allow rapid duplicate confirm submissions before the parent mutation flips busy state.

Evidence:
- `/tenants?q=NoSuchTenant397` rendered filtered empty copy `No tenants match your search`, description `Try adjusting the search, or add a tenant that matches this view.`, and action `Add tenant`.
- `/tenants/1` rendered Avery Ellis, contact information, one active lease, scan-created notes, document panel, and record history.
- Opening lease row `QA-2026-001-1A` from `/tenants/1` navigated to `/leases/1`; network proof included `GET /api/v1/leases/1`, lease ledger/payments, document-status, signature-status, documents, and audit.
- Reopening `Create / Send notice` on Avery Ellis returned the existing draft renewal offer immediately after an earlier timed-out attempt had already created it. The dialog rendered subject `Lease renewal for Cedar Point Flats Unit 1A`, deterministic renewal body, Portal/Email/SMS channel toggles, Dismiss, and Send. Network proof: `POST /api/v1/notices/generate => 200`.
- `/tenants/2` rendered Gray Johnson with `Active Leases` count `0` and no lease rows. Default notice generation showed `No notices are due for this tenant right now.`
- Forcing `Lease renewal offer` on Gray Johnson returned `POST /api/v1/notices/generate => 200` and rendered `No lease renewal offer could be created.` with description `This tenant needs an active eligible lease for that notice type.`
- Uploaded camera-style image fixture `output/scan-fixtures/scan-rent-check-image.png` through the tenant document panel. The document list refreshed after `POST /api/v1/documents => 201` and `GET /api/v1/documents?entityType=Tenant&entityId=2 => 200`.
- The uploaded image row exposed a named file button `scan-rent-check-image.png` and a named icon button `Delete scan-rent-check-image.png`, proving the document delete control is no longer anonymous in the accessibility tree.

Fixed in this pass:
- `TSK397-B056` - Filtered tenants empty states reused first-run copy, making an active no-match search look like an empty account. Fix: the tenants page now uses a tested helper that distinguishes first-run and filtered-empty states.
- `TSK397-B057` - Tenant notice generation could exceed the frontend dialog window when LLM copy generation was slow, even though deterministic copy was available. Fix: notice copy generation now has a short internal timeout and falls back to deterministic templates while respecting caller cancellation.
- `TSK397-B058` - Tenant-scoped notice generation returned only newly created drafts, so a previous timed-out request could leave an existing open draft hidden from the tenant dialog. Fix: tenant-scoped generation now returns DB-side filtered open drafts for that tenant and requested notice type.
- `TSK397-B059` - Forced tenant notices with no eligible active lease used generic "nothing due" copy. Fix: the tenant notice dialog now tracks the forced notice label and renders a specific no-eligible-record message.
- `TSK397-B060` - Document-panel delete icon buttons were unlabeled, making uploaded document deletion ambiguous for assistive technology and Playwright role queries. Fix: delete controls include the document filename in their accessible name.
- `TSK397-B061` - Shared confirmation dialogs could accept rapid duplicate confirm clicks before the parent mutation propagated busy state, matching duplicate DELETE traffic observed during tenant/document delete testing. Fix: `ConfirmDialog` now has an internal submit latch in addition to the external busy prop.

Verification:
- RED: `rtk pnpm --dir web test:unit -- src/lib/tenants/tenant-list-state.test.ts src/lib/components/shared/documents-panel-accessibility.test.ts src/lib/components/shared/confirm-dialog-submit.test.ts` failed before the helpers/labels/latch existed.
- RED: `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter FullyQualifiedName~NoticeDraftServiceTests --no-restore` failed before the notice timeout/existing-draft fix.
- GREEN: `rtk pnpm --dir web test:unit` passed 157/157.
- GREEN: `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter FullyQualifiedName~NoticeDraftServiceTests --no-restore` passed 4/4.
- Browser regression: Playwright CLI confirmed filtered tenant empty copy, existing tenant notice draft retrieval, no-lease forced notice copy, tenant-to-lease handoff, and image upload with named document delete control.

Status: Pass after fixes for tenant filtered-empty copy, notice timeout/draft visibility/no-eligible copy, document delete accessibility, and shared confirmation duplicate-submit guarding. Continue next with lease list/detail gaps and the tracked lifecycle no-op tasks (`TSK-401`, `TSK-404`) after the main inventory lanes.

## Pass 36 Lease List and Agreement Document Corrections

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-36`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Acceptance criteria:
- Lease list search, filter, and sort controls must preserve URL state and continue using the DB-backed paged lease endpoint.
- Filtered/search empty states must explain the active filter context instead of looking like a first-run empty account.
- User-facing lease status controls must render human labels such as `Notice given`, while preserving the API enum value `NoticeGiven`.
- Manual New Lease status selection must use the same human labels.
- Lease detail Agreement & Signing must distinguish generated lease agreements from source scan attachments, including source PDFs and camera images.
- Scanned/source documents must remain visible through the document panels and overview preview, but must not appear as downloadable generated lease agreements.

Evidence:
- `/leases/1` rendered lease `QA-2026-001-1A`, status Active, property Cedar Point Flats, `Unit 1A`, tenant Avery Ellis, rent `$1,125.00`, term Jan 1 2026 to Jan 1 2027, financials, parties, notes, and scanned document preview.
- Give Notice opened a modal, kept `Give notice` disabled with an empty Move-out date, enabled it after valid `09/01/2026`, and Cancel closed without mutation.
- Pre-fix Agreement & Signing repro: before regenerating a true agreement for `/leases/1`, `Download agreement` saved `lease-agreement-1.pdf`, but `file` identified it as JPEG image data. Network proof: `GET /api/v1/leases/1/document => 200` with source scan content type `image/jpeg`.
- Regenerating `/leases/1` created a true two-page PDF; the replacement download saved as `lease-agreement-1.pdf` and `file` identified it as `PDF document, version 1.4`.
- `/leases` initial load and list controls used the paged endpoint, including `GET /api/v1/leases/page?take=20&portfolioId=2 => 200`, `GET /api/v1/leases/page?take=20&portfolioId=2&status=Draft => 200`, search with `NoLease397`, and tenant sort with `sort=tenantName`.
- Pre-fix filtered empty state for `/leases?status=Draft` rendered first-run copy `No leases yet` and `Add your first lease`.
- Post-fix browser proof for `/leases?status=Draft` rendered `No leases match your filters`, description `Try adjusting search or filters, or add a lease that matches this view.`, and action `Add lease`.
- Post-fix lease status menu rendered All statuses, Draft, Active, `Notice given`, Expired, and Terminated. The New Lease modal status picker also rendered `Notice given`.
- Source-scan-only lease `/leases/2` had database attachments `scan-20260623114451` / `image/jpeg` and no `lease-2-agreement.pdf`.
- Post-fix `/leases/2` Agreement & Signing rendered `Generate lease agreement (PDF)` and `No agreement has been generated yet`, with no `Download agreement` action.
- API proof for `/leases/2`: `GET /api/v1/leases/2/document-status => 200` returned `{"leaseId":2,"hasDocument":false,"storedFileId":null,"fileName":null,"fileSize":null,"downloadUrl":null,"generatedAt":null}` while `GET /api/v1/documents?entityType=Lease&entityId=2 => 200` still returned the source image attachment.
- Console check on the post-fix regression route returned zero warning-or-error messages.
- User-reported Unit Command Center `Send renewal` no-op is still intentionally deferred. Current task is `TSK-404`; it already contains the 2026-06-23 screenshot and reproduction notes.

Fixed in this pass:
- `TSK397-B062` - Filtered/search lease empty states reused first-run copy. Fix: lease list state now has tested first-run versus filtered-empty copy.
- `TSK397-B063` - Lease status controls leaked raw enum text `NoticeGiven`. Fix: lease list and manual lease form status controls render human labels while preserving enum values.
- `TSK397-B064` - Lease Agreement download/status counted source scan attachments as generated agreements. Fix: generated agreement lookup now requires the generated agreement filename `lease-{id}-agreement.pdf` and `application/pdf` content type, so source scans remain documents but not agreements.

Verification:
- RED: `rtk pnpm --dir web exec node --test --experimental-strip-types src/lib/leases/lease-list-state.test.ts` failed before the helper existed.
- RED: `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~LeaseAgreementDocumentTests.SourceScanAttachment_DoesNotCountAsGeneratedAgreement" --logger "console;verbosity=normal"` failed before the backend filter because source scans counted as generated agreements.
- GREEN: `rtk pnpm --dir web exec node --test --experimental-strip-types src/lib/leases/lease-list-state.test.ts src/lib/utils/status-labels.test.ts` passed 7/7.
- GREEN: `rtk pnpm --dir web test:unit` passed 160/160.
- GREEN: `rtk pnpm --dir web check` passed with 0 errors and the known four unused-selector warnings in `web/src/lib/components/m3/PageHeader.svelte`.
- GREEN: `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~LeaseAgreementDocumentTests" --logger "console;verbosity=normal"` passed 19/19, with the existing SQLite package advisory warning.
- Browser regression: Playwright CLI confirmed filtered lease empty copy, list status label `Notice given`, New Lease status label `Notice given`, source-scan-only agreement missing state, source document still present in `/documents`, and zero console warnings/errors.

Status: Pass after fixes for lease filtered-empty copy, status-label presentation, and generated agreement/source scan separation. Continue next with remaining lease detail edit/delete/ledger/history workflows and then the tracked lifecycle no-op tasks (`TSK-401`, `TSK-404`) after the main inventory lanes.

## Pass 37 Lease Detail Ledger, Documents, and History Continuation

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-37`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Acceptance criteria:
- Lease detail edit/save must persist user-visible fields and give a visible success result.
- Lease ledger must make charged/paid/balance math understandable and support opening balance create, edit, cancel, and removal paths.
- Lease payment and deposit links must navigate to the corresponding real work surfaces.
- Lease document controls must upload/download/delete both PDF and camera-image style files without losing source records.
- Lease history must expose meaningful field-level changes for user edits, including note-only edits made after scan/import.

Evidence:
- `/leases/1` rendered lease `QA-2026-001-1A`, status Active, Cedar Point Flats `Unit 1A`, tenant Avery Ellis, rent `$1,125.00`, scanned document preview, and editable notes.
- Edited only Notes to `Imported from scanned lease document. Pass 37 history diff proof.`; the page showed `Lease updated.` and the Overview rendered the new note.
- Ledger opening balance validation blocked an empty amount with `Enter an amount of 0 or more.`.
- Created tenant-owed opening balance `$25.50`; ledger updated to Charged `$66.50`, Paid `$41.00`, Balance `$25.50`, and rendered `Avery Ellis still owes $25.50.`.
- Edited the same opening balance to tenant credit `$5.25`; ledger updated to Charged `$41.00`, Paid `$46.25`, Balance `-$5.25`, and rendered `Paid ahead by $5.25 (credit on the account).`.
- Opening-balance removal confirmation Cancel preserved the credit; confirming removal returned the ledger to Charged `$41.00`, Paid `$41.00`, Balance `$0.00`. DB proof: `select ... from "OpeningBalances" where "LeaseId" = 1` returned zero rows.
- Payment ledger row opened `/accounting/payments/2`, rendering Avery Ellis, Rent `$10`, Paid, lease backlink, charge fields, payment tracking, and history. The lease backlink returned to `/leases/1`.
- `View deposits` navigated to `/deposits`, rendering the Security Deposits page and `New Holding`.
- Overview `View scanned document` opened `/lease-file/1`; the raw file tab produced only the already-known `favicon.ico` 404 in that tab.
- Downloaded generated `lease-1-agreement.pdf`; uploaded camera-image fixture `output/scan-fixtures/scan-rent-check-image.png`; the document panel showed the image with correct file metadata; downloaded it back; delete confirmation rendered `Delete "scan-rent-check-image.png"? This cannot be undone.`; confirming delete soft-deleted only that uploaded image. DB proof: `StoredFiles` kept source `scan-20260623112859`, generated `lease-1-agreement.pdf`, and marked uploaded `scan-rent-check-image.png` deleted.
- Pre-fix History repro: the existing `Updated lease` row for the prior note edit was static/non-expandable because `GET /api/v1/audit?...` returned `changes: []`.
- Post-fix browser proof: after the fresh note edit, History rendered the newest `Updated lease` row as an expandable button; expanding it showed field `Notes`, old value `Imported from scanned lease document. Pass 37 lease detail edit proof.`, and new value `Imported from scanned lease document. Pass 37 history diff proof.`.
- Post-fix DB proof: latest `AuditLogs` row `43` has `ChangeReason = Lease QA-2026-001-1A: notes updated`; `OldValues` includes `notes = Imported from scanned lease document. Pass 37 lease detail edit proof.` and `NewValues` includes `notes = Imported from scanned lease document. Pass 37 history diff proof.`.
- Console note: the Playwright console log retained 37 errors from the intentional API restart window (`17:00:59` through `17:03:30`, SignalR/unread-count/appointment calls while the API was stopped). The post-restart lease edit/history verification at `17:04` did not add new console entries in that log.

Fixed in this pass:
- `TSK397-B065` - Lease History did not expose a field-level diff for lease note edits because the explicit lease audit snapshot omitted `Notes`; note-only edits produced `Updated lease` rows with `changes: []` and no expandable detail. Fix: lease audit snapshots now include `notes`, and note changes add `notes updated` to the explicit change reason.

Verification:
- RED: `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter FullyQualifiedName~LeaseServiceAuditTests.UpdateAsync_RecordsNotesChangeInAuditHistoryDiff --no-restore` failed before the snapshot fix because the audit diff collection was empty.
- GREEN: `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter FullyQualifiedName~LeaseServiceAuditTests.UpdateAsync_RecordsNotesChangeInAuditHistoryDiff --no-restore` passed 1/1, with the existing SQLite package advisory warning.
- Browser regression: Playwright CLI confirmed lease note edit/save, History expandable row, and exact `Notes` old/new diff on the running local stack.

Status: Pass for covered lease detail ledger, document, and history workflows after fixing note-only history diffs. Continue next with remaining lease detail lifecycle/delete/signing paths, then resume the broader real-user app inventory.

## Pass 38 Lease Signing Source-Document Separation

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-38`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Acceptance criteria:
- Manual lease creation must support the same dependency chain as a real user expects: property enables units, tenant selection works, required empty submit stays client-side, and created rows open from the grid.
- Sending a Draft lease for signature must generate or reuse the agreement PDF and immediately show download/regenerate controls without a reload.
- Generated and signed lease PDFs must stay in Agreement & Signing, not masquerade as the original scanned/source document on Overview.
- Overview must still hide the scanned-source card for manual leases that have no actual uploaded scan source.
- Local e-sign send may use only the local provider/outbox setup; no production provider action is allowed.

Evidence:
- `/leases` New Lease empty submit showed required validation for property, unit, tenant, lease number, start date, end date, monthly rent, and security deposit without a POST.
- Created disposable UI data before this slice: property `Pass 38 Lifecycle Test`, unit `P38-1`, and tenant `Pass Thirtyeight`.
- New Lease property selection enabled the unit picker and showed only `Unit P38-1 (Vacant)` for `Pass 38 Lifecycle Test`.
- Created manual Draft lease `P38-LIFECYCLE-001` for `Pass Thirtyeight`, `Unit P38-1`, rent `$1,200.00`, dates Jul 1 2026 to Jun 30 2027; grid row opened `/leases/4`.
- Pre-fix send repro on `/leases/4`: Agreement & Signing said `No agreement has been generated yet`; pressing `Send for signature` moved the lease to `Pending signature` but the same card still displayed `No agreement has been generated yet` and no `Download agreement` until reload.
- Reload proof for the same row showed Agreement & Signing then correctly rendered `Regenerate lease agreement (PDF)`, `Download agreement`, `Sent — waiting for signature`, and `Lease pending signature`.
- Pre-fix source-document repro: after reload, Overview for the manual lease showed `Scanned document` / `The original document this lease was created from.` even though the only lease attachment was the generated agreement.
- DB proof for `/leases/4`: `StoredFiles` contained only `lease-4-agreement.pdf` (`application/pdf`) for `EntityType=Lease`, `EntityId=4`.
- Post-fix browser proof on `/leases/4`: Overview no longer rendered a scanned-source card, while Agreement & Signing still showed `Regenerate lease agreement (PDF)` and `Download agreement`.
- Created a second disposable manual Draft lease `P38-LIFECYCLE-002` (`/leases/5`) from the same UI flow to prove the no-reload send path from a clean state.
- Pre-send `/leases/5` Agreement & Signing showed `Generate lease agreement (PDF)`, `No agreement has been generated yet`, `Not sent`, and `Send for signature`.
- Post-fix browser proof: pressing `Send for signature` on `/leases/5` immediately rendered `Regenerate lease agreement (PDF)`, `Download agreement`, `Sent — waiting for signature`, and `Lease pending signature` without reload; console warning/error check returned zero messages.
- Post-fix Overview proof on `/leases/5`: no scanned-source card rendered after send.
- DB proof for disposable signing leases: `Leases` rows `4` and `5` were `PendingSignature`/`Sent`; `StoredFiles` contained only `lease-4-agreement.pdf` and `lease-5-agreement.pdf` for those manual leases.

Fixed in this pass:
- `TSK397-B066` - Manual leases with generated agreement PDFs showed those generated legal documents as Overview `Scanned document` source files because `LeaseService.GetAsync` used the generic latest lease attachment lookup. Fix: lease detail source-file lookup now filters out generated agreement and signed-lease filenames DB-side before checking storage availability.
- `TSK397-B067` - `Send for signature` generated an agreement on demand, but the lease detail page left the Agreement card in the pre-send `No agreement has been generated yet` state until reload. Fix: the send mutation marks the agreement as available when the successful e-sign response returns `Sent`.

Verification:
- RED: `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter FullyQualifiedName~LeaseAgreementDocumentTests.GetAsync_GeneratedAgreementOnly_DoesNotAdvertiseScannedSourceDocument --no-restore --logger "console;verbosity=normal"` failed before the source-file filter because `HasScan` was true.
- RED: `rtk node --test --experimental-strip-types web/src/lib/leases/lease-esign.test.ts` failed before the helper export because the no-reload send state was not modeled.
- GREEN: `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter FullyQualifiedName~LeaseAgreementDocumentTests.GetAsync_GeneratedAgreementOnly_DoesNotAdvertiseScannedSourceDocument --no-restore --logger "console;verbosity=normal"` passed 1/1, with the existing SQLite package advisory warning.
- GREEN: `rtk node --test --experimental-strip-types web/src/lib/leases/lease-esign.test.ts` passed 4/4.
- Browser regression: Playwright CLI confirmed `/leases/4` generated-agreement-only manual lease has no Overview source document, `/leases/4` Agreement tab still exposes the generated PDF, `/leases/5` send-for-signature immediately flips to generated/downloadable agreement state without reload, `/leases/5` Overview has no source document card, and console warnings/errors were zero.

Status: Pass after fixes for manual-lease generated agreement/source scan separation and no-reload send-for-signature document state. Continue next with remaining lease lifecycle/delete paths and then resume broader non-banking/non-QuickBooks inventory.

## Pass 39 Fresh-User Scan-First Onboarding

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-39`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- User: Harper Stone, `tsk397.pass39.202606231342@example.local`
- Portfolio: Harper Stone's Portfolio, id `3`

Acceptance criteria:
- A new live-mode user must be able to start from an empty portfolio and discover the scan-first rental setup path without knowing to use the sidebar.
- Scan-first setup must accept an image source file, extract property, unit, tenant, and lease fields, and preserve image source attachment behavior.
- Extracted tenant contact fields must be visible in the review step and persisted without forcing a second edit.
- No production email/SMS/provider action is allowed.

Synthetic image:
- Source HTML: `output/qa/tsk397-pass39/riverside-courtyard-lease.html`
- Uploaded image: `output/qa/tsk397-pass39/riverside-courtyard-lease-photo.jpg` (`image/jpeg`, 1800x2400)

Evidence:
- Public landing `Get started` reached `/register`; local email/password registration created Harper Stone and showed the email-verification state.
- Local outbox contained the verification email for `tsk397.pass39.202606231342@example.local`. The embedded URL used the default `https://localhost:5667` because this manual API restart omitted `App__WebBaseUrl`; the normal start script derives `App__WebBaseUrl` from `WEB_PORT`, so this was treated as a local-stack note rather than a product bug in this slice.
- After verifying on the active `6042` web port, login landed on `/choose-setup`; selecting `Set up my real portfolio` reached `/onboarding`.
- DB empty-state proof for portfolio `3`: properties `0`, units `0`, tenants `0`, leases `0`, scan drafts `0`.
- Pre-fix gap: onboarding headline promised "The computer does the typing" but only surfaced manual property fields and spreadsheet import; scan-first setup was discoverable only from the sidebar.
- Post-fix browser proof: onboarding renders `Scan a lease` before `Import from a spreadsheet`; clicking it navigates to `/scan/new-rental`.
- Uploaded the synthetic lease JPEG through `Photos of the lease`; draft `10` processed via `claude-cli:sonnet` and reached the five-step guided review automatically.
- Extraction prefilled property `Riverside Courtyard`, address `812 Birch Avenue`, Columbus OH `43215`; unit `2A`, 2 beds, 1 bath, rent `1275.00`; tenant `Maya Ortiz`; lease `RC-2A-2026`, Jul 1 2026 to Jun 30 2027, rent/security deposit `$1,275.00`, late fee `$75.00`, due day `1`.
- Pre-fix repro for `TSK397-B068`: scan draft `10` stored `tenant_email`, `tenant_phone`, and `tenant_emergency_contact` at confidence `1.0`, but the Tenant review step rendered Email, Phone, and Emergency contact blank.
- Post-fix browser proof after reloading draft `10`: Tenant review step prefilled `maya.ortiz.pass39@example.local`, `614-555-0139`, and `Luis Ortiz, 614-555-0140`.
- Confirming the review created lease `/leases/6`; detail rendered `RC-2A-2026`, Active, `Riverside Courtyard · Unit 2A · Maya Ortiz`, rent `$1,275.00`, term dates, financials, notes `Imported from scanned lease document.`, and an image `Scanned document` preview linking to `/lease-file/6`.
- DB proof for lease `RC-2A-2026`: property `Riverside Courtyard`, unit `2A`, tenant `Maya Ortiz`, email `maya.ortiz.pass39@example.local`, phone `614-555-0139`, emergency contact `Luis Ortiz, 614-555-0140`, rent/deposit `1275.00`, dates `2026-07-01` to `2027-06-30`.
- DB proof for portfolio `3` stored files: `Lease` entity `6`, file `scan-20260623174838`, content type `image/jpeg`; thumbnail `scan-thumb-20260623174838`, content type `image/jpeg`.
- Browser console warning/error check after the completed flow returned zero messages.

Fixed in this pass:
- `TSK397-B029` - Clean live setup started with manual property entry while the flagship scan-first path was only discoverable through the sidebar. Fix: onboarding now renders shared setup shortcuts with primary `Scan a lease` linking to `/scan/new-rental`, followed by spreadsheet import. Regression: `web/src/lib/onboarding/setup-shortcuts.test.ts`.
- `TSK397-B068` - New-rental image lease extraction returned tenant contact fields, but `toLeasePrefill` dropped them so the review UI showed blank tenant email/phone/emergency fields and would lose them unless the user manually retyped them. Fix: lease prefill now carries tenant email, phone, and emergency contact through to the tenant step and confirmation overrides. Regression: `web/src/lib/scan/lease-prefill.test.ts`.

Verification:
- RED: `rtk node --test --experimental-strip-types web/src/lib/onboarding/setup-shortcuts.test.ts` failed before the shortcut metadata existed.
- RED: `rtk node --test --experimental-strip-types web/src/lib/scan/lease-prefill.test.ts` failed because `values.tenantEmail` was undefined.
- GREEN: `rtk node --test --experimental-strip-types web/src/lib/scan/lease-prefill.test.ts web/src/lib/onboarding/setup-shortcuts.test.ts` passed 2/2.
- Browser regression: Playwright CLI confirmed the new onboarding scan shortcut, image upload, extracted tenant contact prefill after reload, confirmation, lease detail source-image preview, DB persistence, and zero browser warnings/errors.

Status: Pass for fresh-user scan-first onboarding discovery and the image lease scan path after fixes. Continue next with the newly created rental spine through dashboard, unit/property/tenant follow-through, lease lifecycle/delete paths, and remaining non-banking/non-QuickBooks inventory.

## Pass 40 Fresh-User Dashboard, Settings, Property, Unit, and Payment Continuation

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-40`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- User: Harper Stone, `tsk397.pass39.202606231342@example.local`
- Portfolio: Harper Stone's Portfolio, id `3`

Acceptance criteria:
- A new user who created the first rental by scanning a lease must be able to continue naturally from the dashboard into setup, settings, property financial setup, unit command center, and payment tracking.
- Dashboard setup checklist links must land on actionable settings sections and mark themselves complete when settings are saved.
- Property detail must support real owner setup work: mortgage/loan entry, recurring expense entry, validation, readable saved rows, and schedule visibility.
- Unit Rent must support quick payment posting with method/reference/notes and provide a full payment detail path.
- Payment detail must format money consistently anywhere the same amount appears.

Evidence:
- Login as Harper Stone rendered Dashboard for the scan-created portfolio: 100% occupied, 1/1 occupied, no open work orders, no overdue balance, active lease mix, and recent activity for owner/property/tenant/lease creation.
- Dashboard `See the checklist` opened `/get-started?view=checklist`; the scan-created records checked off the six core setup steps.
- Checklist `Set where alerts go` reached Settings Notifications. Saving `owner.alerts.pass39@example.local` with the dedicated alert-email Save persisted after reload and moved the checklist to 7 of 8. Daily briefing in-app toggle also persisted after reload.
- Checklist `Turn on automatic reminders` reached Settings Automations. Turning on auto-post rent charges and auto-assess late fees, changing rent lead days to `7`, and saving persisted after reload.
- Returning to Dashboard after the last checklist step removed the checklist card and showed the operational portfolio dashboard.
- Dashboard `View properties` opened `/properties`; the properties grid showed `Riverside Courtyard`, address `812 Birch Avenue, Columbus, OH`, status Active, 1 unit, 1 occupied, with search/type/status filters plus New/Edit/Delete controls visible.
- Property detail `/properties/6` rendered identity/address/cost-basis, unit row `2A`, empty loans, empty recurring expenses, lease `RC-2A-2026`, and property history.
- Empty Add Loan submit showed inline required validation for lender, original amount, interest rate, start date, and monthly P&I.
- Added loan `First City Bank`, original `215000`, balance `212500`, rate `6.25`, start `2025-07-01`, P&I `$1,300.42`, escrow `$425.00`, taxes/insurance escrow flags; saved row rendered `$212,500.00`, `6.25`, `$1,300.42`, `$425.00`, Active, Schedule/Edit/Delete controls.
- Loan Schedule expanded inline with `Amortization schedule` and `No payments generated yet. The debt-service worker fills this in monthly.`
- Empty Add Recurring Expense submit showed required validation for description, amount, and start date.
- Added recurring expense `Building insurance`, category Insurance, amount `$185.50`, frequency Monthly, next run `7/1/2026`; saved row used friendly category/frequency labels and exposed Edit/Delete controls.
- Unit detail `/units/6` rendered the scan-created unit command center: occupied header, tenant contact info, lease summary, rent current, 1 document, lifecycle rail, tabs, and recent activity.
- Unit Overview `View lease` switched to the Lease tab; Lease tab exposed current lease `RC-2A-2026`, tenant Maya Ortiz, rent/deposit `$1,275.00`, dates, `Open lease`, and `Scan / upload lease`.
- Unit Rent empty state exposed `Post payment` and `Scan receipt`. Empty payment submit showed `Amount is required`.
- Posted rent payment amount `1275`, method Check, reference `CHK-1001`, notes `First rent payment received at move-in walkthrough.`; the Rent tab immediately showed `Jun 23, 2026 - Rent - Paid - $1,275.00`, outstanding `$0.00`, and recent activity linked to `/accounting/payments/5`.
- Payment row expanded inline with amount, due/paid dates, type, status, and method. Full payment detail showed the reference and notes.
- Pre-fix repro for `TSK397-B069`: payment detail title subtitle rendered `Rent - $1275 - Paid` and Charge Amount rendered `$1275` while the hero rendered `$1,275.00`.
- Post-fix browser proof on `/accounting/payments/5`: subtitle rendered `Rent - $1,275.00 - Paid`, hero amount `$1,275.00`, Charge Amount `$1,275.00`, method Check, reference `CHK-1001`, and notes. Console warning/error check returned zero.
- Browser console warning/error checks returned zero after property financial setup and after the payment detail reload.

Open findings:
- `TSK397-B070` - Property History says "Every recorded change to this property", but loan creation and recurring expense creation did not appear after the property-level financial setup. This appears related to the already-open child-history scope issue `TSK397-B022`, but for property child records.

Fixed in this pass:
- `TSK397-B069` - Payment detail used raw amount interpolation in the page subtitle and Charge Amount field, rendering values like `$1275` instead of `$1,275.00`. Fix: added a focused payment-detail display formatter and used it for hero, subtitle, charge amount, and partial amount-paid display. Regression: `web/src/lib/accounting/payment-detail-display.test.ts`.

Verification:
- RED: `rtk node --test --experimental-strip-types web/src/lib/accounting/payment-detail-display.test.ts` failed before the helper existed.
- GREEN: `rtk node --test --experimental-strip-types web/src/lib/accounting/payment-detail-display.test.ts` passed 1/1.
- Related regression: `rtk node --test --experimental-strip-types web/src/lib/accounting/payment-detail-display.test.ts web/src/lib/accounting/payment-detail-delete.test.ts web/src/lib/accounting/expense-detail-actions.test.ts` passed 7/7.
- `rtk pnpm --dir web check` passed with 0 errors and the known four `PageHeader.svelte` unused-selector warnings.
- Browser regression: Playwright CLI confirmed the full payment detail formatting fix on the running local stack and zero browser warnings/errors.

Status: Pass for the covered fresh-user dashboard checklist, settings notification/automation, property loan/recurring-expense, unit rent, and payment-detail workflows after fixing payment amount formatting. Continue next with remaining unit Maintenance/Documents/Expenses/Timeline, tenant detail/list, lease lifecycle/delete/signing follow-through, and the broader non-banking/non-QuickBooks inventory.

## Pass 41 Fresh-User Maintenance Detail Continuation

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-41`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- User: Harper Stone, `tsk397.pass39.202606231342@example.local`
- Portfolio: Harper Stone's Portfolio, id `3`

Acceptance criteria:
- Unit Maintenance and work-order detail must support a real create -> dispatch -> status -> document -> rating -> edit workflow without leaking implementation enum names.
- Completed work orders must not continue showing active dispatch helper copy that implies the vendor can still close the job by SMS.
- Work-order dispatch with no vendors should give a next action rather than a dead empty state.
- Work-order cost/timing edit must reject completion dates before scheduled visits and allow users to correct invalid data.
- Work-order camera/image attachments must upload, render, download, and preserve image content.

Evidence:
- `/units/6?tab=maintenance` started from the scan-created unit, showed zero work orders, and exposed `New work order` plus `Scan receipt`.
- Empty work-order submit showed `Title is required` and `Description is required`.
- Created `Bathroom vanity leak`, priority High, category Plumbing; Unit header updated to `1 open repair`, and Recent activity linked to `/maintenance/3`.
- Before the no-vendor follow-up, `Text a vendor` on the new work order showed an empty vendor state with only cancel/disabled submit. The page now includes a direct `Add vendor` action to `/vendors?create=1` for that empty state. The current local portfolios both already have vendors after the real dispatch flow, so the markup fix is recorded here without a second no-vendor account.
- Created synthetic vendor `Harbor City Plumbing` through `/vendors?create=1`; empty save showed `Name is required`, then save succeeded with phone `614-555-0188`, email `dispatch@harborcity.example`, Preferred and W-9 flags.
- Dispatch dialog required selecting a vendor before submit; selecting Harbor City Plumbing and submitting a local SMS-provider dispatch produced `Job texted to the vendor.`, assigned vendor links (`Call`, `Text`, `Email`), and a dispatch history row.
- Status path New -> Scheduled -> In progress -> Waiting on parts -> Completed saved notes and rendered readable status history rows. Pre-fix, the modal copy leaked raw enum names such as `InProgress` and `WaitingParts`.
- Uploaded camera image `output/qa/production-scale-scans/05-work-orders-camera/work-order-014.jpg` to the work order; detail listed the JPG, and downloading it produced a real JPEG, 1800x2400, about 130 KB.
- Rating modal stayed disabled until a star value was chosen; saving a 5-star rating with a comment returned `Thanks - rating saved.`
- Pre-fix, the completed work order still displayed `Harbor City Plumbing has the job. When they text back DONE, this work order closes automatically.` Post-fix snapshot of `/maintenance/3` showed no active dispatch hint after status Completed while preserving vendor contact links.
- Pre-fix, editing the completed work order allowed Scheduled For Jun 24, 2026 and Completed Jun 23, 2026. Post-fix browser proof saved the same inverted pair and showed the toast `The completion date can't be before the scheduled visit.`; correcting Scheduled For to Jun 22, 2026 and Completed to Jun 23, 2026 saved successfully.
- Created a second work order, `Pass 41 status label check`, from Unit 2A to prove non-terminal modal text after the fix. Opening `In progress` showed `Change status from New to In progress.`; after saving that transition, opening `Waiting on parts` showed `Change status from In progress to Waiting on parts.` with no raw enum tokens.

Browser console note:
- The Playwright console log includes expected `400` entries for the two intentional invalid work-order edit submissions, plus transient `500` SignalR/API entries from restarting and stopping the local API in the same browser context. These were environment/proof artifacts during the validation check, not crashes from the repaired status/date flows.

Fixed in this pass:
- `TSK397-B071` - Work-order status-change modal copy leaked raw enum tokens for compound statuses. Fix: status transition helper formats both current and next status through the shared status-label helper. Regression: `web/src/lib/maintenance/work-order-dispatch.test.ts`.
- `TSK397-B072` - Completed/cancelled work orders could still show active vendor-dispatch helper copy, and the no-vendor dispatch state did not offer a direct next action. Fix: terminal statuses suppress the active dispatch hint, and the no-vendor empty state links to vendor creation. Regression: `web/src/lib/maintenance/work-order-dispatch.test.ts`; browser proof covered the completed-work-order hint.
- `TSK397-B073` - Work-order edit accepted completion before the scheduled visit, corrupting the maintenance timeline. Fix: `WorkOrderService.UpdateAsync` rejects effective scheduled/completed pairs where completion is before the scheduled visit. Regressions: `WorkOrderCostsTimingAndProjectionTests.UpdateAsync_RejectsCompletedBeforeScheduled_WhenBothTimingFieldsAreSubmitted` and `UpdateAsync_RejectsCompletedBeforeExistingScheduled_WhenCompletionIsSubmitted`.

Verification:
- RED: `rtk node --test --experimental-strip-types web/src/lib/maintenance/work-order-dispatch.test.ts` failed with missing `formatStatusTransitionCopy` / `shouldShowActiveDispatchHint` helpers before the web fix.
- GREEN: `rtk node --test --experimental-strip-types web/src/lib/maintenance/work-order-dispatch.test.ts web/src/lib/utils/status-labels.test.ts web/src/lib/components/unit/maintenance-actions.test.ts` passed 12/12.
- RED: `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter FullyQualifiedName~WorkOrderCostsTimingAndProjectionTests --no-restore` failed because no `DomainValidationException` was thrown for completed-before-scheduled.
- GREEN: `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~WorkOrderCostsTimingAndProjectionTests|FullyQualifiedName~WorkOrderControllerWindowValidationTests" --no-restore` passed 13/13.
- `rtk pnpm --dir web check` passed with 0 errors and the known four `PageHeader.svelte` unused-selector warnings.
- `rtk dotnet build-server shutdown` completed after the focused API tests.
- Browser regression: Playwright CLI confirmed completed-work-order dispatch hint removal, invalid scheduled/completed save rejection with visible toast, successful correction, fresh non-terminal status transition modals using readable labels, image document persistence/download, and unit open-repair count updates.

Status: Pass after fixes for the covered work-order detail/maintenance slice. Continue next with Unit Documents/Expenses/Timeline follow-through, tenant detail/list, lease lifecycle/delete/signing follow-through, and the broader non-banking/non-QuickBooks inventory.

## Pass 42 Auth Recovery and Dashboard Status Follow-Up

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-42`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- User: Harper Stone, `tsk397.pass39.202606231342@example.local`
- Portfolio: Harper Stone's Portfolio, id `3`

Acceptance criteria:
- A successful reset-password flow must leave the user able to sign in immediately, even if the account was locked from prior failed login attempts.
- Dashboard latest-work-order rows must display user-facing status labels, not implementation enum tokens.

Evidence:
- During account recovery, the real forgot-password/reset-password path showed the success state after password reset. The account had been locked by failed login attempts during recovery, so a successful reset that claims "you can now sign in" must clear lockout state as well as changing the password.
- The local reset UI initially reported a server connection error because the API had been manually restarted with the ASP.NET dev certificate instead of the mkcert certificate trusted by the SvelteKit server. Restarting the API with the same mkcert certificate used by the normal stack corrected that local proof environment issue; this is not logged as a product bug.
- Pre-fix dashboard proof: the logged-in Dashboard `Latest Work Orders` row rendered `Pass 41 status label check Normal InProgress · 6/23/2026`.
- Post-fix browser proof: Playwright CLI snapshot of `https://localhost:6042/` rendered the same row as `Pass 41 status label check Normal In progress · 6/23/2026`, with the row subtitle `In progress · 6/23/2026`.

Fixed in this pass:
- `TSK397-B074` - Successful password reset did not clear an active Identity lockout, so the UI could tell a user they could sign in while the lockout window still blocked them. Fix: after a successful reset, `AuthService.ResetPasswordAsync` clears `LockoutEnd` and resets the access-failed count. Regression: `RentalCommand.Api.Tests/Auth/AuthServiceResetPasswordTests.cs`.
- `TSK397-B075` - Dashboard `Latest Work Orders` subtitles rendered raw work-order status enum values such as `InProgress`. Fix: the dashboard uses the shared `formatStatusLabel` helper for work-order status text. Regression: `web/src/lib/dashboard/work-order-display.test.ts`.

Verification:
- RED: `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter FullyQualifiedName~AuthServiceResetPasswordTests.ResetPasswordAsync_ClearsLockoutSoUserCanSignInAfterReset --no-restore --logger "console;verbosity=normal"` failed before the auth fix because the reset user remained locked.
- GREEN: the same focused auth command passed 1/1 after clearing lockout/access-failed state on successful reset.
- RED: `rtk pnpm exec node --test --experimental-strip-types src/lib/dashboard/work-order-display.test.ts` failed before the dashboard fix because the page source contained raw `{order.status} ·` interpolation.
- GREEN: `rtk pnpm exec node --test --experimental-strip-types src/lib/dashboard/work-order-display.test.ts src/lib/utils/status-labels.test.ts` passed 5/5.
- Browser regression: Playwright CLI snapshot confirmed the Dashboard latest-work-order row now shows `In progress` instead of `InProgress`.

Status: Pass after fixes for reset-password lockout recovery and the dashboard work-order status label. Continue next with Unit Documents/Expenses/Timeline follow-through, tenant detail/list, lease lifecycle/delete/signing follow-through, and the broader non-banking/non-QuickBooks inventory.

## Pass 43 Unit Documents, Expenses, and Timeline Continuation

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-43`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- User: Harper Stone, `tsk397.pass39.202606231342@example.local`
- Portfolio: Harper Stone's Portfolio, id `3`

Acceptance criteria:
- Unit Documents must distinguish generic unit-file uploads from scan-to-record workflows.
- Unit camera/image uploads must attach to the unit, refresh the header document count and cross-entity rollup, expose view/delete controls, and preserve image content through the stored-file proxy.
- Unit Expenses must support create validation, create, inline edit, receipt-scan handoff, and deep links to the expense detail page.
- Unit Timeline and record History must show user-facing formatted audit diffs for money fields.

Evidence:
- Pre-fix Unit Documents repro: the tab said `Scan / upload`, but pressing it navigated to `/scan?type=Expense&propertyId=6&unitId=6&returnTo=%2Funits%2F6%3Ftab%3Ddocuments`, so a generic document upload action silently became a receipt/bill scan.
- Post-fix `/units/6?tab=documents` renders `Scan a record` plus a direct `Unit files` panel with an `Upload` action. The page-level header button now says `Scan receipt` because it routes to the default expense scan flow.
- Uploaded camera-style image fixture `output/qa/production-scale-scans/03-payments-camera/payment-001.jpg` through the Unit files uploader. The unit header updated to `3 docs`, the direct Unit files panel listed `payment-001.jpg` with size/date/delete controls, and the cross-entity rollup listed `Unit -> payment-001.jpg` with `View` linking to `/document-file/30`.
- Browser proof for `/document-file/30` showed the uploaded file as a real `1800x2400` image. Delete confirmation rendered `Delete "payment-001.jpg"? This cannot be undone.` and was cancelled to preserve the proof file.
- Unit Expenses empty submit showed `Description is required` and `Amount is required`; a synthetic expense `Replacement smoke detector batteries` saved at `$18.97`, then inline edit changed Amount to `$21.49` with `Expense updated.` toast.
- `Scan receipt` from the Expenses tab reached `/scan?type=Expense&propertyId=6&unitId=6&returnTo=%2Funits%2F6%3Ftab%3Dexpenses`, keeping the correct unit/property context for receipt extraction.
- Unit Timeline rendered newest-first activity rows. Expanding the updated expense row showed the amount diff; pre-fix it showed raw `18.97 -> 21.49`, and post-fix it showed `$18.97 -> $21.49`.
- Following the Timeline `Recorded an expense` link reached `/accounting/expenses/3`; the detail page showed title `Replacement smoke detector batteries`, amount `$21.49`, property `Riverside Courtyard`, unit link back to `/units/6?tab=expenses`, and the same History diff formatted as `$18.97 -> $21.49`.

Fixed in this pass:
- `TSK397-B076` - Unit Documents presented a generic `Scan / upload` action but routed users into the Expense scan flow, and the page-level unit header used the same generic label for its default receipt scan. Fix: the Documents tab now includes a direct `DocumentsPanel` for `entityType="Unit"` uploads, relabels the document scan action to `Scan a record`, refreshes the unit dashboard after upload/delete, and relabels the page-level default action to `Scan receipt`. Regression: `web/src/lib/components/unit/document-actions.test.ts`.
- `TSK397-B077` - Unit Timeline and record History amount diffs rendered raw decimal strings such as `18.97 -> 21.49`. Fix: shared `formatAuditChangeValue` now formats amount-like audit fields as USD currency. Regression: `web/src/lib/utils/status-labels.test.ts`.

Verification:
- RED: `rtk pnpm exec node --test --experimental-strip-types src/lib/components/unit/document-actions.test.ts` failed before the header label fix because `UnitHeader.svelte` still contained `Scan / Upload`.
- GREEN: the same focused unit document/action command passed 3/3 after the direct unit uploader and label changes.
- RED: `rtk pnpm exec node --test --experimental-strip-types src/lib/utils/status-labels.test.ts` failed before the audit formatter fix because `formatAuditChangeValue('Amount', '21.49')` returned `21.49`.
- GREEN: the same focused status-label command passed 5/5 after currency formatting amount-like audit fields.
- Browser regression: Playwright CLI confirmed Unit Documents direct upload/view/delete-confirm state, the corrected `Scan receipt` and `Scan a record` labels, Expense create/edit/scan-handoff, Unit Timeline deep links, and currency-formatted amount diffs in both Unit Timeline and expense History.

Status: Pass after fixes for Unit Documents, Unit Expenses, and Unit Timeline/History money diffs. Continue next with tenant detail/list, lease lifecycle/delete/signing follow-through, the tracked lifecycle no-op tasks, and the broader non-banking/non-QuickBooks inventory.

## Pass 44 Tenant List, Detail, Documents, and Notices Continuation

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-44`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- User: Harper Stone, `tsk397.pass39.202606231342@example.local`
- Portfolio: Harper Stone's Portfolio, id `3`

Acceptance criteria:
- Tenant list search, filtered empty state, create/edit validation, row actions, and detail navigation must behave like a real resident-contact workflow.
- Tenant detail must show linked leases, editable contact fields, document upload/download/delete controls, readable audit history, and safe destructive-action behavior.
- Tenant image uploads must preserve camera-style JPEG content through storage and download.
- Tenant notice creation must expose due-state copy, forced local draft review, delivery-channel choices, dismiss behavior, and must not send externally unless the user explicitly presses Send.
- Tenants with active leases must not present a clickable delete confirmation that is known to fail later.

Evidence:
- `/tenants?q=zz-no-match` rendered the filtered empty state `No tenants match your search`, with the action `Add tenant`. Creating a disposable tenant from that state kept the search context, validated required first/last name fields, saved `Zz-no-match Tenant`, and showed the new row inside the active search.
- Opening the disposable tenant detail showed breadcrumb/title/contact fields, `Active Leases 0`, empty Leases/Documents states, and History with `Added tenant`. Editing with blank last name showed `Last name is required`; saving `Tenant Edited` showed `Tenant updated.` and the expanded History row displayed `Last name` diff `Tenant -> Tenant Edited`.
- Tenant Documents uploaded camera-style fixture `output/qa/production-scale-scans/04-applications-camera/application-001.jpg`. The panel listed `application-001.jpg` at `141.0 KB`, downloading it through the UI produced a JPEG image at `1800x2400`, canceling delete preserved the row, and confirming delete returned to `No documents yet.`
- Disposable tenant delete rendered `Delete "Zz-no-match Tenant Edited"? This cannot be undone.` Cancel preserved the record; confirm removed it and returned to `/tenants` with the remaining active tenant row intact.
- Active tenant list edit opened the populated form for Maya Ortiz and cancel preserved the row. The list and detail delete confirmations now render `Maya Ortiz has 1 active lease. End or reassign the lease before deleting this tenant.` with the Delete button disabled.
- Active tenant detail showed the linked lease row `RC-2A-2026`, unit `2A`, rent `$1,275.00`, dates, and status `Active`.
- `Create / Send notice` for Maya Ortiz showed no automatic notices due and offered forced notice creation. Forcing `Lease renewal offer` produced a local draft with renewal copy, Portal/Email/SMS checkboxes, `Dismiss`, and `Send`; dismissing the draft showed `Draft dismissed.` No external send was submitted.

Fixed in this pass:
- `TSK397-B078` - Tenant list/detail delete confirmations allowed users to press Delete on tenants with active leases even though the API correctly blocks that dependency with a domain validation error. Fix: added shared tenant delete-state copy, added a disabled confirm state to `ConfirmDialog`, and wired tenant list/detail dialogs so active-lease tenants explain the dependency and disable the destructive action before submit. Regression: `web/src/lib/tenants/tenant-delete-state.test.ts` and `web/src/lib/components/shared/confirm-dialog-submit.test.ts`.

Verification:
- RED: `rtk pnpm exec node --test --experimental-strip-types src/lib/tenants/tenant-delete-state.test.ts src/lib/components/shared/confirm-dialog-submit.test.ts` failed before the fix because the helper did not exist and `ConfirmDialog` did not support `confirmDisabled`.
- GREEN: the same focused command passed 3/3 after adding the helper and disabled confirm handling.
- Browser regression: Playwright CLI confirmed the active tenant list and detail delete dialogs render the active-lease dependency message and disable Delete, while zero-lease disposable tenant delete still supports cancel and confirm.
- Browser console checks for the tenant-to-lease path returned zero warning/error messages.
- `rtk pnpm --dir web check`: 0 errors, 4 existing `PageHeader.svelte` unused-selector warnings.

Status: Pass after fixes for tenant list/detail create, edit, documents, history, active-lease delete guard, and local notice draft review/dismiss. Continue next with lease lifecycle/delete/signing follow-through, the tracked lifecycle no-op tasks, and the broader non-banking/non-QuickBooks inventory.

## Pass 45 Lease Delete Copy Continuation

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-45`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- User: Harper Stone, `tsk397.pass39.202606231342@example.local`
- Portfolio: Harper Stone's Portfolio, id `3`

Acceptance criteria:
- Lease detail destructive actions must describe the real business effect before a user can confirm.
- Active lease delete copy must not read like deleting a harmless draft when the backend soft-deletes the lease, logs it as terminated, and releases unit occupancy.
- Cancelling the dialog must leave the active lease untouched.

Evidence:
- Pre-fix browser repro: `/leases/6` active lease `RC-2A-2026` opened a generic confirmation titled `Delete lease` with message `Delete lease RC-2A-2026? This cannot be undone.` even though `LeaseService.DeleteAsync` soft-deletes the lease, logs `Lease RC-2A-2026 terminated`, and calls occupancy sync for the unit.
- Post-fix browser proof: the same active lease dialog now renders title `Remove active lease`, message `RC-2A-2026 is active for Riverside Courtyard - Unit 2A. Removing it will terminate this lease record, release the unit from active occupancy, and hide it from active lease workflows. Use Give Notice for a normal move-out; remove only duplicate or mistaken leases.`, and confirm label `Remove active lease`.
- Cancel was clicked after the post-fix proof, returning to `/leases/6` with the lease still active and no destructive request submitted.

Fixed in this pass:
- `TSK397-B079` - Active lease delete used generic irreversible-delete copy even though the action has lease-lifecycle and unit-occupancy side effects. Fix: added shared lease delete-state copy for active, notice-given, pending-signature, draft, and ordinary lease deletes, then wired the lease detail confirmation to that helper. Regression: `web/src/lib/leases/lease-delete-state.test.ts`.

Verification:
- GREEN: `rtk pnpm exec node --test --experimental-strip-types src/lib/leases/lease-delete-state.test.ts src/lib/tenants/tenant-delete-state.test.ts src/lib/components/shared/confirm-dialog-submit.test.ts` passed 6/6.
- Browser regression: Playwright CLI confirmed the active lease dialog renders the status-aware title, explanatory unit-release message, and `Remove active lease` confirm label; cancel returned to the active lease detail.
- `rtk pnpm --dir web check`: 0 errors, 4 existing `PageHeader.svelte` unused-selector warnings.

Status: Pass after fixing active lease delete copy. Continue next with the rest of lease lifecycle/signing follow-through, the tracked lifecycle no-op tasks, and the broader non-banking/non-QuickBooks inventory.

## Pass 46 Lease Creation and Native Signing Follow-Through

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-46`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- User: Harper Stone, `tsk397.pass39.202606231342@example.local`
- Portfolio: Harper Stone's Portfolio, id `3`

Acceptance criteria:
- A landlord can create a lease from already-created property, vacant unit, and tenant records.
- Empty required fields show validation before save, while a complete draft lease saves and appears in the lease grid.
- Sending a draft lease for native signature generates the agreement if needed, creates only local/synthetic signing delivery, and moves the lease into pending-signature state.
- The public signer can open the PDF, consent to electronic records, sign with a typed signature, and reach a terminal success page.
- After signing, staff-side lease detail must show a coherent active/signed state, suppress send/resend controls, and allow signed PDF download.

Evidence:
- `/leases` New Lease validation showed required errors for property, unit, tenant, lease number, start/end dates, monthly rent, and security deposit.
- Created draft lease `RC-2B-DRAFT-45` for `Riverside Courtyard`, `Unit 2B (Vacant)`, and tenant `Riley Draftsign` with `$1,325.00` rent, `$1,325.00` deposit, dates `8/1/2026` to `7/31/2027`, and status `Draft`; the row appeared in the lease grid and opened `/leases/7`.
- Agreement & Signing direct `Send for signature` from a draft with no generated agreement created `lease-7-agreement.pdf`, changed the card to `Sent - waiting for signature`, and wrote a local outbox email to `riley.draftsign.pass45@example.local` with localhost signing link `https://localhost:6042/sign/...`.
- Public signer page loaded without staff auth, exposed the PDF link, disabled signing until E-SIGN consent was checked, enabled typed signing for `Riley Draftsign`, and submitted to terminal copy `Signed - all done`.
- Signed-document GET returned `application/pdf` for `lease-7-agreement.pdf`; after final signing, staff-side `Download signed lease` saved `.playwright-cli/signed-lease-7.pdf`, verified as a 3-page PDF.

Bug `TSK397-B080`:
- Repro: Send and complete native signing for a draft lease, then return to the already-open staff lease detail.
- Observed: the Agreement card correctly showed `Signed` and `Download signed lease`, but the page header still showed `Pending signature` and top-level actions still offered pending-signature lifecycle controls until the main lease query caught up.
- Root cause: the signature-status query was polling and returning the fresh signed/active status, but visible header/action/detail status surfaces read only the stale `lease.status` cache.
- Fix: added `visibleLeaseStatus` helper and wired lease detail visible lifecycle surfaces and delete-state copy to the fresh signature status while invalidating the main lease cache whenever signature status and lease cache diverge.
- Regression: `web/src/lib/leases/lease-esign.test.ts`.

Verification:
- GREEN: `rtk pnpm exec node --test --experimental-strip-types src/lib/leases/lease-esign.test.ts` passed 5/5.
- GREEN: `rtk pnpm --dir web check` reported 0 errors and the existing 4 `PageHeader.svelte` unused-selector warnings.
- Browser regression: after login redirect to `/leases/7?tab=agreement`, the staff header rendered `Active`, top action rendered `Give Notice`, overview CTA rendered `View ledger`, the Agreement tab rendered `Signed` plus `Download signed lease`, and no send/resend action was present.

Status: Pass after fixing completed-signing staff-side lifecycle coherence. Continue next with the remaining lease lifecycle controls, tracked Unit Command Center lifecycle no-ops, and the broader non-banking/non-QuickBooks inventory.

## Pass 47 Lease Detail Tab Deep-Link Continuation

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-47`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- User: Harper Stone, `tsk397.pass39.202606231342@example.local`
- Portfolio: Harper Stone's Portfolio, id `3`

Acceptance criteria:
- Lease detail tab URLs should be bookmarkable and reload-safe, matching the existing Unit Command Center `?tab=` behavior.
- A protected lease detail deep link should preserve the intended tab through login redirect.
- Clicking detail tabs should replace the current URL query without history spam.
- Starting edit mode from a non-editable tab should move the user to Overview and update the URL consistently.

Bug `TSK397-B081`:
- Repro: Open `/leases/7?tab=agreement` in a fresh unauthenticated browser, log in, and inspect the selected tab.
- Observed: the redirect returned to `/leases/7?tab=agreement`, but the page selected Overview because lease detail initialized `activeTab` to a hard-coded `overview` and did not read or write `?tab=`.
- Fix: added a shared `resolveLeaseDetailTab` helper and wired lease detail tab state to initialize from `page.url.searchParams`, react to URL changes, replace the query on tab clicks, and route edit-mode transitions through the same setter.
- Regression: `web/src/lib/leases/lease-detail-state.test.ts`.

Verification:
- GREEN: `rtk pnpm exec node --test --experimental-strip-types src/lib/leases/lease-detail-state.test.ts` passed 6/6.
- GREEN: `rtk pnpm --dir web check` reported 0 errors and the existing 4 `PageHeader.svelte` unused-selector warnings.
- Browser regression: fresh browser loaded `/leases/7?tab=agreement`, redirected to login, logged in as Harper Stone, and landed with `Agreement & Signing` selected. Clicking Ledger changed the URL to `?tab=ledger`; clicking Edit moved to `?tab=overview`, selected Overview, and showed edit controls. Cancel returned to read-only state without saving.

Status: Pass after fixing lease detail tab deep links. Continue next with remaining lease lifecycle controls, tracked Unit Command Center lifecycle no-ops, and the broader non-banking/non-QuickBooks inventory.

## Pass 48 Lease Give Notice Lifecycle Continuation

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-48`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- User: Harper Stone, `tsk397.pass39.202606231342@example.local`
- Portfolio: Harper Stone's Portfolio, id `3`

Acceptance criteria:
- A landlord can open Give Notice on an active signed lease, type the visible `MM/DD/YYYY` move-out date, and submit without requiring a hidden blur/keyboard workaround.
- Submitting Give Notice stores the move-out date, transitions the lease to Notice given, replaces the Give Notice action with the appropriate active-state recovery action, and keeps header/card/status fields coherent even when e-sign status was previously signed.
- DatePicker should not auto-normalize partial year-first input such as `2027-07-3` while the user may still be typing `2027-07-31`.

Bug `TSK397-B082`:
- Repro: Open `/leases/7?tab=overview`, click `Give Notice`, type `07/31/2027` into the visible `Move-out date` field, and inspect the modal action.
- Observed: the field displayed `07/31/2027`, but the `Give notice` button stayed disabled because `DatePicker` only committed typed text to the bound ISO value on blur or Enter.
- Fix: added `parseCompleteLooseDate` and wired `DatePicker` to commit complete typed dates immediately while leaving partial input uncommitted until blur/Enter.
- Regression: `web/src/lib/utils/parse-date.test.ts`.

Bug `TSK397-B083`:
- Repro: Submit Give Notice on the signed lease after entering the move-out date.
- Observed: the move-out date appeared, but the page still rendered `Active` and kept offering `Give Notice` because stale signature-status cache data overrode the newly updated lease lifecycle status.
- Fix: lifecycle mutations now seed the updated lease query and invalidate signature status; `visibleLeaseStatus` now only uses e-sign status for known signing transitions and will not let stale signature state override explicit lease lifecycle states such as Notice given or Terminated.
- Regression: `web/src/lib/leases/lease-esign.test.ts`.

Verification:
- GREEN: `rtk pnpm exec node --test --experimental-strip-types src/lib/utils/parse-date.test.ts src/lib/leases/lease-detail-state.test.ts src/lib/leases/lease-esign.test.ts` passed 18/18.
- GREEN: `rtk pnpm --dir web check` reported 0 errors and the existing 4 `PageHeader.svelte` unused-selector warnings.
- GREEN: `rtk git diff --check` reported no whitespace errors.
- Browser regression: typing `07/31/2027` into the Give Notice modal immediately enabled `Give notice`; submitting saved the move-out date. After refresh, `/leases/7?tab=overview` rendered header/status card `Notice given`, top action `Set Active`, overview CTA `Set lease active`, and Move-Out `Jul 31, 2027`.

Status: Pass after fixing Give Notice typed-date gating and signed-lease lifecycle state coherence. Continue next with remaining lease lifecycle controls, tracked Unit Command Center lifecycle no-ops, and the broader non-banking/non-QuickBooks inventory.

## Pass 49 Lease Notice Cancellation Continuation

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-49`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- User: Harper Stone, `tsk397.pass39.202606231342@example.local`
- Portfolio: Harper Stone's Portfolio, id `3`

Acceptance criteria:
- A landlord can cancel an accidental Notice given state by using `Set Active`.
- The lease returns to Active, the primary action returns to `Give Notice`, and the overview CTA returns to `View ledger`.
- Cancelling notice clears the expected move-out date; an active lease must not continue to display stale move-out workflow data.
- The audit trail should record that the move-out date changed to none.

Bug `TSK397-B084`:
- Repro: On `/leases/7?tab=overview`, start from a Notice given lease with Move-Out `Jul 31, 2027`, click `Set Active`, confirm `Set active`, and inspect the Term card.
- Observed: status changed to `Active` and the top action returned to `Give Notice`, but the Term card still showed `Move-Out Jul 31, 2027`.
- Root cause: `UpdateLeaseRequest.MoveOutDate` is nullable and omitted nullable dates are preserved by generic PATCH behavior. The lease state machine comment already treats `NoticeGiven -> Active` as notice cancellation, but `LeaseService.UpdateAsync` did not clear `MoveOutDate` for that transition.
- Fix: `LeaseService.UpdateAsync` now clears `MoveOutDate` when transitioning from `NoticeGiven` to `Active` and records a move-out date diff in the audit change reason.
- Regression: `RentalCommand.Api.Tests/Domain/LeaseServiceAuditTests.cs`.

Verification:
- RED: `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter FullyQualifiedName~LeaseServiceAuditTests.UpdateAsync_ClearsMoveOutDateWhenNoticeIsCancelled --no-restore --logger "console;verbosity=normal"` failed before the fix because `result.MoveOutDate` still had `2026-07-31`.
- GREEN: the same focused test passed after clearing `MoveOutDate` on notice cancellation and checking the DB row plus audit reason `move-out date 2026-07-31→none`.
- GREEN: `rtk env MSBUILDDISABLENODEREUSE=1 dotnet build RentalCommand.Api` passed with 0 warnings and 0 errors before browser retest.
- Browser regression: after rebuilding and restarting the local API with the same pass-26 database, Playwright CLI opened `/leases/7?tab=overview`, logged in as Harper Stone, clicked `Give Notice`, confirmed the prefilled `07/31/2027` date, then clicked `Set Active` and confirmed. The final snapshot rendered header/status card `Active`, top action `Give Notice`, overview CTA `View ledger`, and the Term card no longer contained a `Move-Out` row or `Jul 31, 2027` value.

Status: Pass after fixing Notice given cancellation cleanup. Continue next with remaining lease lifecycle controls, tracked Unit Command Center lifecycle no-ops, and the broader non-banking/non-QuickBooks inventory.

## Pass 50 Unit Command Center Lifecycle Action Continuation

Date: 2026-06-23
Branch: `tsk-397-401-404-full-ui-pass-50`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- User: Harper Stone, `tsk397.pass39.202606231342@example.local`
- Portfolio: Harper Stone's Portfolio, id `3`

Acceptance criteria:
- A landlord can use the Unit Command Center Ready-stage `List this unit` next action to leave the unit page and generate an application link scoped to that exact property/unit.
- The generated public application URL carries property/unit context, and the public application form preselects the matching home while rejecting invalid or mismatched query parameters.
- A landlord can use the Renewal-stage `Send renewal` next action to open the tenant notice workflow with a renewal-offer draft, without sending the notice automatically.

Bug `TSK-401`:
- Repro: Open a Ready-stage unit and click the Unit Command Center `List this unit` next action.
- Observed: the action linked back to the same unit Overview tab, so the user could not list the unit or produce an application link from the lifecycle rail.
- Root cause: `UnitDashboardService.NextBestActionHref` treated Ready-stage lifecycle guidance as a local tab hint instead of routing to the existing application-link workflow.
- Fix: Ready-stage next actions now route to `/applications?action=list-unit&propertyId=...&unitId=...`; the applications page auto-creates a public application link for that context, and the public `/apply/[token]` page preselects the scoped property/unit.
- Regression: `UnitDashboardServiceTests.GetDashboardAsync_LinksReadyNextActionToUnitApplicationLinkFlow` and `web/src/lib/applications/application-link.test.ts`.

Bug `TSK-404`:
- Repro: Open a Renewal-stage unit and click `Send renewal`.
- Observed: the action linked to the unit Lease tab instead of starting a renewal workflow.
- Root cause: the renewal next action did not carry the active tenant id and had no route contract for opening a specific notice draft.
- Fix: Renewal-stage next actions now route to `/tenants/{tenantId}?action=create-notice&noticeType=RenewalOffer`; tenant detail reads that URL action once loaded, opens the notice dialog, and forces a renewal-offer draft.
- Regression: `UnitDashboardServiceTests.GetDashboardAsync_LinksRenewalNextActionToTenantRenewalNoticeFlow` and `web/src/lib/tenants/tenant-notice-action.test.ts`.

Verification:
- RED: `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter FullyQualifiedName~UnitDashboardServiceTests.GetDashboardAsync_Links --no-restore --logger "console;verbosity=normal"` failed before the fix because Ready linked to `/units/1?tab=overview` and Renewal linked to `/units/1?tab=lease`.
- RED: `rtk pnpm --dir web test:unit -- src/lib/applications/application-link.test.ts src/lib/tenants/tenant-notice-action.test.ts` failed before the frontend helpers were implemented.
- GREEN: the focused API dashboard-link tests passed 2/2 after routing Ready and Renewal actions to real workflows.
- GREEN: `rtk node --test --experimental-strip-types web/src/lib/applications/application-link.test.ts web/src/lib/tenants/tenant-notice-action.test.ts` passed 9/9.
- GREEN: `rtk pnpm --dir web check` reported 0 errors and the existing 4 `PageHeader.svelte` unused-selector warnings.
- Browser regression for `TSK-401`: Playwright CLI opened `https://localhost:6042/units/8?tab=overview`, confirmed the `List this unit` link target `/applications?action=list-unit&propertyId=6&unitId=8`, clicked it, and saw the `Application link for this unit` dialog. The generated URL was `https://localhost:6042/apply/C76eIUiD7LzzR0qnkRLb7QqM069ObRAPMtTPlqvQOTk?propertyId=6&unitId=8`; opening it rendered public application selects prefilled to `Riverside Courtyard` and `Unit TSK401-Ready`.
- Browser regression for `TSK-404`: Playwright CLI opened `https://localhost:6042/units/6?tab=overview`, confirmed the `Send renewal -- lease ends in 43 days` link target `/tenants/7?action=create-notice&noticeType=RenewalOffer`, clicked it, and saw the `Create / Send notice` dialog with a `Renewal offer` draft for `Riverside Courtyard Unit 2A`. The notice was not sent.

Status: Pass after fixing both Unit Command Center lifecycle no-op actions. Continue the full real-user UI inventory from merged main.

## Pass 51 Fresh-User Camera Scan Storage Continuation

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-51`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- Engine: local worker process with `Assistant__Provider=claude-cli`, `Assistant__ModelId=sonnet`, empty SendGrid/SMTP secrets, and shared `Upload__BasePath=/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26/uploads`
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- User: Jordan Vale, `tsk397.pass51.202606231856@example.local`
- Portfolio: Jordan Vale's Portfolio, id `4`

Acceptance criteria:
- A fresh landlord can start from live onboarding, choose `Scan a lease`, upload a camera-style lease image, and reach the editable five-step review wizard.
- The API and Engine use the same local upload storage path, so the Engine can read files created by the web app without copying files between project directories.
- Confirming the reviewed scan creates the property, unit, tenant, lease, and source image document, and the scanned image opens from the lease detail page.

Bug `TSK-406`:
- Repro: From `/scan/new-rental`, upload `output/qa/production-scale-scans/01-leases-camera/lease-001-1a.jpg` while the API uses its project-relative default `./uploads` and the Engine uses its own project-relative default `./uploads`.
- Observed: scan draft `11` failed with `FileNotFoundException`; the uploaded image existed under `RentalCommand.Api/uploads`, while the Engine looked under `RentalCommand.Engine/uploads`.
- Root cause: `DiskFileStorage` resolved relative upload paths against each process content root. API and Engine content roots differ, so matching `./uploads` values still pointed at different physical directories.
- Fix: default local API and Engine upload paths now resolve to the repo-level `uploads` directory, and local `docker-compose.yml` mounts one named `uploads` volume at `/var/lib/rentalcommand/uploads` for both API and Engine.
- Regression: `RentalCommand.Engine.Tests/Configuration/UploadPathConfigurationTests.cs`.

Verification:
- RED: `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Engine.Tests/RentalCommand.Engine.Tests.csproj --filter "FullyQualifiedName~UploadPathConfigurationTests" --logger "console;verbosity=normal"` failed before the fix because local config resolved API uploads under `RentalCommand.Api/uploads` and local Docker Compose had no shared upload volume/env entries.
- GREEN: the same focused test passed after the config and Docker Compose fix.
- Browser retry: Playwright CLI uploaded `output/qa/production-scale-scans/01-leases-camera/lease-002-2b.jpg` on `/scan/new-rental`; draft `12` stored `224fd603da074a189eafaa517a48811b_scan-20260623230836` and thumbnail `aef406f647f44c1ca674250d3010ff7e_scan-thumb-20260623230836` under the shared `uploads/` directory, with no new files in stale `RentalCommand.Api/uploads`.
- Engine proof: worker log reported `claude-cli extraction: VISION read (image/jpeg, no extractable text)` and `Scan extraction succeeded for draft 12 (model claude-cli:sonnet): 14 field(s) with a value -> Lease, Reviewing`.
- Wizard proof: the scan reached `Step 1 of 5 - Property`, prefilled `Riverside Flats`, `1188 Maple Ave`, `Columbus`, `OH`, `43201`; Unit step prefilled `2B` and `$1200.00`, and the landlord filled missing beds/baths as `2` / `1`; Tenant step prefilled `Blake Hayes`, and the landlord filled synthetic email/phone/emergency contact; Lease step prefilled `QA-2026-002-2B`, `02/01/2026` to `02/01/2027`, rent/deposit `$1200.00`, due day `1`, and status `Active`.
- Confirm proof: `Confirm & create` landed on `/leases/8`. DB proof showed draft `12` as `Confirmed`, lease `8` `QA-2026-002-2B`, property `7` `Riverside Flats`, unit `9` `2B`, tenant `10` `Blake Hayes`, and `StoredFiles` row `39` as `image/jpeg` linked to `Lease` `8`.
- Image proof: the lease Overview rendered the `Scanned document` card, `View scanned document full size` opened `/lease-file/8` in a protected browser tab titled `8 (1800x2400)`, and screenshot proof was saved to `output/playwright/pass51-lease-file-8-image.png`.

Status: Pass after fixing the local API/Engine scan upload storage mismatch and proving a fresh-user camera-image lease scan through confirmation. Continue Pass 51 with appointments, messages, notices, and portal workflows from the scan-created rental spine.

## Pass 52 Appointment Date Edit Continuation

Date: 2026-06-23
Branch: `tsk-397-full-ui-pass-52`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- User: Jordan Vale, `tsk397.pass51.202606231856@example.local`
- Portfolio: Jordan Vale's Portfolio, id `4`

Acceptance criteria:
- A landlord can create an appointment from an empty appointments state with title, type, property, tenant, start time, and end time.
- Empty required fields keep the modal open and show visible validation errors.
- Reopening an appointment from the calendar and typing a new date plus time persists the new local wall-clock date and time together.
- The list and calendar render the updated date/time, and the stored UTC values match the local wall-clock edit.

Bug `TSK-407`:
- Repro: Open `/appointments`, create `Pass 51 service visit`, reopen it from the calendar, type start `06/26/2026 11:15` and end `06/26/2026 12:00`, then save.
- Observed: the row updated the start time but kept the old start date, rendering `6/25/2026, 11:15:00 AM`; the DB row had `ScheduledStart = 2026-06-25 15:15:00+00` while `ScheduledEnd = 2026-06-26 16:00:00+00`.
- Root cause: the shared `DateTimePicker` recombined date/time changes through local component state after child picker events. A typed date change could be lost before a following time edit, so the following time edit recombined with the previous date.
- Fix: extracted tested date-time part helpers and wired `DateTimePicker` to commit explicit next date/time parts from each event, preserving a typed replacement date across subsequent time edits.
- Regression: `web/src/lib/components/shared/date-time-picker-state.test.ts`.

Verification:
- RED: `rtk pnpm --dir web test:unit -- src/lib/components/shared/date-time-picker-state.test.ts` failed before the helper existed with `ERR_MODULE_NOT_FOUND`.
- GREEN: the same command passed after adding the helper and DateTimePicker wiring, with 196/196 frontend unit tests passing.
- GREEN: `rtk pnpm --dir web check` reported 0 errors and the existing 4 `PageHeader.svelte` unused-selector warnings.
- Browser proof: Playwright CLI logged in as Jordan Vale, opened `/appointments`, reopened the existing mixed-date appointment, typed start `06/27/2026 09:30`, end `06/27/2026 10:15`, saved, and the calendar/list rendered `9:30 AM Pass 51 service visit date fixed` plus `6/27/2026, 9:30:00 AM`.
- DB proof: `Appointments` row `1` stored `ScheduledStart = 2026-06-27 13:30:00+00` and `ScheduledEnd = 2026-06-27 14:15:00+00`, matching the EDT local wall-clock edit.
- Screenshot proof: `output/playwright/pass52-appointment-date-edit-fixed.png`.

Status: Pass after fixing typed date/time edits in the shared appointment DateTimePicker. Continue next with appointment list/detail workflows, messages, notices, and portal workflows from the scan-created rental spine.

## Pass 53 Appointment Detail Timezone Continuation

Date: 2026-06-24
Branch: `tsk-397-408-appointment-detail-timezone`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- User: Jordan Vale, `tsk397.pass51.202606231856@example.local`
- Portfolio: Jordan Vale's Portfolio, id `4`

Acceptance criteria:
- Opening appointment detail edit mode seeds native `datetime-local` fields from the local wall-clock time shown in read-only mode.
- Saving an unchanged appointment detail edit keeps the same UTC instants in the database.
- End-before-start edits remain rejected with visible user feedback and no stored row corruption.

Bug `TSK-408`:
- Repro: Open `/appointments/1`, confirm the read-only header shows `6/27/2026, 9:30:00 AM - 6/27/2026, 10:15:00 AM`, then click `Edit`.
- Observed before fix: edit mode displayed raw UTC values `2026-06-27T13:30` and `2026-06-27T14:15` in the native datetime fields. A no-op save would reinterpret those as local wall-clock values and shift the appointment four hours later.
- Root cause: appointment detail `startEditing()` used `appt.scheduledStart?.slice(0, 16)` / `appt.scheduledEnd?.slice(0, 16)` while the calendar modal already used the correct UTC-to-local and local-to-UTC conversion helpers.
- Fix: added tested appointment detail form helpers that convert UTC ISO values to local wall-clock edit values and convert local wall-clock edit values back to UTC before calling the update API. The detail page now uses those helpers instead of raw string slicing.
- Regression: `web/src/routes/(protected)/appointments/appointment-detail-form.test.ts`.

Verification:
- RED: `rtk pnpm --dir web test:unit -- 'src/routes/(protected)/appointments/appointment-detail-form.test.ts'` failed before implementation with `ERR_MODULE_NOT_FOUND` for the new detail form helper.
- GREEN: the same command passed after adding the helper and route wiring, with 200/200 frontend unit tests passing.
- GREEN: `rtk pnpm --dir web check` reported 0 errors and the existing 4 `PageHeader.svelte` unused-selector warnings.
- Browser proof: Playwright CLI logged in as Jordan Vale, opened `/appointments/1`, clicked `Edit`, and the Start/End inputs showed `2026-06-27T09:30` and `2026-06-27T10:15` instead of UTC `13:30`/`14:15`.
- No-op save proof: clicking `Save` unchanged showed `Appointment updated.` and returned to read-only detail with `6/27/2026, 9:30:00 AM - 6/27/2026, 10:15:00 AM`.
- DB proof after no-op save: `Appointments` row `1` still stored `ScheduledStart = 2026-06-27 13:30:00+00` and `ScheduledEnd = 2026-06-27 14:15:00+00`; only `UpdatedAt` changed.
- Unhappy-path proof: editing End to `2026-06-27T09:00` and saving showed the toast `The appointment end time must be after its start time.`; the resulting 400 network console entry was expected for the rejected API request, and the DB row remained at `13:30:00+00` / `14:15:00+00`.
- Screenshot proof: `output/playwright/pass53-appointment-detail-timezone-fixed.png`.

Status: Pass after fixing appointment detail datetime-local timezone handling. Continue next with the remaining appointment detail actions, messages, notices, and portal workflows from the scan-created rental spine.

## Pass 54 Appointment Detail Actions and Badge Refresh

Date: 2026-06-24
Branch: `tsk-397-full-ui-pass-54`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- User: Jordan Vale, `tsk397.pass51.202606231856@example.local`
- Portfolio: Jordan Vale's Portfolio, id `4`

Acceptance criteria:
- Appointment detail delete and document delete confirmations must be cancellable and must not mutate rows/files when cancelled.
- Appointment detail document upload must accept a camera-style image, refresh the document list, link the stored file to the appointment, and download the same image through the app.
- Scheduled appointment detail status actions must transition to Cancelled and No show with visible feedback and DB state changes.
- Terminal No show appointment detail must not leak raw enum text or expose active transition actions.
- Appointment create/status mutations must refresh the app-shell upcoming appointment badge without requiring a full reload.

Bug `TSK-409`:
- Repro: Create a scheduled appointment from `/appointments`, open it through `/appointments/{id}`, and click `No-show`.
- Observed before fix: the header status chip rendered `No Show`, but the What & when status field rendered raw enum `NoShow`; the no-show detail still exposed a `Cancel` transition action; and the app-shell badge could stay one mutation behind, for example `2 upcoming` after no-show left only one scheduled future appointment.
- Root cause: appointment detail displayed `appt.status` directly and only treated Completed/Cancelled as terminal for the Cancel action. The app-shell upcoming count used its own `['header-upcoming-appointments', portfolioId]` query key, while appointment list/detail mutations invalidated only `['appointments', portfolioId]` and optional detail keys.
- Fix: added appointment detail status helpers for human labels and active transition eligibility, reused those helpers in the detail route and status select, normalized No show badge casing, and introduced a shared appointment invalidation helper that refreshes both appointment lists and the app-shell upcoming badge query.
- Regression: `web/src/routes/(protected)/appointments/appointment-detail-state.test.ts` and `web/src/routes/(protected)/appointments/appointment-query-keys.test.ts`.

Verification:
- RED: `rtk pnpm --dir web test:unit -- 'src/routes/(protected)/appointments/appointment-detail-state.test.ts' 'src/routes/(protected)/appointments/appointment-query-keys.test.ts'` failed before implementation because the helper modules did not exist.
- GREEN: focused frontend unit run passed with 205/205 tests after adding the helpers and route wiring.
- GREEN: `rtk pnpm --dir web check` reported 0 errors and the existing 4 `PageHeader.svelte` unused-selector warnings.
- Delete confirmation proof: `/appointments/2` opened `Delete appointment` with `Delete "TSK-397 Pass 54 disposable status"?`; Cancel closed the dialog and DB row `Appointments.Id = 2` remained with status `2`.
- Document proof: uploading `output/qa/production-scale-scans/03-payments-camera/payment-040.jpg` on `/appointments/2` showed toast `"payment-040.jpg" uploaded.`, rendered a `payment-040.jpg` document row with a named delete control, downloaded a valid JPEG from the app, and DB `StoredFiles.Id = 41` linked `EntityType = Appointment`, `EntityId = 2`, `ContentType = image/jpeg`, `FileSize = 116822`.
- Document delete cancel proof: `Delete "payment-040.jpg"? This cannot be undone.` opened from the appointment document row; Cancel preserved DB `StoredFiles.Id = 41` with `DeletedAt` null.
- Cancel status proof: `/appointments/3` clicked `Cancel`, showed `Appointment updated.`, rendered status `Cancelled`, removed active transition actions, and DB row `3` stored status `3`.
- No-show proof before fix: `/appointments/4` clicked `No-show`, showed `Appointment updated.`, but rendered raw `NoShow`, retained `Cancel`, and badge stayed stale; screenshot attached to `TSK-409`.
- No-show proof after fix: reloading `/appointments/4` rendered header and status field `No show`, exposed only `Edit` and `Delete appointment`, and showed `Appointments (1 upcoming)`.
- Badge invalidation proof after fix: creating `/appointments/5` as `TSK-397 Pass 54 badge refresh retest` immediately moved the app-shell badge from `1 upcoming` to `2 upcoming`; clicking `No-show` on `/appointments/5` immediately moved it back to `1 upcoming` without reload, and DB row `5` stored status `4`.

Status: Pass after fixing no-show detail presentation, terminal status action eligibility, and upcoming appointment badge invalidation. Continue next with messages, notices, portal workflows, and the remaining non-banking/non-QuickBooks inventory.

## Pass 55 Tenant Portal User Invite Continuation

Date: 2026-06-24
Branch: `tsk-397-410-portal-user-invite-pass-55`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- Staff user: Jordan Vale, `tsk397.pass51.202606231856@example.local`, portfolio id `4`
- Tenant record from scan-created rental spine: Blake Hayes, tenant id `10`, active lease `QA-2026-002-2B`
- Tenant portal user created during this pass: `blake.hayes.portal.pass55@example.local`

Acceptance criteria:
- A real landlord can create a Tenant-role login from `/admin/users` without calling a seed script or editing the database.
- Choosing role `Tenant` requires selecting an in-portfolio tenant before submit.
- The create request includes `tenantId` only for Tenant-role users, and clears stale tenant linkage when switching back to staff roles.
- The created tenant identity receives the JWT tenant claim and can sign into `/portal` with portal-only navigation and tenant-scoped dashboard data.

Bug `TSK-410`:
- Repro: Open `/admin/users`, click `Invite member`, choose role `Tenant`, fill email/display name/password, and submit.
- Observed before fix: the UI exposed `Tenant` in the role menu but never collected `tenantId`; the backend correctly rejected Tenant-role creation unless `TenantId` was supplied.
- Root cause: `AdminUsersController.Create` and `CreateTeamMemberRequest` already had the tenant-link contract, but the Team dialog still treated every role as staff-like and posted only email, display name, role, and optional password.
- Fix: extracted tested invite-form helpers, added a Tenant selector backed by the existing paged tenant endpoint, disabled submit until a tenant is selected for Tenant-role invites, and sends `tenantId` only when the role is `Tenant`.
- Regression: `web/src/routes/(admin)/admin/users/invite-member-form.test.ts`.

Verification:
- RED: `rtk pnpm --dir web test:unit -- 'src/routes/(admin)/admin/users/invite-member-form.test.ts'` failed before implementation with `ERR_MODULE_NOT_FOUND` for the new form helper.
- GREEN: the same focused frontend command passed after implementation, with the full current web unit suite passing 208/208 through the project test script.
- GREEN: `rtk pnpm --dir web check` reported 0 errors and the existing 4 `PageHeader.svelte` unused-selector warnings.
- Browser proof on `/admin/users`: Playwright CLI opened the Invite member dialog, selected role `Tenant`, saw the required tenant selector, confirmed `Add member` stayed disabled before tenant selection, selected `Blake Hayes`, filled `blake.hayes.portal.pass55@example.local`, submitted, and saw the new Team row `Blake Hayes Portal ... Tenant Active 6/24/2026`.
- DB proof: `AspNetUsers.Id = 5` for `blake.hayes.portal.pass55@example.local` has `PortfolioId = 4`, `TenantId = 10`, display name `Blake Hayes Portal`; matching `UserAccounts` row is active with role `Tenant`.
- Tenant portal proof: Playwright CLI signed in as `blake.hayes.portal.pass55@example.local`, reached `/portal`, and rendered portal-only navigation for Dashboard, Messages, Notifications, Maintenance, Payments, Lease, and Appointments with the user chip `BH Blake Hayes Portal`.
- Portal dashboard proof: the tenant saw only Blake Hayes data, including lease `QA-2026-002-2B`, rent `$1,200.00`, end date `Feb 1, 2027`, no outstanding payments, and no open requests.

Status: Pass for tenant portal user creation. Continue the real-user portal pass from `/portal/messages`, then tenant maintenance with camera-image attachment, payments/autopay unavailable states, lease view, notifications, and appointments.

## Pass 56 Portal Message Live Unread Continuation

Date: 2026-06-24
Branch: `tsk-397-411-portal-unread-pass-56`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- Staff user: Jordan Vale, `tsk397.pass51.202606231856@example.local`, portfolio id `4`
- Tenant portal user: Blake Hayes Portal, `blake.hayes.portal.pass55@example.local`, tenant id `10`

Acceptance criteria:
- A tenant can start a portal message thread and a staff user can reply through portal-only delivery.
- If the tenant has the thread open when staff replies, the new message appears live without reload.
- The open-thread read action must clear tenant-side conversation-list unread badges for the new message version, not only the first time the thread id is opened.
- Staff-side conversation unread badges and the app-shell Messages unread header must refresh when conversation events or read effects change server unread counts.

Bug `TSK-411`:
- Repro: As Blake Hayes Portal, open `/portal/messages`, start `TSK-397 Pass 56 portal question`, then leave the thread open. As Jordan Vale, open `/messages`, reply portal-only. Return to the still-open tenant session without reloading.
- Observed before fix: the tenant detail pane received the staff reply live, and the database had `TenantUnreadCount = 0`, but the portal conversation-list row still showed unread badge `1` until a reload. Staff header unread state could also remain stale because conversation events did not invalidate `['header-unread-messages']`.
- Root cause: both staff and portal message pages tracked only the selected conversation id for "marked read" cleanup. A live reply in the same selected thread refetched the detail and marked the thread read server-side, but the effect did not rerun because the id had not changed.
- Fix: added shared conversation read-state helpers that key read cleanup by `conversation id + message count + last message time`, reused them in staff and portal message pages, patched the portal conversation-list cache immediately after a detail read, and invalidated the staff header unread query on conversation events and read effects.
- Regression: `web/src/lib/messages/conversation-read-state.test.ts` and `web/src/lib/realtime/invalidate.test.ts`.

Verification:
- RED: `rtk pnpm --dir web exec node --test --experimental-strip-types src/lib/messages/conversation-read-state.test.ts` failed before implementation with `ERR_MODULE_NOT_FOUND` for the new helper.
- GREEN: `rtk pnpm --dir web exec node --test --experimental-strip-types src/lib/messages/conversation-read-state.test.ts src/lib/realtime/invalidate.test.ts` passed 5/5 focused tests.
- GREEN: `rtk pnpm --dir web check` reported 0 errors and the existing 4 `PageHeader.svelte` unused-selector warnings.
- GREEN: `rtk pnpm --dir web test:unit` passed 211/211 frontend unit tests.
- Browser proof: Blake's portal session opened the thread, Jordan's staff session sent `Second portal reply for TSK-411: this should not leave Blake's open thread marked unread.` through portal-only delivery, and Blake's still-open portal thread rendered the new Management bubble without reload.
- DOM proof: Playwright CLI evaluated `document.querySelectorAll('[data-testid=portal-conversation-unread-badge]').length` in the tenant session and `document.querySelectorAll('[data-testid=conversation-unread-badge]').length` in the staff session; both returned `0`.
- DB proof: the conversation row for `TSK-397 Pass 56 portal question` ended with `LandlordUnreadCount = 0`, `TenantUnreadCount = 0`, `LastMessagePreview = Second portal reply for TSK-411: this should not leave Blake's open thread marked unread.`, and 3 messages.

Status: Pass after fixing live open-thread unread coherence for portal and staff messages. Continue the real-user portal pass with tenant maintenance and camera-image attachment, payments/autopay unavailable states, lease view, notifications, and appointments.

## Pass 57 Tenant Maintenance Photo Detail Continuation

Date: 2026-06-24
Branch: `tsk-397-412-portal-maintenance-photos-pass-57`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- Staff user: Jordan Vale, `tsk397.pass51.202606231856@example.local`, portfolio id `4`
- Tenant portal user: Blake Hayes Portal, `blake.hayes.portal.pass55@example.local`, tenant id `10`

Acceptance criteria:
- A tenant can create a maintenance request from `/portal/maintenance` with a camera-style image.
- Staff can see and download the same image on the staff maintenance detail page.
- The tenant can reopen the maintenance request detail and see the attached photo/document without switching to the staff app.
- Opening the document from the tenant detail downloads the same image through the authenticated document flow.

Bug `TSK-412`:
- Repro: As Blake Hayes Portal, open `/portal/maintenance`, attach `output/qa/production-scale-scans/02-expenses-camera/expense-001.jpg`, submit `TSK-397 Pass 57 bathroom ceiling leak`, then open the created tenant request detail.
- Observed before fix: staff `/maintenance/5` showed and downloaded the attached `expense-001.jpg`, but tenant portal detail showed only the description and progress timeline. The tenant could not inspect the photo they had just attached.
- Root cause: the portal maintenance detail omitted the shared work-order document panel even though the tenant-owned WorkOrder document authorization path already supports listing and downloading those files.
- Fix: portal maintenance detail now renders `DocumentsPanel` with `entityType="WorkOrder"` and `entityId={detail.id}` under a `Photos & documents` heading.
- Regression: `web/src/lib/portal/maintenance-detail-documents.test.ts`.

Verification:
- Tenant create proof: Playwright CLI submitted `TSK-397 Pass 57 bathroom ceiling leak` with priority `High`, description `Water stain appeared above the shower after the last rain. Photo attached from the bathroom ceiling.`, and camera-style JPEG `output/qa/production-scale-scans/02-expenses-camera/expense-001.jpg`.
- Staff proof before portal fix: `/maintenance/5` showed the work order at `Riverside Flats`, `Unit 2B`, priority `High`, status `New`, and `Photos & documents` with `expense-001.jpg`; clicking the file downloaded a valid 1800x2400 JPEG.
- Tenant proof after fix: `/portal/maintenance` opened the `TSK-397 Pass 57 bathroom ceiling leak` detail and rendered `Photos & documents`, `expense-001.jpg`, `140.4 KB`, `Upload`, and a document-specific delete button. Snapshot: `.playwright-cli/page-2026-06-24T02-16-14-171Z.yml`.
- Tenant download proof after fix: clicking `expense-001.jpg` from the tenant detail downloaded `.playwright-cli/expense-001.jpg`; `file` reported `JPEG image data ... 1800x2400`, size `143793`.
- DB proof: `WorkOrders.Id = 5` for `TSK-397 Pass 57 bathroom ceiling leak` has `Priority = 2`, `Status = 0`, and `StoredFiles.Id = 42`, `FileName = expense-001.jpg`, `ContentType = image/jpeg`, `FileSize = 143793`, `DeletedAt = null`.
- GREEN: `rtk pnpm --dir web exec node --test --experimental-strip-types src/lib/portal/maintenance-detail-documents.test.ts` passed 1/1.
- GREEN: `rtk pnpm --dir web check` reported 0 errors and the existing 4 `PageHeader.svelte` unused-selector warnings.
- GREEN: `rtk pnpm --dir web test:unit` passed 212/212 frontend unit tests.

Status: Pass after exposing tenant-owned work-order photos/documents in portal maintenance detail. Continue the real-user portal pass with payments/autopay unavailable states, lease view, notifications, appointments, and remaining non-banking/non-QuickBooks workflows.

## Pass 58 Tenant Portal Lease Label Continuation

Date: 2026-06-24
Branch: `tsk-397-413-portal-lease-labels-pass-58`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- Staff user: Jordan Vale, `tsk397.pass51.202606231856@example.local`, portfolio id `4`
- Tenant portal user: Blake Hayes Portal, `blake.hayes.portal.pass55@example.local`, tenant id `10`

Acceptance criteria:
- A tenant can open `/portal/lease` and identify which property/unit the displayed lease belongs to.
- Lease card labels must be returned from the server for the authenticated tenant's lease, not inferred from stale client state.
- The portal lease query must remain DB-side and scoped to the tenant/portfolio in a single SQL projection.
- The lease Q&A card still answers common questions after the lease-card data shape changes.

Bug `TSK-413`:
- Repro: As Blake Hayes Portal, open `/portal/lease`.
- Observed before fix: the lease card showed `QA-2026-002-2B`, `Linked property`, and `Rent $1,200.00 · Ends Feb 1, 2027`, even though the lease belongs to Riverside Flats / Unit 2B.
- Root cause: `PortalService.GetLeasesAsync` materialized bare `Lease` entities without loading or projecting related property/unit/tenant labels, then mapped through `LeaseResponse.FromEntity`.
- Fix: `GetLeasesAsync` now projects `LeaseResponse` directly from the EF query, including tenant, unit, and property labels.
- Regression: `RentalCommand.Api.Tests/Domain/PortalServiceLeaseTests.cs`.

Verification:
- RED: the focused test failed before implementation because `LeaseResponse.PropertyName` was `null`.
- GREEN: `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~PortalServiceLeaseTests.GetLeasesAsync_ReturnsTenantLeaseLabelsFromSingleSqlProjection" --logger "console;verbosity=normal"` passed 1/1 after the fix.
- Browser proof: Playwright CLI logged in as Blake Hayes Portal, opened `https://localhost:6042/portal/lease`, and the lease card rendered `QA-2026-002-2B`, `Riverside Flats Unit 2B`, and `Rent $1,200.00 · Ends Feb 1, 2027`. Snapshot: `.playwright-cli/page-2026-06-24T02-49-08-131Z.yml`.
- Q&A proof: clicking `When is rent due?` returned `Rent is due on the **1st of each month**.` and the network log showed `POST /api/v1/portal/lease/ask?leaseId=8 => 200`. Snapshot: `.playwright-cli/page-2026-06-24T02-49-25-271Z.yml`.

Status: Pass after showing tenant portal lease property/unit labels from the server-side projection. Continue the real-user portal pass with notifications and appointments; `/portal/appointments` currently needs scrutiny because the tenant has a linked appointment but the route appears placeholder-only.

## Pass 59 Tenant Portal Appointments Continuation

Date: 2026-06-24
Branch: `tsk-397-414-portal-appointments-pass-59`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- Staff user: Jordan Vale, `tsk397.pass51.202606231856@example.local`, portfolio id `4`
- Tenant portal user: Blake Hayes Portal, `blake.hayes.portal.pass55@example.local`, tenant id `10`
- Local-only credential note: the tenant password was not recorded in the prior evidence, so the synthetic local tenant password was reset through `/auth/forgot-password` + `/auth/reset-password` with `Auth__ExposeDevTokens=true` on a Development-only API process. The API was then restarted without `Auth__ExposeDevTokens` before browser proof.

Acceptance criteria:
- A tenant can open `/portal/appointments` and see their own upcoming appointment rows instead of placeholder-only copy.
- The portal appointments endpoint must be scoped to the JWT tenant id and portfolio id, not request parameters.
- Cancelled, past, and other-tenant appointments must be excluded from the tenant result.
- The query must remain DB-side with filtering, ordering, and a finite limit before projection.

Bug `TSK-414`:
- Repro: As Blake Hayes Portal, open `/portal/appointments`.
- Observed before fix: the route rendered only `Upcoming visits, inspections, and maintenance appointments will appear here.` even though `Appointments.Id = 1` exists for tenant `10`.
- Root cause: the portal appointments route was static placeholder UI and the portal API/client had no tenant appointments endpoint.
- Fix: added `GET /api/v1/portal/appointments`, `IPortalService.GetAppointmentsAsync`, a tenant/portfolio-scoped EF projection limited to 50 future scheduled/confirmed appointments, a `portal.appointments()` client helper, and a real portal appointments page with loading/error/empty/list states.
- Regression: `RentalCommand.Api.Tests/Domain/PortalServiceAppointmentTests.cs` and `web/src/lib/portal/appointments-page.test.ts`.

Verification:
- RED: focused backend test failed before implementation because `PortalService` had no `GetAppointmentsAsync`.
- RED: focused frontend route test failed before implementation because the placeholder route did not call `portal.appointments()`.
- GREEN: `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~PortalServiceAppointmentTests.GetAppointmentsAsync_ReturnsUpcomingTenantAppointmentsFromSingleLimitedSqlProjection" --logger "console;verbosity=normal"` passed 1/1.
- GREEN: `rtk pnpm --dir web exec node --test --experimental-strip-types src/lib/portal/appointments-page.test.ts` passed 1/1.
- GREEN: `rtk pnpm --dir web check` reported 0 errors and the existing 4 `PageHeader.svelte` unused-selector warnings.
- Browser proof: Playwright CLI logged in as Blake Hayes Portal, opened `https://localhost:6042/portal/appointments`, and the page rendered `Pass 51 service visit date fixed`, `Sat, Jun 27, 9:30 AM - 10:15 AM`, `Riverside Flats`, `Maintenance`, and `Scheduled`. Snapshot: `.playwright-cli/page-2026-06-24T03-25-23-623Z.yml`; screenshot: `output/playwright/pass59-portal-appointments.png`.
- API/SQL proof: the local API log showed `GET /api/v1/portal/appointments => 200` and a single SQL query filtering by `a."PortfolioId" = @portfolioId`, `a."TenantId" = @tenantId`, `a."ScheduledStart" >= @now`, `a."Status" IN (0, 1)`, ordered by scheduled start/id, with `LIMIT @p`.
- DB proof: `Appointments.Id = 1`, title `Pass 51 service visit date fixed`, `TenantId = 10`, `PropertyId = 7`, `ScheduledStart = 2026-06-27 13:30:00+00`, `ScheduledEnd = 2026-06-27 14:15:00+00`, `Status = 0`.

Status: Pass after replacing the tenant portal appointments placeholder with tenant-scoped upcoming appointments. Continue the real-user portal pass with notifications and payments/autopay unavailable states, then resume remaining non-banking/non-QuickBooks workflows.

## Pass 60 Tenant Portal Notification Read-State Continuation

Date: 2026-06-24
Branch: `tsk-397-full-ui-pass-60`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- Tenant portal user: Blake Hayes Portal, `blake.hayes.portal.pass55@example.local`, tenant id `10`

Acceptance criteria:
- A tenant can open `/portal/notifications` and use a notification as the next action into the intended workflow.
- Opening an unread notification from the full notifications page marks that notification read.
- The app-shell unread count reconciles immediately, including notifications that are visible on the 50-row page but outside the header dropdown's 20-row recent list.
- The target workflow still opens through the tenant-scoped portal URL normalizer.

Bug `TSK-415`:
- Repro: As Blake Hayes Portal, open `/portal/notifications` with two unread message notifications, then click the first notification.
- Observed before fix: the app navigated to `/portal/messages?conversation=1`, but the header still rendered `2 unread notifications`.
- Root cause: the dropdown notification item used `notificationStore.markAsRead(id)` before `goto()`, but the full portal notifications page rendered plain anchor links and bypassed the read action entirely.
- Fix: the full portal notifications page now opens rows through a button handler, marks unread notifications read, refreshes the notification store, and then navigates with `goto(portalActionUrl(...), { invalidateAll: true })`.
- Regression: `web/src/lib/portal/notifications-page.test.ts`.

Verification:
- RED: `rtk pnpm --dir web exec node --test --experimental-strip-types src/lib/portal/notifications-page.test.ts` failed because the route had no `notificationStore.markAsRead(item.id)` call and still rendered `<a href={portalActionUrl(item.actionUrl)}>`.
- RED: after the first fix, the same test failed on the new older-notification edge because the route did not refresh the notification store.
- GREEN: `rtk pnpm --dir web exec node --test --experimental-strip-types src/lib/portal/notifications-page.test.ts src/lib/portal/appointments-page.test.ts src/lib/portal/maintenance-detail-documents.test.ts` passed 3/3.
- GREEN: `rtk pnpm --dir web check` reported 0 errors and the existing 4 `PageHeader.svelte` unused-selector warnings.
- Browser proof: Playwright CLI logged in as Blake Hayes Portal, opened `https://localhost:6042/portal/notifications`, and the patched page rendered notification rows as buttons with `2 unread notifications`. Snapshot: `.playwright-cli/page-2026-06-24T03-50-32-393Z.yml`.
- Browser proof: clicking the first notification navigated to `/portal/messages?conversation=1` and the header count dropped to `1 unread notification`. Snapshot: `.playwright-cli/page-2026-06-24T03-50-50-694Z.yml`.
- Browser proof: returning to `/portal/notifications` and clicking the second notification navigated back to `/portal/messages?conversation=1` and the header rendered `0 unread notifications`. Snapshot: `.playwright-cli/page-2026-06-24T03-51-30-727Z.yml`.

Status: Pass after fixing full-page tenant portal notification read-state. Continue the real-user portal pass with payments/autopay unavailable states, lease follow-up actions, and remaining non-banking/non-QuickBooks workflows.

## Pass 61 Tenant Portal Payments Autopay Availability Continuation

Date: 2026-06-24
Branch: `tsk-397-full-ui-pass-61`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- Tenant portal user: Blake Hayes Portal, `blake.hayes.portal.pass55@example.local`, tenant id `10`

Acceptance criteria:
- A tenant opening `/portal/payments` should know immediately when online payments/autopay are unavailable.
- The autopay setup action must not invite a tenant into a dead 503 path when Stripe is disabled or suppressed for sandbox.
- The API should expose online-payment availability through an existing tenant-scoped status call, without requiring a checkout/enroll POST.
- The page should preserve the gentle unavailable-provider copy for both up-front status and defensive 503 fallback.

Bug `TSK-416`:
- Repro: As Blake Hayes Portal, open `/portal/payments` on the local stack with Stripe unset.
- Observed before fix: the page showed an active `Set up autopay` button. Clicking it POSTed `/api/v1/portal/autopay/enroll`, returned 503, logged a browser resource error, and only then showed `Online payments aren't set up yet`.
- Root cause: `/api/v1/portal/autopay` returned only `leaseId`, `active`, and `enrolledAt`, so the frontend could not render provider availability until an enroll/payment mutation failed.
- Fix: `IStripePaymentService` now exposes `IsOnlinePaymentsAvailableAsync`, reusing the Stripe config and sandbox gates already used by Checkout creation. `GET /api/v1/portal/autopay` and cancel responses now include `onlinePaymentsAvailable`; the portal payments page uses that field to show the unavailable banner and an inert disabled setup button before any enroll POST.
- Regression: `RentalCommand.Api.Tests/Domain/StripeCheckoutTests.cs` and `web/src/lib/portal/payments-page.test.ts`.

Verification:
- RED: `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~StripeCheckoutTests.IsOnlinePaymentsAvailableAsync_ReflectsStripeConfiguration" --logger "console;verbosity=normal"` failed because `StripePaymentService` did not contain `IsOnlinePaymentsAvailableAsync`.
- RED: `rtk pnpm --dir web exec node --test --experimental-strip-types src/lib/portal/payments-page.test.ts` failed because `AutopayStatus` had no `onlinePaymentsAvailable` field and the page did not use it.
- GREEN: `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~AutopayPortalServiceTests|FullyQualifiedName~StripeCheckoutTests" --no-build --logger "console;verbosity=normal"` passed 12/12.
- GREEN: `rtk pnpm --dir web exec node --test --experimental-strip-types src/lib/portal/payments-page.test.ts src/lib/portal/notifications-page.test.ts src/lib/portal/appointments-page.test.ts src/lib/portal/maintenance-detail-documents.test.ts` passed 4/4.
- GREEN: `rtk pnpm --dir web check` reported 0 errors and the existing 4 `PageHeader.svelte` unused-selector warnings.
- Browser proof: after restarting the patched API, Playwright CLI logged in as Blake Hayes Portal and opened `https://localhost:6042/portal/payments`. The page rendered the upfront unavailable banner, the Autopay card rendered `Online payments aren't set up yet, so autopay is not available right now. Please keep paying rent the way you do today.`, and the `Set up autopay` button was disabled. Screenshot: `output/playwright/pass61-portal-payments-autopay-unavailable.png`.
- API proof: the patched local API log showed `GET /api/v1/portal/autopay => 200` and `GET /api/v1/portal/payments => 200` after login; no enroll POST was needed to render the unavailable state.

Status: Pass after surfacing online-payment availability up front for tenant portal autopay. Continue the real-user portal pass with payment row/pay-now data coverage, lease follow-up actions, maintenance/message variants, and remaining non-banking/non-QuickBooks workflows.

## Pass 62 Tenant Portal Pay-Now Availability and Unit Rent Refresh

Date: 2026-06-24
Branch: `tsk-397-417-418-portal-payments-pass-62`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- Staff user: Jordan Vale, `tsk397.pass51.202606231856@example.local`
- Tenant portal user: Blake Hayes Portal, `blake.hayes.portal.pass55@example.local`, tenant id `10`
- Unit/lease under test: Riverside Flats Unit 2B, lease `QA-2026-002-2B`, lease id `8`

Acceptance criteria:
- A tenant with scheduled rent charges should not be invited into a dead checkout path when online payments are unavailable.
- Payable tenant payment rows should render unavailable provider copy and an inert disabled action before any checkout POST.
- Staff posting a payment from the Unit Rent tab should see the new row immediately, without reloading the unit page or relying on a later navigation.
- The Unit Rent tab should still refresh shared payment, unit dashboard, and unit timeline caches after create/edit mutations.

Bug `TSK-417`:
- Repro: As Blake Hayes Portal, open `/portal/payments` with Stripe unset and a scheduled rent charge visible, then click `Pay now`.
- Observed before fix: the page-level Autopay card was disabled by `TSK-416`, but each payment row still rendered an active `Pay now` button. Clicking it POSTed `/api/v1/portal/payments/6/checkout`, returned 503, and logged a browser resource error.
- Root cause: the portal payment row actions still keyed only off `isPayable(payment)` and the pending payment id, not the page's online-payment availability state.
- Fix: payable row actions now disable when `onlinePaymentsUnavailable` is true, render `Pay unavailable`, and show row-level copy telling the tenant to keep paying rent the current way.
- Regression: `web/src/lib/portal/payments-page.test.ts`.

Bug `TSK-418`:
- Repro: As Jordan Vale, open `/units/9?tab=rent`, post a scheduled payment through the Unit Rent tab, and stay on the same page.
- Observed before fix: the toast said `Payment posted.` and the header/outstanding balance updated, but the tab still showed the stale empty/list state until the payment page data caught up through a reload or later navigation.
- Root cause: the create success handler cleared local `paymentItems` and invalidated the query, but did not render the `Payment` returned by `POST /api/v1/payments` while the cached list refetched.
- Fix: the create success handler now prepends the returned payment into the current Unit Rent list immediately, then invalidates the payment, unit dashboard, and unit timeline query prefixes without clearing the just-created row.
- Regression: `web/src/lib/components/unit/rent-tab-create.test.ts`.

Verification:
- RED: `rtk pnpm --dir web exec node --test --experimental-strip-types src/lib/portal/payments-page.test.ts` failed because the portal payments page had no row-level unavailable copy/action guard.
- GREEN: `rtk pnpm --dir web exec node --test --experimental-strip-types src/lib/portal/payments-page.test.ts` passed 2/2.
- RED: `rtk pnpm --dir web exec node --test --experimental-strip-types src/lib/components/unit/rent-tab-create.test.ts` failed because the Unit Rent create success handler did not accept/use the returned `Payment`.
- GREEN: `rtk pnpm --dir web exec node --test --experimental-strip-types src/lib/components/unit/rent-tab-create.test.ts` passed 2/2.
- Browser proof for `TSK-418`: Playwright CLI logged in as Jordan Vale, opened `https://localhost:6042/units/9?tab=rent`, posted a scheduled `$12.34` synthetic rent row through the real Unit Rent form, and without reloading saw `Payment posted.`, an updated outstanding balance of `$1,212.34`, and a visible `Jun 24, 2026 · Rent Scheduled $12.34` row. Snapshot: `.playwright-cli/page-2026-06-24T04-46-38-127Z.yml`; screenshot: `.playwright-cli/page-2026-06-24T04-46-48-483Z.png`.
- Browser proof for `TSK-417`: Playwright CLI logged in as Blake Hayes Portal and opened `https://localhost:6042/portal/payments`. The page rendered the unavailable banner, disabled Autopay setup, both scheduled rent rows showed disabled `Pay unavailable` buttons, and each row rendered the unavailable-provider explanation. Console check returned 0 errors. Snapshot: `.playwright-cli/page-2026-06-24T04-47-10-759Z.yml`; screenshot: `.playwright-cli/page-2026-06-24T04-47-12-510Z.png`.

Status: Pass after fixing row-level tenant Pay now availability and same-page Unit Rent payment creation refresh. Continue the real-user pass with the remaining tenant portal lease follow-up actions, maintenance/message variants, and then the broader non-banking/non-QuickBooks workflows.

## Pass 63 Tenant Portal Lease Q&A Display

Date: 2026-06-24
Branch: `tsk-397-419-portal-lease-qa-pass-63`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- Tenant portal user: Blake Hayes Portal, `blake.hayes.portal.pass55@example.local`, tenant id `10`
- Lease under test: `QA-2026-002-2B`, Riverside Flats Unit 2B

Acceptance criteria:
- The tenant Lease page should render the tenant's real lease number, property, unit, rent, and end date.
- Suggestion chips and typed questions should submit the selected question and render a useful answer.
- LLM-enhanced or fallback answers must render as safe visible text and must not leak raw Markdown emphasis markers.
- The lease Q&A should continue to show source facts when returned.

Bug `TSK-419`:
- Repro: As Blake Hayes Portal, open `/portal/lease`, click the built-in `When is rent due?` suggestion, and read the answer.
- Observed before fix: the Q&A succeeded but visible text rendered `Rent is due on the **1st of each month**.`
- Root cause: the page rendered answer text directly in a plain paragraph. When the local provider returned Markdown-style emphasis, Svelte safely escaped it as text, which avoided unsafe HTML but leaked the formatting markers to the tenant.
- Fix: added `formatLeaseAnswerText` for tenant portal lease answers. It strips common emphasis/code markers while continuing to render the result as safe text, not unsanitized HTML.
- Regression: `web/src/lib/portal/lease-answer-display.test.ts`.

Verification:
- RED: `rtk pnpm --dir web exec node --test --experimental-strip-types src/lib/portal/lease-answer-display.test.ts` failed because `lease-answer-display.ts` did not exist and the route did not use a formatter.
- GREEN: `rtk pnpm --dir web exec node --test --experimental-strip-types src/lib/portal/lease-answer-display.test.ts` passed 2/2.
- Browser proof before fix: Playwright CLI logged in as Blake Hayes Portal, opened `https://localhost:6042/portal/lease`, clicked `When is rent due?`, and saw `Rent is due on the **1st of each month**.` with 0 console errors. Snapshot: `.playwright-cli/page-2026-06-24T05-03-22-147Z.yml`.
- Browser proof after fix: after reloading the page and clicking the same suggestion, the answer rendered `Rent is due on the 1st of each month.` with 0 console errors. Snapshot: `.playwright-cli/page-2026-06-24T05-04-54-812Z.yml`; screenshot: `.playwright-cli/page-2026-06-24T05-04-56-358Z.png`.

Status: Pass after stripping raw Markdown emphasis from tenant lease Q&A answers. Continue the real-user tenant portal pass with remaining maintenance/message variants, then resume the broader non-banking/non-QuickBooks workflows.

## Pass 64 Tenant Portal Maintenance Camera-Image Request

Date: 2026-06-24
Branch: `tsk-397-full-ui-pass-64`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- Tenant portal user: Blake Hayes Portal, `blake.hayes.portal.pass55@example.local`, tenant id `10`

Acceptance criteria:
- A tenant should be able to submit a new maintenance request from `/portal/maintenance` with title, description, category, priority, and an optional camera-style image.
- The selected image should show a visible preview and remove action before submission.
- Submitting should create the tenant-scoped work order and best-effort document upload without a reload or hidden error.
- The newly created request should appear at the top of the tenant's request list.
- Opening the new request should show the request body, current status/priority, attached photo in `Photos & documents`, a document-specific delete action, and a progress timeline.
- Clicking the attached image should open/download the original JPEG bytes.

Verification:
- Browser proof: Playwright CLI opened `https://localhost:6042/portal/maintenance` as Blake Hayes Portal, filled `TSK-397 Pass 64 garbage disposal leak`, attached `output/qa/production-scale-scans/05-work-orders-camera/work-order-001.jpg`, and saw the attached-image preview plus `Remove photo`. Snapshot: `.playwright-cli/page-2026-06-24T05-23-21-888Z.yml`.
- Browser proof: clicking `Submit Request` created the new request, reset the form, and showed `TSK-397 Pass 64 garbage disposal leak` as the newest list row. Network proof showed `POST /api/v1/portal/tenant/work-orders => 201`, `POST /api/v1/documents => 201`, and the refreshed `GET /api/v1/portal/work-orders => 200`.
- Browser proof: opening the new request detail rendered status `New`, priority `Normal`, the submitted description, `Photos & documents`, `work-order-001.jpg`, `130.1 KB`, `Upload`, `Delete work-order-001.jpg`, and Progress timeline row `New / Created / Tenant / just now`. Snapshot: `.playwright-cli/page-2026-06-24T05-23-54-171Z.yml`; screenshot: `.playwright-cli/page-2026-06-24T05-24-34-865Z.png`.
- Browser proof: clicking `work-order-001.jpg` downloaded `.playwright-cli/work-order-001.jpg`. `shasum -a 256` matched the source fixture exactly: `1216ae38e630e6b6d69ce8044843ce14556eafd361ad1c55012bb137cc0976cd`.
- DB proof: `WorkOrders.Id = 6`, title `TSK-397 Pass 64 garbage disposal leak`, `Status = 0`, `Priority = 1`, `TenantId = 10`; `StoredFiles.Id = 43`, `FileName = work-order-001.jpg`, `ContentType = image/jpeg`, `FileSize = 133260`, `DeletedAt = null`.
- Console proof: Playwright CLI console check returned 0 warning-or-higher messages after create, detail open, and document download.

Status: Pass with no code changes. Continue the real-user tenant portal pass with message variants and then resume the broader non-banking/non-QuickBooks workflows.

## Pass 65 Tenant Portal and Staff Message Thread URL Sync

Date: 2026-06-24
Branch: `tsk-397-420-message-url-sync-pass-65`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh accounts:
- Tenant portal user: Blake Hayes Portal, `blake.hayes.portal.pass55@example.local`, tenant id `10`
- Staff user: Jordan Vale, `tsk397.pass51.202606231856@example.local`

Acceptance criteria:
- A tenant can start a new portal conversation with subject/body, but cannot submit an empty conversation.
- A tenant can reply with Enter-to-send and see the sent message immediately without reload.
- Staff sees tenant-created messages with unread counts, opening a thread clears the landlord unread count, and staff can reply through the Portal channel.
- The tenant sees the staff reply and opening the thread clears the tenant conversation unread count.
- Inbound `?conversation=<id>` deep links still open the selected thread for both staff and tenant routes.
- Selecting a different conversation row updates the browser URL to the visible thread id; selecting a valid row from a stale/bogus deep link replaces the stale query parameter.
- Mobile back-to-list clears the `conversation` query parameter and returns to the thread list.

Bug `TSK-420`:
- Repro: As Blake Hayes Portal, open `https://localhost:6042/portal/messages?conversation=999999`. The page shows `Couldn't load this conversation.` as expected. Click the valid `TSK-397 Pass 65 lease portal question` row.
- Observed before fix: the valid thread opened and `GET /api/v1/portal/conversations/2 => 200`, but the browser URL stayed `/portal/messages?conversation=999999`. Reloading would return the tenant to the stale 404 state even though the visible selected thread had changed.
- Scope check: staff Messages used the same component-state-only row selection pattern while also accepting inbound `?conversation=` deep links.
- Fix: added shared `conversation-url-state` helpers to parse positive integer conversation ids and build query-preserving selected-thread URLs. Staff and tenant Messages now replace the URL on row selection and clear it when returning to the list.
- Regression: `web/src/lib/messages/conversation-url-state.test.ts`.

Verification:
- RED: `rtk pnpm --dir web exec node --test --experimental-strip-types src/lib/messages/conversation-url-state.test.ts` failed before the helper existed.
- GREEN: `rtk pnpm --dir web exec node --test --experimental-strip-types src/lib/messages/conversation-url-state.test.ts src/lib/messages/conversation-read-state.test.ts` passed 5/5.
- GREEN: `rtk pnpm --dir web check` reported 0 errors and the existing 4 `PageHeader.svelte` unused-selector warnings.
- Browser proof before fix: tenant session on `/portal/messages?conversation=999999` clicked the valid `TSK-397 Pass 65 lease portal question` row; the thread opened, but the URL remained `?conversation=999999`. Snapshot: `.playwright-cli/page-2026-06-24T05-31-01-774Z.yml`.
- Browser proof after fix, tenant route: clean Blake session opened `/portal/messages?conversation=999999`, clicked the valid thread row, and the URL replaced to `/portal/messages?conversation=2` while rendering the selected thread. Snapshot: `.playwright-cli/page-2026-06-24T05-34-23-162Z.yml`.
- Browser proof after fix, tenant mobile back: at 390px wide on `/portal/messages?conversation=2`, clicking `Back to messages` cleared the URL to `/portal/messages` and returned to the list. Snapshot: `.playwright-cli/page-2026-06-24T05-35-09-699Z.yml`.
- Browser proof after fix, staff route: Jordan session opened `/messages?conversation=999999`, clicked the valid `TSK-397 Pass 65 lease portal question` row, and the URL replaced to `/messages?conversation=2` while rendering the selected thread. Snapshot: `.playwright-cli/page-2026-06-24T05-34-46-355Z.yml`.
- End-to-end message proof: Blake created `TSK-397 Pass 65 lease portal question`, replied with Enter-to-send, Jordan opened the staff thread and replied via Portal, and Blake's portal session received the updated preview/thread. DB proof showed three ordered messages, final `LastMessagePreview = Yes, keep paying by check for now. We will message you here before online payments are enabled.`, `LandlordUnreadCount = 0`, and `TenantUnreadCount = 0` after the tenant opened the thread.

Status: Pass after fixing stale selected-message URLs across tenant and staff Messages. Continue the real-user pass with remaining tenant portal notification/message variants, then resume the broader non-banking/non-QuickBooks workflows.

## Pass 66 Tenant Portal Message Notification Follow-Through

Date: 2026-06-24
Branch: `tsk-397-full-ui-pass-66`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- Tenant portal user: Blake Hayes Portal, `blake.hayes.portal.pass55@example.local`, tenant id `10`

Acceptance criteria:
- A staff Portal-channel reply should create a tenant-visible notification with the message subject, preview, and `/portal/messages?conversation=<id>` action URL.
- The tenant portal notification list should show the unread message notification and the app shell unread notification badge.
- Clicking the notification should mark it read before/while navigating to the target message thread.
- The target message thread should render with the correct `conversation` query parameter and visible management reply.
- The app shell notification badge should refresh to zero after the click, and conversation unread counters should remain clear after the tenant opens the thread.

Verification:
- Browser proof: in Blake's tenant portal session, `/portal/notifications` rendered shell status `1 unread notification` and the unread `TSK-397 Pass 65 lease portal question` notification with preview `Yes, keep paying by check for now. We will message you here before online payments are enabled.` Snapshot: `.playwright-cli/page-2026-06-24T05-37-56-868Z.yml`.
- Browser proof: clicking that notification navigated to `https://localhost:6042/portal/messages?conversation=2`, rendered the selected message thread and the management reply, and refreshed the app shell to `0 unread notifications`. Snapshot: `.playwright-cli/page-2026-06-24T05-38-11-586Z.yml`.
- Network proof: the click issued `POST /api/v1/notifications/7/read => 204`, refreshed `GET /api/v1/notifications/unread-count => 200`, loaded SvelteKit data for `/portal/messages?conversation=2`, and fetched `GET /api/v1/portal/conversations/2 => 200`.
- DB proof: `Notifications.Id = 7` for `TSK-397 Pass 65 lease portal question` has `ActionUrl = /portal/messages?conversation=2` and a non-null `ReadAt = 2026-06-24 05:38:05.601709+00`; `Conversations.Id = 2` has `LandlordUnreadCount = 0` and `TenantUnreadCount = 0`.
- Console proof: Playwright CLI console check returned 0 warning-or-higher messages for the notification click/navigation flow.

Status: Pass with no code changes. Continue the real-user pass with remaining tenant portal surfaces, then resume the broader non-banking/non-QuickBooks workflows.

## Pass 67 Tenant Dashboard Notification Summary Read-State

Date: 2026-06-24
Branch: `tsk-397-full-ui-pass-67`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh accounts:
- Tenant portal user: Blake Hayes Portal, `blake.hayes.portal.pass55@example.local`, tenant id `10`
- Staff user: Jordan Vale, `tsk397.pass51.202606231856@example.local`

Acceptance criteria:
- The tenant dashboard notification summary should show unread message notifications and the same unread count as the app shell.
- Clicking an unread dashboard notification should mark it read before/while navigating to the target workflow.
- The app-shell unread notification badge should refresh to zero after the click.
- Already-read dashboard notifications should still navigate without issuing unnecessary read calls.

Bug `TSK-421`:
- Repro: As Jordan, send portal-only reply `Dashboard notification read-state proof for Pass 67.` in conversation 2. As Blake, open `/portal` and click the unread dashboard notification link.
- Observed before fix: the dashboard navigated to `/portal/messages?conversation=2`, but the app shell still showed `1 unread notification` and `Notifications.Id = 8` kept `ReadAt = NULL`.
- Scope check: `/portal/notifications` already used `notificationStore.markAsRead()` and refreshed before `goto()`, but `/portal` rendered the summary notifications as plain anchors.
- Fix: the tenant dashboard notification summary now uses `openDashboardNotification()`, marks unread items read through `notificationStore`, refreshes the store and local notification queries, then navigates with `goto(portalActionUrl(...))`.
- Regression: `web/src/lib/portal/notifications-page.test.ts`.

Verification:
- GREEN: `rtk pnpm --dir web exec node --test --experimental-strip-types src/lib/portal/notifications-page.test.ts` passed 2/2.
- GREEN: `rtk pnpm --dir web check` reported 0 errors and the existing 4 `PageHeader.svelte` unused-selector warnings.
- Browser proof before fix: Blake's dashboard rendered `1 unread notification` and the unread `TSK-397 Pass 65 lease portal question` item. Clicking it loaded `/portal/messages?conversation=2`, but the shell still rendered `1 unread notification`. DB proof showed `Notifications.Id = 8` had `ReadAt = NULL`.
- Browser proof after fix: Blake's dashboard rendered the same unread item as a button. Clicking it loaded `/portal/messages?conversation=2`, rendered the message thread, and refreshed the app shell to `0 unread notifications`. Snapshot: `.playwright-cli/page-2026-06-24T05-51-02-560Z.yml`.
- DB proof after fix: `Notifications.Id = 8` for `TSK-397 Pass 65 lease portal question` has `ActionUrl = /portal/messages?conversation=2` and `ReadAt = 2026-06-24 05:50:56.5406+00`.

Status: Pass after fixing tenant dashboard notification read-state. Continue the real-user tenant portal dashboard pass with the remaining dashboard cards and form states, then resume the broader non-banking/non-QuickBooks workflows.

## Pass 68 Tenant Dashboard Cards and Appointments Continuation

Date: 2026-06-25
Branch: `tsk-397-422-423-424-portal-dashboard-cards-pass-68`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- Tenant portal user: Blake Hayes Portal, `blake.hayes.portal.pass55@example.local`, tenant id `10`

Acceptance criteria:
- The tenant dashboard should load the signed-in tenant's real upcoming appointments instead of static placeholder copy.
- Dashboard appointment cards should show title, time window, location, appointment type, and status, then navigate to `/portal/appointments`.
- Due-today scheduled rent should remain outstanding and visible as `0 days`, but should not be counted in the Overdue card or overdue item count.
- Tenant-scoped appointments without a direct `UnitId` should still show the tenant's active/latest lease unit label so the tenant sees the expected home location.

Bug `TSK-422`:
- Repro: As Blake Hayes Portal, open `/portal`. `Appointments.Id = 1` exists for tenant `10`, title `Pass 51 service visit date fixed`, scheduled June 27, 2026, but the dashboard appointments panel rendered placeholder-only copy.
- Observed before fix: the tenant dashboard did not call `portal.appointments()` and showed no appointment rows.
- Fix: `/portal` now loads `['portal-appointments']`, renders loading/error/empty states, displays up to three appointment cards, and links each card to `/portal/appointments`.
- Regression: `web/src/lib/portal/dashboard-page.test.ts`.

Bug `TSK-423`:
- Repro: scheduled rent due on the current UTC calendar date was counted as overdue because `PortalService.GetBalanceAsync` compared `DueDate < DateTime.UtcNow`.
- Observed before fix: a payment due today could be marked overdue for most of the due date after midnight UTC.
- Fix: `GetBalanceAsync` now uses `DateTime.UtcNow.Date` as the overdue cutoff. Scheduled or partial payments are overdue only if explicitly `Late` or due before the current UTC calendar day.
- Regression: `RentalCommand.Api.Tests/Domain/PortalServiceBalanceTests.cs`.

Bug `TSK-424`:
- Repro: the live tenant appointment `Pass 51 service visit date fixed` is tenant-scoped and property-scoped, but `Appointments.UnitId` is null. The tenant has an active lease on Riverside Flats Unit 2B.
- Observed before fix: the dashboard appointment card showed `Riverside Flats` without `Unit 2B`.
- Fix: the portal appointment projection now falls back to the tenant's active/latest lease unit when an appointment itself has no direct unit, keeping the fallback inside the SQL projection.
- Regression: `PortalServiceAppointmentTests.GetAppointmentsAsync_FallsBackToActiveLeaseUnitForTenantScopedAppointments`.

Verification:
- RED: `rtk pnpm --dir web exec node --test --experimental-strip-types src/lib/portal/dashboard-page.test.ts` failed before the dashboard appointments query existed.
- RED: `MSBUILDDISABLENODEREUSE=1 rtk dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter FullyQualifiedName~PortalServiceBalanceTests --no-restore` failed before the due-today cutoff fix with due-today rent counted overdue.
- RED: `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter FullyQualifiedName~PortalServiceAppointmentTests --no-restore --logger "console;verbosity=normal"` failed before the unit fallback with `Expected appointment.UnitNumber to be "2B", but found <null>.`
- GREEN: `rtk env MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter "FullyQualifiedName~PortalServiceBalanceTests|FullyQualifiedName~PortalServiceAppointmentTests" --no-restore --logger "console;verbosity=normal"` passed 3/3 with the existing `SQLitePCLRaw.lib.e_sqlite3` NU1903 warning.
- GREEN: `rtk pnpm --dir web exec node --test --experimental-strip-types src/lib/portal/dashboard-page.test.ts src/lib/portal/notifications-page.test.ts` passed 3/3.
- GREEN: `rtk pnpm --dir web check` reported 0 errors and the existing 4 `PageHeader.svelte` unused-selector warnings.
- UI guide: `Docs/Testing/UI/tsk-397-pass-68-portal-dashboard-cards.md`.
- Browser proof: Playwright CLI signed in as Blake, opened `/portal`, and rendered Overdue `$1,212.34` / `2 overdue items`, Next Rent `0` / `days until $22.25 is due`, the `$22.25` rent row due June 25, 2026 as `0 days`, and the appointment `Pass 51 service visit date fixed` with `Riverside Flats Unit 2B`, `Maintenance`, and `Scheduled`. Screenshot: `output/playwright/pass68-portal-dashboard-cards.png`.
- Browser navigation proof: clicking the dashboard appointment row navigated to `https://localhost:6042/portal/appointments`; the page rendered the same appointment and `Riverside Flats Unit 2B`. Snapshot: `.playwright-cli/page-2026-06-25T01-04-56-411Z.yml`; screenshot: `output/playwright/pass68-portal-appointments-target.png`.
- DB proof: inserted a synthetic local scheduled rent row only for browser setup, `Payments.Id = 8`, amount `$22.25`, due `2026-06-25 00:00:00+00`, note `TSK-423 due-today browser proof`; older June 24 scheduled rows remained overdue and unchanged. Appointment `Id = 1` has `TenantId = 10`, `PropertyId = 7`, `UnitId = NULL`, while tenant lease `Id = 8` has `UnitId = 9`, `UnitNumber = 2B`.

Status: Pass after fixing tenant dashboard appointment visibility, due-today rent classification, and appointment unit fallback. Continue the real-user pass with remaining tenant dashboard form states and then broader non-banking/non-QuickBooks workflows.

## Pass 69 Tenant Dashboard Quick Maintenance Validation

Date: 2026-06-25
Branch: `tsk-397-425-dashboard-maintenance-validation-pass-69`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- Tenant portal user: Blake Hayes Portal, `blake.hayes.portal.pass55@example.local`, tenant id `10`

Acceptance criteria:
- Empty dashboard quick maintenance submit should show visible field-level validation for missing issue title and description.
- Valid dashboard quick maintenance submit should continue creating a tenant work order through the existing tenant-scoped API path.
- Successful submit should clear the validation state and reset the form.
- The dashboard maintenance card and request list should refresh after a successful quick request.

Bug `TSK-425`:
- Repro: As Blake Hayes Portal, open `/portal` and click `Submit Request` in the Maintenance Requests card with title and description blank.
- Observed before fix: the button was active, but `submitWorkOrder()` returned early with no toast, no inline error, no field invalid state, and no focus-changing feedback.
- Fix: the dashboard quick maintenance form now tracks attempted submit state, renders inline errors for missing title and description, marks invalid fields with `aria-invalid`, keeps `aria-required`, and clears validation state after a successful submit.
- Regression: `web/src/lib/portal/dashboard-page.test.ts`.

Verification:
- RED: `rtk pnpm --dir web exec node --test --experimental-strip-types src/lib/portal/dashboard-page.test.ts` failed before the fix because the dashboard route had no `workOrderSubmitted`, title error, or description error state.
- GREEN: `rtk pnpm --dir web exec node --test --experimental-strip-types src/lib/portal/dashboard-page.test.ts` passed 2/2 after the fix.
- UI guide: `Docs/Testing/UI/tsk-397-pass-69-dashboard-maintenance-validation.md`.
- Browser proof before fix: Blake clicked empty `Submit Request`; the dashboard remained unchanged with no visible validation. Snapshot: `.playwright-cli/page-2026-06-25T01-13-06-269Z.yml`.
- Browser proof after fix: empty `Submit Request` rendered `Issue title is required.` and `Describe the issue before submitting.`, with both fields marked invalid. Snapshot: `.playwright-cli/page-2026-06-25T01-15-27-326Z.yml`; screenshot: `output/playwright/pass69-dashboard-maintenance-validation.png`.
- Browser happy-path proof: filling title `TSK-397 Pass 69 dashboard quick maintenance validation proof` and the cabinet-hinge description submitted successfully, incremented the Maintenance card from `2` to `3`, and rendered the new request first as `New · Normal`. Snapshot: `.playwright-cli/page-2026-06-25T01-15-42-654Z.yml`; screenshot: `output/playwright/pass69-dashboard-maintenance-submit.png`.
- DB proof: `WorkOrders.Id = 7`, title `TSK-397 Pass 69 dashboard quick maintenance validation proof`, `TenantId = 10`, `UnitId = 9`, `LeaseId = 8`, `Status = 0`, `Priority = 1`, `CreatedBy = Tenant`.

Status: Pass after fixing tenant dashboard quick maintenance validation and proving the submit path still creates a tenant-scoped work order. Continue the real-user pass with tenant portal maintenance list/detail follow-through and remaining non-banking/non-QuickBooks workflows.

## Pass 70 Tenant Portal Maintenance Detail Follow-Through And Validation

Date: 2026-06-25
Branch: `tsk-397-full-ui-pass-70`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- Tenant portal user: Blake Hayes Portal, `blake.hayes.portal.pass55@example.local`, tenant id `10`

Acceptance criteria:
- A tenant-created dashboard maintenance request should appear on the full tenant maintenance page without reload-only workarounds.
- Opening the request should show the title, status, priority, description, progress, and document empty state in the detail modal.
- A tenant should be able to attach a camera-style image from the detail modal's Photos & documents panel.
- The attached image should be retrievable through the tenant UI and should match the uploaded file bytes.
- Empty submit on the full tenant maintenance page should show visible field-level validation for missing issue title and description.
- Valid submit on the full tenant maintenance page should still create a tenant-scoped work order, clear validation state, reset the form, and refresh the request list.

Bug `TSK-426`:
- Repro: As Blake Hayes Portal, open `/portal/maintenance` and click `Submit Request` in the full Submit a request form with title and description blank.
- Observed before fix: `submit()` returned early when either field was blank, with no toast, inline error, invalid field state, or focus-changing feedback.
- Fix: the full maintenance form now tracks attempted submit state, renders inline errors for missing title and description, marks invalid fields with `aria-invalid`, wires `aria-describedby`, and clears validation state after a successful submit.
- Regression: `web/src/lib/portal/maintenance-page.test.ts`.

Verification:
- UI guide: `Docs/Testing/UI/tsk-397-pass-70-portal-maintenance-detail.md`.
- RED: `rtk pnpm --dir web exec node --test --experimental-strip-types src/lib/portal/maintenance-page.test.ts` failed before the fix because the route had no `requestSubmitted`, title error, or description error state.
- GREEN: `rtk pnpm --dir web exec node --test --experimental-strip-types src/lib/portal/maintenance-page.test.ts` passed 1/1 after the fix.
- GREEN: `rtk pnpm --dir web check` reported 0 errors and the existing 4 `PageHeader.svelte` unused-selector warnings.
- Browser proof: Playwright CLI opened `/portal` as Blake Hayes Portal, clicked the dashboard Maintenance card, and `/portal/maintenance` listed `TSK-397 Pass 69 dashboard quick maintenance validation proof` first with `Normal` and `New`. Snapshot: `.playwright-cli/page-2026-06-25T01-22-08-151Z.yml`.
- Browser detail proof: opening the request showed the detail modal with status `New`, priority `Normal`, description `Kitchen cabinet door hinge is loose. Submitted from the tenant dashboard quick form after validation proof.`, progress row `Tenant · Created`, and the empty Photos & documents state. Snapshot: `.playwright-cli/page-2026-06-25T01-22-16-862Z.yml`.
- Browser upload proof: uploaded `output/qa/production-scale-scans/05-work-orders-camera/work-order-002.jpg` through the modal Upload button. The app showed `"work-order-002.jpg" uploaded.` and listed `work-order-002.jpg` as a `130.1 KB` attachment. Snapshot: `.playwright-cli/page-2026-06-25T01-22-43-237Z.yml`; screenshot: `.playwright-cli/page-2026-06-25T01-23-43-441Z.png`.
- Browser download proof: clicking the document name downloaded `work-order-002.jpg` to `.playwright-cli/work-order-002.jpg`.
- File proof: `file .playwright-cli/work-order-002.jpg` reported a JPEG image, `1800x2400`; SHA-256 matched the uploaded fixture exactly: `f8ab417ce87918bd21905488a252bd17be4c4d1a19d5ea77444f77323e99dc6c`.
- Network proof: request log showed `GET /api/v1/portal/work-orders/7 => 200`, `GET /api/v1/documents?entityType=WorkOrder&entityId=7 => 200`, `POST /api/v1/documents => 201`, refreshed `GET /api/v1/documents?entityType=WorkOrder&entityId=7 => 200`, and `GET /api/v1/documents/44/file => 200`.
- DB proof: `StoredFiles.Id = 44`, `FileName = work-order-002.jpg`, `ContentType = image/jpeg`, `FileSize = 133263`, `EntityType = WorkOrder`, `EntityId = 7`, `DeletedAt = NULL`, `UploadedAt = 2026-06-25 01:22:42.240963+00`.
- Browser validation proof: empty `Submit Request` rendered invalid title/description fields plus `Issue title is required.` and `Describe the issue before submitting.` Snapshot: `.playwright-cli/page-2026-06-25T01-27-20-918Z.yml`; screenshot: `.playwright-cli/page-2026-06-25T01-27-22-088Z.png`.
- Browser happy-path proof after validation: filling `TSK-397 Pass 70 full maintenance validation proof` and the closet-door description submitted successfully, showed `Maintenance request submitted.`, cleared the form, and rendered the new request first as `New` / `Normal`. Snapshot: `.playwright-cli/page-2026-06-25T01-27-35-439Z.yml`; screenshot: `.playwright-cli/page-2026-06-25T01-27-50-018Z.png`.
- DB proof after validation: `WorkOrders.Id = 8`, title `TSK-397 Pass 70 full maintenance validation proof`, `TenantId = 10`, `UnitId = 9`, `LeaseId = 8`, `Status = 0`, `Priority = 1`, `CreatedBy = Tenant`, `RequestedAt = 2026-06-25 01:27:34.428372+00`.
- Network proof after validation: request log showed `POST /api/v1/portal/tenant/work-orders => 201` followed by `GET /api/v1/portal/work-orders => 200`.
- App console proof: Playwright CLI `console warning` and `console error` returned 0 app warnings/errors.

Status: Pass after fixing full tenant maintenance page validation and preserving valid request creation. Continue the real-user tenant portal pass with remaining maintenance close/delete behaviors, then resume remaining non-banking/non-QuickBooks workflows.

## Pass 71 Tenant Portal Maintenance Delete-Cancel Guard

Date: 2026-06-25
Branch: `tsk-397-full-ui-pass-71`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- Tenant portal user: Blake Hayes Portal, `blake.hayes.portal.pass55@example.local`, tenant id `10`

Acceptance criteria:
- Tenant maintenance document delete must be guarded by an explicit confirmation dialog.
- Canceling the confirmation must leave the attachment visible in the detail modal and unchanged in storage metadata.
- Destructive delete confirm remains intentionally unsubmitted without explicit approval.

Verification:
- Browser proof: opened `/portal/maintenance` as Blake Hayes Portal, opened `TSK-397 Pass 69 dashboard quick maintenance validation proof`, and clicked `Delete work-order-002.jpg`. The app showed a `Delete document` confirmation dialog with copy `Delete "work-order-002.jpg"? This cannot be undone.` plus `Cancel` and `Delete` actions. Snapshot: `.playwright-cli/page-2026-06-25T01-31-32-858Z.yml`.
- Browser cancel proof: clicked `Cancel`; the confirmation dialog closed and `work-order-002.jpg` remained visible in the Photos & documents list. Snapshot: `.playwright-cli/page-2026-06-25T01-31-43-471Z.yml`.
- Browser close proof: clicked the detail modal `Close` action and returned to the `/portal/maintenance` request list with the same four visible open requests. Snapshot: `.playwright-cli/page-2026-06-25T01-32-20-158Z.yml`.
- DB proof after cancel: `StoredFiles.Id = 44`, `FileName = work-order-002.jpg`, `EntityType = WorkOrder`, `EntityId = 7`, `DeletedAt = NULL`.
- App console proof: Playwright CLI `console warning` and `console error` returned 0 app warnings/errors.

Status: Pass with no code changes. Continue the tenant portal pass with remaining non-destructive maintenance detail behavior, then resume remaining non-banking/non-QuickBooks workflows.

## Pass 72 Tenant Account Security Menu

Date: 2026-06-25
Branch: `tsk-397-427-tenant-security-menu-pass-71`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- Tenant portal user: Blake Hayes Portal, `blake.hayes.portal.pass55@example.local`, tenant id `10`

Acceptance criteria:
- Tenant-only users clicking the account-menu `Security` item must land on a tenant-accessible account security page instead of the staff protected `/settings/security` route.
- Staff `/settings/security` must keep the same change-password controls and server action.
- The tenant security page must show portal-specific shell/page context and a `Back to portal` escape path.
- Non-destructive password validation states must still work without changing the tenant password.

Bug `TSK-427`:
- Repro before fix: as Blake Hayes Portal on `/portal/maintenance`, open the user menu and click `Security`. The link targeted `/settings/security`; the tenant-only protected-layout guard redirected back to `/portal`, leaving Security as a dead menu item.
- Root cause: `AppShell.svelte` hardcoded `/settings/security` for both staff and tenant user-menu variants, while `(protected)/+layout.server.ts` correctly blocks tenant-only users from protected staff routes.
- Fix: the AppShell security href is now conditional (`/portal/security` for tenant-only users, `/settings/security` for staff). The password form and server action were factored into shared account-security component/action modules. A new `(portal)/portal/security` route reuses them with a portal back link, and the shell title resolver now knows `/portal/security` as a non-sidebar utility route.
- Destructive password-change submit was intentionally not performed in browser proof; the tenant password remained unchanged.

Regression:
- Red proof before fix: `rtk pnpm --dir web exec node --test --experimental-strip-types src/lib/components/app-shell-security-menu.test.ts src/lib/account/security-page-routes.test.ts` failed because `/portal/security`, shared route wrappers, the shared account action, and the conditional AppShell security href did not exist.
- Green proof after fix: the same targeted command passed 5/5 tests.
- `rtk pnpm --dir web check` passed with 0 errors and the existing 4 PageHeader unused-selector warnings.

Verification:
- Browser proof before fix: Blake clicked account-menu `Security`; the app navigated to `/settings/security` and bounced to `/portal`. Snapshot: `.playwright-cli/page-2026-06-25T01-33-05-328Z.yml`.
- Browser proof after fix: Blake opened the account menu on `/portal/maintenance`; the `Security` menu item exposed `/portal/security`. Snapshot: `.playwright-cli/page-2026-06-25T01-41-30-763Z.yml`.
- Browser route proof after fix: clicking `Security` landed on `/portal/security` with page title `Security - Rental Command`, shell header `Security`, `Back to portal`, and the shared change-password card. Snapshot: `.playwright-cli/page-2026-06-25T01-43-38-209Z.yml`.
- Browser validation proof after fix: filling current password, mismatched new/confirm passwords rendered the password rules, `Passwords do not match.`, and left `Change password` disabled; no submit occurred. Screenshot: `.playwright-cli/page-2026-06-25T01-45-29-756Z.png`.
- App console proof: Playwright CLI `console error` and `console warning` returned 0 app errors/warnings.

Status: Pass after fixing tenant Security menu routing and preserving the staff security route through shared account-security components/actions. Continue the real-user tenant portal pass, then resume remaining non-banking/non-QuickBooks workflows.

## Pass 73 Unit Command Center Send Renewal Retest

Date: 2026-06-25
Branch: `tsk-397-404-renewal-pass-73`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- Dev admin sample-data user: Rental Command Admin, `admin@rentalcommand.local`
- Setup path: logged in through the dev-admin helper, chose sample/example data, and verified the app banner states example data does not send real emails/texts or charge cards.

Acceptance criteria:
- A renewal-stage unit must expose `Send renewal -- lease ends in ... days` as a real link, not a button with no action.
- Clicking the link must navigate to the active tenant with `action=create-notice&noticeType=RenewalOffer`.
- The tenant page must open `Create / Send notice` automatically and show a `Renewal offer` draft grounded in the selected unit/lease.
- Browser proof must stop before pressing `Send`; no notice delivery should occur during this retest.

Bug `TSK-404` current-state retest:
- User-reported evidence showed the Unit Command Center `Send renewal` action appearing to do nothing. Retesting on current `main` did not reproduce the no-op for either renewal-stage sample unit.
- Eastland 8-Plex Unit 1 rendered `Send renewal -- lease ends in 61 days` with target `/tenants/19?action=create-notice&noticeType=RenewalOffer`; clicking it opened Kevin Brown's tenant detail and the `Create / Send notice` dialog with `Renewal offer`, `Lease renewal for Eastland 8-Plex Unit 1`, and checked Portal/Email/SMS delivery choices.
- Short North Condo Unit 4B rendered `Send renewal -- lease ends in 30 days` with target `/tenants/18?action=create-notice&noticeType=RenewalOffer`; clicking it opened Emily Chen's tenant detail and the `Create / Send notice` dialog with `Renewal offer`, `Lease renewal for Short North Condo Unit 4B`, and checked Portal/Email/SMS delivery choices.
- No code patch was made in this slice because the current app already satisfied the workflow and no failing behavior was found to protect.

Verification:
- Browser proof, Unit 1 before click: `.playwright-cli/page-2026-06-25T01-59-10-373Z.yml`.
- Browser proof, Unit 1 after click: `.playwright-cli/page-2026-06-25T01-59-22-712Z.yml`.
- Browser proof, Unit 4B before click: `.playwright-cli/page-2026-06-25T02-00-40-502Z.yml`.
- Browser proof, Unit 4B after click: `.playwright-cli/page-2026-06-25T02-00-58-294Z.yml`.
- Screenshot proof, Unit 4B renewal draft: `.playwright-cli/page-2026-06-25T02-01-32-064Z.png`.
- Network proof for Unit 4B: the click loaded `/tenants/18/__data.json?action=create-notice&noticeType=RenewalOffer`, fetched the tenant/lease/document/audit data, and `POST /api/v1/notices/generate` returned `200`.
- App console proof: Playwright CLI `console warning` returned 0 warnings and 0 errors.

Status: Pass with documentation-only current-state verification. Continue the full real-user audit from the next unverified non-banking/non-QuickBooks workflow.

## Pass 74 Admin Team and Superadmin Gate Continuation

Date: 2026-06-25
Branch: `tsk-397-real-user-pass-74`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

Local stack:
- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Fresh account:
- Dev admin sample-data user: Rental Command Admin, `admin@rentalcommand.local`
- Setup path: sample/example data only; the app banner states example data does not send real emails/texts or charge cards.

Acceptance criteria:
- `/superadmin/engine` must stay hidden from ordinary landlord admins when `PLATFORM_ADMIN_EMAILS` is unset.
- `/admin/users` must load the team list through the page contract and prevent the signed-in admin from changing their own role/status.
- Empty invite submit must stay disabled.
- Duplicate-email invite attempts must show a recoverable error and leave the invite dialog open.
- Successful staff invites must add the member, show the generated one-time password, block accidental dismissal until copied or manually saved, and allow role/status changes for non-self users.
- Tenant-role invites must require selecting a tenant, persist the tenant link on both the user account and identity user, and use the same generated-password guard.

Verification:
- Superadmin gate: `/superadmin/engine` returned the app 404 page for the ordinary admin user. Source guard confirms unauthenticated users redirect to login and authenticated non-allowlisted users receive 404; `PLATFORM_ADMIN_EMAILS` is unset locally. Snapshot: `.playwright-cli/page-2026-06-25T02-06-07-728Z.yml`.
- Admin Team load: `/admin/users` rendered the signed-in admin row with disabled role/status controls and `Page 1 - 1 shown`; request `GET /api/v1/admin/users/page?take=20&sort=-createdAt` returned `200`. Snapshot: `.playwright-cli/page-2026-06-25T02-06-49-083Z.yml`.
- Empty invite state: opening `Invite member` rendered email/name/role/password fields and a disabled `Add member`. Snapshot: `.playwright-cli/page-2026-06-25T02-06-56-595Z.yml`.
- Duplicate email: submitting `admin@rentalcommand.local` returned `POST /api/v1/admin/users => 400`, showed toast `A user with that email address already exists.`, and kept the dialog open for correction. Snapshot: `.playwright-cli/page-2026-06-25T02-07-13-241Z.yml`.
- Staff invite: submitting `team.pass74.20260625@example.local` returned `201`, added `Pass 74 Manager` to the grid, and opened `Temporary password -- save it now`. Escape did not close the dialog before the password was copied. Copying the password showed `Password copied to clipboard.` and unlocked `I've copied the password`; closing returned to the team grid. Snapshots: `.playwright-cli/page-2026-06-25T02-07-26-596Z.yml`, `.playwright-cli/page-2026-06-25T02-07-40-839Z.yml`, `.playwright-cli/page-2026-06-25T02-07-51-960Z.yml`.
- Staff role/status: `Pass 74 Manager` changed Manager -> Agent with `PATCH /api/v1/admin/users/6/role => 200`, toggled Active -> Inactive -> Active with two `PATCH /active => 200` requests, then restored Agent -> Manager with another role patch. Final grid row is Manager/Active. Snapshot: `.playwright-cli/page-2026-06-25T02-09-21-241Z.yml`.
- Tenant invite: selecting role `Tenant` added the tenant picker and kept `Add member` disabled until a tenant was selected. Selecting Kevin Brown enabled submit; submitting `tenant.pass74.20260625@example.local` returned `201`, added the row as Tenant/Active, and showed the generated-password guard. Marking it saved manually unlocked the close button. Snapshots: `.playwright-cli/page-2026-06-25T02-09-50-485Z.yml`, `.playwright-cli/page-2026-06-25T02-10-13-605Z.yml`, `.playwright-cli/page-2026-06-25T02-10-23-741Z.yml`, `.playwright-cli/page-2026-06-25T02-10-35-064Z.png`.
- DB proof: `UserAccounts` + `AspNetUsers` show `team.pass74.20260625@example.local` as role `1`, active, with no tenant id; `tenant.pass74.20260625@example.local` as role `4`, active, with `TenantId = 19` on both records. `Tenants.Id = 19` is Kevin Brown.
- Console proof: the only browser console error after this pass is the expected failed-resource entry from the intentional duplicate-email `400` response.

Status: Pass with no code changes. Continue the non-banking/non-QuickBooks audit with the remaining settings/admin/support variants or the next route group from the inventory.
