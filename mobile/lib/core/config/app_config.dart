// ignore_for_file: do_not_use_environment

import 'package:flutter/foundation.dart';

/// Build flavor for the API the app talks to.
///
/// Selected at compile time via `--dart-define=FLAVOR=dev|prod`. A **release**
/// build with no `FLAVOR` defined defaults to **prod** (the live API) so a
/// shipped APK tracks production without a bespoke build; a **debug** build with
/// no `FLAVOR` defaults to **dev** (the local mkcert API on the emulator
/// loopback) so day-to-day development needs no extra flags.
enum AppFlavor { dev, prod }

class AppConfig {
  AppConfig._();

  /// Production API base — the live deployment (`VITE_API_URL=/api/v1` on web).
  static const String prodApiBaseUrl = 'https://rc.coblesolutions.com/api/v1';

  /// Default dev API base — Android emulator loopback (10.0.2.2 → host).
  /// iOS Simulator should pass `--dart-define=API_BASE_URL=https://localhost:5666/api/v1`;
  /// a physical device should pass the host machine's LAN IP.
  static const String devApiBaseUrl = 'https://10.0.2.2:5666/api/v1';

  static const String _flavorName = String.fromEnvironment('FLAVOR');

  /// Optional explicit override (wins over the flavor default when non-empty).
  /// Useful for pointing a debug build at a LAN IP or staging without changing
  /// the flavor.
  static const String _explicitApiBaseUrl = String.fromEnvironment('API_BASE_URL');

  /// A debug-only runtime override set from the in-app server switcher. When
  /// set it takes precedence over every compile-time value. Never honored in
  /// release builds (the server is pinned to the flavor default there).
  static String? _debugRuntimeOverride;

  /// Resolved build flavor.
  static AppFlavor get flavor {
    switch (_flavorName.toLowerCase()) {
      case 'prod':
        return AppFlavor.prod;
      case 'dev':
        return AppFlavor.dev;
      default:
        // No explicit flavor: release → prod, debug/profile → dev.
        return kReleaseMode ? AppFlavor.prod : AppFlavor.dev;
    }
  }

  /// The API base URL the app should call.
  static String get apiBaseUrl {
    if (!kReleaseMode &&
        _debugRuntimeOverride != null &&
        _debugRuntimeOverride!.trim().isNotEmpty) {
      return _debugRuntimeOverride!.trim();
    }
    if (_explicitApiBaseUrl.isNotEmpty) {
      return _explicitApiBaseUrl;
    }
    return flavor == AppFlavor.prod ? prodApiBaseUrl : devApiBaseUrl;
  }

  /// Sets a debug-only API base override (in-app server switcher). No-op in
  /// release builds, where the API is pinned to the flavor default.
  static void setDebugApiBaseUrl(String? url) {
    if (kReleaseMode) return;
    _debugRuntimeOverride = url;
  }

  static String? get debugApiBaseUrlOverride => _debugRuntimeOverride;

  /// True when the app is allowed to accept the mkcert self-signed certificate.
  /// Only ever honored at the call site under `!kReleaseMode`.
  static bool get allowSelfSignedCertInDebug => true;
}

/// Back-compat shim: existing call sites read [kApiBaseUrl]. Resolves through
/// [AppConfig.apiBaseUrl] so the flavor + runtime override take effect.
String get kApiBaseUrl => AppConfig.apiBaseUrl;

/// Back-compat shim for the mkcert bypass flag.
bool get kAllowSelfSignedCertInDebug => AppConfig.allowSelfSignedCertInDebug;
