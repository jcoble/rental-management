import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/models/models.dart';
import '../../core/api/api_exception.dart';
import '../../core/auth/auth_controller.dart';
import '../../core/auth/auth_models.dart';
import '../../core/auth/mobile_access_policy.dart';
import '../../core/widgets/mobile_grid_controls.dart';
import '../../core/widgets/mobile_m3_list.dart';
import '../home/mobile_domain_chrome.dart';
import '../home/mobile_quick_action_fab.dart';
import '../home/mobile_quick_action_helpers.dart';
import 'property_form_sheet.dart';
import 'properties_repository.dart';
import 'property_detail_screen.dart';
import 'property_workspace_sections.dart';
import '../units/unit_navigation.dart';

/// Opens the "New property" bottom sheet and returns the atomic Property + Unit
/// setup result. A dismissed sheet returns `null`.
///
/// Shared by the properties-list FAB and the first-login Live setup screen so
/// both use the same create flow (no duplicate form). [onSaved] still fires on
/// save for callers that want to refresh a list in place.
Future<PropertySetupResult?> showAddPropertySheet(
  BuildContext context, {
  VoidCallback? onSaved,
}) async {
  PropertySetupResult? setupResult;
  final saved = await showPropertyFormSheet(
    context,
    onSaved: (_) => onSaved?.call(),
    onSetupSaved: (result) => setupResult = result,
  );
  return saved == null ? null : setupResult;
}

/// Full-page list of properties with pull-to-refresh and an add-property FAB.
class PropertiesListScreen extends ConsumerStatefulWidget {
  const PropertiesListScreen({super.key});

  @override
  ConsumerState<PropertiesListScreen> createState() =>
      _PropertiesListScreenState();
}

class _PropertiesListScreenState extends ConsumerState<PropertiesListScreen> {
  static const _pageSize = 20;

  final _searchCtrl = TextEditingController();
  String? _search;
  String? _typeFilter;
  String? _statusFilter;
  String _sort = 'name';
  int _skip = 0;

  PropertyListQuery get _query => PropertyListQuery(
    skip: _skip,
    take: _pageSize,
    search: _search,
    sort: _sort,
    type: _typeFilter,
    status: _statusFilter,
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

  void _setTypeFilter(String? type) {
    if (type == _typeFilter) return;
    setState(() {
      _typeFilter = type;
      _skip = 0;
    });
  }

  void _setStatusFilter(String? status) {
    if (status == _statusFilter) return;
    setState(() {
      _statusFilter = status;
      _skip = 0;
    });
  }

  Future<void> _refresh() async {
    ref.invalidate(propertiesPageProvider);
    await ref.read(propertiesPageProvider(_query).future);
  }

  void _openDetail(BuildContext context, Property property) {
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => PropertyDetailScreen(property: property),
      ),
    );
  }

  void _showAddSheet(BuildContext context) {
    showAddPropertySheet(
      context,
      onSaved: () {
        ref.invalidate(propertiesPageProvider);
        ref.read(propertiesProvider.notifier).refresh();
      },
    );
  }

  @override
  Widget build(BuildContext context) {
    final propertiesAsync = ref.watch(propertiesPageProvider(_query));
    final auth = ref.watch(authControllerProvider);
    final canManageRentals =
        auth is AuthStateAuthenticated &&
        hasAllPropertiesRentalsManageAuthority(
          access: auth.access,
          activeExperience: auth.activeExperience,
        );

    return Scaffold(
      appBar: mobileDomainRootAppBar(context, title: const Text('Properties')),
      floatingActionButton: MobileQuickActionFab(
        heroTag: 'properties-fab',
        primaryAction: canManageRentals
            ? MobileQuickAction(
                label: 'Add property',
                icon: Icons.add,
                onPressed: () => _showAddSheet(context),
              )
            : null,
        onChat: () => openMobileAssistant(context),
        onRecord: () => openMobileRecord(context),
        onScan: () => openMobileScan(context),
      ),
      body: Column(
        children: [
          MobileGridControlsBar(
            keyPrefix: 'properties',
            searchController: _searchCtrl,
            searchLabel: 'Search properties',
            onSearch: _submitSearch,
            onClearSearch: _clearSearch,
            sort: _sort,
            defaultSort: 'name',
            sortOptions: const [
              MobileGridControlOption(value: 'name', label: 'Name A-Z'),
              MobileGridControlOption(value: '-name', label: 'Name Z-A'),
              MobileGridControlOption(value: 'city', label: 'City A-Z'),
              MobileGridControlOption(
                value: '-updatedAt',
                label: 'Updated recently',
              ),
              MobileGridControlOption(value: '-unitCount', label: 'Most units'),
            ],
            onSortChanged: _setSort,
            filters: [
              MobileGridChoiceFilter(
                id: 'property-type',
                label: 'Property type',
                value: _typeFilter,
                allLabel: 'All types',
                options: const [
                  MobileGridControlOption(
                    value: 'SingleFamily',
                    label: 'Single-family',
                  ),
                  MobileGridControlOption(
                    value: 'MultiFamily',
                    label: 'Multi-family',
                  ),
                  MobileGridControlOption(value: 'Condo', label: 'Condo'),
                  MobileGridControlOption(value: 'Townhome', label: 'Townhome'),
                  MobileGridControlOption(
                    value: 'Commercial',
                    label: 'Commercial',
                  ),
                  MobileGridControlOption(
                    value: 'MixedUse',
                    label: 'Mixed use',
                  ),
                ],
                onChanged: _setTypeFilter,
              ),
              MobileGridChoiceFilter(
                id: 'property-status',
                label: 'Status',
                value: _statusFilter,
                allLabel: 'All statuses',
                options: const [
                  MobileGridControlOption(value: 'Active', label: 'Active'),
                  MobileGridControlOption(
                    value: 'UnderMaintenance',
                    label: 'Under maintenance',
                  ),
                  MobileGridControlOption(value: 'Inactive', label: 'Inactive'),
                ],
                onChanged: _setStatusFilter,
              ),
            ],
          ),
          Expanded(
            child: RefreshIndicator(
              onRefresh: _refresh,
              child: propertiesAsync.when(
                loading: () => const Center(child: CircularProgressIndicator()),
                error: (e, _) => _ErrorBody(
                  message: e is ApiException ? e.message : e.toString(),
                  onRetry: _refresh,
                ),
                data: (page) {
                  if (page.items.isEmpty) {
                    return _EmptyBody(
                      hasCriteria:
                          (_search ?? '').isNotEmpty ||
                          _typeFilter != null ||
                          _statusFilter != null,
                      onAdd: canManageRentals
                          ? () => _showAddSheet(context)
                          : null,
                    );
                  }
                  final bottomInset = MediaQuery.paddingOf(context).bottom;
                  return ListView.separated(
                    physics: const AlwaysScrollableScrollPhysics(),
                    padding: EdgeInsets.fromLTRB(
                      16,
                      12,
                      16,
                      160.0 + bottomInset,
                    ),
                    itemCount: page.items.length + 1,
                    separatorBuilder: (context, index) {
                      if (index == page.items.length - 1) {
                        return const SizedBox(height: 14);
                      }
                      return const MobileM3ListDivider();
                    },
                    itemBuilder: (context, index) {
                      if (index == page.items.length) {
                        return Padding(
                          padding: const EdgeInsetsDirectional.only(end: 72),
                          child: MobileGridPagingBar(
                            totalCount: page.totalCount,
                            skip: page.skip,
                            itemCount: page.items.length,
                            previousTooltip: 'Previous properties page',
                            nextTooltip: 'Next properties page',
                            onPrevious: page.hasPrevious
                                ? () => setState(() {
                                    _skip = (_skip - _pageSize).clamp(0, _skip);
                                  })
                                : null,
                            onNext: page.hasNext
                                ? () => setState(() => _skip += _pageSize)
                                : null,
                          ),
                        );
                      }
                      final property = page.items[index];
                      final entry = resolvePropertyWorkspaceEntry(
                        propertyId: property.id,
                        rentalStructure: property.rentalStructure.wireValue,
                        serverEntry: page.workspaceEntries[property.id],
                      );
                      return _PropertyCard(
                        property: property,
                        position: MobileM3ListItemPositionForIndex.forIndex(
                          index,
                          page.items.length,
                        ),
                        onTap: () => _openDetail(context, property),
                        onOpenUnitCommand:
                            entry.unitId == null || entry.unitId! <= 0
                            ? null
                            : () => openUnitCommandCenter(
                                context,
                                unitId: entry.unitId!,
                              ),
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

// ── Property Card ─────────────────────────────────────────────────────────────

class _PropertyCard extends StatelessWidget {
  const _PropertyCard({
    required this.property,
    required this.position,
    required this.onTap,
    this.onOpenUnitCommand,
  });

  final Property property;
  final MobileM3ListItemPosition position;
  final VoidCallback onTap;
  final VoidCallback? onOpenUnitCommand;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final unitCount = property.unitCount ?? 0;
    final occupied = property.occupiedUnits ?? 0;
    final isSingleRental =
        property.rentalStructure == RentalStructure.singleRental;

    return MobileM3ListItem(
      position: position,
      onTap: onTap,
      leading: MobileM3LeadingIcon(
        icon: Icons.apartment_outlined,
        backgroundColor: colorScheme.primaryContainer,
        foregroundColor: colorScheme.onPrimaryContainer,
      ),
      title: Text(
        property.name,
        style: theme.textTheme.titleSmall?.copyWith(
          fontWeight: FontWeight.w600,
        ),
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
      ),
      supporting: [
        Text(
          '${property.addressLine1}, ${property.city}, ${property.state} ${property.postalCode}',
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
          _StatusChip(status: property.status, colorScheme: colorScheme),
          _MetaChip(
            icon: isSingleRental
                ? Icons.home_outlined
                : Icons.apartment_outlined,
            label: isSingleRental
                ? 'One rental'
                : '$unitCount ${unitCount == 1 ? 'unit' : 'units'}',
          ),
          _MetaChip(
            icon: Icons.person_outline,
            label: isSingleRental
                ? (occupied > 0 ? 'Occupied' : 'Vacant')
                : '$occupied occupied',
          ),
          _MetaChip(
            icon: Icons.home_outlined,
            label: _formatPropertyType(property.type),
          ),
        ],
      ),
      trailing: Icon(Icons.chevron_right, color: colorScheme.onSurfaceVariant),
      actions: [
        if (onOpenUnitCommand != null)
          IconButton(
            tooltip: "Open today's summary",
            icon: const Icon(Icons.home_work_outlined),
            onPressed: onOpenUnitCommand,
          ),
      ],
    );
  }
}

class _StatusChip extends StatelessWidget {
  const _StatusChip({required this.status, required this.colorScheme});

  final String status;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    final isActive = status.toLowerCase() == 'active';
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(
        color: isActive
            ? colorScheme.primaryContainer
            : colorScheme.surfaceContainerHighest,
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        status,
        style: TextStyle(
          fontSize: 11,
          fontWeight: FontWeight.w600,
          color: isActive
              ? colorScheme.onPrimaryContainer
              : colorScheme.onSurfaceVariant,
        ),
      ),
    );
  }
}

String _formatPropertyType(String type) {
  return switch (type.trim()) {
    'MultiFamily' => 'Multi-family',
    'SingleFamily' => 'Single family',
    final other => other.replaceAll('_', ' '),
  };
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

// ── Empty / Error ─────────────────────────────────────────────────────────────

/// A15: a welcoming first-run empty state — a plain sentence explaining what a
/// property is, plus a primary "Add your first property" button (not just a
/// "tap +" hint), so the next step is obvious.
class _EmptyBody extends StatelessWidget {
  const _EmptyBody({required this.onAdd, required this.hasCriteria});

  final VoidCallback? onAdd;
  final bool hasCriteria;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              Icons.apartment_outlined,
              size: 48,
              color: colorScheme.onSurfaceVariant,
            ),
            const SizedBox(height: 12),
            Text(
              hasCriteria ? 'No matching properties' : 'No rentals yet',
              style: theme.textTheme.titleMedium?.copyWith(
                color: colorScheme.onSurface,
                fontWeight: FontWeight.w700,
              ),
            ),
            const SizedBox(height: 6),
            Text(
              hasCriteria
                  ? 'Try changing your search, filters, or sort.'
                  : 'A property is one building or address. Add your first to get '
                        'started.',
              textAlign: TextAlign.center,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: colorScheme.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 20),
            if (onAdd != null && !hasCriteria)
              FilledButton.icon(
                onPressed: onAdd,
                icon: const Icon(Icons.add),
                label: const Text('Add your first property'),
              ),
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
