import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/models.dart';
import '../../core/widgets/mobile_grid_controls.dart';
import '../../core/widgets/mobile_m3_list.dart';
import '../../core/widgets/tabbed_form_sheet.dart';
import '../home/mobile_domain_chrome.dart';
import '../home/mobile_quick_action_fab.dart';
import '../home/mobile_quick_action_helpers.dart';
import 'tenant_detail_screen.dart';
import 'tenants_repository.dart';

/// Searchable list of tenants with a FAB to add a new tenant.
class TenantsListScreen extends ConsumerStatefulWidget {
  const TenantsListScreen({super.key});

  @override
  ConsumerState<TenantsListScreen> createState() => _TenantsListScreenState();
}

class _TenantsListScreenState extends ConsumerState<TenantsListScreen> {
  static const _pageSize = 20;

  final _searchCtrl = TextEditingController();
  String? _search;
  String _sort = 'name';
  int _skip = 0;

  TenantListQuery get _listQuery => TenantListQuery(
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

  Future<void> _refresh() async {
    ref.invalidate(tenantsPageProvider);
    ref.invalidate(tenantsProvider);
  }

  void _openDetail(BuildContext context, Tenant tenant) {
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => TenantDetailScreen(tenant: tenant),
      ),
    );
  }

  void _showAddSheet(BuildContext context) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => TenantFormSheet(
        onSaved: () {
          ref.invalidate(tenantsPageProvider);
          ref.read(tenantsProvider.notifier).refresh();
        },
      ),
    );
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
    final tenantsAsync = ref.watch(tenantsPageProvider(_listQuery));
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return Scaffold(
      appBar: mobileDomainRootAppBar(context, title: const Text('Tenants')),
      floatingActionButton: MobileQuickActionFab(
        heroTag: 'tenants-fab',
        primaryAction: MobileQuickAction(
          label: 'Add tenant',
          icon: Icons.add,
          onPressed: () => _showAddSheet(context),
        ),
        onChat: () => openMobileAssistant(context),
        onRecord: () => openMobileRecord(context),
        onScan: () => openMobileScan(context),
      ),
      body: Column(
        children: [
          MobileGridControlsBar(
            keyPrefix: 'tenants',
            searchController: _searchCtrl,
            searchLabel: 'Search tenants',
            onSearch: _submitSearch,
            onClearSearch: _clearSearch,
            sort: _sort,
            defaultSort: 'name',
            sortLabel: 'Sort tenants',
            sortOptions: const [
              MobileGridControlOption(value: 'name', label: 'Name A-Z'),
              MobileGridControlOption(value: '-name', label: 'Name Z-A'),
              MobileGridControlOption(
                value: '-updatedAt',
                label: 'Recently updated',
              ),
              MobileGridControlOption(
                value: 'activeLeaseCount',
                label: 'Fewest active leases',
              ),
            ],
            onSortChanged: _setSort,
          ),
          Expanded(
            child: RefreshIndicator(
              onRefresh: _refresh,
              child: tenantsAsync.when(
                loading: () => const Center(child: CircularProgressIndicator()),
                error: (e, _) => _ErrorBody(
                  message: e is ApiException ? e.message : e.toString(),
                  onRetry: _refresh,
                ),
                data: (page) {
                  final tenants = page.items;
                  if (page.totalCount == 0 && _search == null) {
                    return _EmptyBody(onAdd: () => _showAddSheet(context));
                  }
                  if (tenants.isEmpty) {
                    return ListView(
                      physics: const AlwaysScrollableScrollPhysics(),
                      children: [
                        Padding(
                          padding: const EdgeInsets.all(24),
                          child: Text(
                            _search == null
                                ? 'No tenants on this page.'
                                : 'No tenants match "$_search".',
                            style: TextStyle(
                              color: colorScheme.onSurfaceVariant,
                            ),
                            textAlign: TextAlign.center,
                          ),
                        ),
                      ],
                    );
                  }
                  final bottomInset = MediaQuery.paddingOf(context).bottom;
                  return ListView.separated(
                    padding: EdgeInsets.fromLTRB(
                      16,
                      8,
                      16,
                      160.0 + bottomInset,
                    ),
                    itemCount: tenants.length + 1,
                    separatorBuilder: (_, idx) => idx >= tenants.length - 1
                        ? const SizedBox(height: 8)
                        : const MobileM3ListDivider(),
                    itemBuilder: (context, index) {
                      if (index == tenants.length) {
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
                      final tenant = tenants[index];
                      return _TenantCard(
                        tenant: tenant,
                        position: MobileM3ListItemPositionForIndex.forIndex(
                          index,
                          tenants.length,
                        ),
                        onTap: () => _openDetail(context, tenant),
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

// ── Tenant card ───────────────────────────────────────────────────────────────

class _TenantCard extends StatelessWidget {
  const _TenantCard({
    required this.tenant,
    required this.position,
    required this.onTap,
  });

  final Tenant tenant;
  final MobileM3ListItemPosition position;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final hasActiveLease = (tenant.activeLeaseCount ?? 0) > 0;
    final phone = tenant.phone;
    final email = tenant.email;

    return MobileM3ListItem(
      position: position,
      leading: _TenantInitialsAvatar(
        initials: _initials(tenant.firstName, tenant.lastName),
      ),
      title: Text(
        '${tenant.firstName} ${tenant.lastName}'.trim(),
        style: theme.textTheme.titleSmall?.copyWith(
          fontWeight: FontWeight.w700,
        ),
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
      ),
      supporting: [
        if (email != null)
          Text(
            email,
            style: theme.textTheme.bodySmall?.copyWith(
              color: colorScheme.onSurfaceVariant,
            ),
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
          ),
        if (phone != null)
          Text(
            phone,
            style: theme.textTheme.bodySmall?.copyWith(
              color: colorScheme.onSurfaceVariant,
            ),
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
          ),
      ],
      trailing: _LeaseStatusChip(
        active: hasActiveLease,
        count: tenant.activeLeaseCount ?? 0,
        colorScheme: colorScheme,
      ),
      onTap: onTap,
    );
  }

  static String _initials(String first, String last) {
    final f = first.isNotEmpty ? first[0].toUpperCase() : '';
    final l = last.isNotEmpty ? last[0].toUpperCase() : '';
    return '$f$l';
  }
}

class _TenantInitialsAvatar extends StatelessWidget {
  const _TenantInitialsAvatar({required this.initials});

  final String initials;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    return Container(
      width: 44,
      height: 44,
      decoration: BoxDecoration(
        color: colorScheme.primaryContainer,
        borderRadius: BorderRadius.circular(14),
      ),
      alignment: Alignment.center,
      child: Text(
        initials,
        style: TextStyle(
          color: colorScheme.onPrimaryContainer,
          fontWeight: FontWeight.w800,
          fontSize: 14,
        ),
      ),
    );
  }
}

class _LeaseStatusChip extends StatelessWidget {
  const _LeaseStatusChip({
    required this.active,
    required this.count,
    required this.colorScheme,
  });

  final bool active;
  final int count;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(
        color: active
            ? colorScheme.primaryContainer
            : colorScheme.surfaceContainerHighest,
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        active ? '$count active' : 'No lease',
        style: TextStyle(
          fontSize: 11,
          fontWeight: FontWeight.w600,
          color: active
              ? colorScheme.onPrimaryContainer
              : colorScheme.onSurfaceVariant,
        ),
      ),
    );
  }
}

// ── Empty / Error ─────────────────────────────────────────────────────────────

/// A15: a welcoming first-run empty state with a plain explanation and a
/// primary "Add your first tenant" button instead of a cold "tap +" hint.
class _EmptyBody extends StatelessWidget {
  const _EmptyBody({required this.onAdd});

  final VoidCallback onAdd;

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
              Icons.people_outline,
              size: 48,
              color: colorScheme.onSurfaceVariant,
            ),
            const SizedBox(height: 12),
            Text(
              'No tenants yet',
              style: theme.textTheme.titleMedium?.copyWith(
                color: colorScheme.onSurface,
                fontWeight: FontWeight.w700,
              ),
            ),
            const SizedBox(height: 6),
            Text(
              'Tenants are the people who rent from you. Adding their email or '
              'phone lets the app send them reminders.',
              textAlign: TextAlign.center,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: colorScheme.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 20),
            FilledButton.icon(
              onPressed: onAdd,
              icon: const Icon(Icons.add),
              label: const Text('Add your first tenant'),
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

// ── Tenant form bottom sheet (add / edit) ─────────────────────────────────────

/// Public so that [TenantDetailScreen] can reuse it.
class TenantFormSheet extends ConsumerStatefulWidget {
  const TenantFormSheet({super.key, required this.onSaved, this.existing});

  final VoidCallback onSaved;
  final Tenant? existing;

  @override
  ConsumerState<TenantFormSheet> createState() => _TenantFormSheetState();
}

class _TenantFormSheetState extends ConsumerState<TenantFormSheet> {
  final _formKey = GlobalKey<FormState>();

  late final TextEditingController _firstCtrl;
  late final TextEditingController _lastCtrl;
  late final TextEditingController _emailCtrl;
  late final TextEditingController _phoneCtrl;
  late final TextEditingController _emergencyCtrl;

  bool _saving = false;
  String? _error;

  bool get _isEdit => widget.existing != null;

  @override
  void initState() {
    super.initState();
    final t = widget.existing;
    _firstCtrl = TextEditingController(text: t?.firstName ?? '');
    _lastCtrl = TextEditingController(text: t?.lastName ?? '');
    _emailCtrl = TextEditingController(text: t?.email ?? '');
    _phoneCtrl = TextEditingController(text: t?.phone ?? '');
    _emergencyCtrl = TextEditingController(text: t?.emergencyContact ?? '');
  }

  @override
  void dispose() {
    _firstCtrl.dispose();
    _lastCtrl.dispose();
    _emailCtrl.dispose();
    _phoneCtrl.dispose();
    _emergencyCtrl.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;

    setState(() {
      _saving = true;
      _error = null;
    });

    final body = <String, dynamic>{
      'firstName': _firstCtrl.text.trim(),
      'lastName': _lastCtrl.text.trim(),
      if (_emailCtrl.text.trim().isNotEmpty) 'email': _emailCtrl.text.trim(),
      if (_phoneCtrl.text.trim().isNotEmpty) 'phone': _phoneCtrl.text.trim(),
      if (_emergencyCtrl.text.trim().isNotEmpty)
        'emergencyContact': _emergencyCtrl.text.trim(),
    };

    try {
      final repo = ref.read(tenantsRepositoryProvider);
      if (_isEdit) {
        body['clearEmail'] =
            _emailCtrl.text.trim().isEmpty && widget.existing!.email != null;
        body['clearPhone'] =
            _phoneCtrl.text.trim().isEmpty && widget.existing!.phone != null;
        body['clearEmergencyContact'] =
            _emergencyCtrl.text.trim().isEmpty &&
            widget.existing!.emergencyContact != null;
        await repo.updateTenant(widget.existing!.id, body);
      } else {
        await repo.createTenant(body);
      }
      widget.onSaved();
      if (mounted) Navigator.of(context).pop();
    } on ApiException catch (e) {
      setState(() => _error = e.message);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    const gap = SizedBox(height: 12);

    return Form(
      key: _formKey,
      child: TabbedFormSheet(
        title: _isEdit ? 'Edit Tenant' : 'New Tenant',
        saveLabel: _isEdit ? 'Save Changes' : 'Add Tenant',
        saving: _saving,
        error: _error,
        onSave: _submit,
        tabs: [
          TabbedFormStepSpec(
            label: 'Contact',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Row(
                  children: [
                    Expanded(
                      child: TextFormField(
                        key: const Key('tenant-first-name-field'),
                        controller: _firstCtrl,
                        textInputAction: TextInputAction.next,
                        textCapitalization: TextCapitalization.words,
                        decoration: const InputDecoration(
                          labelText: 'First name',
                        ),
                        validator: (v) => (v == null || v.trim().isEmpty)
                            ? 'First name is required'
                            : null,
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: TextFormField(
                        key: const Key('tenant-last-name-field'),
                        controller: _lastCtrl,
                        textInputAction: TextInputAction.next,
                        textCapitalization: TextCapitalization.words,
                        decoration: const InputDecoration(
                          labelText: 'Last name',
                        ),
                        validator: (v) => (v == null || v.trim().isEmpty)
                            ? 'Last name is required'
                            : null,
                      ),
                    ),
                  ],
                ),
                gap,
                TextFormField(
                  controller: _emailCtrl,
                  textInputAction: TextInputAction.next,
                  keyboardType: TextInputType.emailAddress,
                  decoration: const InputDecoration(
                    labelText: 'Email (optional)',
                  ),
                ),
                gap,
                TextFormField(
                  controller: _phoneCtrl,
                  textInputAction: TextInputAction.next,
                  keyboardType: TextInputType.phone,
                  decoration: const InputDecoration(
                    labelText: 'Phone (optional)',
                  ),
                ),
              ],
            ),
          ),
          TabbedFormStepSpec(
            label: 'Emergency',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                TextFormField(
                  controller: _emergencyCtrl,
                  textInputAction: TextInputAction.done,
                  decoration: const InputDecoration(
                    labelText: 'Emergency contact (optional)',
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}
