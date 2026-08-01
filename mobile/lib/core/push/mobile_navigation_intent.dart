import '../auth/auth_controller.dart';
import '../auth/auth_models.dart';
import 'notification_routing.dart';

enum MobileNavigationDestination {
  home,
  notifications,
  rentals,
  owners,
  money,
  work,
  inbox,
  unitSummary,
  unitTenantLease,
  unitMoney,
  unitMaintenance,
  unitRecords,
  tenantLedgerEntry,
  expense,
  scanDraft,
  message,
  workOrder,
  technicianWork,
  leasingRental,
  leasingApplication,
  leasingAppointment,
  leasingConversation,
  leasingMoveIn,
  tenantAccount,
}

enum MobileNavigationAction { open, review, resolve }

class MobileNavigationResource {
  const MobileNavigationResource({required this.kind, required this.id});

  final String kind;
  final int id;
}

/// A server-issued, short-lived request to open one member of the closed
/// mobile destination set. It contains identifiers, never a route or URL.
class MobileNavigationIntent {
  const MobileNavigationIntent({
    required this.experience,
    required this.destination,
    required this.accessContextId,
    required this.accessRevision,
    required this.action,
    required this.expiresAtUtc,
    required this.fallbackDestination,
    this.resource,
    this.parentResource,
    this.childResource,
  });

  final WorkspaceExperience experience;
  final MobileNavigationDestination destination;
  final int accessContextId;
  final int accessRevision;
  final MobileNavigationResource? resource;
  final MobileNavigationResource? parentResource;
  final MobileNavigationResource? childResource;
  final MobileNavigationAction action;
  final DateTime expiresAtUtc;
  final MobileNavigationDestination fallbackDestination;

  /// Strictly parses the typed wire contract. Unknown enum values, malformed
  /// resources, non-UTC expiry, and any legacy raw URL fail closed.
  static MobileNavigationIntent? tryParse(Object? value) {
    if (value is! Map || value.containsKey('actionUrl')) return null;
    try {
      final json = Map<String, dynamic>.from(value);
      final experience = _parseExperience(json['experience']);
      final destination = _parseEnum(
        json['destination'],
        MobileNavigationDestination.values,
      );
      final action = _parseEnum(json['action'], MobileNavigationAction.values);
      final fallback = _parseEnum(
        json['fallbackDestination'],
        MobileNavigationDestination.values,
      );
      if (fallback != MobileNavigationDestination.home &&
          fallback != MobileNavigationDestination.notifications) {
        return null;
      }

      final accessContextId = _positiveInt(json['accessContextId']);
      final accessRevision = _positiveInt(json['accessRevision']);
      final expiresAtUtc = DateTime.parse(json['expiresAtUtc'] as String);
      if (!expiresAtUtc.isUtc) return null;

      return MobileNavigationIntent(
        experience: experience,
        destination: destination,
        accessContextId: accessContextId,
        accessRevision: accessRevision,
        resource: _parseResource(json['resource']),
        parentResource: _parseResource(json['parentResource']),
        childResource: _parseResource(json['childResource']),
        action: action,
        expiresAtUtc: expiresAtUtc,
        fallbackDestination: fallback,
      );
    } on Object {
      return null;
    }
  }

  /// Returns a route only while the exact server-validated authority that
  /// received the intent is still active.
  String resolveFor(
    AuthStateAuthenticated authority, {
    required DateTime nowUtc,
  }) {
    final fallback = resolveSafeFallbackRoute(fallbackDestination);
    if (!nowUtc.isBefore(expiresAtUtc)) return fallback;
    final selected = authority.access.selectedContext;
    if (accessContextId != selected.accessContextId ||
        accessRevision != selected.accessRevision ||
        experience != authority.activeExperience) {
      return fallback;
    }
    return resolveNavigationIntentRoute(this) ?? fallback;
  }
}

WorkspaceExperience _parseExperience(Object? value) {
  final normalized = value is String ? value.trim().toLowerCase() : '';
  return WorkspaceExperience.values.singleWhere(
    (experience) => experience.name.toLowerCase() == normalized,
  );
}

T _parseEnum<T extends Enum>(Object? value, List<T> values) {
  final normalized = value is String ? value.trim().toLowerCase() : '';
  return values.singleWhere(
    (candidate) => candidate.name.toLowerCase() == normalized,
  );
}

int _positiveInt(Object? value) {
  final result = switch (value) {
    int integer => integer,
    String text => int.parse(text),
    _ => throw const FormatException('Expected integer identifier.'),
  };
  if (result <= 0) throw const FormatException('Expected positive identifier.');
  return result;
}

MobileNavigationResource? _parseResource(Object? value) {
  if (value == null) return null;
  if (value is! Map) throw const FormatException('Malformed resource.');
  final json = Map<String, dynamic>.from(value);
  final kind = json['kind'] is String ? (json['kind'] as String).trim() : '';
  if (kind.isEmpty) throw const FormatException('Missing resource kind.');
  return MobileNavigationResource(kind: kind, id: _positiveInt(json['id']));
}
