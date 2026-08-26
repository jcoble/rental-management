import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../units/unit_command_center_screen.dart';
import '../units/unit_navigation.dart';
import '../units/units_repository.dart';
import 'work_order_detail_screen.dart';
import 'work_orders_repository.dart';

class WorkOrderUnitAwareLoaderScreen extends ConsumerWidget {
  const WorkOrderUnitAwareLoaderScreen({super.key, required this.workOrderId});

  final int workOrderId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final detailAsync = ref.watch(workOrderDetailProvider(workOrderId));

    return detailAsync.when(
      loading: () => const _WorkOrderUnitAwareLoading(),
      error: (e, _) => Scaffold(
        appBar: AppBar(title: const Text('Repair')),
        body: Center(
          child: Padding(
            padding: const EdgeInsets.all(24),
            child: Text(
              e is ApiException ? e.message : e.toString(),
              textAlign: TextAlign.center,
              style: TextStyle(color: Theme.of(context).colorScheme.error),
            ),
          ),
        ),
      ),
      data: (detail) {
        final workOrder = detail.workOrder;
        final unitId = workOrder.unitId;
        if (unitId != null) {
          final dashboardAsync = ref.watch(unitDashboardProvider(unitId));
          if (dashboardAsync.isLoading && !dashboardAsync.hasValue) {
            return const _WorkOrderUnitAwareLoading();
          }

          return UnitCommandCenterLoaderScreen(
            unitId: unitId,
            initialTab: UnitCommandCenterTab.maintenance,
            initialView: UnitCommandCenterView.workOrders,
            initialWorkOrder: workOrder,
          );
        }

        return WorkOrderDetailScreen(workOrderId: workOrderId);
      },
    );
  }
}

class _WorkOrderUnitAwareLoading extends StatelessWidget {
  const _WorkOrderUnitAwareLoading();

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colors = theme.colorScheme;

    return Scaffold(
      appBar: AppBar(title: const Text('Repair')),
      body: ListView(
        key: const Key('work-order-unit-aware-loading'),
        padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
        children: [
          Card.filled(
            margin: EdgeInsets.zero,
            color: colors.surfaceContainerHigh,
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Row(
                children: [
                  Container(
                    width: 44,
                    height: 44,
                    decoration: BoxDecoration(
                      color: colors.primaryContainer,
                      borderRadius: BorderRadius.circular(16),
                    ),
                    child: Icon(
                      Icons.home_repair_service_outlined,
                      color: colors.onPrimaryContainer,
                    ),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          'Loading repair',
                          style: theme.textTheme.titleMedium?.copyWith(
                            fontWeight: FontWeight.w700,
                          ),
                        ),
                        const SizedBox(height: 2),
                        Text(
                          'Details, schedule, and activity',
                          style: theme.textTheme.bodySmall?.copyWith(
                            color: colors.onSurfaceVariant,
                          ),
                        ),
                      ],
                    ),
                  ),
                  const SizedBox(width: 12),
                  const SizedBox.square(
                    dimension: 22,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  ),
                ],
              ),
            ),
          ),
          const SizedBox(height: 16),
          Card.filled(
            margin: EdgeInsets.zero,
            color: colors.surfaceContainer,
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    'Repair details',
                    style: theme.textTheme.labelLarge?.copyWith(
                      color: colors.onSurfaceVariant,
                    ),
                  ),
                  const SizedBox(height: 14),
                  Row(
                    children: [
                      Icon(
                        Icons.location_on_outlined,
                        size: 20,
                        color: colors.onSurfaceVariant,
                      ),
                      const SizedBox(width: 10),
                      const Expanded(child: LinearProgressIndicator()),
                    ],
                  ),
                  const SizedBox(height: 16),
                  Row(
                    children: [
                      Icon(
                        Icons.schedule_outlined,
                        size: 20,
                        color: colors.onSurfaceVariant,
                      ),
                      const SizedBox(width: 10),
                      const Expanded(child: LinearProgressIndicator()),
                    ],
                  ),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class WorkOrderShellTargetLoaderScreen extends ConsumerStatefulWidget {
  const WorkOrderShellTargetLoaderScreen({
    super.key,
    required this.workOrderId,
  });

  final int workOrderId;

  @override
  ConsumerState<WorkOrderShellTargetLoaderScreen> createState() =>
      _WorkOrderShellTargetLoaderScreenState();
}

class _WorkOrderShellTargetLoaderScreenState
    extends ConsumerState<WorkOrderShellTargetLoaderScreen> {
  bool _redirected = false;

  @override
  Widget build(BuildContext context) {
    final detailAsync = ref.watch(workOrderDetailProvider(widget.workOrderId));

    return detailAsync.when(
      loading: () => Scaffold(
        appBar: AppBar(title: const Text('Repair')),
        body: const Center(child: CircularProgressIndicator()),
      ),
      error: (e, _) => Scaffold(
        appBar: AppBar(title: const Text('Repair')),
        body: Center(
          child: Padding(
            padding: const EdgeInsets.all(24),
            child: Text(
              e is ApiException ? e.message : e.toString(),
              textAlign: TextAlign.center,
              style: TextStyle(color: Theme.of(context).colorScheme.error),
            ),
          ),
        ),
      ),
      data: (detail) {
        final workOrder = detail.workOrder;
        final unitId = workOrder.unitId;
        if (unitId == null) {
          return WorkOrderDetailScreen(workOrderId: widget.workOrderId);
        }

        if (!_redirected) {
          _redirected = true;
          WidgetsBinding.instance.addPostFrameCallback((_) {
            if (!mounted) return;
            openUnitCommandCenter(
              context,
              unitId: unitId,
              initialTab: UnitCommandCenterTab.maintenance,
              initialView: UnitCommandCenterView.workOrders,
              workOrder: workOrder,
            );
          });
        }

        return Scaffold(
          appBar: AppBar(title: const Text('Repair')),
          body: const Center(child: CircularProgressIndicator()),
        );
      },
    );
  }
}
