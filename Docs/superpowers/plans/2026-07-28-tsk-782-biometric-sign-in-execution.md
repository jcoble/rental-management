# TSK-782 Biometric Sign-In Execution Plan

**Goal:** Let a previously signed-in Android user unlock Rental Command with an enrolled device biometric without storing a password or bypassing the existing server session.
**Source brief:** `Docs/superpowers/plans/2026-07-28-tsk-782-biometric-sign-in-discovery.md`
**Active goal:** TSK-782
**Plan state:** Complete

## Contract

### Acceptance criteria

- AC-1: Android exposes a system biometric prompt through the Flutter-maintained plugin and reports supported, not-enrolled, unavailable, cancellation, and lockout outcomes without collecting biometric data.
- AC-2: An opted-in persisted session remains locked on cold start until biometric success; success revalidates the existing server session, while failure preserves password/Google fallback and invalid sessions clear biometric eligibility.
- AC-3: A signed-in user can explicitly enable or disable biometric sign-in in Account settings, and the login screen exposes an automatic prompt plus a retry action only for a biometric-locked session.
- AC-4: The flow passes focused Flutter tests, Android host checks/build validation, and real Android UI proof.

### Explicit non-goals

- API, database, web, iOS, passkey, Credential Manager, per-transaction biometric confirmation, and token-envelope cryptographic redesign.

### Deferred items

- Binding the refresh-token ciphertext to an auth-per-use Android Keystore key would require a native token-storage redesign because refresh rotation can occur after the biometric operation. It is not required for this local app-entry gate.

### UI proof

- UI impact: Yes — login and Account settings gain biometric controls and the native system prompt.
- Supported scenario: sign in normally, enable biometric sign-in, relaunch, unlock with the Android system prompt, then disable or log out and confirm the biometric path is removed.
- Required target: connected physical Android phone when available; Android emulator is the fallback.
- Proof artifacts: `Docs/Testing/TSK-782/` screenshots and a short verification log.

## Task 1 — Add the Android biometric capability boundary

**Status:** Complete
**Allowed files:**
- Modify: `mobile/pubspec.yaml`
- Modify: `mobile/pubspec.lock`
- Create: `mobile/lib/core/auth/biometric_auth_service.dart`
- Modify: `mobile/android/app/src/main/AndroidManifest.xml`
- Modify: `mobile/android/app/src/main/kotlin/com/rentalcommand/rental_command/MainActivity.kt`
- Modify: `mobile/android/app/src/main/res/values/styles.xml`
- Modify: `mobile/android/app/src/main/res/values-night/styles.xml`
- Create/Test: `mobile/test/biometric_auth_service_test.dart`

**Acceptance:** AC-1

1. [x] Add failing service tests for capability, enrollment, success, cancellation, and lockout mapping.
2. [x] Add `local_auth`, an injectable Android-only service, secure opt-in marker, and the plugin-required host configuration.
3. [x] Run: `flutter test test/biometric_auth_service_test.dart`; expect: all tests pass.
4. [x] Run `relevance-reviewer` against Task 1; require `RELEVANCE PASS`.

## Task 2 — Gate stored-session restoration behind biometric success

**Status:** Complete
**Allowed files:**
- Modify: `mobile/lib/core/auth/auth_controller.dart`
- Create/Test: `mobile/test/biometric_auth_controller_test.dart`
- Modify/Test: `mobile/test/auth_repository_test.dart` only if its `TokenStore` fake requires a new interface member.

**Acceptance:** AC-2

1. [x] Add failing controller tests for cold-start lock, success revalidation, cancel/failure fallback, invalid-session cleanup, and logout cleanup.
2. [x] Add a biometric-locked auth state and unlock transition without changing credential/Google server authentication.
3. [x] Run: `flutter test test/biometric_auth_controller_test.dart test/auth_repository_test.dart`; expect: all tests pass.
4. [x] Run `relevance-reviewer` against Task 2; require `RELEVANCE PASS`.

## Task 3 — Add opt-in settings and login retry UI

**Status:** Complete
**Allowed files:**
- Modify: `mobile/lib/features/auth/login_screen.dart`
- Modify: `mobile/lib/features/settings/settings_screen.dart`
- Create/Test: `mobile/test/biometric_login_screen_test.dart`
- Create/Test: `mobile/test/biometric_settings_test.dart`

**Acceptance:** AC-3

1. [x] Add failing widget tests for visible locked-session affordance, automatic single prompt, retry, enable confirmation, disable, and unavailable/not-enrolled copy.
2. [x] Add the smallest Account settings control and login-screen biometric affordance.
3. [x] Run: `flutter test test/biometric_login_screen_test.dart test/biometric_settings_test.dart`; expect: all tests pass.
4. [x] Run `relevance-reviewer` against Task 3; require `RELEVANCE PASS`.

## Task 4 — Validate Android and prove the real flow

**Status:** Complete
**Allowed files:**
- Create: `Docs/Testing/TSK-782/verification.md`
- Create: `Docs/Testing/TSK-782/*.png`

**Acceptance:** AC-4

1. [x] Run full `flutter analyze` and a scoped analysis of the task-owned login files. The scoped analysis reported no issues; the full repository reported 36 pre-existing diagnostics outside TSK-782.
2. [x] Run: `flutter test test/biometric_auth_service_test.dart test/biometric_auth_controller_test.dart test/biometric_login_screen_test.dart test/biometric_settings_test.dart test/auth_repository_test.dart`; result: `+28: All tests passed!`.
3. [x] Build and install the production-flavor Android debug APK sequentially, then execute the declared supported flow on the physical Galaxy S22.
4. [x] Run `ui-proof-verifier`; result: `UI PROOF PASS`.
5. [x] Run final `relevance-reviewer`; result: `RELEVANCE PASS`.

## Completion gate

- [x] Every changed file maps to a task above.
- [x] Every acceptance criterion has fresh evidence.
- [x] No deferred item was implemented.
- [x] Final relevance review returned `RELEVANCE PASS`.
- [x] `ui-proof-verifier` returned `UI PROOF PASS`; every verified UI defect task is closed with fresh proof.
