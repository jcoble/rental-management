# TSK-782 Biometric Sign-In Discovery Brief

**Goal:** Let a previously signed-in Android user unlock Rental Command with an enrolled fingerprint or other device biometric, while retaining password/Google sign-in as the initial and fallback paths.

## Acceptance criteria

- After a user explicitly enables biometric sign-in, the next cold start keeps the persisted server session locked until the Android system biometric prompt succeeds.
- Cancellation, unavailable hardware, no enrolled biometric, and biometric lockout leave the user on the ordinary sign-in screen with a clear password/Google fallback.
- Initial sign-in still uses the existing server credential/Google flow; the app never stores a password or treats a biometric as new server authority.
- Logout, refresh-token invalidation, or a failed stored-session restore clears biometric eligibility along with the local session.

## Explicit non-goals

- No API, database, web, iOS, passkey, or Credential Manager changes.
- No custom biometric collection, fingerprint storage, or replacement for Android's system prompt.
- No biometric confirmation for individual payments or other high-value commands.
- No cryptographic redesign of the existing `flutter_secure_storage` token envelope.

## Discovery questions

1. Which existing Flutter state and router path owns cold-start session restoration?
2. Where can biometric opt-in and retry controls fit without creating a second authentication authority?
3. Which Android host changes does the current official Flutter plugin require?
4. What focused tests can prove the gate, fallback, opt-out, and platform configuration?

## Bounded investigation

- Inspect: `mobile/lib/core/auth/`, `mobile/lib/core/router/app_router.dart`, `mobile/lib/main.dart`, `mobile/lib/features/auth/login_screen.dart`, `mobile/lib/features/settings/settings_screen.dart`, `mobile/android/app/src/main/`, `mobile/pubspec.yaml`, and focused mobile tests.
- Research: current Android biometric guidance and the Flutter-maintained `local_auth` package/setup documentation.
- Commands allowed: read-only `rg`, `sed`, `find`, `flutter --version`, and official-documentation searches.
- Do not: edit production code, broaden authentication requirements, or change server/session semantics during discovery.

## Stop condition

Discovery ends when the exact state transition, service boundary, Android host files, UI surfaces, tests, and target verification commands are known.

## Discovery evidence

- `mobile/lib/main.dart:103-157` calls `AuthController.restoreSession()` once at startup and holds the splash only while auth is unknown.
- `mobile/lib/core/auth/auth_controller.dart:102-135` restores a persisted access token directly into authenticated state after `/auth/me` and `/auth/access`; this is the smallest place to insert a local biometric lock before server validation.
- `mobile/lib/core/router/app_router.dart:101-145` already routes every non-authenticated state to `/login`, so a distinct biometric-locked auth state can reuse the existing route without adding a route.
- `mobile/lib/core/auth/token_store.dart:7-103` stores only access/refresh tokens and the access envelope in `flutter_secure_storage`; no password is persisted.
- `mobile/lib/core/auth/auth_controller.dart:319-340` and `mobile/lib/core/auth/auth_repository.dart:374-391` own logout and local token deletion, which must also remove the biometric opt-in marker.
- `mobile/lib/features/auth/login_screen.dart:47-95,369-415` owns password and Google fallback actions; it can surface a biometric retry button when state is locked.
- `mobile/lib/features/settings/settings_screen.dart:95-106` already has an Account security section suitable for an explicit biometric toggle.
- `mobile/pubspec.yaml:5-22` uses Dart 3.12 and Flutter 3.44, compatible with the Flutter-maintained `local_auth` 3.0.2 release.
- `mobile/android/app/src/main/kotlin/com/rentalcommand/rental_command/MainActivity.kt:1-5` currently extends `FlutterActivity`; `local_auth_android` requires `FlutterFragmentActivity`.
- `mobile/android/app/src/main/AndroidManifest.xml:1-5` does not declare `android.permission.USE_BIOMETRIC`.
- `mobile/android/app/src/main/res/values/styles.xml:3-17` and `values-night/styles.xml:3-17` use framework themes; `local_auth_android` requires an AppCompat `LaunchTheme` to avoid Android 8-and-lower crashes.
- Android's current guidance says initial device sign-in should remain credential-based and subsequent reauthorization may use `BiometricPrompt`. The Flutter plugin exposes that prompt, enrolled-biometric inspection, biometric-only mode, background retry, and typed lockout/hardware exceptions.
- Research sources: `https://developer.android.com/identity/sign-in/biometric-auth`, `https://developer.android.com/privacy-and-security/keystore`, `https://pub.dev/packages/local_auth`, and `https://pub.dev/packages/local_auth_android`.
