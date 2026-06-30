import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/models.dart';
import '../home/mobile_domain_chrome.dart';
import '../units/unit_command_center_screen.dart';
import '../units/unit_navigation.dart';
import 'create_work_order_sheet.dart';
import 'work_order_detail_screen.dart';
import 'work_orders_repository.dart';

// ── Helpers ───────────────────────────────────────────────────────────────────

Color _priorityColor(String priority, ColorScheme cs) {
  switch (priority.toLowerCase()) {
    case 'emergency':
      return cs.error;
    case 'high':
      return cs.errorContainer;
    case 'normal':
      return cs.secondaryContainer;
    default:
      return cs.surfaceContainerHighest;
  }
}

Color _priorityTextColor(String priority, ColorScheme cs) {
  switch (priority.toLowerCase()) {
    case 'emergency':
      return cs.onError;
    case 'high':
      return cs.onErrorContainer;
    case 'normal':
      return cs.onSecondaryContainer;
    default:
      return cs.onSurfaceVariant;
  }
}

Color _statusColor(String status, ColorScheme cs) {
  switch (status.toLowerCase()) {
    case 'completed':
      return cs.primaryContainer;
    case 'inprogress':
      return cs.tertiaryContainer;
    case 'cancelled':
      return cs.surfaceContainerHighest;
    case 'new':
      return cs.secondaryContainer;
    default:
      return cs.surfaceContainerHighest;
  }
}

Color _statusTextColor(String status, ColorScheme cs) {
  switch (status.toLowerCase()) {
    case 'completed':
      return cs.onPrimaryContainer;
    case 'inprogress':
      return cs.onTertiaryContainer;
    case 'cancelled':
      return cs.onSurfaceVariant;
    case 'new':
      return cs.onSecondaryContainer;
    default:
      return cs.onSurfaceVariant;
  }
}

// ── Entry widget ──────────────────────────────────────────────────────────────

/// Work orders list screen with open/all filter and create FAB.
class WorkOrdersScreen extends ConsumerStatefulWidget {
  const WorkOrdersScreen({super.key});

  @override
  ConsumerState<WorkOrdersScreen> createState() => _WorkOrdersScreenState();
}

class _WorkOrdersScreenState extends ConsumerState<WorkOrdersScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(() => ref.read(workOrdersProvider.notifier).load());
  }

  Future<void> _refresh() => ref.read(workOrdersProvider.notifier).refresh();

  void _openDetail(BuildContext context, WorkOrder wo) {
    final unitId = wo.unitId;
    if (unitId != null) {
      openUnitCommandCenter(
        context,
        unitId: unitId,
        initialTab: UnitCommandCenterTab.work,
        workOrder: wo,
      );
      return;
    }

    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => WorkOrderDetailScreen(workOrderId: wo.id),
      ),
    );
  }

  void _showCreateSheet(BuildContext context) {
    showCreateWorkOrderSheet(
      context: context,
      ref: ref,
      onSaved: () => ref.read(workOrdersProvider.notifier).refresh(),
    );
  }

  @override
  Widget build(BuildContext context) {
    final workOrdersAsync = ref.watch(workOrdersProvider);
    final notifier = ref.read(workOrdersProvider.notifier);
    final currentFilter = notifier.filter;
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final filterControl = SegmentedButton<WorkOrderFilter>(
      segments: const [
        ButtonSegment(value: WorkOrderFilter.open, label: Text('Open')),
        ButtonSegment(value: WorkOrderFilter.all, label: Text('All')),
      ],
      selected: {currentFilter},
      onSelectionChanged: (s) {
        notifier.setFilter(s.first);
      },
      style: ButtonStyle(
        tapTargetSize: MaterialTapTargetSize.shrinkWrap,
        visualDensity: VisualDensity.compact,
      ),
    );

    return Scaffold(
      appBar: mobileDomainRootAppBar(
        context,
        // A6: one professional term for the "things to fix" concept across the
        // app — "Work Orders" (the bottom-nav tab stays the short "Work").
        title: const Text('Work Orders'),
        actions: [
          // Open / All toggle
          Padding(
            padding: const EdgeInsets.only(right: 8),
            child: filterControl,
          ),
        ],
      ),
      floatingActionButton: FloatingActionButton(
        heroTag: 'work-orders-fab',
        onPressed: () => _showCreateSheet(context),
        tooltip: 'New work order',
        child: const Icon(Icons.add),
      ),
      body: Column(
        children: [
          MobileDomainEmbeddedToolbar(children: [filterControl]),
          Expanded(
            child: RefreshIndicator(
              onRefresh: _refresh,
              child: workOrdersAsync.when(
                loading: () => const Center(child: CircularProgressIndicator()),
                error: (e, _) => _ErrorBody(
                  message: e is ApiException ? e.message : e.toString(),
                  onRetry: _refresh,
                ),
                data: (list) {
                  if (list.isEmpty) {
                    return _EmptyBody(filter: currentFilter);
                  }
                  return ListView.separated(
                    physics: const AlwaysScrollableScrollPhysics(),
                    padding: const EdgeInsets.fromLTRB(16, 16, 16, 88),
                    itemCount: list.length,
                    separatorBuilder: (context, index) =>
                        const SizedBox(height: 8),
                    itemBuilder: (ctx, i) => _WorkOrderCard(
                      workOrder: list[i],
                      colorScheme: colorScheme,
                      theme: theme,
                      onTap: () => _openDetail(ctx, list[i]),
                    ),
                  );
                },
              ),
            ),
          ),
        ],
      ),
    );
  }
}

// ── Work Order Card ───────────────────────────────────────────────────────────

class _WorkOrderCard extends StatelessWidget {
  const _WorkOrderCard({
    required this.workOrder,
    required this.colorScheme,
    required this.theme,
    required this.onTap,
  });

  final WorkOrder workOrder;
  final ColorScheme colorScheme;
  final ThemeData theme;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return Card(
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.all(14),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Expanded(
                    child: Text(
                      workOrder.title,
                      style: theme.textTheme.titleSmall?.copyWith(
                        fontWeight: FontWeight.w600,
                      ),
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                  const SizedBox(width: 8),
                  _PriorityChip(
                    priority: workOrder.priority,
                    colorScheme: colorScheme,
                  ),
                ],
              ),
              const SizedBox(height: 4),
              if (workOrder.propertyName != null)
                Text(
                  workOrder.propertyName!,
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: colorScheme.onSurfaceVariant,
                  ),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                ),
              const SizedBox(height: 8),
              Row(
                children: [
                  _StatusChip(
                    status: workOrder.status,
                    colorScheme: colorScheme,
                  ),
                  const SizedBox(width: 8),
                  _CategoryChip(
                    category: workOrder.category,
                    colorScheme: colorScheme,
                  ),
                  const Spacer(),
                  Icon(
                    Icons.chevron_right,
                    size: 18,
                    color: colorScheme.onSurfaceVariant,
                  ),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _PriorityChip extends StatelessWidget {
  const _PriorityChip({required this.priority, required this.colorScheme});

  final String priority;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 3),
      decoration: BoxDecoration(
        color: _priorityColor(priority, colorScheme),
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        priority,
        style: TextStyle(
          fontSize: 11,
          fontWeight: FontWeight.w700,
          color: _priorityTextColor(priority, colorScheme),
        ),
      ),
    );
  }
}

class _StatusChip extends StatelessWidget {
  const _StatusChip({required this.status, required this.colorScheme});

  final String status;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 3),
      decoration: BoxDecoration(
        color: _statusColor(status, colorScheme),
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        status,
        style: TextStyle(
          fontSize: 11,
          fontWeight: FontWeight.w600,
          color: _statusTextColor(status, colorScheme),
        ),
      ),
    );
  }
}

class _CategoryChip extends StatelessWidget {
  const _CategoryChip({required this.category, required this.colorScheme});

  final String category;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 3),
      decoration: BoxDecoration(
        color: colorScheme.surfaceContainerHighest,
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        category,
        style: TextStyle(fontSize: 11, color: colorScheme.onSurfaceVariant),
      ),
    );
  }
}

// ── Empty / Error ─────────────────────────────────────────────────────────────

class _EmptyBody extends StatelessWidget {
  const _EmptyBody({required this.filter});

  final WorkOrderFilter filter;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    final msg = filter == WorkOrderFilter.open
        ? 'No open work orders'
        : 'No work orders yet';
    final sub = filter == WorkOrderFilter.open
        ? 'All caught up! Tap + to create one.'
        : 'Tap + to create a work order.';
    return ListView(
      children: [
        SizedBox(
          height: 300,
          child: Center(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(
                  Icons.build_outlined,
                  size: 48,
                  color: colorScheme.onSurfaceVariant,
                ),
                const SizedBox(height: 12),
                Text(
                  msg,
                  style: Theme.of(context).textTheme.titleMedium?.copyWith(
                    color: colorScheme.onSurfaceVariant,
                  ),
                ),
                const SizedBox(height: 4),
                Text(
                  sub,
                  style: TextStyle(color: colorScheme.onSurfaceVariant),
                ),
              ],
            ),
          ),
        ),
      ],
    );
  }
}

class _ErrorBody extends StatelessWidget {
  const _ErrorBody({required this.message, required this.onRetry});

  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.error_outline, size: 40, color: colorScheme.error),
            const SizedBox(height: 12),
            Text(
              message,
              textAlign: TextAlign.center,
              style: TextStyle(color: colorScheme.error),
            ),
            const SizedBox(height: 16),
            FilledButton.tonal(onPressed: onRetry, child: const Text('Retry')),
          ],
        ),
      ),
    );
  }
}
