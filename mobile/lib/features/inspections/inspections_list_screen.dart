import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/models.dart';
import '../../core/widgets/mobile_grid_controls.dart';
import '../../core/widgets/mobile_m3_list.dart';
import '../home/mobile_domain_chrome.dart';
import '../home/mobile_quick_action_fab.dart';
import '../home/mobile_quick_action_helpers.dart';
import 'inspection_run_screen.dart';
import 'inspections_models.dart';
import 'inspections_repository.dart';
import 'new_inspection_screen.dart';
import '../../core/presentation/formatting.dart';

/// On-site inspections list: each row shows the property/unit, type, scheduled
/// date and status. A "New inspection" flow picks a template + property/unit.
class InspectionsListScreen extends ConsumerStatefulWidget {
  const InspectionsListScreen({super.key});

  @override
  ConsumerState<InspectionsListScreen> createState() =>
      _InspectionsListScreenState();
}

class _InspectionsListScreenState extends ConsumerState<InspectionsListScreen> {
  static const _pageSize = 20;

  final _searchCtrl = TextEditingController();
  String? _search;
  String _sort = '-scheduledFor';
  int _skip = 0;

  InspectionListQuery get _query => InspectionListQuery(
    skip: _skip,
    take: _pageSize,
    search: _search,
    sort: _sort,
  );

  @override
  void dispose() {
    _searchCtrl.dispose();
    super.dispose();
  }

  void _invalidateLists() {
    ref.invalidate(inspectionsPageProvider);
    ref.invalidate(inspectionsProvider);
  }

  Future<void> _refresh() async => _invalidateLists();

  Future<void> _newInspection() async {
    final created = await Navigator.of(context).push<int>(
      MaterialPageRoute<int>(builder: (_) => const NewInspectionScreen()),
    );
    if (created == null || !mounted) return;
    _invalidateLists();
    await Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => InspectionRunScreen(inspectionId: created),
      ),
    );
    if (!mounted) return;
    _invalidateLists();
  }

  Future<void> _openDetail(Inspection inspection) async {
    await Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => InspectionRunScreen(inspectionId: inspection.id),
      ),
    );
    if (!mounted) return;
    _invalidateLists();
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

  void _setSort(String? value) {
    if (value == null || value == _sort) return;
    setState(() {
      _sort = value;
      _skip = 0;
    });
  }

  @override
  Widget build(BuildContext context) {
    final inspectionsAsync = ref.watch(inspectionsPageProvider(_query));

    return Scaffold(
      appBar: mobileDomainRootAppBar(context, title: const Text('Inspections')),
      floatingActionButton: MobileQuickActionFab(
        heroTag: 'inspections-fab',
        primaryAction: MobileQuickAction(
          label: 'New inspection',
          icon: Icons.add,
          onPressed: _newInspection,
        ),
        onChat: () => openMobileAssistant(context),
        onRecord: () => openMobileRecord(context),
        onScan: () => openMobileScan(context),
      ),
      body: Column(
        children: [
          MobileGridControlsBar(
            keyPrefix: 'inspections',
            searchController: _searchCtrl,
            searchLabel: 'Search inspections',
            onSearch: _submitSearch,
            onClearSearch: _clearSearch,
            sort: _sort,
            defaultSort: '-scheduledFor',
            sortLabel: 'Sort inspections',
            sortOptions: const [
              MobileGridControlOption(
                value: '-scheduledFor',
                label: 'Newest scheduled',
              ),
              MobileGridControlOption(
                value: 'scheduledFor',
                label: 'Oldest scheduled',
              ),
              MobileGridControlOption(value: 'status', label: 'Status'),
              MobileGridControlOption(value: 'type', label: 'Type'),
              MobileGridControlOption(
                value: '-updatedAt',
                label: 'Recently updated',
              ),
            ],
            onSortChanged: _setSort,
          ),
          Expanded(
            child: RefreshIndicator(
              onRefresh: _refresh,
              child: inspectionsAsync.when(
                loading: () => const _InspectionsLoadingBody(),
                error: (e, _) => _ErrorBody(
                  message: e is ApiException ? e.message : e.toString(),
                  onRetry: _refresh,
                ),
                data: (page) {
                  final inspections = page.items;
                  if (inspections.isEmpty && _search == null && _skip == 0) {
                    return const _EmptyBody();
                  }
                  if (inspections.isEmpty) {
                    return ListView(
                      physics: const AlwaysScrollableScrollPhysics(),
                      children: [
                        Padding(
                          padding: const EdgeInsets.all(24),
                          child: Text(
                            _search == null
                                ? 'No inspections on this page.'
                                : 'No inspections match "$_search".',
                            textAlign: TextAlign.center,
                          ),
                        ),
                      ],
                    );
                  }

                  return ListView.separated(
                    physics: const AlwaysScrollableScrollPhysics(),
                    padding: const EdgeInsets.fromLTRB(16, 12, 16, 96),
                    itemCount: inspections.length + 1,
                    separatorBuilder: (_, index) =>
                        index == inspections.length - 1
                        ? const SizedBox(height: 14)
                        : const MobileM3ListDivider(),
                    itemBuilder: (_, i) {
                      if (i == inspections.length) {
                        return MobileGridPagingBar(
                          totalCount: page.totalCount,
                          skip: page.skip,
                          itemCount: page.items.length,
                          onPrevious: page.hasPrevious
                              ? () => setState(() {
                                  _skip = _skip <= _pageSize
                                      ? 0
                                      : _skip - _pageSize;
                                })
                              : null,
                          onNext: page.hasNext
                              ? () => setState(() => _skip += _pageSize)
                              : null,
                        );
                      }
                      final inspection = inspections[i];
                      return _InspectionCard(
                        inspection: inspection,
                        position: MobileM3ListItemPositionForIndex.forIndex(
                          i,
                          inspections.length,
                        ),
                        onTap: () => _openDetail(inspection),
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

class _InspectionsLoadingBody extends StatelessWidget {
  const _InspectionsLoadingBody();

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colors = theme.colorScheme;

    return ListView(
      key: const Key('inspections-loading'),
      physics: const AlwaysScrollableScrollPhysics(),
      padding: const EdgeInsets.fromLTRB(16, 12, 16, 96),
      children: [
        MobileM3ListItem(
          position: MobileM3ListItemPosition.single,
          leading: MobileM3LeadingIcon(
            icon: Icons.fact_check_outlined,
            backgroundColor: colors.secondaryContainer,
            foregroundColor: colors.onSecondaryContainer,
          ),
          title: Text(
            'Loading inspections',
            style: theme.textTheme.titleSmall?.copyWith(
              fontWeight: FontWeight.w700,
            ),
          ),
          supporting: [
            Text(
              'Property, unit, schedule, and status',
              style: theme.textTheme.bodySmall?.copyWith(
                color: colors.onSurfaceVariant,
              ),
            ),
          ],
          trailing: const SizedBox.square(
            dimension: 22,
            child: CircularProgressIndicator(strokeWidth: 2),
          ),
        ),
      ],
    );
  }
}

class _InspectionCard extends StatelessWidget {
  const _InspectionCard({
    required this.inspection,
    required this.position,
    required this.onTap,
  });

  final Inspection inspection;
  final MobileM3ListItemPosition position;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final i = inspection;

    final where = StringBuffer(
      i.propertyName?.isNotEmpty == true
          ? i.propertyName!
          : 'Property #${i.propertyId}',
    );
    if (i.unitNumber?.isNotEmpty == true) {
      where.write('  ·  Unit ${i.unitNumber}');
    } else if (i.unitId != null) {
      where.write('  ·  Unit #${i.unitId}');
    }

    return MobileM3ListItem(
      position: position,
      leading: MobileM3LeadingIcon(
        icon: Icons.fact_check_outlined,
        backgroundColor: cs.secondaryContainer,
        foregroundColor: cs.onSecondaryContainer,
      ),
      title: Text(
        where.toString(),
        maxLines: 2,
        overflow: TextOverflow.ellipsis,
        style: theme.textTheme.titleSmall?.copyWith(
          fontWeight: FontWeight.w700,
        ),
      ),
      supporting: [
        Wrap(
          spacing: 10,
          runSpacing: 2,
          crossAxisAlignment: WrapCrossAlignment.center,
          children: [
            Text(
              friendlyInspectionType(i.type),
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: theme.textTheme.bodySmall?.copyWith(
                color: cs.onSurfaceVariant,
              ),
            ),
            Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(
                  Icons.schedule_outlined,
                  size: 14,
                  color: cs.onSurfaceVariant,
                ),
                const SizedBox(width: 4),
                Flexible(
                  child: Text(
                    dateFmt(i.scheduledFor.toLocal()),
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: cs.onSurfaceVariant,
                    ),
                  ),
                ),
              ],
            ),
          ],
        ),
      ],
      trailing: InspectionStatusChip(status: i.status),
      onTap: onTap,
    );
  }
}

/// Coloured status pill shared across the inspections screens.
class InspectionStatusChip extends StatelessWidget {
  const InspectionStatusChip({super.key, required this.status});

  final String status;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    Color bg;
    Color fg;
    switch (status) {
      case 'Completed':
      case 'Reviewed':
        bg = cs.primaryContainer;
        fg = cs.onPrimaryContainer;
      case 'NeedsFollowUp':
        bg = cs.errorContainer;
        fg = cs.onErrorContainer;
      case 'Cancelled':
      case 'Archived':
        bg = cs.surfaceContainerHighest;
        fg = cs.onSurfaceVariant;
      default:
        bg = cs.secondaryContainer;
        fg = cs.onSecondaryContainer;
    }
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
      decoration: BoxDecoration(
        color: bg,
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        friendlyInspectionStatus(status),
        style: TextStyle(color: fg, fontSize: 12, fontWeight: FontWeight.w600),
      ),
    );
  }
}

class _EmptyBody extends StatelessWidget {
  const _EmptyBody();

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(24, 80, 24, 24),
          child: Column(
            children: [
              Icon(
                Icons.fact_check_outlined,
                size: 40,
                color: cs.onSurfaceVariant,
              ),
              const SizedBox(height: 12),
              Text(
                'No inspections yet.',
                textAlign: TextAlign.center,
                style: TextStyle(color: cs.onSurfaceVariant),
              ),
              const SizedBox(height: 6),
              Text(
                'Tap "New inspection" to walk a unit with a checklist.',
                textAlign: TextAlign.center,
                style: Theme.of(
                  context,
                ).textTheme.bodySmall?.copyWith(color: cs.onSurfaceVariant),
              ),
            ],
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
    final cs = Theme.of(context).colorScheme;
    return ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(24, 80, 24, 24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(Icons.error_outline, size: 40, color: cs.error),
              const SizedBox(height: 12),
              Text(
                message,
                textAlign: TextAlign.center,
                style: TextStyle(color: cs.error),
              ),
              const SizedBox(height: 16),
              FilledButton.tonal(
                onPressed: onRetry,
                child: const Text('Retry'),
              ),
            ],
          ),
        ),
      ],
    );
  }
}
