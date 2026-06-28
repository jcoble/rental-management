import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../units/unit_command_center_screen.dart';
import '../units/unit_navigation.dart';
import 'work_order_detail_screen.dart';
import 'work_orders_repository.dart';

class WorkOrderUnitAwareLoaderScreen extends ConsumerWidget {
  const WorkOrderUnitAwareLoaderScreen({super.key, required this.workOrderId});

  final int workOrderId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final detailAsync = ref.watch(workOrderDetailProvider(workOrderId));

    return detailAsync.when(
      loading: () => Scaffold(
        appBar: AppBar(title: const Text('Work order')),
        body: const Center(child: CircularProgressIndicator()),
      ),
      error: (e, _) => Scaffold(
        appBar: AppBar(title: const Text('Work order')),
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
          return UnitCommandCenterLoaderScreen(
            unitId: unitId,
            initialTab: UnitCommandCenterTab.work,
            initialWorkOrder: workOrder,
          );
        }

        return WorkOrderDetailScreen(workOrderId: workOrderId);
      },
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
        appBar: AppBar(title: const Text('Work order')),
        body: const Center(child: CircularProgressIndicator()),
      ),
      error: (e, _) => Scaffold(
        appBar: AppBar(title: const Text('Work order')),
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
              initialTab: UnitCommandCenterTab.work,
              workOrder: workOrder,
            );
          });
        }

        return Scaffold(
          appBar: AppBar(title: const Text('Work order')),
          body: const Center(child: CircularProgressIndicator()),
        );
      },
    );
  }
}
