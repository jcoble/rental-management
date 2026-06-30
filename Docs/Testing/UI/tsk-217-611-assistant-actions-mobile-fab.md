# TSK-217 + TSK-611 UI Verification

## Scope

- TSK-217: Assistant action mode can draft a write operation, requires explicit write mode/confirmation, and does not treat read-only questions as write actions.
- TSK-611: Mobile lower-right quick-action FAB pattern exposes Chat, Record, and Scan while preserving an existing page action as the first menu item.

## Environment

- Branch: `cdx/tsk-217-611-agentic-actions-fab`
- Web: `https://localhost:5767`
- API: `https://localhost:5766`
- Seeded dev admin: `admin@rentalcommand.local` / `Admin123!`
- Use the repo `scripts/start-dev.sh` launcher so Development user-secrets remain available for email, Google auth, and AI provider configuration.

## Web Assistant Happy Path

1. Open `/ai`.
2. Sign in with the seeded dev admin if redirected to `/login`.
3. Turn on `Action mode`.
4. Send a write-intent prompt such as `Log a $42 repairs expense for Eastland`.
5. Expected:
   - Assistant returns a review/draft card, not a freeform answer.
   - The card explains that write mode/confirmation is required.
   - The final create action is not silently executed before confirmation.

Verified on 2026-06-30 against the local stack:

- Logged in through `/login?redirectTo=%2Fai` using `Fill dev login (admin)`.
- Enabled `Action mode`.
- Sent `Create a 42 dollar plumbing expense for Eastland 8-Plex Unit 2 paid today.`
- Confirmed the rendered draft with `Confirm & create expense`.
- Browser result: `Created expense #1047 for $42.00.`

## Web Assistant Read-Only Path

1. Keep `Action mode` on.
2. Send a read-only prompt such as `Who is late on rent?`.
3. Expected:
   - The prompt stays in normal Q&A behavior or returns an unsupported-action message.
   - No create/confirm action card is shown for a read-only question.

## Mobile FAB Widget Coverage

1. Run the Flutter widget test for the shared FAB.
2. Expected:
   - Closed state hides `Chat`, `Record`, and `Scan`.
   - Opening the menu reveals all three actions.
   - A page primary action, such as `New work order`, remains in the menu with the three global actions.

## Unhappy / Regression Checks

- App pages with an existing FAB should not render a second standalone FAB; they should use `MobileQuickActionFab`.
- Tenant-only hidden-FAB behavior for messages remains preserved.
- No standalone `FloatingActionButton` definitions remain under `mobile/lib/features`.

Verified commands:

```bash
flutter analyze lib/features/home/mobile_quick_action_fab.dart lib/features/home/mobile_quick_action_helpers.dart lib/features/ai/ai_models.dart lib/features/ai/ai_repository.dart lib/features/ai/qa_screen.dart test/mobile_quick_action_fab_test.dart test/ai_action_models_test.dart
flutter test test/mobile_quick_action_fab_test.dart test/ai_action_models_test.dart
```
