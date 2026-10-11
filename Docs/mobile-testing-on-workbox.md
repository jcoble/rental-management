# Mobile testing from the agent-workbox

## Why there is no emulator here
`agent-workbox` is a KVM guest with no nested virtualisation (`/dev/kvm` absent, no `vmx` CPU flag).
The Android emulator cannot run accelerated, so the owner's phone over Tailscale is the device.

## Physical phone (Android, Tailscale) — verified 2026-08-27
Workbox Tailscale IP: `100.86.236.69`. Phone: `100.73.198.92` (Galaxy S22+, Android 16).

1. Phone: Developer options → Wireless debugging → "Pair device with pairing code".
   `adb pair 100.73.198.92:<pair-port> <code>` then `adb connect 100.73.198.92:<connect-port>`.
   The connect port rotates when the screen turns off — re-read it from the phone.
2. Dev stack must listen on all interfaces:
   `API_HTTPS_URL=https://0.0.0.0:5666 API_HTTP_URL=http://0.0.0.0:5665 ./scripts/start-dev.sh`
3. Build **with the flavor flag** (without `--flavor dev` Gradle produces no APK):
   `flutter build apk --debug --flavor dev --dart-define=FLAVOR=dev --dart-define=API_BASE_URL=https://100.86.236.69:5666/api/v1`
4. If the phone already has `com.rentalcommand.rental_command.dev` signed by another machine,
   `adb install -r` fails with `INSTALL_FAILED_UPDATE_INCOMPATIBLE` — and `flutter run` reports it
   only after the fact while the OLD app keeps running. `adb uninstall com.rentalcommand.rental_command.dev` first.
5. `adb install -r mobile/build/app/outputs/flutter-apk/app-dev-debug.apk`; launch with
   `adb shell monkey -p com.rentalcommand.rental_command.dev -c android.intent.category.LAUNCHER 1`.
   Sophos on the phone shows a "Low reputation app" dialog on first launch — tap Allow.
6. Drive with `adb shell input tap X Y`, screenshot with `adb exec-out screencap -p > ~/Workbox/screenshots/<name>.png`.
   Debug builds accept the mkcert cert (`badCertificateCallback` in `dio_client.dart`).

## Headless path without the phone (Flutter web) — blocked, small fix known
`flutter build web` succeeds, but the app hangs on the splash: `biometric_auth_service.dart:46`
reads `Platform.isAndroid`, unsupported on web (`Unsupported operation: Platform._operatingSystem`).
Guarding it with `kIsWeb` unblocks Flutter-web + Playwright as a headless mobile smoke path.
