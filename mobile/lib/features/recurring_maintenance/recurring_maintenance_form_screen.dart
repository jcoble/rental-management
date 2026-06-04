import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import 'recurring_maintenance_list_screen.dart' show fmtDueDate;
import 'recurring_maintenance_models.dart';
import 'recurring_maintenance_repository.dart';

String _fmtIso(DateTime d) =>
    '${d.year}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';

/// Create or edit a recurring maintenance task.
///
/// Pops with `true` when a save succeeds so the list can refresh.
class RecurringMaintenanceFormScreen extends ConsumerStatefulWidget {
  const RecurringMaintenanceFormScreen({super.key, this.task});

  final RecurringMaintenanceTask? task;

  bool get isEditing => task != null;

  @override
  ConsumerState<RecurringMaintenanceFormScreen> createState() =>
      _RecurringMaintenanceFormScreenState();
}

class _RecurringMaintenanceFormScreenState
    extends ConsumerState<RecurringMaintenanceFormScreen> {
  final _formKey = GlobalKey<FormState>();
  late final TextEditingController _titleCtrl;
  late final TextEditingController _descCtrl;

  int? _propertyId;
  int? _unitId;
  int? _vendorId;
  String? _category;
  late String _interval;
  late String _priority;
  DateTime? _nextDueDate;
  late bool _isActive;

  bool _saving = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    final t = widget.task;
    _titleCtrl = TextEditingController(text: t?.title ?? '');
    _descCtrl = TextEditingController(text: t?.description ?? '');
    _propertyId = t?.propertyId;
    _unitId = t?.unitId;
    _vendorId = t?.vendorId;
    _category = t?.category;
    _interval = t?.recurrenceInterval ?? 'Monthly';
    _priority = t?.priority ?? 'Normal';
    _nextDueDate = t?.nextDueDate;
    _isActive = t?.isActive ?? true;
  }

  @override
  void dispose() {
    _titleCtrl.dispose();
    _descCtrl.dispose();
    super.dispose();
  }

  Future<void> _pickDueDate() async {
    final now = DateTime.now();
    final picked = await showDatePicker(
      context: context,
      initialDate: _nextDueDate ?? now,
      firstDate: DateTime(now.year - 1),
      lastDate: DateTime(now.year + 10),
    );
    if (picked == null || !mounted) return;
    setState(() => _nextDueDate = picked);
  }

  void _onPropertyChanged(int? value) {
    setState(() {
      _propertyId = value;
      // Unit is scoped to the property, so reset it when the property changes.
      _unitId = null;
    });
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;
    if (_nextDueDate == null) {
      setState(() => _error = 'Please pick the next due date.');
      return;
    }

    setState(() {
      _saving = true;
      _error = null;
    });

    final category = _category;
    final desc = _descCtrl.text.trim();
    final body = <String, dynamic>{
      'propertyId': _propertyId,
      'unitId': ?_unitId,
      'vendorId': ?_vendorId,
      'title': _titleCtrl.text.trim(),
      if (desc.isNotEmpty) 'description': desc,
      if (category != null && category.isNotEmpty) 'category': category,
      'recurrenceInterval': _interval,
      'nextDueDate': _fmtIso(_nextDueDate!),
      'isActive': _isActive,
      'priority': _priority,
    };

    try {
      final repo = ref.read(recurringMaintenanceRepositoryProvider);
      if (widget.isEditing) {
        await repo.update(widget.task!.id, body);
      } else {
        await repo.create(body);
      }
      if (!mounted) return;
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(
          SnackBar(
            content: Text(widget.isEditing ? 'Task updated' : 'Task created'),
          ),
        );
      Navigator.of(context).pop(true);
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
    final propertiesAsync = ref.watch(recurringPropertiesProvider);
    final vendorsAsync = ref.watch(recurringVendorsProvider);
    final propertyId = _propertyId;

    return Scaffold(
      appBar: AppBar(
        title: Text(widget.isEditing ? 'Edit Task' : 'New Recurring Task'),
      ),
      body: Form(
        key: _formKey,
        child: ListView(
          padding: const EdgeInsets.fromLTRB(20, 20, 20, 40),
          children: [
            // Title
            TextFormField(
              controller: _titleCtrl,
              textInputAction: TextInputAction.next,
              decoration: const InputDecoration(
                labelText: 'Title',
                hintText: 'e.g. Replace HVAC filter',
              ),
              validator: (v) =>
                  (v == null || v.trim().isEmpty) ? 'Title is required' : null,
            ),
            const SizedBox(height: 16),

            // Description
            TextFormField(
              controller: _descCtrl,
              maxLines: 3,
              textInputAction: TextInputAction.newline,
              decoration: const InputDecoration(
                labelText: 'Description (optional)',
              ),
            ),
            const SizedBox(height: 16),

            // Property (required)
            propertiesAsync.when(
              loading: () => const _FieldLoader(label: 'Property'),
              error: (e, _) => _FieldError(
                message:
                    'Could not load properties: ${e is ApiException ? e.message : e}',
              ),
              data: (properties) => DropdownButtonFormField<int>(
                initialValue: _propertyId,
                isExpanded: true,
                decoration: const InputDecoration(labelText: 'Property'),
                items: properties
                    .map(
                      (p) => DropdownMenuItem(
                        value: p.id,
                        child: Text(p.name, overflow: TextOverflow.ellipsis),
                      ),
                    )
                    .toList(),
                onChanged: _onPropertyChanged,
                validator: (v) =>
                    v == null ? 'Please select a property' : null,
              ),
            ),
            const SizedBox(height: 16),

            // Unit (optional, scoped to property)
            if (propertyId != null) ...[
              _UnitPicker(
                propertyId: propertyId,
                value: _unitId,
                onChanged: (v) => setState(() => _unitId = v),
              ),
              const SizedBox(height: 16),
            ],

            // Vendor (optional)
            vendorsAsync.when(
              loading: () => const _FieldLoader(label: 'Vendor (optional)'),
              error: (_, _) => const SizedBox.shrink(),
              data: (vendors) => DropdownButtonFormField<int?>(
                initialValue: _vendorId,
                isExpanded: true,
                decoration:
                    const InputDecoration(labelText: 'Vendor (optional)'),
                items: [
                  const DropdownMenuItem<int?>(
                    value: null,
                    child: Text('None'),
                  ),
                  ...vendors.map(
                    (v) => DropdownMenuItem<int?>(
                      value: v.id,
                      child: Text(v.name, overflow: TextOverflow.ellipsis),
                    ),
                  ),
                ],
                onChanged: (v) => setState(() => _vendorId = v),
              ),
            ),
            const SizedBox(height: 16),

            // Category (optional)
            DropdownButtonFormField<String?>(
              initialValue:
                  recurringCategories.contains(_category) ? _category : null,
              isExpanded: true,
              decoration:
                  const InputDecoration(labelText: 'Category (optional)'),
              items: [
                const DropdownMenuItem<String?>(
                  value: null,
                  child: Text('None'),
                ),
                ...recurringCategories.map(
                  (c) => DropdownMenuItem<String?>(value: c, child: Text(c)),
                ),
              ],
              onChanged: (v) => setState(() => _category = v),
            ),
            const SizedBox(height: 16),

            // Interval + Priority
            Row(
              children: [
                Expanded(
                  child: DropdownButtonFormField<String>(
                    initialValue: _interval,
                    isExpanded: true,
                    decoration:
                        const InputDecoration(labelText: 'Repeats'),
                    items: recurrenceIntervals
                        .map(
                          (i) => DropdownMenuItem(
                            value: i,
                            child: Text(
                              recurrenceLabel(i),
                              overflow: TextOverflow.ellipsis,
                            ),
                          ),
                        )
                        .toList(),
                    onChanged: (v) {
                      if (v != null) setState(() => _interval = v);
                    },
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: DropdownButtonFormField<String>(
                    initialValue: _priority,
                    isExpanded: true,
                    decoration:
                        const InputDecoration(labelText: 'Priority'),
                    items: recurringPriorities
                        .map(
                          (p) => DropdownMenuItem(value: p, child: Text(p)),
                        )
                        .toList(),
                    onChanged: (v) {
                      if (v != null) setState(() => _priority = v);
                    },
                  ),
                ),
              ],
            ),
            const SizedBox(height: 16),

            // Next due date
            _DateField(
              label: 'Next due date',
              date: _nextDueDate,
              onTap: _pickDueDate,
            ),
            const SizedBox(height: 8),

            // Active switch
            SwitchListTile(
              contentPadding: EdgeInsets.zero,
              title: const Text('Active'),
              subtitle: Text(
                _isActive
                    ? 'Auto-creates work orders when due'
                    : 'Paused — no work orders are created',
                style: theme.textTheme.bodySmall?.copyWith(
                  color: colorScheme.onSurfaceVariant,
                ),
              ),
              value: _isActive,
              onChanged: (v) => setState(() => _isActive = v),
            ),

            if (_error != null) ...[
              const SizedBox(height: 8),
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
                    color: colorScheme.onErrorContainer,
                    fontSize: 13,
                  ),
                ),
              ),
            ],

            const SizedBox(height: 24),
            FilledButton(
              onPressed: _saving ? null : _submit,
              child: _saving
                  ? const SizedBox(
                      height: 20,
                      width: 20,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : Text(widget.isEditing ? 'Save Changes' : 'Create Task'),
            ),
          ],
        ),
      ),
    );
  }
}

// ── Unit picker (scoped to a property) ────────────────────────────────────────

class _UnitPicker extends ConsumerWidget {
  const _UnitPicker({
    required this.propertyId,
    required this.value,
    required this.onChanged,
  });

  final int propertyId;
  final int? value;
  final ValueChanged<int?> onChanged;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final unitsAsync = ref.watch(recurringUnitsProvider(propertyId));
    return unitsAsync.when(
      loading: () => const _FieldLoader(label: 'Unit (optional)'),
      error: (_, _) => const SizedBox.shrink(),
      data: (units) {
        if (units.isEmpty) return const SizedBox.shrink();
        // Guard against a stale unit id that doesn't belong to this property.
        final ids = units.map((u) => u.id).toSet();
        final current = ids.contains(value) ? value : null;
        return DropdownButtonFormField<int?>(
          initialValue: current,
          isExpanded: true,
          decoration: const InputDecoration(labelText: 'Unit (optional)'),
          items: [
            const DropdownMenuItem<int?>(value: null, child: Text('None')),
            ...units.map(
              (u) => DropdownMenuItem<int?>(
                value: u.id,
                child: Text(
                  'Unit ${u.unitNumber}',
                  overflow: TextOverflow.ellipsis,
                ),
              ),
            ),
          ],
          onChanged: onChanged,
        );
      },
    );
  }
}

// ── Small shared field widgets ────────────────────────────────────────────────

class _DateField extends StatelessWidget {
  const _DateField({
    required this.label,
    required this.date,
    required this.onTap,
  });

  final String label;
  final DateTime? date;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    return InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(4),
      child: InputDecorator(
        decoration: InputDecoration(
          labelText: label,
          suffixIcon: const Icon(Icons.calendar_today_outlined, size: 18),
        ),
        child: Text(
          date != null ? fmtDueDate(date!) : 'Select date',
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

class _FieldLoader extends StatelessWidget {
  const _FieldLoader({required this.label});

  final String label;

  @override
  Widget build(BuildContext context) {
    return InputDecorator(
      decoration: InputDecoration(labelText: label),
      child: const Padding(
        padding: EdgeInsets.symmetric(vertical: 2),
        child: SizedBox(
          height: 18,
          width: 18,
          child: CircularProgressIndicator(strokeWidth: 2),
        ),
      ),
    );
  }
}

class _FieldError extends StatelessWidget {
  const _FieldError({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    return Text(
      message,
      style: TextStyle(color: colorScheme.error, fontSize: 13),
    );
  }
}
