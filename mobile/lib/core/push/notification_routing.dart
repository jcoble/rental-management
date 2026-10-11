import '../auth/auth_models.dart';
import 'mobile_navigation_intent.dart';

/// Exhaustively maps the closed intent set to canonical app destinations.
/// Resource kinds and positive identifiers must match the selected destination;
/// malformed combinations return null so callers use the safe fallback.
String? resolveNavigationIntentRoute(MobileNavigationIntent intent) {
  final resource = intent.resource;
  final parent = intent.parentResource;
  return switch (intent.destination) {
    MobileNavigationDestination.home => '/',
    MobileNavigationDestination.notifications => '/notifications',
    MobileNavigationDestination.rentals => '/rentals',
    MobileNavigationDestination.owners => '/owners',
    MobileNavigationDestination.money => '/money',
    MobileNavigationDestination.work => '/work',
    MobileNavigationDestination.inbox => '/inbox',
    MobileNavigationDestination.unitSummary => _unitRoute(resource, 'summary'),
    MobileNavigationDestination.unitTenantLease => _unitRoute(
      resource,
      'tenant-lease',
    ),
    MobileNavigationDestination.unitMoney => _unitRoute(resource, 'money'),
    MobileNavigationDestination.unitMaintenance => _unitRoute(
      resource,
      'maintenance',
    ),
    MobileNavigationDestination.unitRecords => _unitRoute(resource, 'records'),
    MobileNavigationDestination.tenantLedgerEntry => _tenantLedgerRoute(
      intent,
      parent,
      resource,
    ),
    MobileNavigationDestination.expense => _detailRoute(
      '/expenses',
      resource,
      'Expense',
    ),
    MobileNavigationDestination.scanDraft => _detailRoute(
      '/scan',
      resource,
      'ScanDraft',
    ),
    MobileNavigationDestination.message => _detailRoute(
      '/messages',
      resource,
      'Conversation',
    ),
    MobileNavigationDestination.workOrder => _detailRoute(
      '/maintenance',
      resource,
      'WorkOrder',
    ),
    MobileNavigationDestination.technicianWork => _detailRoute(
      '/maintenance/work',
      resource,
      'WorkOrder',
    ),
    MobileNavigationDestination.leasingRental => _detailRoute(
      '/leasing/rentals',
      resource,
      'Unit',
    ),
    MobileNavigationDestination.leasingApplication => _detailRoute(
      '/leasing/applications',
      resource,
      'RentalApplication',
    ),
    MobileNavigationDestination.leasingAppointment => _detailRoute(
      '/leasing/appointments',
      resource,
      'Appointment',
    ),
    MobileNavigationDestination.leasingConversation => _detailRoute(
      '/leasing/conversations',
      resource,
      'Conversation',
    ),
    MobileNavigationDestination.leasingMoveIn => _detailRoute(
      '/leasing/move-ins',
      resource,
      'LeaseManagement',
    ),
    MobileNavigationDestination.tenantAccount => _tenantAccountRoute(
      intent,
      resource,
    ),
  };
}

String? _tenantAccountRoute(
  MobileNavigationIntent intent,
  MobileNavigationResource? resource,
) {
  final accountId = _kindId(resource, 'TenantAccount');
  if (accountId == null || intent.experience != WorkspaceExperience.tenant) {
    return null;
  }
  return '/portal/tenant-accounts/$accountId';
}

String resolveSafeFallbackRoute(MobileNavigationDestination destination) =>
    switch (destination) {
      MobileNavigationDestination.notifications => '/notifications',
      MobileNavigationDestination.home => '/',
      _ => '/',
    };

String? _unitRoute(MobileNavigationResource? resource, String tab) {
  final id = _kindId(resource, 'Unit');
  return id == null ? null : '/units/$id?tab=$tab';
}

String? _detailRoute(
  String prefix,
  MobileNavigationResource? resource,
  String kind,
) {
  final id = _kindId(resource, kind);
  return id == null ? null : '$prefix/$id';
}

int? _kindId(MobileNavigationResource? resource, String kind) =>
    resource != null && resource.kind == kind && resource.id > 0
    ? resource.id
    : null;

String? _tenantLedgerRoute(
  MobileNavigationIntent intent,
  MobileNavigationResource? parent,
  MobileNavigationResource? resource,
) {
  final accountId = _kindId(parent, 'TenantAccount');
  final entryId = _kindId(resource, 'TenantLedgerEntry');
  if (accountId == null || entryId == null) return null;
  return intent.experience == WorkspaceExperience.tenant
      ? '/portal/tenant-accounts/$accountId/entries/$entryId'
      : '/tenant-accounts/$accountId/entries/$entryId';
}
