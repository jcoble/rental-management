import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:material_symbols_icons/symbols.dart';

import '../../core/api/api_exception.dart';
import '../../core/router/mobile_restoration_state.dart';
import '../../core/widgets/mobile_grid_controls.dart';
import '../../core/widgets/mobile_m3_list.dart';
import '../home/mobile_domain_chrome.dart';
import 'unit_navigation.dart';
import 'unit_command_center_tabs.dart';
import 'units_repository.dart';

class UnitsListScreen extends ConsumerStatefulWidget {
  const UnitsListScreen({super.key});

  @override
  ConsumerState<UnitsListScreen> createState() => _UnitsListScreenState();
}

class _UnitsListScreenState extends ConsumerState<UnitsListScreen> {
  static const _pageSize = 20;

  final _searchCtrl = TextEditingController();
  final _scrollController = ScrollController();
  RestorableMobileRestorationState? _restoration;
  bool _restorationLoaded = false;
  double? _pendingRestoredScrollOffset;
  bool _scrollRestorationScheduled = false;
  String? _search;
  String _sort = 'propertyName';
  String? _stageFilter;
  int _skip = 0;

  UnitHealthListQuery get _listQuery => UnitHealthListQuery(
    search: _search,
    sort: _sort,
    stage: _stageFilter,
    skip: _skip,
    take: _pageSize,
  );

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    if (_restorationLoaded) return;
    _restorationLoaded = true;
    _restoration = MobileRestorationScope.maybeOf(context);
    final restored = _restoration?.value;
    if (restored == null) return;
    _search = restored.query;
    _searchCtrl.text = restored.query ?? '';
    _sort = restored.sort;
    _stageFilter = restored.stage;
    _skip = restored.skip;
    _pendingRestoredScrollOffset = restored.scrollOffset;
    _scrollController.addListener(_saveRestoration);
  }

  void _scheduleScrollRestoration() {
    if (_pendingRestoredScrollOffset == null || _scrollRestorationScheduled) {
      return;
    }
    _scrollRestorationScheduled = true;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      _scrollRestorationScheduled = false;
      final restoredOffset = _pendingRestoredScrollOffset;
      if (!mounted || restoredOffset == null || !_scrollController.hasClients) {
        return;
      }
      _pendingRestoredScrollOffset = null;
      _scrollController.jumpTo(
        restoredOffset.clamp(0, _scrollController.position.maxScrollExtent),
      );
    });
  }

  void _saveRestoration() {
    final restoration = _restoration;
    if (restoration == null) return;
    final scrollOffset = _scrollController.hasClients
        ? _scrollController.offset
        : _pendingRestoredScrollOffset ?? restoration.value.scrollOffset;
    restoration.value = restoration.value.updateCollection(
      query: _search,
      sort: _sort,
      stage: _stageFilter,
      skip: _skip,
      scrollOffset: scrollOffset,
    );
  }

  @override
  void dispose() {
    _saveRestoration();
    _scrollController.dispose();
    _searchCtrl.dispose();
    super.dispose();
  }

  Future<void> _refresh() {
    return ref.refresh(unitHealthPageProvider(_listQuery).future);
  }

  void _submitSearch([String? value]) {
    final next = (value ?? _searchCtrl.text).trim();
    setState(() {
      _search = next.isEmpty ? null : next;
      _skip = 0;
    });
    _saveRestoration();
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
    _saveRestoration();
  }

  void _setStageFilter(String? value) {
    setState(() {
      _stageFilter = value;
      _skip = 0;
    });
    _saveRestoration();
  }

  void _openUnit(UnitHealth unit) {
    final restoration = _restoration;
    if (restoration != null) {
      restoration.value = restoration.value.updateUnit(
        unitId: unit.id,
        destination: UnitCommandCenterTab.summary.name,
      );
    }
    openUnitCommandCenter(context, unitId: unit.id);
  }

  @override
  Widget build(BuildContext context) {
    final unitsAsync = ref.watch(unitHealthPageProvider(_listQuery));

    return Scaffold(
      appBar: mobileDomainRootAppBar(context, title: const Text('Units')),
      body: Column(
        children: [
          MobileGridControlsBar(
            keyPrefix: 'units',
            searchController: _searchCtrl,
            searchLabel: 'Search units',
            onSearch: _submitSearch,
            onClearSearch: _clearSearch,
            sort: _sort,
            defaultSort: 'propertyName',
            sortLabel: 'Sort units',
            sortOptions: const [
              MobileGridControlOption(
                value: 'propertyName',
                label: 'Property A-Z',
              ),
              MobileGridControlOption(
                value: 'unitNumber',
                label: 'Unit number',
              ),
              MobileGridControlOption(
                value: '-updatedAt',
                label: 'Updated recently',
              ),
              MobileGridControlOption(
                value: '-openWorkOrderCount',
                label: 'Most open work',
              ),
              MobileGridControlOption(
                value: '-marketRent',
                label: 'Highest rent',
              ),
              MobileGridControlOption(
                value: 'status',
                label: 'Occupancy status',
              ),
            ],
            onSortChanged: _setSort,
            filters: [
              MobileGridChoiceFilter(
                id: 'stage',
                label: 'Stage',
                value: _stageFilter,
                allLabel: 'All stages',
                options: const [
                  MobileGridControlOption(value: 'Active', label: 'Active'),
                  MobileGridControlOption(value: 'Renewal', label: 'Renewal'),
                  MobileGridControlOption(value: 'Move-Out', label: 'Move-out'),
                  MobileGridControlOption(value: 'Lease', label: 'Lease'),
                  MobileGridControlOption(value: 'Vacant', label: 'Vacant'),
                  MobileGridControlOption(value: 'Turnover', label: 'Turnover'),
                ],
                onChanged: _setStageFilter,
              ),
            ],
            trailingActions: [
              IconButton(
                tooltip: 'Refresh units',
                icon: const Icon(Icons.refresh),
                onPressed: _refresh,
              ),
            ],
          ),
          Expanded(
            child: RefreshIndicator(
              onRefresh: _refresh,
              child: unitsAsync.when(
                loading: () => const Center(child: CircularProgressIndicator()),
                error: (e, _) => _UnitsErrorBody(
                  message: e is ApiException ? e.message : e.toString(),
                  onRetry: _refresh,
                ),
                data: (page) {
                  if (page.items.isEmpty) {
                    return _UnitsEmptyBody(
                      hasSearch: _search != null,
                      hasFilter: _stageFilter != null,
                    );
                  }

                  final bottomInset = MediaQuery.paddingOf(context).bottom;
                  _scheduleScrollRestoration();
                  return ListView.separated(
                    controller: _scrollController,
                    padding: EdgeInsets.fromLTRB(
                      16,
                      8,
                      16,
                      160.0 + bottomInset,
                    ),
                    itemCount: page.items.length + 1,
                    separatorBuilder: (_, index) =>
                        index >= page.items.length - 1
                        ? const SizedBox(height: 8)
                        : const MobileM3ListDivider(),
                    itemBuilder: (context, index) {
                      if (index == page.items.length) {
                        return MobileGridPagingBar(
                          totalCount: page.totalCount,
                          skip: page.skip,
                          itemCount: page.items.length,
                          previousTooltip: 'Previous units page',
                          nextTooltip: 'Next units page',
                          onPrevious: page.hasPrevious
                              ? () {
                                  setState(() {
                                    _skip = _skip <= _pageSize
                                        ? 0
                                        : _skip - _pageSize;
                                  });
                                  _saveRestoration();
                                }
                              : null,
                          onNext: page.hasNext
                              ? () {
                                  setState(() => _skip += _pageSize);
                                  _saveRestoration();
                                }
                              : null,
                        );
                      }

                      final unit = page.items[index];
                      return _UnitHealthRow(
                        unit: unit,
                        position: MobileM3ListItemPositionForIndex.forIndex(
                          index,
                          page.items.length,
                        ),
                        onTap: () => _openUnit(unit),
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

class _UnitHealthRow extends StatelessWidget {
  const _UnitHealthRow({
    required this.unit,
    required this.position,
    required this.onTap,
  });

  final UnitHealth unit;
  final MobileM3ListItemPosition position;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return MobileM3ListItem(
      position: position,
      leading: MobileM3LeadingIcon(
        icon: Symbols.home_work_rounded,
        backgroundColor: colorScheme.primaryContainer,
        foregroundColor: colorScheme.onPrimaryContainer,
      ),
      title: Row(
        children: [
          Expanded(
            child: Text(
              _unitLabel(unit),
              style: theme.textTheme.titleSmall?.copyWith(
                fontWeight: FontWeight.w700,
              ),
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
            ),
          ),
          const SizedBox(width: 8),
          _ToneChip(label: unit.simpleStage),
        ],
      ),
      supporting: [
        Text(
          unit.propertyName,
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
          _MetaChip(
            icon: Symbols.payments_rounded,
            label: '${_formatCurrency(unit.marketRent)}/mo',
          ),
          _MetaChip(
            icon: Symbols.build_rounded,
            label: '${unit.openWorkOrderCount} open',
          ),
          if (unit.leaseEndsInDays != null)
            _MetaChip(
              icon: Symbols.event_rounded,
              label: _leaseEndsLabel(unit.leaseEndsInDays!),
            ),
          if (unit.docsNeedingReviewCount > 0)
            _MetaChip(
              icon: Symbols.folder_open_rounded,
              label: '${unit.docsNeedingReviewCount} docs',
            ),
        ],
      ),
      trailing: Icon(
        Symbols.chevron_right_rounded,
        color: colorScheme.onSurfaceVariant,
      ),
      onTap: onTap,
    );
  }
}

class _ToneChip extends StatelessWidget {
  const _ToneChip({required this.label});

  final String label;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    final (bg, fg) = switch (label) {
      'Active' => (
        colorScheme.primaryContainer,
        colorScheme.onPrimaryContainer,
      ),
      'Renewal' => (
        colorScheme.tertiaryContainer,
        colorScheme.onTertiaryContainer,
      ),
      'Move-Out' => (colorScheme.errorContainer, colorScheme.onErrorContainer),
      'Turnover' => (
        colorScheme.secondaryContainer,
        colorScheme.onSecondaryContainer,
      ),
      _ => (colorScheme.surfaceContainerHighest, colorScheme.onSurfaceVariant),
    };

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(
        color: bg,
        borderRadius: BorderRadius.circular(999),
      ),
      child: Text(
        label.isEmpty ? 'Unit' : label,
        style: TextStyle(fontSize: 11, fontWeight: FontWeight.w700, color: fg),
      ),
    );
  }
}

class _MetaChip extends StatelessWidget {
  const _MetaChip({required this.icon, required this.label});

  final IconData icon;
  final String label;

  @override
  Widget build(BuildContext context) {
    final color = Theme.of(context).colorScheme.onSurfaceVariant;
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Icon(icon, size: 14, color: color),
        const SizedBox(width: 3),
        Text(label, style: TextStyle(fontSize: 12, color: color)),
      ],
    );
  }
}

class _UnitsEmptyBody extends StatelessWidget {
  const _UnitsEmptyBody({required this.hasSearch, required this.hasFilter});

  final bool hasSearch;
  final bool hasFilter;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;

    return ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      padding: const EdgeInsets.all(32),
      children: [
        const SizedBox(height: 96),
        Icon(
          Symbols.home_work_rounded,
          size: 48,
          color: colorScheme.onSurfaceVariant,
        ),
        const SizedBox(height: 12),
        Text(
          hasSearch || hasFilter ? 'No units match.' : 'No units yet',
          textAlign: TextAlign.center,
          style: Theme.of(context).textTheme.titleMedium,
        ),
        const SizedBox(height: 6),
        Text(
          hasSearch || hasFilter
              ? 'Try a different unit, property, sort, or filter.'
              : 'Units live under a property. Add a property and its units to start managing them here.',
          textAlign: TextAlign.center,
          style: TextStyle(color: colorScheme.onSurfaceVariant),
        ),
      ],
    );
  }
}

class _UnitsErrorBody extends StatelessWidget {
  const _UnitsErrorBody({required this.message, required this.onRetry});

  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;

    return ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      padding: const EdgeInsets.all(32),
      children: [
        const SizedBox(height: 96),
        Icon(Symbols.error_rounded, size: 40, color: colorScheme.error),
        const SizedBox(height: 12),
        Text(
          message,
          textAlign: TextAlign.center,
          style: TextStyle(color: colorScheme.error),
        ),
        const SizedBox(height: 16),
        Center(
          child: FilledButton.tonal(
            onPressed: onRetry,
            child: const Text('Retry'),
          ),
        ),
      ],
    );
  }
}

String _unitLabel(UnitHealth unit) {
  final value = unit.unitNumber.trim();
  if (value.isEmpty) return 'Unit';
  return value.toLowerCase().startsWith('unit ') ? value : 'Unit $value';
}

String _leaseEndsLabel(int days) {
  if (days == 0) return 'Lease ends today';
  if (days == 1) return 'Lease ends tomorrow';
  return 'Lease ends in ${days}d';
}

String _formatCurrency(double amount) {
  final rounded = amount.round();
  final s = rounded.toString();
  final buf = StringBuffer(r'$');
  final start = s.length % 3;
  if (start > 0) buf.write(s.substring(0, start));
  for (var i = start; i < s.length; i += 3) {
    if (i > 0) buf.write(',');
    buf.write(s.substring(i, i + 3));
  }
  return buf.toString();
}
