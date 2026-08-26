# Mobile bug-fix lane: bug-a

- Started: 2026-08-25T20:22:23-04:00
- Scope: findings 1, 22, 4, 6, 12, 13, and 26, including the `app_clock` root cause.

## Results

- Finding #1 — `73c79a91`; added appointment response-localization and request-offset behavioral tests; passed.
- Finding #22 — `73c79a91`; added end-before-start widget validation test; passed.
- Finding #4 and `app_clock` root cause — `0e1c862a`; extended the clock fallback test and added the tenant-ledger UTC month-range test; passed.
- Finding #6 — `08b8f3ab`; added DST fall-back renewal-start test; passed with `TZ=America/New_York`.
- Finding #12 — `6fd11941`; added UTC money-period widget test; passed. The preset builders already used `DateTime.now().toUtc()`, so the permitted small-diff fallback was retained after the root clock-kind fix; custom dates now normalize to UTC date-only values.
- Finding #13 — `6fd11941`; added custom-range cancellation widget test; passed.
- Finding #26 — `b85ac7f5`; added scan response local-time model test with a fixed-offset date comparison; passed.

## Verification

- `flutter analyze` — exit 0; `23 issues found. (ran in 1.6s)`. No new diagnostics. One pre-existing info remains in a touched file: `lib/features/leases/successor_agreement_sheet.dart:425:23` (`deprecated_member_use`).
- `flutter test` — exit 0; `00:38 +530: All tests passed!`

## Review fix

- Started: 2026-08-25 (bug-a REWORK round 1)
- Finding BLOCKER picker defaults — `ac1764b7`; added `appointment picker defaults to the local calendar date`; failed before the fix with August 26 and passed after the four picker defaults used local time.
- `appNowProvider` consumer sweep:
  - `features/appointments/appointments_screen.dart`: two date-picker defaults; convert the provider instant to local time.
  - `features/maintenance/create_work_order_sheet.dart`: one date-picker default; convert the provider instant to local time. The other consumer initializes a time picker and already calls `toLocal()`.
  - `features/maintenance/work_order_detail_screen.dart`: one date-picker default; convert the provider instant to local time. The other consumer initializes a time picker and already calls `toLocal()`.
  - `features/payments/payments_screen.dart`, `features/money/record_payment_sheet.dart`, `features/money/expense_form_sheet.dart`, `features/money/one_time_charge_sheet.dart`, `features/money/recurring_charge_sheet.dart`, and `features/money/tenant_credit_sheet.dart`: provider values initialize stored business-date fields; retain UTC business-calendar behavior.
  - `features/money/tenant_ledger_view.dart`: provider values feed the explicit business-date calculation; retain UTC.
  - `features/home/home_shell.dart`: watches and refreshes the shared clock; no picker default.
  - `features/owner_reports/owner_reports_screen.dart`: uses the provider for report business dates; no picker default directly consumes it.
- Finding REWORK renewal term end — `9370ce3e`; added `lease renewal preserves calendar-day term length across DST`; failed before the fix with `2025-12-27 22:00:00.000` and passed after calendar-day arithmetic produced `2025-12-28 00:00:00.000`.
- Finding REWORK scan rendering — `04f55509`; replaced the detached fixed-offset assertion with `scan list renders the local calendar day`, which renders the actual model through `ScanListScreen`; passed with `TZ=America/New_York`.

### Review-fix verification

- `flutter analyze` — exit 1; `23 issues found. (ran in 2.8s)`. No new diagnostics. The only diagnostic in a touched file is the pre-existing `deprecated_member_use` info at `lib/features/leases/successor_agreement_sheet.dart:436:23`.
- `flutter test` — exit 0; `00:47 +532: All tests passed!`
- `TZ=America/New_York flutter test test/date_time_bug_fixes_test.dart` — exit 0; `00:02 +10: All tests passed!`
