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
