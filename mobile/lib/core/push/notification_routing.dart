/// Maps a server-emitted `actionUrl` to a safe in-app route.
///
/// The server usually emits paths that match mobile go_router routes directly
/// (`/tenant-accounts/{accountId}/entries/{entryId}`, `/work-orders/{id}`,
/// `/expenses/{id}`, `/scan/{id}`,
/// `/messages/{id}`, plus section roots), but older notification emitters still
/// use query ids such as `/messages?conversationId=...`. Those are normalized
/// here before allowlist checks. Anything outside the allowlist resolves to
/// `/notifications` so a stray or future action type lands on the inbox rather
/// than a 404 / arbitrary navigation.
///
/// Centralized here so the push tap handler and the inbox tap handler share one
/// allowlist.
library;

/// Known route prefixes that an `actionUrl` may target. Order doesn't matter;
/// these are membership tests, not a switch.
const _allowedPrefixes = <String>[
  '/work-orders/',
  '/expenses/',
  '/scan/',
  '/messages/',
  '/units/',
  '/notifications/',
];

/// Known exact section roots (no trailing id).
const _allowedExact = <String>{
  '/money',
  '/work',
  '/rentals',
  '/owners',
  '/units',
  '/inbox',
  '/notifications',
};

/// Resolves [actionUrl] to a route the app can navigate to. Returns
/// `/notifications` for unknown/missing targets.
String resolveNotificationRoute(String? actionUrl) {
  final url = actionUrl?.trim();
  if (url == null || url.isEmpty || !url.startsWith('/')) {
    return '/notifications';
  }

  final uri = Uri.tryParse(url);
  if (uri == null) return '/notifications';

  final normalized = _normalizeQueryRoute(uri);
  if (normalized != null) return normalized;

  final path = uri.path;

  if (_allowedExact.contains(path)) return path;

  if (RegExp(r'^/tenant-accounts/[1-9]\d*/entries/[1-9]\d*$').hasMatch(path)) {
    return path;
  }

  for (final prefix in _allowedPrefixes) {
    if (path.startsWith(prefix) && path.length > prefix.length) {
      return uri.hasQuery ? '$path?${uri.query}' : path;
    }
  }

  return '/notifications';
}

String? _normalizeQueryRoute(Uri uri) {
  switch (uri.path) {
    case '/messages':
      return _detailRoute('/messages', uri.queryParameters['conversationId']);
    case '/work-orders':
      return _detailRoute('/work-orders', uri.queryParameters['workOrderId']);
    default:
      return null;
  }
}

String? _detailRoute(String prefix, String? rawId) {
  final id = rawId?.trim();
  if (id == null || !RegExp(r'^[1-9]\d*$').hasMatch(id)) {
    return null;
  }
  return '$prefix/$id';
}
