import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/widgets/mobile_grid_controls.dart';
import '../../core/widgets/mobile_m3_list.dart';
import '../home/mobile_domain_chrome.dart';
import '../home/mobile_quick_action_fab.dart';
import '../home/mobile_quick_action_helpers.dart';
import 'dispatch_vendor_sheet.dart' show VendorRatingSummary;
import 'vendor_detail_screen.dart';
import 'vendor_form_sheet.dart';
import 'vendors_models.dart';
import 'vendors_repository.dart';

/// Landlord-facing list of vendors with their service type and rating summary.
class VendorsListScreen extends ConsumerStatefulWidget {
  const VendorsListScreen({super.key});

  @override
  ConsumerState<VendorsListScreen> createState() => _VendorsListScreenState();
}

class _VendorsListScreenState extends ConsumerState<VendorsListScreen> {
  static const _pageSize = 20;

  final _searchCtrl = TextEditingController();
  String? _search;
  String _sort = 'name';
  int _skip = 0;

  VendorListQuery get _query => VendorListQuery(
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

  void _refreshVendors() {
    ref.invalidate(vendorsProvider);
    ref.invalidate(vendorsBySortProvider(_sort));
    ref.invalidate(vendorsPageProvider);
  }

  void _showVendorForm(BuildContext context, {Vendor? vendor}) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      useRootNavigator: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) =>
          VendorFormSheet(existing: vendor, onSaved: _refreshVendors),
    );
  }

  @override
  Widget build(BuildContext context) {
    final vendorsAsync = ref.watch(vendorsPageProvider(_query));

    return Scaffold(
      appBar: mobileDomainRootAppBar(context, title: const Text('Vendors')),
      floatingActionButton: MobileQuickActionFab(
        heroTag: 'vendors-fab',
        primaryAction: MobileQuickAction(
          label: 'Add vendor',
          icon: Icons.add,
          onPressed: () => _showVendorForm(context),
        ),
        onChat: () => openMobileAssistant(context),
        onRecord: () => openMobileRecord(context),
        onScan: () => openMobileScan(context),
      ),
      body: RefreshIndicator(
        onRefresh: () async => _refreshVendors(),
        child: vendorsAsync.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (e, _) => _ErrorBody(
            message: e is ApiException ? e.message : e.toString(),
            onRetry: _refreshVendors,
          ),
          data: (page) {
            if (page.items.isEmpty) {
              return ListView(
                physics: const AlwaysScrollableScrollPhysics(),
                padding: const EdgeInsets.fromLTRB(16, 12, 16, 32),
                children: [
                  _VendorGridControls(
                    searchController: _searchCtrl,
                    sort: _sort,
                    onSearch: _submitSearch,
                    onClearSearch: _clearSearch,
                    onSortChanged: _setSort,
                  ),
                  _EmptyBody(
                    hasSearch: (_search ?? '').isNotEmpty,
                    onAdd: () => _showVendorForm(context),
                  ),
                ],
              );
            }
            return ListView.separated(
              physics: const AlwaysScrollableScrollPhysics(),
              padding: const EdgeInsets.fromLTRB(16, 12, 16, 32),
              itemCount: page.items.length + 2,
              separatorBuilder: (_, index) {
                if (index == 0) return const SizedBox(height: 12);
                if (index == page.items.length) {
                  return const SizedBox(height: 14);
                }
                return const MobileM3ListDivider();
              },
              itemBuilder: (_, i) {
                if (i == 0) {
                  return _VendorGridControls(
                    searchController: _searchCtrl,
                    sort: _sort,
                    onSearch: _submitSearch,
                    onClearSearch: _clearSearch,
                    onSortChanged: _setSort,
                  );
                }

                if (i == page.items.length + 1) {
                  return MobileGridPagingBar(
                    totalCount: page.totalCount,
                    skip: page.skip,
                    itemCount: page.items.length,
                    previousTooltip: 'Previous vendors page',
                    nextTooltip: 'Next vendors page',
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

                final vendor = page.items[i - 1];
                return _VendorCard(
                  vendor: vendor,
                  position: MobileM3ListItemPositionForIndex.forIndex(
                    i - 1,
                    page.items.length,
                  ),
                  onTap: () => Navigator.of(context).push<void>(
                    MaterialPageRoute<void>(
                      builder: (_) => VendorDetailScreen(vendor: vendor),
                    ),
                  ),
                );
              },
            );
          },
        ),
      ),
    );
  }
}

class _VendorGridControls extends StatelessWidget {
  const _VendorGridControls({
    required this.searchController,
    required this.sort,
    required this.onSearch,
    required this.onClearSearch,
    required this.onSortChanged,
  });

  final TextEditingController searchController;
  final String sort;
  final ValueChanged<String> onSearch;
  final VoidCallback onClearSearch;
  final ValueChanged<String?> onSortChanged;

  @override
  Widget build(BuildContext context) {
    return MobileGridControlsBar(
      keyPrefix: 'vendors',
      padding: EdgeInsets.zero,
      searchController: searchController,
      searchLabel: 'Search vendors',
      onSearch: onSearch,
      onClearSearch: onClearSearch,
      sort: sort,
      defaultSort: 'name',
      sortLabel: 'Sort vendors',
      sortOptions: const [
        MobileGridControlOption(value: 'name', label: 'Name A-Z'),
        MobileGridControlOption(value: '-name', label: 'Name Z-A'),
        MobileGridControlOption(value: 'serviceType', label: 'Service type'),
        MobileGridControlOption(value: '-updatedAt', label: 'Updated recently'),
      ],
      onSortChanged: onSortChanged,
    );
  }
}

class _VendorCard extends StatelessWidget {
  const _VendorCard({
    required this.vendor,
    required this.position,
    required this.onTap,
  });

  final Vendor vendor;
  final MobileM3ListItemPosition position;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    return MobileM3ListItem(
      position: position,
      leading: MobileM3LeadingIcon(
        icon: Icons.handyman_outlined,
        backgroundColor: cs.tertiaryContainer,
        foregroundColor: cs.onTertiaryContainer,
      ),
      title: Row(
        children: [
          Flexible(
            child: Text(
              vendor.name,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: theme.textTheme.titleSmall?.copyWith(
                fontWeight: FontWeight.w700,
              ),
            ),
          ),
          if (vendor.preferred) ...[
            const SizedBox(width: 6),
            Icon(Icons.star_rounded, size: 16, color: Colors.amber.shade600),
          ],
        ],
      ),
      supporting: [
        Text(
          vendor.serviceType,
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
          style: theme.textTheme.bodySmall?.copyWith(
            color: cs.onSurfaceVariant,
          ),
        ),
      ],
      meta: Row(
        children: [
          VendorRatingSummary(vendor: vendor),
          const SizedBox(width: 10),
          Flexible(
            child: Text(
              '${vendor.jobsCompleted} '
              'job${vendor.jobsCompleted == 1 ? '' : 's'}',
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: theme.textTheme.bodySmall?.copyWith(
                color: cs.onSurfaceVariant,
              ),
            ),
          ),
        ],
      ),
      trailing: Icon(Icons.chevron_right, color: cs.onSurfaceVariant),
      onTap: onTap,
    );
  }
}

class _EmptyBody extends StatelessWidget {
  const _EmptyBody({required this.hasSearch, required this.onAdd});

  final bool hasSearch;
  final VoidCallback onAdd;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Padding(
      padding: const EdgeInsets.fromLTRB(24, 80, 24, 24),
      child: Column(
        children: [
          Icon(Icons.handyman_outlined, size: 40, color: cs.onSurfaceVariant),
          const SizedBox(height: 12),
          Text(
            hasSearch ? 'No matching vendors.' : 'No vendors yet.',
            textAlign: TextAlign.center,
            style: TextStyle(color: cs.onSurfaceVariant),
          ),
          const SizedBox(height: 6),
          Text(
            hasSearch
                ? 'Try another name, service type or email.'
                : 'Add service providers here, then assign or text them from '
                      'work orders.',
            textAlign: TextAlign.center,
            style: Theme.of(
              context,
            ).textTheme.bodySmall?.copyWith(color: cs.onSurfaceVariant),
          ),
          if (!hasSearch) ...[
            const SizedBox(height: 18),
            FilledButton.icon(
              onPressed: onAdd,
              icon: const Icon(Icons.add),
              label: const Text('Add your first vendor'),
            ),
          ],
        ],
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
