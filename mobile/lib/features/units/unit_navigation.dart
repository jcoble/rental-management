import 'package:flutter/material.dart';

import '../../core/models/lease.dart';
import '../../core/models/work_order.dart';
import '../applications/applications_models.dart';
import '../home/mobile_domain_navigation.dart';
import 'unit_command_center_screen.dart';

void openUnitCommandCenter(
  BuildContext context, {
  required int unitId,
  UnitCommandCenterTab initialTab = UnitCommandCenterTab.overview,
  Lease? lease,
  RentalApplication? application,
  WorkOrder? workOrder,
  int? tenantId,
}) {
  Widget detailBuilder(BuildContext _) => UnitCommandCenterLoaderScreen(
    unitId: unitId,
    initialTab: initialTab,
    initialLease: lease,
    initialApplication: application,
    initialWorkOrder: workOrder,
    selectedTenantId: tenantId,
  );

  final shellNavigator = mobileShellNavigatorOf(context);
  if (shellNavigator != null) {
    shellNavigator.openTab(
      MobileShellTabId.rentals,
      destination: MobileDestinationId.units,
      detailBuilder: detailBuilder,
    );
    revealMobileShellIfDetached(context);
    return;
  }

  Navigator.of(
    context,
  ).push<void>(MaterialPageRoute<void>(builder: detailBuilder));
}

bool openUnitCommandCenterRoute(BuildContext context, String route) {
  final target = parseUnitCommandCenterRoute(route);
  if (target == null) return false;

  openUnitCommandCenter(
    context,
    unitId: target.unitId,
    initialTab: target.initialTab,
  );
  return true;
}
