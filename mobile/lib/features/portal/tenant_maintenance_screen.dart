import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/models.dart';
import '../../core/widgets/mobile_grid_controls.dart';
import '../../core/widgets/mobile_m3_list.dart';
import '../home/mobile_shell_actions.dart';
import '../maintenance/work_order_timeline.dart';
import 'create_tenant_work_order_sheet.dart';
import 'tenant_portal_repository.dart';
import 'tenant_work_order_detail_screen.dart';

class TenantMaintenanceScreen extends ConsumerStatefulWidget {
  const TenantMaintenanceScreen({super.key});

  @override
  ConsumerState<TenantMaintenanceScreen> createState() =>
      _TenantMaintenanceScreenState();
}

class _TenantMaintenanceScreenState
    extends ConsumerState<TenantMaintenanceScreen> {
  static const _pageSize = 20;

  final _searchController = TextEditingController();
  final _scrollController = ScrollController();
  String _search = '';
  bool _openOnly = true;
  String? _status;
  String _sort = '-requestedAt';
  String _period = MobileGridPeriod.all;
  int _skip = 0;

  TenantWorkOrderPageRequest get _request {
    final dateRange = mobileGridDateRangeForPeriod(_period);
    return (
      skip: _skip,
      take: _pageSize,
      openOnly: _openOnly,
      search: _search,
      status: _status,
      sort: _sort,
      from: dateRange.from,
      to: dateRange.to,
    );
  }

  @override
  void dispose() {
    _searchController.dispose();
    _scrollController.dispose();
    super.dispose();
  }

  Future<void> _refresh() {
    ref.invalidate(tenantPortalSnapshotProvider);
    return ref.refresh(tenantPortalWorkOrdersPageProvider(_request).future);
  }

  void _resetPaging() {
    _skip = 0;
  }

  void _submitSearch([String? value]) {
    final next = (value ?? _searchController.text).trim();
    setState(() {
      _search = next;
      _resetPaging();
    });
  }

  void _clearSearch() {
    _searchController.clear();
    _submitSearch('');
  }

  void _setView(String? value) {
    final nextOpenOnly = value != 'all';
    if (nextOpenOnly == _openOnly) return;
    setState(() {
      _openOnly = nextOpenOnly;
      _resetPaging();
    });
  }

  void _setStatus(String? value) {
    if (value == _status) return;
    setState(() {
      _status = value;
      _resetPaging();
    });
  }

  void _setSort(String? value) {
    if (value == null || value == _sort) return;
    setState(() {
      _sort = value;
      _resetPaging();
    });
  }

  void _setPeriod(String? value) {
    if (value == null || value == _period) return;
    setState(() {
      _period = value;
      _resetPaging();
    });
  }

  Future<void> _openCreateSheet() async {
    final result = await showCreateTenantWorkOrderSheet(
      context: context,
      ref: ref,
    );
    if (!mounted || result == null) return;
    setState(_resetPaging);
    await _refresh();
    if (!mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(
          result == CreateTenantWorkOrderResult.submittedPhotoFailed
              ? 'Repair request submitted. Photo failed to upload.'
              : 'Repair request submitted.',
        ),
      ),
    );
  }

  void _openDetail(WorkOrder workOrder) {
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => TenantWorkOrderDetailScreen(workOrderId: workOrder.id),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final pageAsync = ref.watch(tenantPortalWorkOrdersPageProvider(_request));

    return Scaffold(
      appBar: AppBar(
        title: const Text('Maintenance'),
        actions: const [MobileNotificationBell(), MobileAccountMenu()],
      ),
      floatingActionButton: FloatingActionButton.extended(
        key: const Key('tenant-repairs-add-fab'),
        heroTag: 'tenant-repairs-add-fab',
        onPressed: _openCreateSheet,
        icon: const Icon(Icons.add),
        label: const Text('Add repair'),
      ),
      body: RefreshIndicator(
        onRefresh: _refresh,
        child: pageAsync.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (error, _) => ListView(
            physics: const AlwaysScrollableScrollPhysics(),
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 96),
            children: [
              _TenantRepairsControls(
                searchController: _searchController,
                openOnly: _openOnly,
                status: _status,
                sort: _sort,
                period: _period,
                onSearch: _submitSearch,
                onClearSearch: _clearSearch,
                onViewChanged: _setView,
                onStatusChanged: _setStatus,
                onSortChanged: _setSort,
                onPeriodChanged: _setPeriod,
              ),
              const SizedBox(height: 24),
              _TenantRepairError(
                message: error is ApiException
                    ? error.message
                    : "We couldn't load maintenance requests.",
                onRetry: _refresh,
              ),
            ],
          ),
          data: (page) {
            if (page.items.isEmpty) {
              return ListView(
                controller: _scrollController,
                physics: const AlwaysScrollableScrollPhysics(),
                padding: const EdgeInsets.fromLTRB(16, 16, 16, 96),
                children: [
                  _TenantRepairsControls(
                    searchController: _searchController,
                    openOnly: _openOnly,
                    status: _status,
                    sort: _sort,
                    period: _period,
                    onSearch: _submitSearch,
                    onClearSearch: _clearSearch,
                    onViewChanged: _setView,
                    onStatusChanged: _setStatus,
                    onSortChanged: _setSort,
                    onPeriodChanged: _setPeriod,
                  ),
                  const SizedBox(height: 24),
                  const _TenantRepairEmpty(),
                ],
              );
            }

            return ListView.separated(
              controller: _scrollController,
              physics: const AlwaysScrollableScrollPhysics(),
              padding: const EdgeInsets.fromLTRB(16, 16, 16, 96),
              itemCount: page.items.length + 2,
              separatorBuilder: (context, index) {
                if (index == 0) return const SizedBox(height: 12);
                if (index == page.items.length)
                  return const SizedBox(height: 14);
                return const MobileM3ListDivider();
              },
              itemBuilder: (context, index) {
                if (index == 0) {
                  return _TenantRepairsControls(
                    searchController: _searchController,
                    openOnly: _openOnly,
                    status: _status,
                    sort: _sort,
                    period: _period,
                    onSearch: _submitSearch,
                    onClearSearch: _clearSearch,
                    onViewChanged: _setView,
                    onStatusChanged: _setStatus,
                    onSortChanged: _setSort,
                    onPeriodChanged: _setPeriod,
                  );
                }

                if (index == page.items.length + 1) {
                  return MobileGridPagingBar(
                    totalCount: page.totalCount,
                    skip: page.skip,
                    itemCount: page.items.length,
                    previousTooltip: 'Previous repairs page',
                    nextTooltip: 'Next repairs page',
                    onPrevious: page.skip <= 0
                        ? null
                        : () => setState(() {
                            _skip = (_skip - _pageSize).clamp(0, _skip).toInt();
                          }),
                    onNext: page.skip + page.items.length >= page.totalCount
                        ? null
                        : () => setState(() => _skip += _pageSize),
                  );
                }

                final workOrder = page.items[index - 1];
                return _TenantRepairListItem(
                  workOrder: workOrder,
                  position: MobileM3ListItemPositionForIndex.forIndex(
                    index - 1,
                    page.items.length,
                  ),
                  onTap: () => _openDetail(workOrder),
                );
              },
            );
          },
        ),
      ),
    );
  }
}

class _TenantRepairsControls extends StatelessWidget {
  const _TenantRepairsControls({
    required this.searchController,
    required this.openOnly,
    required this.status,
    required this.sort,
    required this.period,
    required this.onSearch,
    required this.onClearSearch,
    required this.onViewChanged,
    required this.onStatusChanged,
    required this.onSortChanged,
    required this.onPeriodChanged,
  });

  final TextEditingController searchController;
  final bool openOnly;
  final String? status;
  final String sort;
  final String period;
  final ValueChanged<String> onSearch;
  final VoidCallback onClearSearch;
  final ValueChanged<String?> onViewChanged;
  final ValueChanged<String?> onStatusChanged;
  final ValueChanged<String?> onSortChanged;
  final ValueChanged<String?> onPeriodChanged;

  @override
  Widget build(BuildContext context) {
    return MobileGridControlsBar(
      keyPrefix: 'tenant-repairs',
      padding: EdgeInsets.zero,
      searchController: searchController,
      searchLabel: 'Search repairs',
      onSearch: onSearch,
      onClearSearch: onClearSearch,
      sort: sort,
      defaultSort: '-requestedAt',
      sortLabel: 'Sort repairs',
      sortOptions: const [
        MobileGridControlOption(
          value: '-requestedAt',
          label: 'Requested newest',
        ),
        MobileGridControlOption(
          value: 'requestedAt',
          label: 'Requested oldest',
        ),
        MobileGridControlOption(value: '-updatedAt', label: 'Updated recently'),
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
          value: openOnly ? 'open' : 'all',
          defaultValue: 'open',
          allLabel: 'Open',
          options: const [MobileGridControlOption(value: 'all', label: 'All')],
          onChanged: onViewChanged,
        ),
        MobileGridChoiceFilter(
          id: 'status',
          label: 'Status',
          value: status,
          defaultValue: null,
          options: const [
            MobileGridControlOption(value: 'New', label: 'New'),
            MobileGridControlOption(value: 'Scheduled', label: 'Scheduled'),
            MobileGridControlOption(value: 'InProgress', label: 'In progress'),
            MobileGridControlOption(
              value: 'WaitingParts',
              label: 'Waiting parts',
            ),
            MobileGridControlOption(value: 'OnHold', label: 'On hold'),
            MobileGridControlOption(value: 'Completed', label: 'Completed'),
            MobileGridControlOption(value: 'Cancelled', label: 'Cancelled'),
            MobileGridControlOption(value: 'Archived', label: 'Archived'),
          ],
          onChanged: onStatusChanged,
        ),
      ],
    );
  }
}

class _TenantRepairListItem extends StatelessWidget {
  const _TenantRepairListItem({
    required this.workOrder,
    required this.position,
    required this.onTap,
  });

  final WorkOrder workOrder;
  final MobileM3ListItemPosition position;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
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
        Text(
          _locationLabel(workOrder),
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
          _TenantRepairChip(label: workOrder.priority),
          _TenantRepairChip(label: _statusLabel(workOrder.status)),
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

class _TenantRepairChip extends StatelessWidget {
  const _TenantRepairChip({required this.label});

  final String label;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 3),
      decoration: BoxDecoration(
        color: colorScheme.surfaceContainerHighest,
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        label,
        style: TextStyle(
          fontSize: 11,
          fontWeight: FontWeight.w600,
          color: colorScheme.onSurfaceVariant,
        ),
      ),
    );
  }
}

class _TenantRepairError extends StatelessWidget {
  const _TenantRepairError({required this.message, required this.onRetry});

  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Icon(Icons.error_outline, color: colorScheme.error, size: 40),
        const SizedBox(height: 12),
        Text(message, style: TextStyle(color: colorScheme.error)),
        const SizedBox(height: 16),
        FilledButton.tonal(onPressed: onRetry, child: const Text('Try again')),
      ],
    );
  }
}

class _TenantRepairEmpty extends StatelessWidget {
  const _TenantRepairEmpty();

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Icon(
          Icons.build_outlined,
          color: colorScheme.onSurfaceVariant,
          size: 40,
        ),
        const SizedBox(height: 12),
        Text(
          'No repairs found.',
          style: theme.textTheme.titleMedium?.copyWith(
            fontWeight: FontWeight.w700,
          ),
        ),
        const SizedBox(height: 4),
        Text(
          'Submitted repairs will appear here.',
          style: theme.textTheme.bodyMedium?.copyWith(
            color: colorScheme.onSurfaceVariant,
          ),
        ),
      ],
    );
  }
}

String _statusLabel(String status) {
  switch (status) {
    case 'InProgress':
      return 'In progress';
    case 'WaitingParts':
      return 'Waiting parts';
    case 'OnHold':
      return 'On hold';
    default:
      return workOrderStatusLabel(status);
  }
}

String _locationLabel(WorkOrder workOrder) {
  final property = workOrder.propertyName;
  final unit = workOrder.unitNumber;
  if (property != null &&
      property.isNotEmpty &&
      unit != null &&
      unit.isNotEmpty) {
    return '$property · Unit $unit';
  }
  if (property != null && property.isNotEmpty) return property;
  if (unit != null && unit.isNotEmpty) return 'Unit $unit';
  return 'Your home';
}
