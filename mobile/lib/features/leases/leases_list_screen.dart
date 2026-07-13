import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/lease.dart';
import '../../core/widgets/mobile_grid_controls.dart';
import '../../core/widgets/mobile_m3_list.dart';
import '../applications/applications_list_screen.dart';
import '../home/mobile_domain_chrome.dart';
import '../home/mobile_quick_action_fab.dart';
import '../home/mobile_quick_action_helpers.dart';
import '../units/unit_command_center_tabs.dart';
import '../units/unit_navigation.dart';
import 'leases_repository.dart';

class LeasesListScreen extends ConsumerStatefulWidget {
  const LeasesListScreen({super.key});

  @override
  ConsumerState<LeasesListScreen> createState() => _LeasesListScreenState();
}

class _LeasesListScreenState extends ConsumerState<LeasesListScreen> {
  static const _pageSize = 20;
  final _searchController = TextEditingController();
  String? _search;
  String? _lifecycle;
  String _sort = '-updatedAt';
  int _skip = 0;

  LeaseManagementListQuery get _query => LeaseManagementListQuery(
    skip: _skip,
    take: _pageSize,
    search: _search,
    lifecycle: _lifecycle,
    sort: _sort,
  );

  @override
  void dispose() {
    _searchController.dispose();
    super.dispose();
  }

  void _submitSearch([String? value]) {
    final search = (value ?? _searchController.text).trim();
    setState(() {
      _search = search.isEmpty ? null : search;
      _skip = 0;
    });
  }

  void _open(LeaseManagementSummary management) {
    openUnitCommandCenter(
      context,
      unitId: management.unitId,
      initialTab: UnitCommandCenterTab.lease,
      leaseManagementId: management.id,
    );
  }

  void _prepareMoveIn() {
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(builder: (_) => const ApplicationsListScreen()),
    );
  }

  @override
  Widget build(BuildContext context) {
    final page = ref.watch(leaseManagementsPageProvider(_query));
    return Scaffold(
      appBar: mobileDomainRootAppBar(
        context,
        title: const Text('Tenant & leases'),
      ),
      floatingActionButton: MobileQuickActionFab(
        heroTag: 'tenant-leases-fab',
        primaryAction: MobileQuickAction(
          label: 'Prepare move-in',
          icon: Icons.person_add_alt_1_outlined,
          onPressed: _prepareMoveIn,
        ),
        onScan: () => openMobileScan(context),
        onRecord: () => openMobileRecord(context),
        onChat: () => openMobileAssistant(context),
      ),
      body: RefreshIndicator(
        onRefresh: () async =>
            ref.invalidate(leaseManagementsPageProvider(_query)),
        child: page.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (error, _) => ListView(
            physics: const AlwaysScrollableScrollPhysics(),
            padding: const EdgeInsets.all(24),
            children: [
              Text(
                error is ApiException ? error.message : error.toString(),
                textAlign: TextAlign.center,
              ),
              const SizedBox(height: 12),
              FilledButton.tonal(
                onPressed: () =>
                    ref.invalidate(leaseManagementsPageProvider(_query)),
                child: const Text('Try again'),
              ),
            ],
          ),
          data: (result) => ListView.separated(
            physics: const AlwaysScrollableScrollPhysics(),
            padding: EdgeInsets.fromLTRB(
              16,
              12,
              16,
              140 + MediaQuery.paddingOf(context).bottom,
            ),
            itemCount: result.items.length + 2,
            separatorBuilder: (_, index) => index == 0
                ? const SizedBox(height: 12)
                : const MobileM3ListDivider(),
            itemBuilder: (context, index) {
              if (index == 0) {
                return MobileGridControlsBar(
                  keyPrefix: 'tenant-leases',
                  padding: EdgeInsets.zero,
                  searchController: _searchController,
                  searchLabel: 'Search tenant, property, unit, or agreement',
                  onSearch: _submitSearch,
                  onClearSearch: () {
                    _searchController.clear();
                    _submitSearch('');
                  },
                  sort: _sort,
                  defaultSort: '-updatedAt',
                  sortOptions: const [
                    MobileGridControlOption(
                      value: '-updatedAt',
                      label: 'Updated recently',
                    ),
                    MobileGridControlOption(
                      value: 'tenantName',
                      label: 'Tenant A-Z',
                    ),
                    MobileGridControlOption(
                      value: 'propertyName',
                      label: 'Property A-Z',
                    ),
                    MobileGridControlOption(
                      value: '-rent',
                      label: 'Rent high-low',
                    ),
                  ],
                  onSortChanged: (value) {
                    if (value == null) return;
                    setState(() {
                      _sort = value;
                      _skip = 0;
                    });
                  },
                  filters: [
                    MobileGridChoiceFilter(
                      id: 'lifecycle',
                      label: 'Lifecycle',
                      value: _lifecycle,
                      options: const [
                        MobileGridControlOption(
                          value: 'Planned',
                          label: 'Planned',
                        ),
                        MobileGridControlOption(
                          value: 'Occupied',
                          label: 'Occupied',
                        ),
                        MobileGridControlOption(
                          value: 'Ending',
                          label: 'Ending',
                        ),
                        MobileGridControlOption(
                          value: 'Closed',
                          label: 'Closed',
                        ),
                        MobileGridControlOption(
                          value: 'Canceled',
                          label: 'Canceled',
                        ),
                      ],
                      onChanged: (value) => setState(() {
                        _lifecycle = value;
                        _skip = 0;
                      }),
                    ),
                  ],
                );
              }
              if (index == result.items.length + 1) {
                if (result.items.isEmpty) {
                  return _EmptyState(onPrepareMoveIn: _prepareMoveIn);
                }
                return MobileGridPagingBar(
                  totalCount: result.totalCount,
                  skip: result.skip,
                  itemCount: result.items.length,
                  previousTooltip: 'Previous tenant and lease page',
                  nextTooltip: 'Next tenant and lease page',
                  onPrevious: result.hasPrevious
                      ? () => setState(
                          () => _skip = (_skip - _pageSize).clamp(0, _skip),
                        )
                      : null,
                  onNext: result.hasNext
                      ? () => setState(() => _skip += _pageSize)
                      : null,
                );
              }
              final item = result.items[index - 1];
              return _RelationshipTile(
                item: item,
                position: MobileM3ListItemPositionForIndex.forIndex(
                  index - 1,
                  result.items.length,
                ),
                onTap: () => _open(item),
              );
            },
          ),
        ),
      ),
    );
  }
}

class _RelationshipTile extends StatelessWidget {
  const _RelationshipTile({
    required this.item,
    required this.position,
    required this.onTap,
  });

  final LeaseManagementSummary item;
  final MobileM3ListItemPosition position;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final term = item.termStartOn == null
        ? 'Agreement not issued'
        : '${_date(item.termStartOn!)} – '
              '${item.termEndOn == null ? 'Month-to-month' : _date(item.termEndOn!)}';
    return MobileM3ListItem(
      position: position,
      leading: MobileM3LeadingIcon(
        icon: Icons.home_work_outlined,
        backgroundColor: theme.colorScheme.primaryContainer,
        foregroundColor: theme.colorScheme.onPrimaryContainer,
      ),
      title: Text(
        item.primaryTenantName?.trim().isNotEmpty == true
            ? item.primaryTenantName!
            : item.relationshipNumber,
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
      ),
      supporting: [
        Text('${item.propertyName} · Unit ${item.unitNumber}'),
        Text(term),
        if (item.upcomingAgreementId != null)
          const Text('Upcoming agreement ready'),
      ],
      trailing: Chip(label: Text(item.lifecycle)),
      onTap: onTap,
      promoted: item.hasReconciliationException,
    );
  }
}

class _EmptyState extends StatelessWidget {
  const _EmptyState({required this.onPrepareMoveIn});

  final VoidCallback onPrepareMoveIn;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 48),
    child: Column(
      children: [
        const Icon(Icons.home_work_outlined, size: 48),
        const SizedBox(height: 12),
        Text(
          'No tenant relationships found',
          style: Theme.of(context).textTheme.titleMedium,
        ),
        const SizedBox(height: 8),
        const Text(
          'Approve an application, then prepare the move-in. The tenant '
          'relationship, account, household, and first agreement draft are '
          'created together.',
          textAlign: TextAlign.center,
        ),
        const SizedBox(height: 16),
        FilledButton.icon(
          onPressed: onPrepareMoveIn,
          icon: const Icon(Icons.person_add_alt_1_outlined),
          label: const Text('Open applications'),
        ),
      ],
    ),
  );
}

String _date(DateTime value) => '${value.month}/${value.day}/${value.year}';
