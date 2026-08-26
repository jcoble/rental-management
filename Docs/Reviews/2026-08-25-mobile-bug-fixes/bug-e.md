# Mobile bug-fix lane: bug-e

Start time: 2026-08-25T20:36:44-04:00

## Findings

- #9 — FIXED in `171d97f5`. `executeAction()` captures the assistant turn index before awaiting and blocks new questions during execution. Added `action execution blocks a newer answer from replacing its turn` in `mobile/test/qa_screen_concurrency_test.dart`; targeted and full suites passed.
- #28 — FIXED in `407ab428`. Delivery state records the delivered turn index, renders the note only on that turn, and blocks new questions while delivery is active. Added `delivery stays attached to its answer while delivery is active` in `mobile/test/qa_screen_concurrency_test.dart`; targeted and full suites passed.
- #29 — FIXED in `171d97f5`. Non-`Created` execution responses replace the target turn text and clear the action draft. Added `terminal action response removes the dead confirm card` in `mobile/test/qa_screen_concurrency_test.dart`; targeted and full suites passed.
- #10 — FIXED in `0a17b2ef`. Conversation loading moved from provider construction to each detail-screen mount. Added `opening message detail twice reloads the conversation` in `mobile/test/message_detail_composer_test.dart`; the mock repository received exactly two loads and the full suite passed.
- #31 — FIXED in `bf69d754`. `flutter_tts` 4.2.5 routes native `speak.onCancel` events to the registered cancel handler; Android emits that event from `onStop`, and iOS emits it from `didCancel`. `_speaking = true` now runs after `await _tts.stop()`. No automated test added because the behavior is implemented by platform plugin callbacks; package-source inspection and the full suite passed.

## Verification

- `cd mobile && flutter analyze` — exit 1 with the repository's 23 pre-existing issues; no issue was reported in any touched file. Last line: `23 issues found. (ran in 11.3s)`.
- `cd mobile && flutter test` — exit 0. Last line: `01:15 +532: All tests passed!`.

## Review fix

- #10 — FIXED in `83c9097d`. Restored automatic conversation loading when the provider rebuilds while preserving the detail screen's refresh-on-mount. Extended the existing test as `opening and invalidating message detail reloads conversation`; invalidation performed another repository load and rendered the loaded message instead of leaving the spinner active.
- `cd mobile && flutter analyze` — exit 1 with the repository's 23 pre-existing issues; no issue was reported in either touched Dart file. Last line: `23 issues found. (ran in 2.1s)`.
- `cd mobile && flutter test` — exit 0. Last line: `01:07 +532: All tests passed!`.
