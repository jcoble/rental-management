import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:rental_command/core/models/work_order.dart';
import 'package:rental_command/features/home/mobile_domain_navigation.dart';
import 'package:rental_command/features/maintenance/work_order_unit_aware_loader.dart';
import 'package:rental_command/features/maintenance/work_orders_repository.dart';
import 'package:rental_command/features/units/unit_command_center_screen.dart';

void main() {
  tearDown(() {
    final current = MobileShellNavigationRegistry.current;
    if (current != null) {
      MobileShellNavigationRegistry.detach(current);
    }
  });

  testWidgets(
    'unit-scoped work-order deep links redirect to Rentals > Units > Work',
    (tester) async {
      final calls =
          <
            ({
              MobileShellTabId tab,
              MobileDestinationId? destination,
              MobileDetailBuilder? detailBuilder,
            })
          >[];
      late BuildContext hostContext;

      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            workOrdersRepositoryProvider.overrideWithValue(
              _FakeWorkOrdersRepository(
                detail: WorkOrderDetail(
                  workOrder: _workOrder(id: 17, unitId: 42),
                  timeline: const [],
                ),
              ),
            ),
          ],
          child: MaterialApp(
            home: MobileShellNavigation(
              controller: MobileShellNavigator(
                openTab: (tab, {destination, detailBuilder}) {
                  calls.add((
                    tab: tab,
                    destination: destination,
                    detailBuilder: detailBuilder,
                  ));
                },
                openRoute: (_) => false,
              ),
              child: Builder(
                builder: (context) {
                  hostContext = context;
                  return const WorkOrderShellTargetLoaderScreen(
                    workOrderId: 17,
                  );
                },
              ),
            ),
          ),
        ),
      );

      await tester.pump();
      await tester.pump();
      await tester.pump();

      expect(calls, hasLength(1));
      expect(calls.single.tab, MobileShellTabId.rentals);
      expect(calls.single.destination, MobileDestinationId.units);

      final detail = calls.single.detailBuilder!(hostContext);
      expect(detail, isA<UnitCommandCenterLoaderScreen>());
      final loader = detail as UnitCommandCenterLoaderScreen;
      expect(loader.unitId, 42);
      expect(loader.initialTab, UnitCommandCenterTab.maintenance);
      expect(loader.initialView, UnitCommandCenterView.workOrders);
      expect(loader.initialWorkOrder?.id, 17);
    },
  );
}

class _FakeWorkOrdersRepository extends WorkOrdersRepository {
  _FakeWorkOrdersRepository({required this.detail}) : super(Dio());

  final WorkOrderDetail detail;

  @override
  Future<WorkOrderDetail> getWorkOrderDetail(int id) async {
    return detail;
  }
}

WorkOrder _workOrder({required int id, required int? unitId}) {
  return WorkOrder(
    id: id,
    portfolioId: 1,
    propertyId: 7,
    unitId: unitId,
    title: 'Leaky sink',
    description: 'Kitchen sink is leaking.',
    category: 'Plumbing',
    priority: 'High',
    status: 'InProgress',
    requestedAt: DateTime(2026),
    updatedAt: DateTime(2026),
  );
}
