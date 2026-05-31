import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/models/models.dart';
import '../../core/api/api_exception.dart';
import 'properties_repository.dart';

String _formatCurrency(double amount) {
  final rounded = amount.round();
  // Insert commas: e.g. 1200 -> $1,200
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

const _monthNames = [
  '', 'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun',
  'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec',
];

String _formatDate(DateTime d) =>
    '${_monthNames[d.month]} ${d.day}, ${d.year}';

/// Detail screen for a single property.
///
/// Shows a header with name / address / status, a Units section (with add +
/// inline edit), and a read-only Leases section for active leases.
class PropertyDetailScreen extends ConsumerStatefulWidget {
  const PropertyDetailScreen({super.key, required this.property});

  final Property property;

  @override
  ConsumerState<PropertyDetailScreen> createState() =>
      _PropertyDetailScreenState();
}

class _PropertyDetailScreenState
    extends ConsumerState<PropertyDetailScreen> {
  @override
  void initState() {
    super.initState();
    // Trigger first load for both providers.
    Future.microtask(() {
      ref.read(unitsProvider(widget.property.id).notifier).load();
      ref.read(propertyLeasesProvider(widget.property.id).notifier).load();
    });
  }

  Future<void> _refresh() async {
    await Future.wait<void>([
      ref.read(unitsProvider(widget.property.id).notifier).refresh(),
      ref.read(propertyLeasesProvider(widget.property.id).notifier).refresh(),
    ]);
  }

  void _showAddUnitSheet(BuildContext context) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => _AddUnitSheet(
        propertyId: widget.property.id,
        onSaved: () =>
            ref.read(unitsProvider(widget.property.id).notifier).refresh(),
      ),
    );
  }

  void _showEditUnitSheet(BuildContext context, Unit unit) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => _EditUnitSheet(
        unit: unit,
        onSaved: () =>
            ref.read(unitsProvider(widget.property.id).notifier).refresh(),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final property = widget.property;
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final unitsAsync = ref.watch(unitsProvider(property.id));
    final leasesAsync = ref.watch(propertyLeasesProvider(property.id));

    return Scaffold(
      appBar: AppBar(
        title: Text(property.name, overflow: TextOverflow.ellipsis),
      ),
      body: RefreshIndicator(
        onRefresh: _refresh,
        child: ListView(
          padding: const EdgeInsets.all(16),
          children: [
            // ── Property header ────────────────────────────────────────────
            _PropertyHeader(
                property: property,
                theme: theme,
                colorScheme: colorScheme),
            const SizedBox(height: 24),

            // ── Units section ──────────────────────────────────────────────
            Row(
              children: [
                Expanded(
                  child: Text(
                    'Units',
                    style: theme.textTheme.titleMedium
                        ?.copyWith(fontWeight: FontWeight.w700),
                  ),
                ),
                TextButton.icon(
                  onPressed: () => _showAddUnitSheet(context),
                  icon: const Icon(Icons.add, size: 18),
                  label: const Text('Add unit'),
                ),
              ],
            ),
            const SizedBox(height: 8),

            unitsAsync.when(
              loading: () =>
                  const Center(child: CircularProgressIndicator()),
              error: (e, _) => _InlineError(
                message: e is ApiException ? e.message : e.toString(),
              ),
              data: (units) {
                if (units.isEmpty) {
                  return Padding(
                    padding: const EdgeInsets.symmetric(vertical: 16),
                    child: Text(
                      'No units yet. Tap "Add unit" to create one.',
                      style: TextStyle(color: colorScheme.onSurfaceVariant),
                    ),
                  );
                }
                return Column(
                  children: units
                      .map(
                        (u) => _UnitTile(
                          unit: u,
                          onEdit: () => _showEditUnitSheet(context, u),
                        ),
                      )
                      .toList(),
                );
              },
            ),

            const SizedBox(height: 24),

            // ── Leases section ─────────────────────────────────────────────
            Text(
              'Active Leases',
              style: theme.textTheme.titleMedium
                  ?.copyWith(fontWeight: FontWeight.w700),
            ),
            const SizedBox(height: 8),

            leasesAsync.when(
              loading: () =>
                  const Center(child: CircularProgressIndicator()),
              error: (e, _) => _InlineError(
                message: e is ApiException ? e.message : e.toString(),
              ),
              data: (leases) {
                final active = leases
                    .where((l) => l.status.toLowerCase() == 'active')
                    .toList();
                if (active.isEmpty) {
                  return Padding(
                    padding: const EdgeInsets.symmetric(vertical: 16),
                    child: Text(
                      'No active leases on this property.',
                      style:
                          TextStyle(color: colorScheme.onSurfaceVariant),
                    ),
                  );
                }
                return Column(
                  children: active
                      .map((l) => _LeaseTile(lease: l))
                      .toList(),
                );
              },
            ),

            const SizedBox(height: 32),
          ],
        ),
      ),
    );
  }
}

// ── Property header card ──────────────────────────────────────────────────────

class _PropertyHeader extends StatelessWidget {
  const _PropertyHeader({
    required this.property,
    required this.theme,
    required this.colorScheme,
  });

  final Property property;
  final ThemeData theme;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Expanded(
                  child: Text(
                    property.name,
                    style: theme.textTheme.titleLarge
                        ?.copyWith(fontWeight: FontWeight.w700),
                  ),
                ),
                const SizedBox(width: 8),
                _StatusBadge(
                    status: property.status,
                    colorScheme: colorScheme),
              ],
            ),
            const SizedBox(height: 4),
            Text(
              property.addressLine1,
              style: theme.textTheme.bodyMedium?.copyWith(
                  color: colorScheme.onSurfaceVariant),
            ),
            Text(
              '${property.city}, ${property.state} ${property.postalCode}',
              style: theme.textTheme.bodySmall
                  ?.copyWith(color: colorScheme.onSurfaceVariant),
            ),
            const SizedBox(height: 12),
            Wrap(
              spacing: 16,
              runSpacing: 4,
              children: [
                _KeyValue(label: 'Type', value: property.type),
                _KeyValue(
                  label: 'Units',
                  value: '${property.unitCount ?? 0}',
                ),
                _KeyValue(
                  label: 'Occupied',
                  value: '${property.occupiedUnits ?? 0}',
                ),
                if (property.ownerName != null)
                  _KeyValue(label: 'Owner', value: property.ownerName!),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

class _StatusBadge extends StatelessWidget {
  const _StatusBadge({required this.status, required this.colorScheme});

  final String status;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    final isActive = status.toLowerCase() == 'active';
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
      decoration: BoxDecoration(
        color: isActive
            ? colorScheme.primaryContainer
            : colorScheme.surfaceContainerHighest,
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        status,
        style: TextStyle(
          fontSize: 12,
          fontWeight: FontWeight.w600,
          color: isActive
              ? colorScheme.onPrimaryContainer
              : colorScheme.onSurfaceVariant,
        ),
      ),
    );
  }
}

class _KeyValue extends StatelessWidget {
  const _KeyValue({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(label,
            style: TextStyle(fontSize: 11, color: colorScheme.onSurfaceVariant)),
        Text(value,
            style: theme.textTheme.bodyMedium
                ?.copyWith(fontWeight: FontWeight.w600)),
      ],
    );
  }
}

// ── Unit tile ─────────────────────────────────────────────────────────────────

class _UnitTile extends StatelessWidget {
  const _UnitTile({required this.unit, required this.onEdit});

  final Unit unit;
  final VoidCallback onEdit;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final isOccupied = unit.status.toLowerCase() == 'occupied';

    return Card(
      margin: const EdgeInsets.only(bottom: 8),
      child: ListTile(
        leading: CircleAvatar(
          radius: 20,
          backgroundColor: isOccupied
              ? colorScheme.primaryContainer
              : colorScheme.surfaceContainerHighest,
          child: Icon(
            isOccupied ? Icons.person : Icons.home_outlined,
            size: 18,
            color: isOccupied
                ? colorScheme.onPrimaryContainer
                : colorScheme.onSurfaceVariant,
          ),
        ),
        title: Text(
          'Unit ${unit.unitNumber}',
          style: theme.textTheme.bodyMedium
              ?.copyWith(fontWeight: FontWeight.w600),
        ),
        subtitle: Text(
          '${unit.bedrooms} bd / ${unit.bathrooms} ba  ·  '
          '${_formatCurrency(unit.marketRent)}/mo',
          style: TextStyle(fontSize: 12, color: colorScheme.onSurfaceVariant),
        ),
        trailing: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Container(
              padding:
                  const EdgeInsets.symmetric(horizontal: 7, vertical: 2),
              decoration: BoxDecoration(
                color: isOccupied
                    ? colorScheme.primaryContainer
                    : colorScheme.surfaceContainerHighest,
                borderRadius: BorderRadius.circular(12),
              ),
              child: Text(
                unit.status,
                style: TextStyle(
                  fontSize: 10,
                  fontWeight: FontWeight.w600,
                  color: isOccupied
                      ? colorScheme.onPrimaryContainer
                      : colorScheme.onSurfaceVariant,
                ),
              ),
            ),
            IconButton(
              icon: const Icon(Icons.edit_outlined, size: 18),
              onPressed: onEdit,
              tooltip: 'Edit unit',
            ),
          ],
        ),
      ),
    );
  }
}

// ── Lease tile (read-only) ────────────────────────────────────────────────────

class _LeaseTile extends StatelessWidget {
  const _LeaseTile({required this.lease});

  final Lease lease;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return Card(
      margin: const EdgeInsets.only(bottom: 8),
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    lease.tenantName ?? 'Lease #${lease.leaseNumber}',
                    style: theme.textTheme.bodyMedium
                        ?.copyWith(fontWeight: FontWeight.w600),
                  ),
                ),
                Text(
                  _formatCurrency(lease.monthlyRent),
                  style: theme.textTheme.bodyMedium?.copyWith(
                    fontWeight: FontWeight.w700,
                    color: colorScheme.primary,
                  ),
                ),
                const Text('/mo', style: TextStyle(fontSize: 12)),
              ],
            ),
            const SizedBox(height: 4),
            Text(
              'Unit ${lease.unitNumber ?? lease.unitId}  ·  '
              '${_formatDate(lease.startDate)} – ${_formatDate(lease.endDate)}',
              style: TextStyle(
                  fontSize: 12, color: colorScheme.onSurfaceVariant),
            ),
          ],
        ),
      ),
    );
  }
}

// ── Inline error ──────────────────────────────────────────────────────────────

class _InlineError extends StatelessWidget {
  const _InlineError({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 12),
      child: Row(
        children: [
          Icon(Icons.error_outline, size: 18, color: colorScheme.error),
          const SizedBox(width: 8),
          Expanded(
            child: Text(
              message,
              style: TextStyle(color: colorScheme.error, fontSize: 13),
            ),
          ),
        ],
      ),
    );
  }
}

// ── Add Unit bottom sheet ─────────────────────────────────────────────────────

class _AddUnitSheet extends ConsumerStatefulWidget {
  const _AddUnitSheet({required this.propertyId, required this.onSaved});

  final int propertyId;
  final VoidCallback onSaved;

  @override
  ConsumerState<_AddUnitSheet> createState() => _AddUnitSheetState();
}

class _AddUnitSheetState extends ConsumerState<_AddUnitSheet> {
  final _formKey = GlobalKey<FormState>();

  final _numberCtrl = TextEditingController();
  final _bedsCtrl = TextEditingController(text: '1');
  final _bathsCtrl = TextEditingController(text: '1');
  final _rentCtrl = TextEditingController(text: '1200');

  bool _saving = false;
  String? _error;

  static const _statuses = ['Vacant', 'Occupied', 'Maintenance'];
  String _selectedStatus = 'Vacant';

  @override
  void dispose() {
    _numberCtrl.dispose();
    _bedsCtrl.dispose();
    _bathsCtrl.dispose();
    _rentCtrl.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;

    setState(() {
      _saving = true;
      _error = null;
    });

    try {
      await ref.read(propertiesRepositoryProvider).createUnit(
        widget.propertyId,
        {
          'unitNumber': _numberCtrl.text.trim(),
          'bedrooms': int.tryParse(_bedsCtrl.text) ?? 1,
          'bathrooms': double.tryParse(_bathsCtrl.text) ?? 1.0,
          'marketRent': double.tryParse(_rentCtrl.text) ?? 0.0,
          'status': _selectedStatus,
        },
      );

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
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final bottomPadding = MediaQuery.viewInsetsOf(context).bottom;

    return Padding(
      padding: EdgeInsets.fromLTRB(20, 20, 20, 20 + bottomPadding),
      child: Form(
        key: _formKey,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Row(
                children: [
                  Expanded(
                    child: Text(
                      'Add Unit',
                      style: theme.textTheme.titleLarge
                          ?.copyWith(fontWeight: FontWeight.w700),
                    ),
                  ),
                  IconButton(
                    icon: const Icon(Icons.close),
                    onPressed: () => Navigator.of(context).pop(),
                  ),
                ],
              ),
              const SizedBox(height: 16),

              TextFormField(
                controller: _numberCtrl,
                textInputAction: TextInputAction.next,
                decoration: const InputDecoration(labelText: 'Unit number'),
                validator: (v) =>
                    (v == null || v.trim().isEmpty)
                        ? 'Unit number is required'
                        : null,
              ),
              const SizedBox(height: 12),

              Row(
                children: [
                  Expanded(
                    child: TextFormField(
                      controller: _bedsCtrl,
                      keyboardType: TextInputType.number,
                      textInputAction: TextInputAction.next,
                      decoration: const InputDecoration(labelText: 'Beds'),
                      validator: (v) =>
                          (v == null || int.tryParse(v) == null)
                              ? 'Enter a number'
                              : null,
                    ),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: TextFormField(
                      controller: _bathsCtrl,
                      keyboardType:
                          const TextInputType.numberWithOptions(decimal: true),
                      textInputAction: TextInputAction.next,
                      decoration: const InputDecoration(labelText: 'Baths'),
                      validator: (v) =>
                          (v == null || double.tryParse(v) == null)
                              ? 'Enter a number'
                              : null,
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 12),

              TextFormField(
                controller: _rentCtrl,
                keyboardType:
                    const TextInputType.numberWithOptions(decimal: true),
                textInputAction: TextInputAction.done,
                decoration:
                    const InputDecoration(labelText: 'Market rent (\$)'),
                validator: (v) =>
                    (v == null || double.tryParse(v) == null)
                        ? 'Enter an amount'
                        : null,
                onFieldSubmitted: (_) => _saving ? null : _submit(),
              ),
              const SizedBox(height: 12),

              DropdownButtonFormField<String>(
                initialValue: _selectedStatus,
                decoration: const InputDecoration(labelText: 'Status'),
                items: _statuses
                    .map((s) => DropdownMenuItem(value: s, child: Text(s)))
                    .toList(),
                onChanged: (v) {
                  if (v != null) setState(() => _selectedStatus = v);
                },
              ),

              if (_error != null) ...[
                const SizedBox(height: 12),
                Container(
                  padding:
                      const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
                  decoration: BoxDecoration(
                    color: colorScheme.errorContainer,
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: Text(
                    _error!,
                    style: TextStyle(
                        color: colorScheme.onErrorContainer, fontSize: 13),
                  ),
                ),
              ],

              const SizedBox(height: 20),

              FilledButton(
                onPressed: _saving ? null : _submit,
                child: _saving
                    ? const SizedBox(
                        height: 20,
                        width: 20,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : const Text('Add Unit'),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

// ── Edit Unit bottom sheet ────────────────────────────────────────────────────

class _EditUnitSheet extends ConsumerStatefulWidget {
  const _EditUnitSheet({required this.unit, required this.onSaved});

  final Unit unit;
  final VoidCallback onSaved;

  @override
  ConsumerState<_EditUnitSheet> createState() => _EditUnitSheetState();
}

class _EditUnitSheetState extends ConsumerState<_EditUnitSheet> {
  final _formKey = GlobalKey<FormState>();

  late final TextEditingController _numberCtrl;
  late final TextEditingController _bedsCtrl;
  late final TextEditingController _bathsCtrl;
  late final TextEditingController _rentCtrl;

  bool _saving = false;
  String? _error;

  static const _statuses = ['Vacant', 'Occupied', 'Maintenance'];
  late String _selectedStatus;

  @override
  void initState() {
    super.initState();
    _numberCtrl =
        TextEditingController(text: widget.unit.unitNumber);
    _bedsCtrl =
        TextEditingController(text: widget.unit.bedrooms.toString());
    _bathsCtrl =
        TextEditingController(text: widget.unit.bathrooms.toString());
    _rentCtrl =
        TextEditingController(text: widget.unit.marketRent.toString());
    _selectedStatus = _statuses.contains(widget.unit.status)
        ? widget.unit.status
        : _statuses.first;
  }

  @override
  void dispose() {
    _numberCtrl.dispose();
    _bedsCtrl.dispose();
    _bathsCtrl.dispose();
    _rentCtrl.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;

    setState(() {
      _saving = true;
      _error = null;
    });

    try {
      await ref.read(propertiesRepositoryProvider).updateUnit(
        widget.unit.id,
        {
          'unitNumber': _numberCtrl.text.trim(),
          'bedrooms': int.tryParse(_bedsCtrl.text) ?? widget.unit.bedrooms,
          'bathrooms':
              double.tryParse(_bathsCtrl.text) ?? widget.unit.bathrooms,
          'marketRent':
              double.tryParse(_rentCtrl.text) ?? widget.unit.marketRent,
          'status': _selectedStatus,
        },
      );

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
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final bottomPadding = MediaQuery.viewInsetsOf(context).bottom;

    return Padding(
      padding: EdgeInsets.fromLTRB(20, 20, 20, 20 + bottomPadding),
      child: Form(
        key: _formKey,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Row(
                children: [
                  Expanded(
                    child: Text(
                      'Edit Unit ${widget.unit.unitNumber}',
                      style: theme.textTheme.titleLarge
                          ?.copyWith(fontWeight: FontWeight.w700),
                    ),
                  ),
                  IconButton(
                    icon: const Icon(Icons.close),
                    onPressed: () => Navigator.of(context).pop(),
                  ),
                ],
              ),
              const SizedBox(height: 16),

              TextFormField(
                controller: _numberCtrl,
                textInputAction: TextInputAction.next,
                decoration: const InputDecoration(labelText: 'Unit number'),
                validator: (v) =>
                    (v == null || v.trim().isEmpty)
                        ? 'Unit number is required'
                        : null,
              ),
              const SizedBox(height: 12),

              Row(
                children: [
                  Expanded(
                    child: TextFormField(
                      controller: _bedsCtrl,
                      keyboardType: TextInputType.number,
                      textInputAction: TextInputAction.next,
                      decoration: const InputDecoration(labelText: 'Beds'),
                      validator: (v) =>
                          (v == null || int.tryParse(v) == null)
                              ? 'Enter a number'
                              : null,
                    ),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: TextFormField(
                      controller: _bathsCtrl,
                      keyboardType: const TextInputType.numberWithOptions(
                          decimal: true),
                      textInputAction: TextInputAction.next,
                      decoration: const InputDecoration(labelText: 'Baths'),
                      validator: (v) =>
                          (v == null || double.tryParse(v) == null)
                              ? 'Enter a number'
                              : null,
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 12),

              TextFormField(
                controller: _rentCtrl,
                keyboardType:
                    const TextInputType.numberWithOptions(decimal: true),
                textInputAction: TextInputAction.done,
                decoration:
                    const InputDecoration(labelText: 'Market rent (\$)'),
                validator: (v) =>
                    (v == null || double.tryParse(v) == null)
                        ? 'Enter an amount'
                        : null,
                onFieldSubmitted: (_) => _saving ? null : _submit(),
              ),
              const SizedBox(height: 12),

              DropdownButtonFormField<String>(
                initialValue: _selectedStatus,
                decoration: const InputDecoration(labelText: 'Status'),
                items: _statuses
                    .map((s) => DropdownMenuItem(value: s, child: Text(s)))
                    .toList(),
                onChanged: (v) {
                  if (v != null) setState(() => _selectedStatus = v);
                },
              ),

              if (_error != null) ...[
                const SizedBox(height: 12),
                Container(
                  padding:
                      const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
                  decoration: BoxDecoration(
                    color: colorScheme.errorContainer,
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: Text(
                    _error!,
                    style: TextStyle(
                        color: colorScheme.onErrorContainer, fontSize: 13),
                  ),
                ),
              ],

              const SizedBox(height: 20),

              FilledButton(
                onPressed: _saving ? null : _submit,
                child: _saving
                    ? const SizedBox(
                        height: 20,
                        width: 20,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : const Text('Save Changes'),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
