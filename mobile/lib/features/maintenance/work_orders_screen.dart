import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/auth/auth_controller.dart';
import '../../core/models/models.dart';
import '../../core/widgets/mobile_grid_controls.dart';
import '../../core/widgets/mobile_m3_list.dart';
import '../home/mobile_domain_chrome.dart';
import '../home/mobile_quick_action_fab.dart';
import '../home/mobile_quick_action_helpers.dart';
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
  static const _pageSize = 20;

  final _searchCtrl = TextEditingController();
  WorkOrderFilter _filter = WorkOrderFilter.open;
  String? _search;
  String _sort = '-updatedAt';
  String _period = MobileGridPeriod.all;
  int _skip = 0;

  WorkOrderListQuery get _query {
    final dateRange = mobileGridDateRangeForPeriod(_period);
    return WorkOrderListQuery(
      skip: _skip,
      take: _pageSize,
      openOnly: _filter == WorkOrderFilter.open,
      search: _search,
      sort: _sort,
      requestedFrom: dateRange.from,
      requestedTo: dateRange.to,
    );
  }

  @override
  void initState() {
    super.initState();
    Future.microtask(() => ref.read(workOrdersProvider.notifier).load());
  }

  @override
  void dispose() {
    _searchCtrl.dispose();
    super.dispose();
  }

  Future<void> _refresh() async {
    await ref.read(workOrdersProvider.notifier).refresh();
    return ref.refresh(workOrdersPageProvider(_query).future);
  }

  void _submitSearch([String? value]) {
    final next = (value ?? _searchCtrl.text).trim();
    setState(() {
      _search = next.isEmpty ? null : next;
      _skip = 0;
    });
  }

  void _clearSearch() {
    _searchCtrl.clear();
    _submitSearch('');
  }

  void _setSort(String? sort) {
    if (sort == null || sort == _sort) return;
    setState(() {
      _sort = sort;
      _skip = 0;
    });
  }

  void _setPeriod(String? period) {
    if (period == null || period == _period) return;
    setState(() {
      _period = period;
      _skip = 0;
    });
  }

  void _setFilter(WorkOrderFilter filter) {
    if (filter == _filter) return;
    setState(() {
      _filter = filter;
      _skip = 0;
    });
    ref.read(workOrdersProvider.notifier).setFilter(filter);
  }

  void _setFilterValue(String? value) {
    _setFilter(
      value == WorkOrderFilter.all.name
          ? WorkOrderFilter.all
          : WorkOrderFilter.open,
    );
  }

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
      onSaved: () {
        ref.read(workOrdersProvider.notifier).refresh();
        ref.invalidate(workOrdersPageProvider);
      },
    );
  }

  @override
  Widget build(BuildContext context) {
    final workOrdersAsync = ref.watch(workOrdersPageProvider(_query));
    final currentFilter = _filter;
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final auth = ref.watch(authControllerProvider);
    final canManageWork = auth is AuthStateAuthenticated &&
        auth.capabilities.contains('work.manage');

    return Scaffold(
      appBar: mobileDomainRootAppBar(
        context,
        // A6: one professional term for the "things to fix" concept across the
        // app — "Work Orders" (the bottom-nav tab stays the short "Work").
        title: const Text('Work Orders'),
      ),
      floatingActionButton: MobileQuickActionFab(
        heroTag: 'work-orders-fab',
        primaryAction: canManageWork ? MobileQuickAction(
          label: 'New work order',
          icon: Icons.add,
          onPressed: () => _showCreateSheet(context),
        ) : null,
        onChat: () => openMobileAssistant(context),
        onRecord: () => openMobileRecord(context),
        onScan: () => openAuthorizedMobileScan(context, ref),
      ),
      body: Column(
        children: [
          Expanded(
            child: RefreshIndicator(
              onRefresh: _refresh,
              child: workOrdersAsync.when(
                loading: () => const Center(child: CircularProgressIndicator()),
                error: (e, _) => _ErrorBody(
                  message: e is ApiException ? e.message : e.toString(),
                  onRetry: _refresh,
                ),
                data: (page) {
                  if (page.items.isEmpty) {
                    return ListView(
                      physics: const AlwaysScrollableScrollPhysics(),
                      padding: const EdgeInsets.fromLTRB(16, 16, 16, 88),
                      children: [
                        _WorkOrdersGridControls(
                          searchController: _searchCtrl,
                          filter: currentFilter,
                          sort: _sort,
                          period: _period,
                          onSearch: _submitSearch,
                          onClearSearch: _clearSearch,
                          onFilterChanged: _setFilterValue,
                          onSortChanged: _setSort,
                          onPeriodChanged: _setPeriod,
                        ),
                        _EmptyBody(filter: currentFilter),
                      ],
                    );
                  }
                  return ListView.separated(
                    physics: const AlwaysScrollableScrollPhysics(),
                    padding: const EdgeInsets.fromLTRB(16, 16, 16, 88),
                    itemCount: page.items.length + 2,
                    separatorBuilder: (context, index) {
                      if (index == 0) return const SizedBox(height: 12);
                      if (index == page.items.length) {
                        return const SizedBox(height: 14);
                      }
                      return const MobileM3ListDivider();
                    },
                    itemBuilder: (ctx, i) {
                      if (i == 0) {
                        return _WorkOrdersGridControls(
                          searchController: _searchCtrl,
                          filter: currentFilter,
                          sort: _sort,
                          period: _period,
                          onSearch: _submitSearch,
                          onClearSearch: _clearSearch,
                          onFilterChanged: _setFilterValue,
                          onSortChanged: _setSort,
                          onPeriodChanged: _setPeriod,
                        );
                      }

                      if (i == page.items.length + 1) {
                        return MobileGridPagingBar(
                          totalCount: page.totalCount,
                          skip: page.skip,
                          itemCount: page.items.length,
                          previousTooltip: 'Previous work orders page',
                          nextTooltip: 'Next work orders page',
                          onPrevious: page.hasPrevious
                              ? () => setState(() {
                                  _skip = (_skip - _pageSize).clamp(0, _skip);
                                })
                              : null,
                          onNext: page.hasNext
                              ? () => setState(() => _skip += _pageSize)
                              : null,
                        );
                      }

                      final workOrder = page.items[i - 1];
                      return _WorkOrderCard(
                        workOrder: workOrder,
                        position: MobileM3ListItemPositionForIndex.forIndex(
                          i - 1,
                          page.items.length,
                        ),
                        colorScheme: colorScheme,
                        theme: theme,
                        onTap: () => _openDetail(ctx, workOrder),
                      );
                    },
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

class _WorkOrdersGridControls extends StatelessWidget {
  const _WorkOrdersGridControls({
    required this.searchController,
    required this.filter,
    required this.sort,
    required this.period,
    required this.onSearch,
    required this.onClearSearch,
    required this.onFilterChanged,
    required this.onSortChanged,
    required this.onPeriodChanged,
  });

  final TextEditingController searchController;
  final WorkOrderFilter filter;
  final String sort;
  final String period;
  final ValueChanged<String> onSearch;
  final VoidCallback onClearSearch;
  final ValueChanged<String?> onFilterChanged;
  final ValueChanged<String?> onSortChanged;
  final ValueChanged<String?> onPeriodChanged;

  @override
  Widget build(BuildContext context) {
    return MobileGridControlsBar(
      keyPrefix: 'work-orders',
      padding: EdgeInsets.zero,
      searchController: searchController,
      searchLabel: 'Search work orders',
      onSearch: onSearch,
      onClearSearch: onClearSearch,
      sort: sort,
      defaultSort: '-updatedAt',
      sortLabel: 'Sort work orders',
      sortOptions: const [
        MobileGridControlOption(value: '-updatedAt', label: 'Updated recently'),
        MobileGridControlOption(
          value: '-requestedAt',
          label: 'Requested newest',
        ),
        MobileGridControlOption(
          value: 'requestedAt',
          label: 'Requested oldest',
        ),
        MobileGridControlOption(value: 'priority', label: 'Priority'),
        MobileGridControlOption(value: 'status', label: 'Status'),
        MobileGridControlOption(value: 'title', label: 'Title'),
      ],
      onSortChanged: onSortChanged,
      period: period,
      defaultPeriod: MobileGridPeriod.all,
      periodLabel: 'Requested period',
      onPeriodChanged: onPeriodChanged,
      filters: [
        MobileGridChoiceFilter(
          id: 'view',
          label: 'View',
          value: filter.name,
          defaultValue: WorkOrderFilter.open.name,
          allLabel: 'Open',
          options: [
            MobileGridControlOption(
              value: WorkOrderFilter.all.name,
              label: 'All',
            ),
          ],
          onChanged: onFilterChanged,
        ),
      ],
    );
  }
}

// ── Work Order Card ───────────────────────────────────────────────────────────

class _WorkOrderCard extends StatelessWidget {
  const _WorkOrderCard({
    required this.workOrder,
    required this.position,
    required this.colorScheme,
    required this.theme,
    required this.onTap,
  });

  final WorkOrder workOrder;
  final MobileM3ListItemPosition position;
  final ColorScheme colorScheme;
  final ThemeData theme;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return MobileM3ListItem(
      position: position,
      leading: MobileM3LeadingIcon(
        icon: Icons.build_outlined,
        backgroundColor: colorScheme.tertiaryContainer,
        foregroundColor: colorScheme.onTertiaryContainer,
      ),
      title: Text(
        workOrder.title,
        style: theme.textTheme.titleSmall?.copyWith(
          fontWeight: FontWeight.w600,
        ),
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
      ),
      supporting: [
        if (workOrder.propertyName != null)
          Text(
            workOrder.propertyName!,
            style: theme.textTheme.bodySmall?.copyWith(
              color: colorScheme.onSurfaceVariant,
            ),
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
          ),
      ],
      meta: Wrap(
        spacing: 8,
        runSpacing: 4,
        children: [
          _PriorityChip(priority: workOrder.priority, colorScheme: colorScheme),
          _StatusChip(status: workOrder.status, colorScheme: colorScheme),
          _CategoryChip(category: workOrder.category, colorScheme: colorScheme),
        ],
      ),
      trailing: Icon(
        Icons.chevron_right,
        size: 20,
        color: colorScheme.onSurfaceVariant,
      ),
      onTap: onTap,
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
    return SizedBox(
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
            Text(sub, style: TextStyle(color: colorScheme.onSurfaceVariant)),
          ],
        ),
      ),
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
