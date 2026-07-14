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
    case 'tenantlease':
      return canonical(
        UnitCommandCenterTab.tenantLease,
        UnitCommandCenterView.agreements,
        {
          'agreements': UnitCommandCenterView.agreements,
          'lease': UnitCommandCenterView.agreements,
          'residents': UnitCommandCenterView.residents,
          'tenants': UnitCommandCenterView.residents,
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
          'work': UnitCommandCenterView.workOrders,
          'turnover': UnitCommandCenterView.turnover,
        },
      );
    case 'documents-history':
    case 'documentshistory':
      return canonical(
        UnitCommandCenterTab.documentsHistory,
        UnitCommandCenterView.documents,
        {
          'documents': UnitCommandCenterView.documents,
          'history': UnitCommandCenterView.history,
          'timeline': UnitCommandCenterView.history,
        },
      );
    case 'listing':
    case 'listings':
    case 'zillow':
      return const UnitCommandCenterDestination(
        tab: UnitCommandCenterTab.leasing,
        view: UnitCommandCenterView.listing,
      );
    case 'app':
    case 'apps':
    case 'application':
    case 'applications':
      return const UnitCommandCenterDestination(
        tab: UnitCommandCenterTab.leasing,
        view: UnitCommandCenterView.applications,
      );
    case 'lease':
    case 'leases':
      return const UnitCommandCenterDestination(
        tab: UnitCommandCenterTab.tenantLease,
        view: UnitCommandCenterView.agreements,
      );
    case 'tenant':
    case 'tenants':
      return const UnitCommandCenterDestination(
        tab: UnitCommandCenterTab.tenantLease,
        view: UnitCommandCenterView.residents,
      );
    case 'ledger':
    case 'rent':
    case 'rents':
    case 'payment':
    case 'payments':
    case 'expense':
    case 'expenses':
      return const UnitCommandCenterDestination(
        tab: UnitCommandCenterTab.money,
      );
    case 'work':
    case 'work-orders':
    case 'workorders':
      return const UnitCommandCenterDestination(
        tab: UnitCommandCenterTab.maintenance,
        view: UnitCommandCenterView.workOrders,
      );
    case 'turnover':
    case 'make-ready':
    case 'makeready':
    case 'move-out':
    case 'moveout':
      return const UnitCommandCenterDestination(
        tab: UnitCommandCenterTab.maintenance,
        view: UnitCommandCenterView.turnover,
      );
    case 'documents':
      return const UnitCommandCenterDestination(
        tab: UnitCommandCenterTab.documentsHistory,
        view: UnitCommandCenterView.documents,
      );
    case 'timeline':
    case 'history':
      return const UnitCommandCenterDestination(
        tab: UnitCommandCenterTab.documentsHistory,
        view: UnitCommandCenterView.history,
      );
    case 'summary':
    case 'overview':
    default:
      return const UnitCommandCenterDestination(
        tab: UnitCommandCenterTab.summary,
      );
  }
}
