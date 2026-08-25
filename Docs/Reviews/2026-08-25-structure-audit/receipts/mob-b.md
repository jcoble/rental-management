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
