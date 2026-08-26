# Mobile bug-fix lane: bug-b

Started: 2026-08-25T20:22:16-04:00

## Findings

- #2 — FIXED in `cd374236`. `BankingRepository` now retains one operation key across a failed attempt and its retry. Added `mobile/test/banking_repository_test.dart` test `failed banking mutation retry reuses its operation key`. Result: passed in the full `flutter test` run.
- #3 — FIXED in `cd374236`. Review-card actions disable while their repository future is pending. Added `mobile/test/banking_repository_test.dart` widget test `review card ignores a second tap while save is in flight`. Result: passed in the full `flutter test` run.
- #35 — FIXED in `cd374236`. The transactions response retains `totalCount` and the screen shows `Showing 50 of N` when results are truncated. Added `mobile/test/banking_repository_test.dart` test `transactions retain the server total count`. Result: passed in the full `flutter test` run.
- #7 — FIXED in `865ef732`. Successfully approved notice ids are excluded from retries and partial failures report the sent count. Added `mobile/test/create_tenant_notice_test.dart` widget test `retry after partial notice failure sends only the unsent draft`. Result: passed in the full `flutter test` run.
- #11 — FIXED in `2a35d126`. Empty refresh-cookie values are treated as absent and the stored refresh token is retained. Added `mobile/test/auth_interceptor_test.dart` test `clearing refresh cookie preserves the stored refresh token`. Result: passed in the full `flutter test` run.
- #33 — FIXED in `f37265ce`. Event delivery ignores a closed controller, disposal disconnects before closing, and connect handles a concurrent disconnect. Added `mobile/test/signalr_service_test.dart` test `event arriving after dispose is ignored`. Result: passed in the full `flutter test` run.

## Verification

- `cd mobile && flutter analyze`: underlying Flutter exit code 1 with the 23 documented pre-existing issues; final line `23 issues found. (ran in 1.7s)`. No new issue was reported. The only issue in a touched file is the pre-existing `curly_braces_in_flow_control_structures` info at `lib/features/banking/banking_screen.dart:44`.
- `cd mobile && flutter test`: exit code 0; final line `00:37 +528: All tests passed!`.

## Scope notes

- Nothing else was changed.
