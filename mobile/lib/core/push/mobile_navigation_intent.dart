import '../auth/auth_controller.dart';
import '../auth/auth_models.dart';
import 'notification_routing.dart';

/// A short-lived, access-bound request to open a mobile destination.
///
/// Push taps may arrive before authentication. Keeping the route together with
/// its creation time and the authority that received it prevents a stale
/// notification from crossing a workspace switch or capability revision.
class MobileNavigationIntent {
  const MobileNavigationIntent({
    required this.route,
    required this.fallbackRoute,
    required this.createdAtUtc,
    required this.expiresAtUtc,
    this.accessContextId,
    this.accessRevision,
    this.experience,
  });

  final String route;
  final String fallbackRoute;
  final DateTime createdAtUtc;
  final DateTime expiresAtUtc;
  final int? accessContextId;
  final int? accessRevision;
  final WorkspaceExperience? experience;

  factory MobileNavigationIntent.fromNotification({
    required String? actionUrl,
    required DateTime nowUtc,
    AuthStateAuthenticated? authority,
  }) {
    final selected = authority?.access.selectedContext;
    return MobileNavigationIntent(
      route: resolveNotificationRoute(actionUrl),
      fallbackRoute: '/notifications',
      createdAtUtc: nowUtc,
      expiresAtUtc: nowUtc.add(const Duration(minutes: 15)),
      accessContextId: selected?.accessContextId,
      accessRevision: selected?.accessRevision,
      experience: authority?.activeExperience,
    );
  }

  /// Returns the requested route only while its access authority is current.
  /// Cold-start intents have no pre-auth authority and are bound to the first
  /// authenticated context by the shell's normal route capability check.
  String resolveFor(
    AuthStateAuthenticated authority, {
    required DateTime nowUtc,
  }) {
    if (!nowUtc.isBefore(expiresAtUtc)) return fallbackRoute;
    final selected = authority.access.selectedContext;
    if (accessContextId != null &&
        accessContextId != selected.accessContextId) {
      return fallbackRoute;
    }
    if (accessRevision != null && accessRevision != selected.accessRevision) {
      return fallbackRoute;
    }
    if (experience != null && experience != authority.activeExperience) {
      return fallbackRoute;
    }
    return route;
  }
}
