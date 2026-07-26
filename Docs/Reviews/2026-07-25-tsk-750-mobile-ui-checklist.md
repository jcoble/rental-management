# TSK-750 mobile UI rescue checklist

Test target: Azure Android emulator `emulator-5554`, 1080 × 2400 at 420 dpi.

Backend: protected Rental Command preview stack. The audit was read-only; no stack,
database, seed, or emulator state was reset.

## Route and state coverage

- [x] Unit Command Center — Documents & history (`00-current-screen.png`)
- [x] Today — header and priority (`01-today.png`)
- [x] Today — overdue items (`02-today-briefing-mid.png`)
- [x] Today — money and messages (`03-today-briefing-lower.png`)
- [x] Today — work queue (`04-today-briefing-bottom.png`)
- [x] Work order — loading (`05-work-order-detail-from-today.png`)
- [x] Work order — detail (`06-work-order-detail-loaded.png`)
- [x] Work order — back-navigation result (`07-back-from-work-order.png`)
- [x] Work — calendar (`08-work-calendar.png`)
- [x] Appointment — detail (`09-appointment-detail.png`)
- [x] Appointment editor — details (`10-appointment-edit-form.png`)
- [x] Appointment editor — schedule (`11-appointment-edit-schedule.png`)
- [x] Appointment editor — contact (`12-appointment-edit-contact.png`)
- [x] Inspections — loading (`13-work-inspections.png`)
- [x] Inspections — list and paging (`14-work-inspections-loaded.png`)
- [x] Inspection — run/detail (`15-inspection-detail.png`)
- [x] Work — vendors (`16-work-vendors.png`)
- [x] Work — automations empty state (`18-work-automations.png`)
- [x] Work — notices (`20-work-notices.png`)
- [x] Inbox — messages (`21-inbox.png`)
- [x] Inbox and global notifications (`22-inbox-notifications.png`, `23-global-notifications.png`)
- [x] Activity history (`24-inbox-activity-history.png`)
- [x] Money — portfolio (`25-money-dashboard.png`, `26-money-portfolio-lower.png`)
- [x] Money — insights (`27-money-insights.png`, `28-money-insights-lower.png`)
- [x] Money — ledger payments (`29-money-ledger.png`, `30-money-ledger-loaded.png`)
- [x] Money — ledger expenses (`31-money-ledger-expenses.png`)
- [x] Money — deposits loading (`32-money-deposits-loading.png`, `33-money-deposits.png`)
- [x] Money — deposits loaded after approximately 17 seconds (`34-money-deposits-after-17s.png`)
- [x] Money — deposits filter (`35-money-deposits-filter.png`)
- [x] Money — banking (`36-money-banking.png`, `37-money-banking-loaded.png`)
- [x] Money — reports (`38-money-reports.png`)
- [x] Rentals — properties (`39-rentals-properties.png`)
- [x] Rentals — units (`40-rentals-units.png`, `52-rentals-return.png`, `53-rentals-tabs-scroll.png`)
- [x] Rentals — tenants (`54-rentals-tenants.png`)
- [x] Rentals — leases (`55-rentals-leases.png`)
- [x] Rentals — applications (`56-rentals-applications.png`, `57-rentals-tabs-end.png`)
- [x] Unit Command Center — summary (`41-unit-main-overview.png`)
- [x] Unit Command Center — money (`42-unit-main-money.png`)
- [x] Payment — detail and history (`43-unit-payment-detail.png`, `44-unit-payment-history.png`)
- [x] Payment — correction and Material date picker (`45-correct-payment-sheet.png`, `46-default-date-picker.png`)
- [x] Global Scan / Add (`47-scan-add-sheet.png`)
- [x] Record receipt (`48-record-receipt-sheet.png`)
- [x] Account and settings (`49-account.png`, `50-settings.png`)
- [x] My alerts (`51-my-alerts.png`)

## Required re-test after implementation

- [x] Closed quick-action control never obscures a primary action, final row, or form field.
- [x] Appointment Save Changes, Inspection Complete, Notice SMS, Append correction,
      and Record receipt remain fully tappable.
- [x] Unit and Rentals section navigation exposes every destination without clipped
      labels or guesswork.
- [ ] Unit Money uses plain-English transaction and balance language.
- [ ] Money ledger contains no internal enum values such as `PaymentReceipt`.
- [x] Work order opened from Today returns to Today at its prior position.
- [ ] Empty states reference only controls that are actually visible and available.
- [ ] Deposit loading has contextual progress immediately and the server response is
      measured after the query fix.
- [ ] Portfolio rent figures reconcile or clearly describe different measures.
- [ ] Legal notice preview contains no unresolved braces or administrator-only text.
- [ ] Month labels use readable month names.
- [ ] Dense feeds preserve the information needed to distinguish rows.
- [ ] Standard Material date picker remains cancellable and readable.
- [ ] Alert switches retain checked/disabled semantics.
- [ ] TalkBack traversal is performed for changed navigation and forms.

All screenshots and UI hierarchy captures are under
`Docs/Reviews/artifacts/tsk-750/`.
