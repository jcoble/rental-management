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
import '../properties/properties_repository.dart';
import '../tenants/tenants_repository.dart';
import '../units/unit_command_center_screen.dart';
import '../units/unit_navigation.dart';
import 'lease_form_defaults.dart';
import 'lease_form_payload.dart';
import 'leases_repository.dart';

const _monthNames = [
  '',
  'Jan',
  'Feb',
  'Mar',
  'Apr',
  'May',
  'Jun',
  'Jul',
  'Aug',
  'Sep',
  'Oct',
  'Nov',
  'Dec',
];

String _fmtDate(DateTime d) => '${_monthNames[d.month]} ${d.day}, ${d.year}';

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

/// Lease status values (string enum from server).
const _leaseStatuses = [
  'Draft',
  'Active',
  'NoticeGiven',
  'Expired',
  'Terminated',
];

enum _LeaseTenantMode { existing, newTenant }

const _rentTrackingStartOptions = <String, String>{
  'ForwardOnly': 'Start from today',
  'BackfillFromLeaseStart': 'Backfill from lease start',
  'CustomCutoffDate': 'Use cutoff date',
  'OpeningBalanceOnly': 'Use opening balance',
};

const _maximumLeaseAmount = 99999999.0;

bool _looksLikeEmail(String value) =>
    RegExp(r'^[^\s@]+@[^\s@]+\.[^\s@]+$').hasMatch(value.trim());

bool _isFiniteAmountInRange(
  double? value, {
  required double minimum,
  double maximum = _maximumLeaseAmount,
}) => value != null && value.isFinite && value >= minimum && value <= maximum;

DateTime _addCalendarYear(DateTime value) {
  final nextYear = value.year + 1;
  final lastDayOfMonth = DateTime(nextYear, value.month + 1, 0).day;
  return DateTime(nextYear, value.month, value.day.clamp(1, lastDayOfMonth));
}

/// List of all leases with "+" FAB to create a new lease.
class LeasesListScreen extends ConsumerStatefulWidget {
  const LeasesListScreen({super.key});

  @override
  ConsumerState<LeasesListScreen> createState() => _LeasesListScreenState();
}

class _LeasesListScreenState extends ConsumerState<LeasesListScreen> {
  static const _pageSize = 20;

  final _searchCtrl = TextEditingController();
  String? _search;
  String _sort = '-updatedAt';
  String _period = MobileGridPeriod.all;
  int _skip = 0;

  LeaseListQuery get _query {
    final dateRange = mobileGridDateRangeForPeriod(_period);
    return LeaseListQuery(
      skip: _skip,
      take: _pageSize,
      search: _search,
      sort: _sort,
      activeFrom: dateRange.from,
      activeTo: dateRange.to,
    );
  }

  @override
  void initState() {
    super.initState();
    Future.microtask(() => ref.read(leasesProvider.notifier).load());
  }

  @override
  void dispose() {
    _searchCtrl.dispose();
    super.dispose();
  }

  Future<void> _refresh() async {
    await ref.read(leasesProvider.notifier).refresh();
    return ref.refresh(leasesPageProvider(_query).future);
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

  void _openDetail(BuildContext context, Lease lease) {
    openUnitCommandCenter(
      context,
      unitId: lease.unitId,
      initialTab: UnitCommandCenterTab.lease,
      lease: lease,
    );
  }

  void _showAddSheet(BuildContext context) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      isDismissible: false,
      enableDrag: false,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => LeaseFormSheet(
        onSaved: () {
          ref.read(leasesProvider.notifier).refresh();
          ref.invalidate(leasesPageProvider);
        },
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final leasesAsync = ref.watch(leasesPageProvider(_query));

    return Scaffold(
      appBar: mobileDomainRootAppBar(context, title: const Text('Leases')),
      floatingActionButton: MobileQuickActionFab(
        heroTag: 'leases-fab',
        primaryAction: MobileQuickAction(
          label: 'Create lease',
          icon: Icons.add,
          onPressed: () => _showAddSheet(context),
        ),
        onChat: () => openMobileAssistant(context),
        onRecord: () => openMobileRecord(context),
        onScan: () => openMobileScan(context),
      ),
      body: RefreshIndicator(
        onRefresh: _refresh,
        child: leasesAsync.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (e, _) => _ErrorBody(
            message: e is ApiException ? e.message : e.toString(),
            onRetry: _refresh,
          ),
          data: (page) {
            if (page.items.isEmpty) {
              final bottomInset = MediaQuery.paddingOf(context).bottom;
              return ListView(
                physics: const AlwaysScrollableScrollPhysics(),
                padding: EdgeInsets.fromLTRB(16, 16, 16, 160.0 + bottomInset),
                children: [
                  _LeasesGridControls(
                    searchController: _searchCtrl,
                    sort: _sort,
                    period: _period,
                    onSearch: _submitSearch,
                    onClearSearch: _clearSearch,
                    onSortChanged: _setSort,
                    onPeriodChanged: _setPeriod,
                  ),
                  const SizedBox(height: 24),
                  _EmptyBody(
                    hasSearch: (_search ?? '').isNotEmpty,
                    onAdd: () => _showAddSheet(context),
                  ),
                ],
              );
            }
            final bottomInset = MediaQuery.paddingOf(context).bottom;
            return ListView.separated(
              padding: EdgeInsets.fromLTRB(16, 16, 16, 160.0 + bottomInset),
              itemCount: page.items.length + 2,
              separatorBuilder: (_, idx) {
                if (idx == 0 || idx == page.items.length) {
                  return const SizedBox(height: 8);
                }
                return const MobileM3ListDivider();
              },
              itemBuilder: (context, index) {
                if (index == 0) {
                  return _LeasesGridControls(
                    searchController: _searchCtrl,
                    sort: _sort,
                    period: _period,
                    onSearch: _submitSearch,
                    onClearSearch: _clearSearch,
                    onSortChanged: _setSort,
                    onPeriodChanged: _setPeriod,
                  );
                }

                if (index == page.items.length + 1) {
                  return MobileGridPagingBar(
                    totalCount: page.totalCount,
                    skip: page.skip,
                    itemCount: page.items.length,
                    previousTooltip: 'Previous leases page',
                    nextTooltip: 'Next leases page',
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

                final leaseIndex = index - 1;
                final lease = page.items[leaseIndex];
                return _LeaseCard(
                  lease: lease,
                  position: MobileM3ListItemPositionForIndex.forIndex(
                    leaseIndex,
                    page.items.length,
                  ),
                  onTap: () => _openDetail(context, lease),
                );
              },
            );
          },
        ),
      ),
    );
  }
}

class _LeasesGridControls extends StatelessWidget {
  const _LeasesGridControls({
    required this.searchController,
    required this.sort,
    required this.period,
    required this.onSearch,
    required this.onClearSearch,
    required this.onSortChanged,
    required this.onPeriodChanged,
  });

  final TextEditingController searchController;
  final String sort;
  final String period;
  final ValueChanged<String> onSearch;
  final VoidCallback onClearSearch;
  final ValueChanged<String?> onSortChanged;
  final ValueChanged<String?> onPeriodChanged;

  @override
  Widget build(BuildContext context) {
    return MobileGridControlsBar(
      keyPrefix: 'leases',
      padding: EdgeInsets.zero,
      searchController: searchController,
      searchLabel: 'Search leases',
      onSearch: onSearch,
      onClearSearch: onClearSearch,
      sort: sort,
      defaultSort: '-updatedAt',
      sortLabel: 'Sort leases',
      sortOptions: const [
        MobileGridControlOption(value: '-updatedAt', label: 'Updated recently'),
        MobileGridControlOption(value: 'tenantName', label: 'Tenant A-Z'),
        MobileGridControlOption(value: 'propertyName', label: 'Property A-Z'),
        MobileGridControlOption(value: '-startDate', label: 'Start newest'),
        MobileGridControlOption(value: 'startDate', label: 'Start oldest'),
        MobileGridControlOption(value: '-monthlyRent', label: 'Rent high-low'),
      ],
      onSortChanged: onSortChanged,
      period: period,
      defaultPeriod: MobileGridPeriod.all,
      periodLabel: 'Active period',
      onPeriodChanged: onPeriodChanged,
    );
  }
}

// ── Lease card ────────────────────────────────────────────────────────────────

class _LeaseCard extends StatelessWidget {
  const _LeaseCard({
    required this.lease,
    required this.position,
    required this.onTap,
  });

  final Lease lease;
  final MobileM3ListItemPosition position;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final title = lease.tenantName ?? 'Lease #${lease.leaseNumber}';
    final propertyLabel =
        '${lease.propertyName ?? 'Property #${lease.propertyId}'}'
        '${lease.unitNumber != null ? ' · Unit ${lease.unitNumber}' : ''}';
    final dateLabel =
        '${_fmtDate(lease.startDate)} - ${_fmtDate(lease.endDate)}';

    return MobileM3ListItem(
      position: position,
      leading: MobileM3LeadingIcon(
        icon: Icons.description_outlined,
        backgroundColor: colorScheme.primaryContainer,
        foregroundColor: colorScheme.onPrimaryContainer,
      ),
      title: Text(
        title,
        style: theme.textTheme.titleSmall?.copyWith(
          fontWeight: FontWeight.w700,
        ),
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
      ),
      supporting: [
        Text(
          propertyLabel,
          style: theme.textTheme.bodySmall?.copyWith(
            color: colorScheme.onSurfaceVariant,
          ),
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
        ),
        Text(
          '$dateLabel · ${_formatCurrency(lease.monthlyRent)}/mo',
          style: theme.textTheme.bodySmall?.copyWith(
            color: colorScheme.onSurfaceVariant,
          ),
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
        ),
      ],
      trailing: _StatusChip(status: lease.status, colorScheme: colorScheme),
      onTap: onTap,
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
    final isNotice = status.toLowerCase() == 'noticegiven';
    Color bgColor;
    Color fgColor;

    if (isActive) {
      bgColor = colorScheme.primaryContainer;
      fgColor = colorScheme.onPrimaryContainer;
    } else if (isNotice) {
      bgColor = colorScheme.tertiaryContainer;
      fgColor = colorScheme.onTertiaryContainer;
    } else {
      bgColor = colorScheme.surfaceContainerHighest;
      fgColor = colorScheme.onSurfaceVariant;
    }

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(
        color: bgColor,
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        status,
        style: TextStyle(
          fontSize: 11,
          fontWeight: FontWeight.w600,
          color: fgColor,
        ),
      ),
    );
  }
}

// ── Empty / Error ─────────────────────────────────────────────────────────────

/// A15: a welcoming first-run empty state with a plain explanation and a
/// primary "Create your first lease" button instead of a cold "tap +" hint.
class _EmptyBody extends StatelessWidget {
  const _EmptyBody({required this.hasSearch, required this.onAdd});

  final bool hasSearch;
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
              Icons.description_outlined,
              size: 48,
              color: colorScheme.onSurfaceVariant,
            ),
            const SizedBox(height: 12),
            Text(
              hasSearch ? 'No matching leases' : 'No leases yet',
              style: theme.textTheme.titleMedium?.copyWith(
                color: colorScheme.onSurface,
                fontWeight: FontWeight.w700,
              ),
            ),
            const SizedBox(height: 6),
            Text(
              hasSearch
                  ? 'Try another tenant, property, unit or lease number.'
                  : 'A lease ties a tenant to a unit and sets the rent, dates, and '
                        'deposit. Create your first to start tracking rent.',
              textAlign: TextAlign.center,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: colorScheme.onSurfaceVariant,
              ),
            ),
            if (!hasSearch) ...[
              const SizedBox(height: 20),
              FilledButton.icon(
                onPressed: onAdd,
                icon: const Icon(Icons.add),
                label: const Text('Create your first lease'),
              ),
            ],
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

// ── Lease form bottom sheet (add / edit) ──────────────────────────────────────
//
// Flow: pick a property → unit dropdown updates → pick tenant → fill dates/amounts.

/// Public so that [LeaseDetailScreen] can reuse it.
class LeaseFormSheet extends ConsumerStatefulWidget {
  const LeaseFormSheet({
    super.key,
    required this.onSaved,
    this.existing,
    this.initialPropertyId,
    this.initialUnitId,
    this.initialPropertyLabel,
    this.initialUnitLabel,
    this.unitDefaults,
  });

  final VoidCallback onSaved;
  final Lease? existing;
  final int? initialPropertyId;
  final int? initialUnitId;
  final String? initialPropertyLabel;
  final String? initialUnitLabel;
  final UnitLeaseFormDefaults? unitDefaults;

  @override
  ConsumerState<LeaseFormSheet> createState() => _LeaseFormSheetState();
}

class _LeaseFormSheetState extends ConsumerState<LeaseFormSheet> {
  final _formKey = GlobalKey<FormState>();

  // Dropdown selections
  int? _selectedPropertyId;
  int? _selectedUnitId;
  List<int> _selectedTenantIds = <int>[];
  _LeaseTenantMode _tenantMode = _LeaseTenantMode.existing;
  String _selectedStatus = 'Active';
  String _rentTrackingStartMode = 'ForwardOnly';
  bool _rentTrackingChanged = false;

  // Date pickers
  DateTime? _startDate;
  DateTime? _endDate;
  DateTime? _rentTrackingStartDate;
  DateTime? _openingBalanceAsOfDate;

  // Text controllers
  late final TextEditingController _leaseNumberCtrl;
  late final TextEditingController _rentCtrl;
  late final TextEditingController _depositCtrl;
  late final TextEditingController _lateFeeCtrl;
  late final TextEditingController _dueDayCtrl;
  late final TextEditingController _notesCtrl;
  late final TextEditingController _newTenantFirstNameCtrl;
  late final TextEditingController _newTenantLastNameCtrl;
  late final TextEditingController _newTenantEmailCtrl;
  late final TextEditingController _newTenantPhoneCtrl;
  late final TextEditingController _openingBalanceAmountCtrl;
  late final TextEditingController _openingBalanceNoteCtrl;

  bool _saving = false;
  String? _error;

  bool get _isEdit => widget.existing != null;
  bool get _isUnitPrefilled =>
      widget.unitDefaults != null ||
      (widget.initialPropertyId != null && widget.initialUnitId != null);

  int? get _initialPropertyId =>
      widget.unitDefaults?.propertyId ?? widget.initialPropertyId;

  int? get _initialUnitId =>
      widget.unitDefaults?.unitId ?? widget.initialUnitId;

  String? get _initialPropertyLabel =>
      widget.unitDefaults?.propertyLabel ?? widget.initialPropertyLabel;

  String? get _initialUnitLabel =>
      widget.unitDefaults?.unitLabel ?? widget.initialUnitLabel;

  @override
  void initState() {
    super.initState();
    final e = widget.existing;
    final defaults = widget.unitDefaults;
    _selectedPropertyId = _initialPropertyId;
    _selectedUnitId = _initialUnitId;
    _leaseNumberCtrl = TextEditingController(
      text: e?.leaseNumber ?? 'L-${DateTime.now().year}-001',
    );
    _rentCtrl = TextEditingController(
      text: e?.monthlyRent.toString() ?? defaults?.monthlyRent ?? '',
    );
    _depositCtrl = TextEditingController(
      text: e?.securityDeposit.toString() ?? defaults?.securityDeposit ?? '',
    );
    _lateFeeCtrl = TextEditingController(
      text: e?.lateFeeAmount.toString() ?? defaults?.lateFeeAmount ?? '75',
    );
    _dueDayCtrl = TextEditingController(
      text: e?.rentDueDay.toString() ?? defaults?.rentDueDay ?? '1',
    );
    _notesCtrl = TextEditingController(text: e?.notes ?? '');
    _newTenantFirstNameCtrl = TextEditingController();
    _newTenantLastNameCtrl = TextEditingController();
    _newTenantEmailCtrl = TextEditingController();
    _newTenantPhoneCtrl = TextEditingController();
    _openingBalanceAmountCtrl = TextEditingController();
    _openingBalanceNoteCtrl = TextEditingController();
    _selectedStatus = defaults?.status ?? _selectedStatus;

    if (e != null) {
      _selectedPropertyId = e.propertyId;
      _selectedUnitId = e.unitId;
      _selectedTenantIds = e.tenantIds.isNotEmpty
          ? List<int>.of(e.tenantIds)
          : <int>[if (e.tenantId > 0) e.tenantId];
      _selectedStatus = _leaseStatuses.contains(e.status)
          ? e.status
          : _leaseStatuses.first;
      _startDate = e.startDate;
      _endDate = e.endDate;
      _rentTrackingStartDate = e.rentTrackingStartDate;
      _rentTrackingStartMode = e.rentTrackingStartDate != null
          ? 'CustomCutoffDate'
          : (e.status == 'Active' ? 'BackfillFromLeaseStart' : 'ForwardOnly');
    }

    // Load dropdowns
    Future.microtask(() {
      if (_isEdit || _isUnitPrefilled) {
        ref.read(propertiesProvider.notifier).load();
      }
      final propertyId = _selectedPropertyId;
      if (propertyId != null && (_isEdit || _isUnitPrefilled)) {
        ref.read(unitsProvider(propertyId).notifier).load();
      }
    });
  }

  @override
  void dispose() {
    _leaseNumberCtrl.dispose();
    _rentCtrl.dispose();
    _depositCtrl.dispose();
    _lateFeeCtrl.dispose();
    _dueDayCtrl.dispose();
    _notesCtrl.dispose();
    _newTenantFirstNameCtrl.dispose();
    _newTenantLastNameCtrl.dispose();
    _newTenantEmailCtrl.dispose();
    _newTenantPhoneCtrl.dispose();
    _openingBalanceAmountCtrl.dispose();
    _openingBalanceNoteCtrl.dispose();
    super.dispose();
  }

  Future<void> _pickDate(BuildContext context, {required bool isStart}) async {
    final initial = isStart
        ? (_startDate ?? DateTime.now())
        : (_endDate ?? DateTime.now().add(const Duration(days: 365)));
    final picked = await showDatePicker(
      context: context,
      initialDate: initial,
      firstDate: DateTime(2000),
      lastDate: DateTime(2100),
    );
    if (picked != null) {
      setState(() {
        if (isStart) {
          _startDate = picked;
          _endDate ??= _addCalendarYear(picked);
        } else {
          _endDate = picked;
        }
        if ((_startDate != null && _endDate != null) ||
            (isStart && _error == 'Please select a start date.') ||
            (!isStart && _error == 'Please select an end date.')) {
          _error = null;
        }
      });
    }
  }

  Future<void> _pickTrackingDate(
    BuildContext context, {
    required bool isOpeningBalance,
  }) async {
    final current = isOpeningBalance
        ? _openingBalanceAsOfDate
        : _rentTrackingStartDate;
    final initial = current ?? _startDate ?? DateTime.now();
    final picked = await showDatePicker(
      context: context,
      initialDate: initial,
      firstDate: DateTime(2000),
      lastDate: DateTime(2100),
    );
    if (picked == null) return;
    setState(() {
      if (isOpeningBalance) {
        _openingBalanceAsOfDate = picked;
      } else {
        _rentTrackingStartDate = picked;
      }
      _rentTrackingChanged = true;
      _error = null;
    });
  }

  bool _validateLocationStep() {
    if (_selectedPropertyId == null) {
      setState(() => _error = 'Select a property.');
      return false;
    }
    if (_selectedUnitId == null) {
      setState(() => _error = 'Select a unit.');
      return false;
    }
    if (_error == 'Select a property.' || _error == 'Select a unit.') {
      setState(() => _error = null);
    }
    return true;
  }

  bool _validateTenantsStep() {
    if (_tenantMode == _LeaseTenantMode.existing &&
        _selectedTenantIds.isEmpty) {
      setState(() => _error = 'Select at least one tenant.');
      return false;
    }
    if (_tenantMode == _LeaseTenantMode.newTenant) {
      final firstName = _newTenantFirstNameCtrl.text.trim();
      final lastName = _newTenantLastNameCtrl.text.trim();
      final email = _newTenantEmailCtrl.text.trim();
      final phone = _newTenantPhoneCtrl.text.trim();
      if (firstName.isEmpty ||
          lastName.isEmpty ||
          firstName.length > 100 ||
          lastName.length > 100 ||
          email.isEmpty ||
          email.length > 200 ||
          !_looksLikeEmail(email) ||
          phone.isEmpty ||
          phone.length > 50) {
        setState(() => _error = 'Complete the required tenant details.');
        return false;
      }
    }
    if (_error == 'Select at least one tenant.' ||
        _error == 'Complete the required tenant details.') {
      setState(() => _error = null);
    }
    return true;
  }

  bool _validateDatesStep() {
    if (_startDate == null) {
      setState(() => _error = 'Please select a start date.');
      return false;
    }
    if (_endDate == null) {
      setState(() => _error = 'Please select an end date.');
      return false;
    }
    if (_endDate!.isBefore(_startDate!)) {
      setState(() => _error = 'End date must be on or after start date.');
      return false;
    }
    if (_error == 'Please select a start date.' ||
        _error == 'Please select an end date.' ||
        _error == 'Please select start and end dates.' ||
        _error == 'End date must be on or after start date.') {
      setState(() => _error = null);
    }
    return true;
  }

  bool _validateIdentityStep() {
    if (_leaseNumberCtrl.text.trim().isEmpty) {
      setState(() => _error = 'Enter a lease number.');
      return false;
    }
    if (_leaseNumberCtrl.text.trim().length > 100) {
      setState(() => _error = 'Lease number must be 100 characters or less.');
      return false;
    }
    if (_error == 'Enter a lease number.' ||
        _error == 'Lease number must be 100 characters or less.') {
      setState(() => _error = null);
    }
    return true;
  }

  bool _validateRentStep() {
    final rent = double.tryParse(_rentCtrl.text);
    final deposit = double.tryParse(_depositCtrl.text);
    if (!_isFiniteAmountInRange(rent, minimum: 0.01)) {
      setState(
        () => _error = 'Monthly rent must be between \$0.01 and \$99,999,999.',
      );
      return false;
    }
    if (!_isFiniteAmountInRange(deposit, minimum: 0)) {
      setState(
        () => _error = 'Security deposit must be between \$0 and \$99,999,999.',
      );
      return false;
    }
    if (_error == 'Monthly rent must be between \$0.01 and \$99,999,999.' ||
        _error == 'Security deposit must be between \$0 and \$99,999,999.') {
      setState(() => _error = null);
    }
    return true;
  }

  bool _validateFeesStep() {
    final lateFee = double.tryParse(_lateFeeCtrl.text);
    final dueDay = int.tryParse(_dueDayCtrl.text);
    if (!_isFiniteAmountInRange(lateFee, minimum: 0)) {
      setState(() => _error = 'Late fee must be between \$0 and \$99,999,999.');
      return false;
    }
    if (dueDay == null || dueDay < 1 || dueDay > 31) {
      setState(() => _error = 'Rent due day must be between 1 and 31.');
      return false;
    }
    if (_error == 'Late fee must be between \$0 and \$99,999,999.' ||
        _error == 'Rent due day must be between 1 and 31.') {
      setState(() => _error = null);
    }
    return true;
  }

  bool _validateStatusStep() {
    if (_notesCtrl.text.trim().length > 2000) {
      setState(() => _error = 'Notes must be 2,000 characters or less.');
      return false;
    }
    if (_error == 'Notes must be 2,000 characters or less.') {
      setState(() => _error = null);
    }
    return true;
  }

  bool _validateTrackingStep() {
    if (_selectedStatus != 'Active') return true;
    if (_rentTrackingStartMode == 'CustomCutoffDate' &&
        _rentTrackingStartDate == null) {
      setState(() => _error = 'Select a rent tracking cutoff date.');
      return false;
    }
    if (_rentTrackingStartMode == 'OpeningBalanceOnly') {
      final amountText = _openingBalanceAmountCtrl.text.trim();
      final note = _openingBalanceNoteCtrl.text.trim();
      final hasRelatedValue =
          _openingBalanceAsOfDate != null || note.isNotEmpty;
      if (hasRelatedValue && double.tryParse(amountText) == null) {
        setState(() => _error = 'Enter the opening balance amount.');
        return false;
      }
      if (amountText.isNotEmpty && double.tryParse(amountText) == null) {
        setState(() => _error = 'Enter a valid opening balance amount.');
        return false;
      }
      final amount = double.tryParse(amountText);
      if (amountText.isNotEmpty &&
          !_isFiniteAmountInRange(amount, minimum: -_maximumLeaseAmount)) {
        setState(
          () => _error =
              'Opening balance must be between -\$99,999,999 and \$99,999,999.',
        );
        return false;
      }
      if (amountText.isNotEmpty && _openingBalanceAsOfDate == null) {
        setState(() => _error = 'Select the opening balance as-of date.');
        return false;
      }
      if (note.length > 2000) {
        setState(
          () =>
              _error = 'Opening balance note must be 2,000 characters or less.',
        );
        return false;
      }
    }
    setState(() => _error = null);
    return true;
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;
    if (!_validateLocationStep() ||
        !_validateTenantsStep() ||
        !_validateDatesStep() ||
        !_validateIdentityStep() ||
        !_validateRentStep() ||
        !_validateFeesStep() ||
        !_validateStatusStep() ||
        !_validateTrackingStep()) {
      return;
    }

    setState(() {
      _saving = true;
      _error = null;
    });

    final body = buildLeaseSubmitPayload(
      isEdit: _isEdit,
      propertyId: _selectedPropertyId!,
      unitId: _selectedUnitId!,
      tenantIds: _tenantMode == _LeaseTenantMode.existing
          ? _selectedTenantIds
          : const [],
      newTenant: _tenantMode == _LeaseTenantMode.newTenant
          ? buildInlineTenantPayload(
              firstName: _newTenantFirstNameCtrl.text,
              lastName: _newTenantLastNameCtrl.text,
              email: _newTenantEmailCtrl.text,
              phone: _newTenantPhoneCtrl.text,
            )
          : null,
      startDate: _startDate!,
      endDate: _endDate!,
      monthlyRent: _rentCtrl.text,
      securityDeposit: _depositCtrl.text,
      lateFeeAmount: _lateFeeCtrl.text,
      rentDueDay: _dueDayCtrl.text,
      status: _selectedStatus,
      leaseNumber: _leaseNumberCtrl.text,
      rentTrackingStartMode: _isEdit && !_rentTrackingChanged
          ? null
          : _rentTrackingStartMode,
      rentTrackingStartDate: _rentTrackingStartDate,
      openingBalanceAmount: _openingBalanceAmountCtrl.text,
      openingBalanceAsOfDate: _openingBalanceAsOfDate,
      openingBalanceNote: _openingBalanceNoteCtrl.text,
      notes: _notesCtrl.text,
    );

    try {
      final repo = ref.read(leasesRepositoryProvider);
      if (_isEdit) {
        await repo.updateLease(widget.existing!.id, body);
      } else {
        await repo.createLease(body);
      }
      widget.onSaved();
      if (mounted) Navigator.of(context).pop();
    } on ApiException catch (e) {
      setState(() => _error = e.message);
      rethrow;
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final unrestrictedPickers = _isEdit || _isUnitPrefilled;
    final propertiesAsync = unrestrictedPickers
        ? ref.watch(propertiesProvider)
        : ref.watch(availableForLeasePropertiesProvider);
    final tenantsQuery = TenantListQuery(
      take: 200,
      sort: 'name',
      availableForLease: true,
      includeLeaseId: _isEdit ? widget.existing!.id : null,
    );
    final tenantsAsync = ref.watch(tenantsPageProvider(tenantsQuery));

    final properties = propertiesAsync.value ?? <Property>[];
    final tenants = tenantsAsync.value?.items ?? <Tenant>[];

    // When property changes, reset unit selection.
    final unitsAsync = _selectedPropertyId != null
        ? unrestrictedPickers
              ? ref.watch(unitsProvider(_selectedPropertyId!))
              : ref.watch(availableForLeaseUnitsProvider(_selectedPropertyId!))
        : const AsyncValue<List<Unit>>.data([]);
    final units = unitsAsync.value ?? <Unit>[];
    const gap = SizedBox(height: 12);

    return Form(
      key: _formKey,
      child: TabbedFormSheet(
        title: _isEdit ? 'Edit Lease' : 'New Lease',
        saveLabel: _isEdit ? 'Save Changes' : 'Create Lease',
        saving: _saving,
        error: _error,
        onSave: _submit,
        tabs: [
          TabbedFormStepSpec(
            label: 'Location',
            validate: _validateLocationStep,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                if (_isUnitPrefilled)
                  _ReadOnlyFormValue(
                    label: 'Property',
                    value:
                        _initialPropertyLabel ??
                        'Property #$_initialPropertyId',
                  )
                else
                  Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      if (!_isEdit &&
                          propertiesAsync.hasValue &&
                          properties.isEmpty)
                        const _FormHint(
                          text: 'No properties have a vacant unit available.',
                        ),
                      DropdownButtonFormField<int>(
                        initialValue:
                            properties.any((p) => p.id == _selectedPropertyId)
                            ? _selectedPropertyId
                            : null,
                        decoration: const InputDecoration(
                          labelText: 'Property',
                        ),
                        items: properties
                            .map(
                              (p) => DropdownMenuItem(
                                value: p.id,
                                child: Text(
                                  p.name,
                                  overflow: TextOverflow.ellipsis,
                                ),
                              ),
                            )
                            .toList(),
                        onChanged: (v) {
                          setState(() {
                            _selectedPropertyId = v;
                            _selectedUnitId = null;
                          });
                          if (v != null && unrestrictedPickers) {
                            ref.read(unitsProvider(v).notifier).load();
                          }
                        },
                        validator: (_) => _selectedPropertyId == null
                            ? 'Select a property'
                            : null,
                      ),
                    ],
                  ),
                gap,
                if (_isUnitPrefilled)
                  _ReadOnlyFormValue(
                    label: 'Unit',
                    value: _initialUnitLabel ?? 'Unit #$_initialUnitId',
                  )
                else
                  DropdownButtonFormField<int>(
                    initialValue: units.any((u) => u.id == _selectedUnitId)
                        ? _selectedUnitId
                        : null,
                    decoration: InputDecoration(
                      labelText: 'Unit',
                      helperText: _selectedPropertyId == null
                          ? 'Select a property first'
                          : (!_isEdit && unitsAsync.hasValue && units.isEmpty)
                          ? 'No vacant units are available for this property'
                          : null,
                    ),
                    items: units
                        .map(
                          (u) => DropdownMenuItem(
                            value: u.id,
                            child: Text(
                              'Unit ${u.unitNumber}'
                              '${u.bedrooms > 0 ? ' · ${u.bedrooms}bd' : ''}',
                            ),
                          ),
                        )
                        .toList(),
                    onChanged: _selectedPropertyId == null
                        ? null
                        : (v) => setState(() => _selectedUnitId = v),
                    validator: (_) =>
                        _selectedUnitId == null ? 'Select a unit' : null,
                  ),
              ],
            ),
          ),
          TabbedFormStepSpec(
            label: 'Tenants',
            validate: _validateTenantsStep,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                if (!_isEdit) ...[
                  Text(
                    'Tenant type',
                    style: Theme.of(context).textTheme.labelMedium,
                  ),
                  const SizedBox(height: 8),
                  SegmentedButton<_LeaseTenantMode>(
                    segments: const [
                      ButtonSegment(
                        value: _LeaseTenantMode.existing,
                        icon: Icon(Icons.people_outline),
                        label: Text('Existing'),
                      ),
                      ButtonSegment(
                        value: _LeaseTenantMode.newTenant,
                        icon: Icon(Icons.person_add_outlined),
                        label: Text('New tenant'),
                      ),
                    ],
                    selected: {_tenantMode},
                    onSelectionChanged: (selection) {
                      setState(() {
                        _tenantMode = selection.first;
                        if (_tenantMode == _LeaseTenantMode.newTenant) {
                          _selectedTenantIds = <int>[];
                        }
                        _error = null;
                      });
                    },
                  ),
                  const SizedBox(height: 16),
                ],
                if (_tenantMode == _LeaseTenantMode.existing)
                  _TenantMultiSelect(
                    tenants: tenants,
                    selectedTenantIds: _selectedTenantIds,
                    emptyText: _isEdit
                        ? 'No tenants yet.'
                        : 'No available tenants.',
                    errorText: _error == 'Select at least one tenant.'
                        ? _error
                        : null,
                    onChanged: (ids) {
                      setState(() {
                        _selectedTenantIds = ids;
                        if (_error == 'Select at least one tenant.') {
                          _error = null;
                        }
                      });
                    },
                  )
                else
                  Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      Text(
                        'The tenant and lease are saved together. If the lease fails, neither record is created.',
                        style: Theme.of(context).textTheme.bodySmall?.copyWith(
                          color: Theme.of(context).colorScheme.onSurfaceVariant,
                        ),
                      ),
                      const SizedBox(height: 12),
                      _LabeledTenantField(
                        label: 'First name',
                        controller: _newTenantFirstNameCtrl,
                        maxLength: 100,
                        textInputAction: TextInputAction.next,
                        autofillHints: const [AutofillHints.givenName],
                        validator: (value) {
                          final name = value?.trim() ?? '';
                          if (name.isEmpty) return 'First name is required';
                          return name.length > 100
                              ? 'Use 100 characters or fewer'
                              : null;
                        },
                      ),
                      gap,
                      _LabeledTenantField(
                        label: 'Last name',
                        controller: _newTenantLastNameCtrl,
                        maxLength: 100,
                        textInputAction: TextInputAction.next,
                        autofillHints: const [AutofillHints.familyName],
                        validator: (value) {
                          final name = value?.trim() ?? '';
                          if (name.isEmpty) return 'Last name is required';
                          return name.length > 100
                              ? 'Use 100 characters or fewer'
                              : null;
                        },
                      ),
                      gap,
                      _LabeledTenantField(
                        label: 'Email',
                        controller: _newTenantEmailCtrl,
                        maxLength: 200,
                        keyboardType: TextInputType.emailAddress,
                        textInputAction: TextInputAction.next,
                        autofillHints: const [AutofillHints.email],
                        validator: (value) {
                          final email = value?.trim() ?? '';
                          if (email.isEmpty) return 'Email is required';
                          if (email.length > 200) {
                            return 'Use 200 characters or fewer';
                          }
                          return _looksLikeEmail(email)
                              ? null
                              : 'Enter a valid email address';
                        },
                      ),
                      gap,
                      _LabeledTenantField(
                        label: 'Phone',
                        controller: _newTenantPhoneCtrl,
                        maxLength: 50,
                        keyboardType: TextInputType.phone,
                        textInputAction: TextInputAction.done,
                        autofillHints: const [AutofillHints.telephoneNumber],
                        validator: (value) {
                          final phone = value?.trim() ?? '';
                          if (phone.isEmpty) return 'Phone is required';
                          return phone.length > 50
                              ? 'Use 50 characters or fewer'
                              : null;
                        },
                      ),
                    ],
                  ),
              ],
            ),
          ),
          TabbedFormStepSpec(
            label: 'Lease #',
            validate: _validateIdentityStep,
            child: TextFormField(
              controller: _leaseNumberCtrl,
              textInputAction: TextInputAction.done,
              maxLength: 100,
              decoration: const InputDecoration(
                labelText: 'Lease number',
                helperText: 'A reference you can recognize later',
              ),
              validator: (value) {
                final number = value?.trim() ?? '';
                if (number.isEmpty) return 'Enter a lease number';
                return number.length > 100
                    ? 'Use 100 characters or fewer'
                    : null;
              },
            ),
          ),
          TabbedFormStepSpec(
            label: 'Dates',
            validate: _validateDatesStep,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                _DateTile(
                  label: 'Start date',
                  date: _startDate,
                  onTap: () => _pickDate(context, isStart: true),
                  hasError: _startDate == null && _error != null,
                ),
                gap,
                _DateTile(
                  label: 'End date',
                  date: _endDate,
                  onTap: () => _pickDate(context, isStart: false),
                  hasError: _endDate == null && _error != null,
                ),
              ],
            ),
          ),
          TabbedFormStepSpec(
            label: 'Rent',
            validate: _validateRentStep,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Row(
                  children: [
                    Expanded(
                      child: TextFormField(
                        controller: _rentCtrl,
                        keyboardType: const TextInputType.numberWithOptions(
                          decimal: true,
                        ),
                        textInputAction: TextInputAction.next,
                        decoration: const InputDecoration(
                          labelText: 'Monthly rent (\$)',
                        ),
                        validator: (v) {
                          final amount = double.tryParse(v ?? '');
                          return !_isFiniteAmountInRange(amount, minimum: 0.01)
                              ? 'Enter \$0.01–\$99,999,999'
                              : null;
                        },
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: TextFormField(
                        controller: _depositCtrl,
                        keyboardType: const TextInputType.numberWithOptions(
                          decimal: true,
                        ),
                        textInputAction: TextInputAction.next,
                        decoration: const InputDecoration(
                          labelText: 'Security deposit (\$)',
                        ),
                        validator: (v) {
                          final amount = double.tryParse(v ?? '');
                          return !_isFiniteAmountInRange(amount, minimum: 0)
                              ? 'Enter \$0–\$99,999,999'
                              : null;
                        },
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
          TabbedFormStepSpec(
            label: 'Fees',
            validate: _validateFeesStep,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                TextFormField(
                  controller: _lateFeeCtrl,
                  keyboardType: const TextInputType.numberWithOptions(
                    decimal: true,
                  ),
                  textInputAction: TextInputAction.next,
                  decoration: const InputDecoration(labelText: 'Late fee (\$)'),
                  validator: (v) {
                    final amount = double.tryParse(v ?? '');
                    return !_isFiniteAmountInRange(amount, minimum: 0)
                        ? 'Enter \$0–\$99,999,999'
                        : null;
                  },
                ),
                gap,
                TextFormField(
                  controller: _dueDayCtrl,
                  keyboardType: TextInputType.number,
                  textInputAction: TextInputAction.next,
                  decoration: const InputDecoration(
                    labelText: 'Rent due day (1–31)',
                  ),
                  validator: (v) {
                    final n = int.tryParse(v ?? '');
                    if (n == null || n < 1 || n > 31) {
                      return 'Enter 1–31';
                    }
                    return null;
                  },
                ),
              ],
            ),
          ),
          TabbedFormStepSpec(
            label: 'Status',
            validate: _validateStatusStep,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Text(
                  'Active starts rent tracking now. Choose Draft only when the lease is intentionally incomplete and should not occupy the unit yet.',
                  style: Theme.of(context).textTheme.bodySmall?.copyWith(
                    color: Theme.of(context).colorScheme.onSurfaceVariant,
                  ),
                ),
                gap,
                DropdownButtonFormField<String>(
                  initialValue: _selectedStatus,
                  decoration: const InputDecoration(labelText: 'Status'),
                  items: _leaseStatuses
                      .map((s) => DropdownMenuItem(value: s, child: Text(s)))
                      .toList(),
                  onChanged: (v) {
                    if (v != null) setState(() => _selectedStatus = v);
                  },
                ),
                gap,
                TextFormField(
                  controller: _notesCtrl,
                  maxLines: 3,
                  maxLength: 2000,
                  textCapitalization: TextCapitalization.sentences,
                  decoration: const InputDecoration(
                    labelText: 'Notes',
                    hintText: 'Optional lease notes',
                    alignLabelWithHint: true,
                  ),
                  validator: (value) => (value?.trim().length ?? 0) > 2000
                      ? 'Use 2,000 characters or fewer'
                      : null,
                ),
              ],
            ),
          ),
          TabbedFormStepSpec(
            label: 'Tracking',
            validate: _validateTrackingStep,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                if (_selectedStatus != 'Active')
                  const _FormHint(
                    text:
                        'Rent tracking options apply after the lease becomes Active.',
                  )
                else ...[
                  DropdownButtonFormField<String>(
                    initialValue: _rentTrackingStartMode,
                    decoration: const InputDecoration(
                      labelText: 'Rent tracking start',
                    ),
                    items: _rentTrackingStartOptions.entries
                        .map(
                          (option) => DropdownMenuItem(
                            value: option.key,
                            child: Text(option.value),
                          ),
                        )
                        .toList(),
                    onChanged: (value) {
                      if (value == null) return;
                      setState(() {
                        _rentTrackingStartMode = value;
                        _rentTrackingChanged = true;
                        if (value != 'CustomCutoffDate') {
                          _rentTrackingStartDate = null;
                        }
                        if (value != 'OpeningBalanceOnly') {
                          _openingBalanceAmountCtrl.clear();
                          _openingBalanceAsOfDate = null;
                          _openingBalanceNoteCtrl.clear();
                        }
                        _error = null;
                      });
                    },
                  ),
                  gap,
                  if (_rentTrackingStartMode == 'CustomCutoffDate')
                    _DateTile(
                      label: 'Cutoff date',
                      date: _rentTrackingStartDate,
                      onTap: () =>
                          _pickTrackingDate(context, isOpeningBalance: false),
                      hasError: _error == 'Select a rent tracking cutoff date.',
                    ),
                  if (_rentTrackingStartMode == 'OpeningBalanceOnly') ...[
                    TextFormField(
                      controller: _openingBalanceAmountCtrl,
                      keyboardType: const TextInputType.numberWithOptions(
                        decimal: true,
                        signed: true,
                      ),
                      textInputAction: TextInputAction.next,
                      decoration: const InputDecoration(
                        labelText: 'Opening balance (\$)',
                        helperText:
                            'Positive for money owed; negative for credit',
                      ),
                      validator: (value) {
                        final amount = value?.trim() ?? '';
                        if (amount.isEmpty) return null;
                        return _isFiniteAmountInRange(
                              double.tryParse(amount),
                              minimum: -_maximumLeaseAmount,
                            )
                            ? null
                            : 'Enter -\$99,999,999–\$99,999,999';
                      },
                      onChanged: (_) => _rentTrackingChanged = true,
                    ),
                    gap,
                    _DateTile(
                      label: 'Balance as-of date',
                      date: _openingBalanceAsOfDate,
                      onTap: () =>
                          _pickTrackingDate(context, isOpeningBalance: true),
                      hasError:
                          _error == 'Select the opening balance as-of date.',
                    ),
                    gap,
                    TextFormField(
                      controller: _openingBalanceNoteCtrl,
                      maxLines: 2,
                      maxLength: 2000,
                      textCapitalization: TextCapitalization.sentences,
                      decoration: const InputDecoration(
                        labelText: 'Opening balance note',
                        hintText: 'Optional source or explanation',
                        alignLabelWithHint: true,
                      ),
                      onChanged: (_) => _rentTrackingChanged = true,
                    ),
                  ],
                ],
              ],
            ),
          ),
        ],
      ),
    );
  }
}

class _LabeledTenantField extends StatelessWidget {
  const _LabeledTenantField({
    required this.label,
    required this.controller,
    required this.validator,
    this.keyboardType,
    this.textInputAction,
    this.autofillHints,
    this.maxLength,
  });

  final String label;
  final TextEditingController controller;
  final FormFieldValidator<String> validator;
  final TextInputType? keyboardType;
  final TextInputAction? textInputAction;
  final Iterable<String>? autofillHints;
  final int? maxLength;

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(label, style: Theme.of(context).textTheme.labelMedium),
        const SizedBox(height: 6),
        TextFormField(
          controller: controller,
          keyboardType: keyboardType,
          textInputAction: textInputAction,
          autofillHints: autofillHints,
          maxLength: maxLength,
          validator: validator,
          decoration: InputDecoration(hintText: label),
        ),
      ],
    );
  }
}

class _ReadOnlyFormValue extends StatelessWidget {
  const _ReadOnlyFormValue({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    return InputDecorator(
      decoration: InputDecoration(labelText: label),
      child: Text(value, maxLines: 2, overflow: TextOverflow.ellipsis),
    );
  }
}

class _FormHint extends StatelessWidget {
  const _FormHint({required this.text});

  final String text;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: Text(
        text,
        style: Theme.of(context).textTheme.bodySmall?.copyWith(
          color: Theme.of(context).colorScheme.onSurfaceVariant,
        ),
      ),
    );
  }
}

class _TenantMultiSelect extends StatelessWidget {
  const _TenantMultiSelect({
    required this.tenants,
    required this.selectedTenantIds,
    required this.onChanged,
    this.emptyText = 'No tenants yet.',
    this.errorText,
  });

  final List<Tenant> tenants;
  final List<int> selectedTenantIds;
  final ValueChanged<List<int>> onChanged;
  final String emptyText;
  final String? errorText;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return InputDecorator(
      decoration: InputDecoration(labelText: 'Tenants', errorText: errorText),
      child: tenants.isEmpty
          ? Text(
              emptyText,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: colorScheme.onSurfaceVariant,
              ),
            )
          : Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                for (final tenant in tenants)
                  _TenantChoiceRow(
                    tenant: tenant,
                    selected: selectedTenantIds.contains(tenant.id),
                    primary:
                        selectedTenantIds.isNotEmpty &&
                        selectedTenantIds.first == tenant.id,
                    onChanged: (selected) {
                      final next = <int>[...selectedTenantIds];
                      if (selected) {
                        if (!next.contains(tenant.id)) next.add(tenant.id);
                      } else {
                        next.remove(tenant.id);
                      }
                      onChanged(next);
                    },
                  ),
              ],
            ),
    );
  }
}

class _TenantChoiceRow extends StatelessWidget {
  const _TenantChoiceRow({
    required this.tenant,
    required this.selected,
    required this.primary,
    required this.onChanged,
  });

  final Tenant tenant;
  final bool selected;
  final bool primary;
  final ValueChanged<bool> onChanged;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final fullName = '${tenant.firstName} ${tenant.lastName}'.trim();
    final subtitle = tenant.email?.trim().isNotEmpty == true
        ? tenant.email!.trim()
        : tenant.phone?.trim();

    return CheckboxListTile(
      dense: true,
      visualDensity: VisualDensity.compact,
      contentPadding: EdgeInsets.zero,
      controlAffinity: ListTileControlAffinity.leading,
      value: selected,
      onChanged: (value) => onChanged(value ?? false),
      title: Row(
        children: [
          Expanded(
            child: Text(
              fullName.isEmpty ? 'Tenant #${tenant.id}' : fullName,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
            ),
          ),
          if (primary)
            Padding(
              padding: const EdgeInsets.only(left: 8),
              child: Text(
                'Primary',
                style: theme.textTheme.labelSmall?.copyWith(
                  color: colorScheme.primary,
                  fontWeight: FontWeight.w700,
                ),
              ),
            ),
        ],
      ),
      subtitle: subtitle == null || subtitle.isEmpty
          ? null
          : Text(subtitle, maxLines: 1, overflow: TextOverflow.ellipsis),
    );
  }
}

// ── Date selection tile ───────────────────────────────────────────────────────

class _DateTile extends StatelessWidget {
  const _DateTile({
    required this.label,
    required this.date,
    required this.onTap,
    this.hasError = false,
  });

  final String label;
  final DateTime? date;
  final VoidCallback onTap;
  final bool hasError;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final borderColor = hasError ? colorScheme.error : colorScheme.outline;

    return InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(4),
      child: InputDecorator(
        decoration: InputDecoration(
          labelText: label,
          enabledBorder: OutlineInputBorder(
            borderSide: BorderSide(color: borderColor),
          ),
          focusedBorder: OutlineInputBorder(
            borderSide: BorderSide(color: colorScheme.primary, width: 2),
          ),
          suffixIcon: const Icon(Icons.calendar_today_outlined, size: 18),
        ),
        child: Text(
          date != null ? _fmtDate(date!) : 'Select date',
          style: theme.textTheme.bodyMedium?.copyWith(
            color: date != null
                ? colorScheme.onSurface
                : colorScheme.onSurfaceVariant,
          ),
        ),
      ),
    );
  }
}
