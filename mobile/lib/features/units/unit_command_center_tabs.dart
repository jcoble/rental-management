enum UnitCommandCenterTab {
  summary,
  leasing,
  tenantLease,
  money,
  maintenance,
  documentsHistory,
}

enum UnitCommandCenterView {
  listing,
  applications,
  agreements,
  residents,
  workOrders,
  turnover,
  documents,
  history,
}

class UnitCommandCenterDestination {
  const UnitCommandCenterDestination({required this.tab, this.view});

  final UnitCommandCenterTab tab;
  final UnitCommandCenterView? view;
}

class UnitCommandCenterRouteTarget {
  const UnitCommandCenterRouteTarget({
    required this.unitId,
    required this.initialTab,
    this.initialView,
  });

  final int unitId;
  final UnitCommandCenterTab initialTab;
  final UnitCommandCenterView? initialView;
}

UnitCommandCenterRouteTarget? parseUnitCommandCenterRoute(String route) {
  final uri = Uri.tryParse(route.trim());
  if (uri == null) return null;

  final segments = uri.pathSegments;
  if (segments.length != 2 || segments.first != 'units') return null;

  final unitId = int.tryParse(segments[1]);
  if (unitId == null || unitId <= 0) return null;

  final destination = unitCommandCenterDestinationFromName(
    uri.queryParameters['tab'],
    uri.queryParameters['view'],
  );
  return UnitCommandCenterRouteTarget(
    unitId: unitId,
    initialTab: destination.tab,
    initialView: destination.view,
  );
}

UnitCommandCenterTab unitCommandCenterTabFromName(String? raw) =>
    unitCommandCenterDestinationFromName(raw, null).tab;

UnitCommandCenterDestination unitCommandCenterDestinationFromName(
  String? rawTab,
  String? rawView,
) {
  final tab = rawTab?.trim().toLowerCase();
  final view = rawView?.trim().toLowerCase();

  UnitCommandCenterDestination canonical(
    UnitCommandCenterTab area,
    UnitCommandCenterView? defaultView,
    Map<String, UnitCommandCenterView> validViews,
  ) {
    return UnitCommandCenterDestination(
      tab: area,
      view: validViews[view] ?? defaultView,
    );
  }

  switch (tab) {
    case 'leasing':
      return canonical(
        UnitCommandCenterTab.leasing,
        UnitCommandCenterView.listing,
        {
          'listing': UnitCommandCenterView.listing,
          'applications': UnitCommandCenterView.applications,
        },
      );
    case 'tenant-lease':
      return canonical(
        UnitCommandCenterTab.tenantLease,
        UnitCommandCenterView.agreements,
        {
          'agreements': UnitCommandCenterView.agreements,
          'residents': UnitCommandCenterView.residents,
        },
      );
    case 'money':
      return const UnitCommandCenterDestination(
        tab: UnitCommandCenterTab.money,
      );
    case 'maintenance':
      return canonical(
        UnitCommandCenterTab.maintenance,
        UnitCommandCenterView.workOrders,
        {
          'work-orders': UnitCommandCenterView.workOrders,
          'turnover': UnitCommandCenterView.turnover,
        },
      );
    case 'documents-history':
      return canonical(
        UnitCommandCenterTab.documentsHistory,
        UnitCommandCenterView.documents,
        {
          'documents': UnitCommandCenterView.documents,
          'history': UnitCommandCenterView.history,
        },
      );
    case 'summary':
    default:
      return const UnitCommandCenterDestination(
        tab: UnitCommandCenterTab.summary,
      );
  }
}
