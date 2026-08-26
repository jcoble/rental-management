# Mobile bug-fix receipt: bug-c

- Lane: `bug-c`
- Started: `2026-08-25T20:22:28-04:00`

## Findings

- #5 — FIXED in `0cb1fca3`. `owner_reports_repository.dart` now decodes the endpoint's bare array. Test: `owner distributions parse the bare-array response` — passed.
- #8 — SKIPPED. The API DTO permits nulls, but `PropertyTenantCrudRule.cs:692-694` treats each null as "unchanged" and exposes no clear flags. A mobile-only payload cannot clear these fields.
- #14 — SKIPPED. `WorkOrderDtos.cs:639-643` exposes nullable costs without clear flags and the update contract documents null as unchanged; `WorkOrderService.cs:483` forwards only the nullable values. A mobile-only payload cannot clear costs.
- #15 — FIXED in `0f1a254a`. Missing responsibility candidates now show the existing work-order error snackbar instead of throwing. Test: `missing current assignee candidate shows an error` — passed.
- #36 — FIXED in `0f1a254a`. Active dispatches now suppress the second-dispatch action. Covered by the focused work-order widget suite — passed.
- #18 — FIXED in `7f21501f`. The Today date instant is converted to local time once before greeting/date formatting. Test: `UTC clock is converted to local time for the greeting` — passed.
- #19 — FIXED in `7f21501f`. Pull-to-refresh invalidates briefing, messages, field queue, money, and clock providers and awaits the briefing reload. Focused home-shell widget test — passed.
- #25 — SKIPPED as not reproducible in the current shell. Non-management experiences return their dedicated landing screens at `home_shell.dart:857-928`; the remaining empty landlord-tab case returns `MobileAccessDeniedScreen` before indexing at `home_shell.dart:935-945`.
- #20 — FIXED in `e409a1bb`. Appointment status changes and deletes refresh `appointmentsProvider`. Focused suite — passed.
- #21 — FIXED in `e409a1bb`. Application mutations now invalidate the same list families as delete. Focused suite — passed.
- #23 — FIXED in `e409a1bb`. Tenant edit refresh now invalidates both tenant list providers. Focused suite — passed.
- #24 — FIXED in `6d165fbd`. Generate, approve, and dismiss surface `ApiException.message`. Test: `approve failure shows the API message` — passed.
- #34 — FIXED in `35fdcdfb`. Deposit pagination converts API failures into an `AsyncError`. Test: `deposit load more surfaces API failures` — passed.

## Focused verification

- `flutter test test/owner_reports_monthly_contract_test.dart test/work_order_detail_edit_tabs_test.dart test/deposits_repository_test.dart test/notices_screen_error_test.dart test/home_shell_regression_test.dart` — exit 0; `00:02 +19: All tests passed!`
