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
