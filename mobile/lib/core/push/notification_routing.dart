/// Maps a server-emitted `actionUrl` to a safe in-app route.
///
/// The server usually emits paths that match mobile go_router routes directly
/// (`/tenant-accounts/{accountId}/entries/{entryId}`, `/work-orders/{id}`,
/// `/expenses/{id}`, `/scan/{id}`,
/// `/messages/{id}`, plus section roots). Anything outside the canonical
/// allowlist resolves to `/notifications` so a stale or future action type
/// lands on the inbox rather than creating a second route contract.
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
  '/leasing/rentals/',
  '/leasing/applications/',
  '/leasing/appointments/',
  '/leasing/conversations/',
  '/leasing/move-ins/',
  '/technician/assignments/',
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
