import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/widgets/mobile_grid_controls.dart';
import '../../core/widgets/mobile_m3_list.dart';
import '../home/mobile_domain_chrome.dart';
import '../home/mobile_quick_action_fab.dart';
import '../home/mobile_quick_action_helpers.dart';
import 'owner_detail_screen.dart';
import 'owner_form_sheet.dart';
import 'owners_models.dart';
import 'owners_repository.dart';

class OwnersListScreen extends ConsumerStatefulWidget {
  const OwnersListScreen({super.key});

  @override
  ConsumerState<OwnersListScreen> createState() => _OwnersListScreenState();
}

class _OwnersListScreenState extends ConsumerState<OwnersListScreen> {
  static const _pageSize = 20;

  final _searchCtrl = TextEditingController();
  String? _search;
  OwnerEntityType? _typeFilter;
  String _sort = 'name';
  int _skip = 0;

  OwnerListQuery get _query => OwnerListQuery(
    skip: _skip,
    take: _pageSize,
    search: _search,
    sort: _sort,
    ownerEntityType: _typeFilter,
  );

  @override
  void dispose() {
    _searchCtrl.dispose();
    super.dispose();
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

  void _setTypeFilter(OwnerEntityType? type) {
    if (type == _typeFilter) return;
    setState(() {
      _typeFilter = type;
      _skip = 0;
    });
  }

  void _showOwnerForm({OwnerEntity? owner}) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      useRootNavigator: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => OwnerFormSheet(
        existing: owner,
        onSaved: () => ref.invalidate(ownersPageProvider),
      ),
    );
  }

  void _openDetail(OwnerEntity owner) {
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => OwnerDetailScreen(
          owner: owner,
          onChanged: () => ref.invalidate(ownersPageProvider),
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final ownersAsync = ref.watch(ownersPageProvider(_query));

    return Scaffold(
      appBar: mobileDomainRootAppBar(context, title: const Text('Owners')),
      floatingActionButton: MobileQuickActionFab(
        heroTag: 'owners-fab',
        primaryAction: MobileQuickAction(
          label: 'Add owner',
          icon: Icons.add,
          onPressed: () => _showOwnerForm(),
        ),
        onChat: () => openMobileAssistant(context),
        onRecord: () => openMobileRecord(context),
        onScan: () => openMobileScan(context),
      ),
      body: Column(
        children: [
          MobileGridControlsBar(
            keyPrefix: 'owners',
            searchController: _searchCtrl,
            searchLabel: 'Search owners',
            onSearch: _submitSearch,
            onClearSearch: _clearSearch,
            sort: _sort,
            defaultSort: 'name',
            sortOptions: const [
              MobileGridControlOption(value: 'name', label: 'Name A-Z'),
              MobileGridControlOption(value: '-name', label: 'Name Z-A'),
              MobileGridControlOption(
                value: '-updatedAt',
                label: 'Updated recently',
              ),
              MobileGridControlOption(value: 'type', label: 'Owner type'),
            ],
            onSortChanged: _setSort,
            filters: [
              MobileGridChoiceFilter(
                id: 'owner-type',
                label: 'Owner type',
                value: _typeFilter?.name,
                allLabel: 'All owners',
                options: [
                  for (final type in OwnerEntityType.values)
                    MobileGridControlOption(
                      value: type.name,
                      label: type.label,
                    ),
                ],
                onChanged: (value) => _setTypeFilter(
                  value == null ? null : OwnerEntityType.values.byName(value),
                ),
              ),
            ],
          ),
          Expanded(
            child: RefreshIndicator(
              onRefresh: () async => ref.invalidate(ownersPageProvider),
              child: ownersAsync.when(
                loading: () => const Center(child: CircularProgressIndicator()),
                error: (error, _) => _ErrorBody(
                  message: error is ApiException
                      ? error.message
                      : error.toString(),
                  onRetry: () => ref.invalidate(ownersPageProvider),
                ),
                data: (page) {
                  if (page.items.isEmpty) {
                    return _EmptyBody(
                      hasSearch: (_search ?? '').isNotEmpty,
                      hasFilter: _typeFilter != null,
                      onAdd: () => _showOwnerForm(),
                    );
                  }

                  return ListView.separated(
                    physics: const AlwaysScrollableScrollPhysics(),
                    padding: const EdgeInsets.fromLTRB(16, 12, 16, 96),
                    itemCount: page.items.length + 1,
                    separatorBuilder: (_, index) {
                      if (index == page.items.length - 1) {
                        return const SizedBox(height: 14);
                      }
                      return const MobileM3ListDivider();
                    },
                    itemBuilder: (_, index) {
                      if (index == page.items.length) {
                        return MobileGridPagingBar(
                          totalCount: page.totalCount,
                          skip: page.skip,
                          itemCount: page.items.length,
                          previousTooltip: 'Previous owners page',
                          nextTooltip: 'Next owners page',
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

                      final owner = page.items[index];
                      return _OwnerCard(
                        owner: owner,
                        position: MobileM3ListItemPositionForIndex.forIndex(
                          index,
                          page.items.length,
                        ),
                        onTap: () => _openDetail(owner),
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

class _OwnerCard extends StatelessWidget {
  const _OwnerCard({
    required this.owner,
    required this.position,
    required this.onTap,
  });

  final OwnerEntity owner;
  final MobileM3ListItemPosition position;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final assignmentLabel = owner.assignedPropertyCount == 1
        ? '1 property'
        : '${owner.assignedPropertyCount} properties';

    return MobileM3ListItem(
      position: position,
      onTap: onTap,
      leading: MobileM3LeadingIcon(
        icon: Icons.account_balance_outlined,
        backgroundColor: colorScheme.primaryContainer,
        foregroundColor: colorScheme.onPrimaryContainer,
      ),
      title: Row(
        children: [
          Flexible(
            child: Text(
              owner.name,
              style: theme.textTheme.titleSmall?.copyWith(
                fontWeight: FontWeight.w600,
              ),
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
            ),
          ),
          if (owner.isPrimary) ...[
            const SizedBox(width: 6),
            Icon(Icons.verified_outlined, size: 16, color: colorScheme.primary),
          ],
        ],
      ),
      supporting: [
        Text(
          owner.typeLabel,
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
          _MetaChip(icon: Icons.apartment_outlined, label: assignmentLabel),
          if (owner.hasPhone)
            _MetaChip(icon: Icons.phone_outlined, label: owner.phone!),
          if (owner.hasEmail)
            _MetaChip(icon: Icons.email_outlined, label: owner.email!),
        ],
      ),
      trailing: Icon(Icons.chevron_right, color: colorScheme.onSurfaceVariant),
    );
  }
}

class _MetaChip extends StatelessWidget {
  const _MetaChip({required this.icon, required this.label});

  final IconData icon;
  final String label;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Icon(icon, size: 14, color: cs.onSurfaceVariant),
        const SizedBox(width: 4),
        Flexible(
          child: Text(
            label,
            overflow: TextOverflow.ellipsis,
            style: theme.textTheme.bodySmall?.copyWith(
              color: cs.onSurfaceVariant,
            ),
          ),
        ),
      ],
    );
  }
}

class _EmptyBody extends StatelessWidget {
  const _EmptyBody({
    required this.hasSearch,
    required this.hasFilter,
    required this.onAdd,
  });

  final bool hasSearch;
  final bool hasFilter;
  final VoidCallback onAdd;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    return ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(24, 80, 24, 24),
          child: Column(
            children: [
              Icon(
                Icons.account_balance_outlined,
                size: 40,
                color: cs.onSurfaceVariant,
              ),
              const SizedBox(height: 12),
              Text(
                hasSearch || hasFilter
                    ? 'No matching owners.'
                    : 'No owners yet.',
                textAlign: TextAlign.center,
                style: TextStyle(color: cs.onSurfaceVariant),
              ),
              const SizedBox(height: 6),
              Text(
                hasSearch || hasFilter
                    ? 'Try another name, email, phone or tax ID.'
                    : 'Add owners here, then assign them to properties.',
                textAlign: TextAlign.center,
                style: theme.textTheme.bodySmall?.copyWith(
                  color: cs.onSurfaceVariant,
                ),
              ),
              if (!hasSearch && !hasFilter) ...[
                const SizedBox(height: 18),
                FilledButton.icon(
                  onPressed: onAdd,
                  icon: const Icon(Icons.add),
                  label: const Text('Add your first owner'),
                ),
              ],
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
