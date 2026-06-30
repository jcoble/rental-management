enum UnitCommandCenterTab {
  overview,
  listing,
  lease,
  applications,
  ledger,
  tenants,
  work,
}

class UnitCommandCenterRouteTarget {
  const UnitCommandCenterRouteTarget({
    required this.unitId,
    required this.initialTab,
  });

  final int unitId;
  final UnitCommandCenterTab initialTab;
}

UnitCommandCenterRouteTarget? parseUnitCommandCenterRoute(String route) {
  final uri = Uri.tryParse(route.trim());
  if (uri == null) return null;

  final segments = uri.pathSegments;
  if (segments.length != 2 || segments.first != 'units') return null;

  final unitId = int.tryParse(segments[1]);
  if (unitId == null || unitId <= 0) return null;

  return UnitCommandCenterRouteTarget(
    unitId: unitId,
    initialTab: unitCommandCenterTabFromName(uri.queryParameters['tab']),
  );
}

UnitCommandCenterTab unitCommandCenterTabFromName(String? raw) {
  switch (raw?.trim().toLowerCase()) {
    case 'listing':
    case 'listings':
    case 'zillow':
      return UnitCommandCenterTab.listing;
    case 'lease':
    case 'leases':
      return UnitCommandCenterTab.lease;
    case 'app':
    case 'apps':
    case 'application':
    case 'applications':
      return UnitCommandCenterTab.applications;
    case 'ledger':
    case 'rent':
    case 'rents':
    case 'payment':
    case 'payments':
    case 'expense':
    case 'expenses':
      return UnitCommandCenterTab.ledger;
    case 'tenant':
    case 'tenants':
      return UnitCommandCenterTab.tenants;
    case 'maintenance':
    case 'work':
    case 'work-orders':
    case 'workorders':
      return UnitCommandCenterTab.work;
    case 'overview':
    default:
      return UnitCommandCenterTab.overview;
  }
}
