# Lane mob-b — one money/date/label formatter for the mobile app

Start: 2026-08-25
Worktree: worktrees/rental-management/simplify-mob-b (branch tsk-1018-mob-b)

## Progress

### Item 1 — move money_format to core/presentation/formatting.dart, fold in date_labels
- `git mv mobile/lib/features/money/money_format.dart mobile/lib/core/presentation/formatting.dart`
- `date_labels.dart` deleted; `shortMonthLabel` and the month table folded into formatting.dart
  (one table, `monthAbbrs`, 1-indexed).
- 21 importers rewritten; `test/date_labels_test.dart` renamed to `test/formatting_test.dart`.
- `flutter analyze`: 24 pre-existing infos/warnings, no errors.

### Item 2 — one money formatter
Deleted display money helpers and routed them at `moneyFmt`; added the single optional
`whole: true` parameter for the four screens that deliberately show rounded whole dollars.
- Cents: owner_reports `_fmtCurrency`, insights `_fmtCurrency`, work_order_detail `_fmtCost`,
  lease_ledger_view `_money`, scan_review `_formatMoney` + `_fmtMoney`, owner_landing
  `_ownerMoney`, leasing_detail_screens `_money`, lease_detail `_money`,
  recurring_maintenance_list `_fmtMoney`.
- Whole dollars: tenant_detail, property_detail, units_list, unit_command_center
  `_formatCurrency`; applications_shared `formatMonthlyIncome`.
- Left alone: deposits `_fmtCurrency(amount, currency)` and home_shell `_money(value, currency)`
  (real non-USD paths); every `_moneyInput` / `_dateInput` form-field serializer; scan_review
  `_fmtNum`.
- Test changed: `test/analytics_rent_labels_test.dart` asserted the deleted `_fmtCurrency`
  call text; updated to the `moneyFmt` call it now reads.

### Item 3 — one date formatter
Deleted 13 copies of the month-name table; every `MMM d, yyyy` helper now calls `dateFmt`.
Deleted: tenant_detail `_fmt`, work_order_detail `_fmtDate` + `_fmtEditDate`, property_detail
`_formatDate`, lease_ledger_view `_fmtDate`, deposits `_fmtDate`, applications_shared
`formatApplicationDate`, recurring_maintenance_list `fmtDueDate`, inspections_list
`fmtInspectionDate`, appointments_shared `formatAppointmentDate`, home_shell `_shortDate`
(now `shortDateFmt`), portal `_shortDate` wrapper.
Rewired onto the shared month table: work_order_timeline `formatTimelineMoment`,
message_detail `_fmtBubbleTime`, messages_list `_fmtRelative`.
Deliberately kept (distinct formats): the numeric `M/d/yyyy` helpers in owner_reports,
unit_command_center (`Not set` guard), scan_list, property_documents, team, leasing,
leases_list, lease_detail, addendum_action_sheets, owner_landing; owner_reports `_fmtMonth`;
tenant_notices `_shortDate` (date + time); scan_review `_formatDate` (ISO `yyyy-MM-dd`);
and every ISO serializer (`_dateOnly`, `_fmtIso`, `_dateInput`, `_dateOnlyQuery`).

### Item 4 — duplicate label switches
- `_effectTypeLabel`: one copy left, now `leaseEffectTypeLabel` in
  `leases/addendum_action_sheets.dart`; successor_agreement_sheet imports it.
- `_loanPaymentStatusLabel` and the `PaidOff -> 'Paid off'` switch: both now live in
  `properties/property_labels.dart` as `loanPaymentStatusLabel` and `loanStatusLabel`;
  property_detail_screen, property_loan_form_sheet and scan_review_screen import them.
- Left alone as out of scope: the wider `plainEnglishLabel` sweep.

### Item 5 — looking at it in the running app: NOT POSSIBLE
The dev API is up (`POST /api/v1/auth/login` with the seeded admin returns HTTP 200), and
`flutter build web --release --dart-define=API_BASE_URL=https://localhost:5666/api/v1`
succeeds, but the built app never leaves its splash spinner in a browser: startup throws
before `restoreSession()` resolves, so `AuthStateUnknown` is never replaced
(`mobile/lib/main.dart` `_AppStartupState`, `mobile/lib/core/auth/auth_controller.dart`
`restoreSession` — no try/catch around the secure-token read). None of those files are in this
lane's diff, so this is the app's existing Android/iOS-only startup path, not a regression here.
Evidence: `receipts/mob-b/00-web-build-stalls-at-splash.png` (headless Chromium, viewport
verified 1710x990).

Falling back to the widget tests, as the spec allows:
- `flutter test` — 522 passing, including the screens that assert on rendered money and date
  text (unit/tenant money contracts, tenant portal account history, property detail).
- `test/formatting_test.dart` gained two cases pinning the visible output:
  `moneyFmt(1234.5)` = `$1,234.50`, `moneyFmt(1200, whole: true)` = `$1,200`,
  `dateFmt(DateTime(2026, 8, 25))` = `Aug 25, 2026`.

Processes: local static server on 5770 stopped (port free); the headless Chromium this lane
started exited with the script. Other agents' chromium processes were left alone.

### Left in place, out of scope
The full-month-name tables (`January`…`December`) in owner_reports `_fmtMonth`, home_shell
`_formattedDate` and ai/briefing_screen `_format` — a different table from the abbreviations.

## Rework (controller, after SOL review)
- `moneyFmt` now decides the sign after whole-dollar rounding, so `moneyFmt(-0.4, whole: true)` is `$0` as the deleted helpers produced (pinned by a test); negatives render `-$1,234.56`.
- `formatAppointmentDateTime` formats unguarded again (`Jan 1, 0  12:00 AM` for the `DateTime(0)` sentinel, as before) instead of going through `dateFmt`, which blanks years ≤ 1.

## Rework round 2 (controller, after the second SOL review)
- Deliberate normalisation, accepted by the controller: `dateFmt` renders a blank for sentinel years (≤ 1) where the deleted screen-local copies printed `Jan 1, 0`. That is the canonical helper's existing behaviour on main for its 20 importers; a blank beats a bogus date for a `DateTime(0)` fallback, and keeping unguarded copies only for the sentinel case would mean two formatters again. `formatAppointmentDateTime` goes back to `dateFmt` for the same reason.
- `_fmtMoney` null-guard wrappers removed from `scan_review_screen.dart` and `recurring_maintenance_list_screen.dart`; the four call sites inline the null check.
- `accounting_impact_card_test.dart` updated from `$-225.00` to `-$225.00` (the sign-placement change from round 1).
- Trailing blank line at the end of `units_list_screen.dart` removed.
- Round 3: the `_money` wrapper in `tenant_ledger_view.dart` was removed; its four call sites inline the null fallback.
