import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/models.dart';
import '../../core/widgets/tabbed_form_sheet.dart';
import '../properties/properties_repository.dart';
import '../tenants/tenants_repository.dart';
import '../units/unit_command_center_screen.dart';
import '../units/unit_navigation.dart';
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

String _fmtIso(DateTime d) =>
    '${d.year}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';

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

/// List of all leases with "+" FAB to create a new lease.
class LeasesListScreen extends ConsumerStatefulWidget {
  const LeasesListScreen({super.key});

  @override
  ConsumerState<LeasesListScreen> createState() => _LeasesListScreenState();
}

class _LeasesListScreenState extends ConsumerState<LeasesListScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(() => ref.read(leasesProvider.notifier).load());
  }

  Future<void> _refresh() => ref.read(leasesProvider.notifier).refresh();

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
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => LeaseFormSheet(
        onSaved: () => ref.read(leasesProvider.notifier).refresh(),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final leasesAsync = ref.watch(leasesProvider);
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return Scaffold(
      appBar: AppBar(title: const Text('Leases')),
      floatingActionButton: FloatingActionButton(
        heroTag: 'leases-fab',
        onPressed: () => _showAddSheet(context),
        tooltip: 'Create lease',
        child: const Icon(Icons.add),
      ),
      body: RefreshIndicator(
        onRefresh: _refresh,
        child: leasesAsync.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (e, _) => _ErrorBody(
            message: e is ApiException ? e.message : e.toString(),
            onRetry: _refresh,
          ),
          data: (list) {
            if (list.isEmpty) {
              return _EmptyBody(onAdd: () => _showAddSheet(context));
            }
            final bottomInset = MediaQuery.paddingOf(context).bottom;
            return ListView.separated(
              padding: EdgeInsets.fromLTRB(16, 16, 16, 160.0 + bottomInset),
              itemCount: list.length,
              separatorBuilder: (_, idx) =>
                  _GroupedListDivider(colorScheme: colorScheme),
              itemBuilder: (context, index) {
                final lease = list[index];
                return _LeaseCard(
                  lease: lease,
                  colorScheme: colorScheme,
                  theme: theme,
                  first: index == 0,
                  last: index == list.length - 1,
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

// ── Lease card ────────────────────────────────────────────────────────────────

class _LeaseCard extends StatelessWidget {
  const _LeaseCard({
    required this.lease,
    required this.colorScheme,
    required this.theme,
    required this.first,
    required this.last,
    required this.onTap,
  });

  final Lease lease;
  final ColorScheme colorScheme;
  final ThemeData theme;
  final bool first;
  final bool last;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final borderRadius = BorderRadius.vertical(
      top: first ? const Radius.circular(20) : Radius.zero,
      bottom: last ? const Radius.circular(20) : Radius.zero,
    );

    return Material(
      color: colorScheme.surfaceContainerHigh,
      borderRadius: borderRadius,
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onTap,
        borderRadius: borderRadius,
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          lease.tenantName ?? 'Lease #${lease.leaseNumber}',
                          style: theme.textTheme.titleSmall?.copyWith(
                            fontWeight: FontWeight.w600,
                          ),
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                        ),
                        if (lease.propertyName != null) ...[
                          const SizedBox(height: 2),
                          Text(
                            '${lease.propertyName}'
                            '${lease.unitNumber != null ? ' · Unit ${lease.unitNumber}' : ''}',
                            style: theme.textTheme.bodySmall?.copyWith(
                              color: colorScheme.onSurfaceVariant,
                            ),
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                          ),
                        ],
                      ],
                    ),
                  ),
                  const SizedBox(width: 8),
                  _StatusChip(status: lease.status, colorScheme: colorScheme),
                ],
              ),
              const SizedBox(height: 8),
              Wrap(
                spacing: 8,
                runSpacing: 4,
                children: [
                  _MetaChip(
                    icon: Icons.attach_money,
                    label: '${_formatCurrency(lease.monthlyRent)}/mo',
                  ),
                  _MetaChip(
                    icon: Icons.calendar_today_outlined,
                    label:
                        '${_fmtDate(lease.startDate)} – ${_fmtDate(lease.endDate)}',
                  ),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _GroupedListDivider extends StatelessWidget {
  const _GroupedListDivider({required this.colorScheme});

  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    return Divider(
      height: 1,
      thickness: 1,
      indent: 16,
      endIndent: 16,
      color: colorScheme.outlineVariant.withValues(alpha: 0.48),
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
        Icon(icon, size: 13, color: color),
        const SizedBox(width: 3),
        Text(label, style: TextStyle(fontSize: 12, color: color)),
      ],
    );
  }
}

// ── Empty / Error ─────────────────────────────────────────────────────────────

/// A15: a welcoming first-run empty state with a plain explanation and a
/// primary "Create your first lease" button instead of a cold "tap +" hint.
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
              Icons.description_outlined,
              size: 48,
              color: colorScheme.onSurfaceVariant,
            ),
            const SizedBox(height: 12),
            Text(
              'No leases yet',
              style: theme.textTheme.titleMedium?.copyWith(
                color: colorScheme.onSurface,
                fontWeight: FontWeight.w700,
              ),
            ),
            const SizedBox(height: 6),
            Text(
              'A lease ties a tenant to a unit and sets the rent, dates, and '
              'deposit. Create your first to start tracking rent.',
              textAlign: TextAlign.center,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: colorScheme.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 20),
            FilledButton.icon(
              onPressed: onAdd,
              icon: const Icon(Icons.add),
              label: const Text('Create your first lease'),
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

// ── Lease form bottom sheet (add / edit) ──────────────────────────────────────
//
// Flow: pick a property → unit dropdown updates → pick tenant → fill dates/amounts.

/// Public so that [LeaseDetailScreen] can reuse it.
class LeaseFormSheet extends ConsumerStatefulWidget {
  const LeaseFormSheet({super.key, required this.onSaved, this.existing});

  final VoidCallback onSaved;
  final Lease? existing;

  @override
  ConsumerState<LeaseFormSheet> createState() => _LeaseFormSheetState();
}

class _LeaseFormSheetState extends ConsumerState<LeaseFormSheet> {
  final _formKey = GlobalKey<FormState>();

  // Dropdown selections
  int? _selectedPropertyId;
  int? _selectedUnitId;
  int? _selectedTenantId;
  String _selectedStatus = 'Active';

  // Date pickers
  DateTime? _startDate;
  DateTime? _endDate;

  // Text controllers
  late final TextEditingController _rentCtrl;
  late final TextEditingController _depositCtrl;
  late final TextEditingController _lateFeeCtrl;
  late final TextEditingController _dueDayCtrl;

  bool _saving = false;
  String? _error;

  bool get _isEdit => widget.existing != null;

  @override
  void initState() {
    super.initState();
    final e = widget.existing;
    _rentCtrl = TextEditingController(text: e?.monthlyRent.toString() ?? '');
    _depositCtrl = TextEditingController(
      text: e?.securityDeposit.toString() ?? '',
    );
    _lateFeeCtrl = TextEditingController(
      text: e?.lateFeeAmount.toString() ?? '50',
    );
    _dueDayCtrl = TextEditingController(text: e?.rentDueDay.toString() ?? '1');

    if (e != null) {
      _selectedPropertyId = e.propertyId;
      _selectedUnitId = e.unitId;
      _selectedTenantId = e.tenantId;
      _selectedStatus = _leaseStatuses.contains(e.status)
          ? e.status
          : _leaseStatuses.first;
      _startDate = e.startDate;
      _endDate = e.endDate;
    }

    // Load dropdowns
    Future.microtask(() {
      ref.read(propertiesProvider.notifier).load();
      ref.read(tenantsProvider.notifier).load();
    });
  }

  @override
  void dispose() {
    _rentCtrl.dispose();
    _depositCtrl.dispose();
    _lateFeeCtrl.dispose();
    _dueDayCtrl.dispose();
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
        } else {
          _endDate = picked;
        }
      });
    }
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;
    if (_startDate == null || _endDate == null) {
      setState(() => _error = 'Please select start and end dates.');
      return;
    }

    setState(() {
      _saving = true;
      _error = null;
    });

    final body = <String, dynamic>{
      'unitId': _selectedUnitId,
      'tenantId': _selectedTenantId,
      'startDate': _fmtIso(_startDate!),
      'endDate': _fmtIso(_endDate!),
      'monthlyRent': double.tryParse(_rentCtrl.text) ?? 0.0,
      'securityDeposit': double.tryParse(_depositCtrl.text) ?? 0.0,
      'lateFeeAmount': double.tryParse(_lateFeeCtrl.text) ?? 0.0,
      'rentDueDay': int.tryParse(_dueDayCtrl.text) ?? 1,
      'status': _selectedStatus,
    };

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
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final propertiesAsync = ref.watch(propertiesProvider);
    final tenantsAsync = ref.watch(tenantsProvider);

    final properties = propertiesAsync.value ?? <Property>[];
    final tenants = tenantsAsync.value ?? <Tenant>[];

    // When property changes, reset unit selection.
    final unitsAsync = _selectedPropertyId != null
        ? ref.watch(unitsProvider(_selectedPropertyId!))
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
            label: 'Unit',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                DropdownButtonFormField<int>(
                  initialValue:
                      properties.any((p) => p.id == _selectedPropertyId)
                      ? _selectedPropertyId
                      : null,
                  decoration: const InputDecoration(labelText: 'Property'),
                  items: properties
                      .map(
                        (p) => DropdownMenuItem(
                          value: p.id,
                          child: Text(p.name, overflow: TextOverflow.ellipsis),
                        ),
                      )
                      .toList(),
                  onChanged: (v) {
                    setState(() {
                      _selectedPropertyId = v;
                      _selectedUnitId = null;
                    });
                    if (v != null) {
                      ref.read(unitsProvider(v).notifier).load();
                    }
                  },
                  validator: (_) =>
                      _selectedPropertyId == null ? 'Select a property' : null,
                ),
                gap,
                DropdownButtonFormField<int>(
                  initialValue: units.any((u) => u.id == _selectedUnitId)
                      ? _selectedUnitId
                      : null,
                  decoration: InputDecoration(
                    labelText: 'Unit',
                    helperText: _selectedPropertyId == null
                        ? 'Select a property first'
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
                gap,
                DropdownButtonFormField<int>(
                  initialValue: tenants.any((t) => t.id == _selectedTenantId)
                      ? _selectedTenantId
                      : null,
                  decoration: const InputDecoration(labelText: 'Tenant'),
                  items: tenants
                      .map(
                        (t) => DropdownMenuItem(
                          value: t.id,
                          child: Text(
                            '${t.firstName} ${t.lastName}',
                            overflow: TextOverflow.ellipsis,
                          ),
                        ),
                      )
                      .toList(),
                  onChanged: (v) => setState(() => _selectedTenantId = v),
                  validator: (_) =>
                      _selectedTenantId == null ? 'Select a tenant' : null,
                ),
                gap,
                _DateTile(
                  label: 'Start date',
                  date: _startDate,
                  onTap: () => _pickDate(context, isStart: true),
                  hasError: _startDate == null && _error != null,
                ),
              ],
            ),
          ),
          TabbedFormStepSpec(
            label: 'Terms',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                _DateTile(
                  label: 'End date',
                  date: _endDate,
                  onTap: () => _pickDate(context, isStart: false),
                  hasError: _endDate == null && _error != null,
                ),
                gap,
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
                        validator: (v) =>
                            (v == null || double.tryParse(v) == null)
                            ? 'Enter an amount'
                            : null,
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
                        validator: (v) =>
                            (v == null || double.tryParse(v) == null)
                            ? 'Enter an amount'
                            : null,
                      ),
                    ),
                  ],
                ),
                gap,
                TextFormField(
                  controller: _lateFeeCtrl,
                  keyboardType: const TextInputType.numberWithOptions(
                    decimal: true,
                  ),
                  textInputAction: TextInputAction.next,
                  decoration: const InputDecoration(labelText: 'Late fee (\$)'),
                  validator: (v) => (v == null || double.tryParse(v) == null)
                      ? 'Enter an amount'
                      : null,
                ),
              ],
            ),
          ),
          TabbedFormStepSpec(
            label: 'Rules',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                TextFormField(
                  controller: _dueDayCtrl,
                  keyboardType: TextInputType.number,
                  textInputAction: TextInputAction.next,
                  decoration: const InputDecoration(
                    labelText: 'Rent due day (1–28)',
                  ),
                  validator: (v) {
                    final n = int.tryParse(v ?? '');
                    if (n == null || n < 1 || n > 28) {
                      return 'Enter 1–28';
                    }
                    return null;
                  },
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
              ],
            ),
          ),
        ],
      ),
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
