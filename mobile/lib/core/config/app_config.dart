// ignore_for_file: do_not_use_environment

/// API base URL resolved at compile time via --dart-define=API_BASE_URL=...
///
/// Defaults to Android emulator loopback (10.0.2.2 → host localhost).
/// For iOS Simulator, pass: --dart-define=API_BASE_URL=https://localhost:5666/api/v1
/// For physical devices, pass the host machine's LAN IP, e.g.:
///   --dart-define=API_BASE_URL=https://192.168.1.x:5666/api/v1
const String kApiBaseUrl = String.fromEnvironment(
  'API_BASE_URL',
  defaultValue: 'https://10.0.2.2:5666/api/v1',
);

/// When true, a custom [BadCertificateCallback] is installed on the HTTP client
/// so that the mkcert self-signed certificate used in local development is
/// accepted. This flag must only ever be true in debug builds; it is guarded by
/// [kDebugMode] at the call site.
const bool kAllowSelfSignedCertInDebug = true;
