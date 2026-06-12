/// Maps a server-emitted `actionUrl` to a safe in-app route.
///
/// The server emits paths that match the mobile go_router routes directly
/// (`/payments/{id}`, `/work-orders/{id}`, `/expenses/{id}`, `/scan/{id}`,
/// `/messages/{id}`, plus the section roots `/money`, `/work`, and the
/// notification inbox `/notifications`). Anything outside this allowlist — or a
/// missing/empty url — resolves to `/notifications` so a stray or future
/// action type lands on the inbox rather than a 404 / arbitrary navigation.
///
/// Centralized here so the push tap handler and the inbox tap handler share one
/// allowlist.
library;

/// Known route prefixes that an `actionUrl` may target. Order doesn't matter;
/// these are membership tests, not a switch.
const _allowedPrefixes = <String>[
  '/work-orders/',
  '/payments/',
  '/expenses/',
  '/scan/',
  '/messages/',
  '/notifications/',
];

/// Known exact section roots (no trailing id).
const _allowedExact = <String>{
  '/money',
  '/work',
  '/notifications',
};

/// Resolves [actionUrl] to a route the app can navigate to. Returns
/// `/notifications` for unknown/missing targets.
String resolveNotificationRoute(String? actionUrl) {
  final url = actionUrl?.trim();
  if (url == null || url.isEmpty || !url.startsWith('/')) {
    return '/notifications';
  }

  // Strip any query/hash so prefix matching is stable.
  final path = url.split('?').first.split('#').first;

  if (_allowedExact.contains(path)) return path;

  for (final prefix in _allowedPrefixes) {
    if (path.startsWith(prefix) && path.length > prefix.length) {
      return path;
    }
  }

  return '/notifications';
}
