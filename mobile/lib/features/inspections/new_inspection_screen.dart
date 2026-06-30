import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/widgets/tabbed_form_sheet.dart';
import 'inspections_list_screen.dart' show fmtInspectionDate;
import 'inspections_models.dart';
import 'inspections_repository.dart';

String _fmtIso(DateTime d) =>
    '${d.year}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';

/// New-inspection flow: pick a checklist template (which sets the type) plus a
/// property/unit and scheduled date, then create. Pops the new inspection id.
class NewInspectionScreen extends ConsumerStatefulWidget {
  const NewInspectionScreen({super.key});

  @override
  ConsumerState<NewInspectionScreen> createState() =>
      _NewInspectionScreenState();
}

class _NewInspectionScreenState extends ConsumerState<NewInspectionScreen> {
  InspectionTemplate? _template;
  int? _propertyId;
  int? _unitId;
  DateTime _scheduledFor = DateTime.now();

  bool _creating = false;
  String? _error;

  Future<void> _pickDate() async {
    final now = DateTime.now();
    final picked = await showDatePicker(
      context: context,
      initialDate: _scheduledFor,
      firstDate: DateTime(now.year - 1),
      lastDate: DateTime(now.year + 5),
    );
    if (picked == null || !mounted) return;
    setState(() => _scheduledFor = picked);
  }

  Future<void> _create() async {
    final template = _template;
    final propertyId = _propertyId;
    if (template == null) {
      setState(() => _error = 'Please choose a checklist template.');
      throw StateError('Checklist template is required.');
    }
    if (propertyId == null) {
      setState(() => _error = 'Please choose a property.');
      throw StateError('Property is required.');
    }

    setState(() {
      _creating = true;
      _error = null;
    });
    try {
      final detail = await ref.read(inspectionsRepositoryProvider).create({
        'propertyId': propertyId,
        if (_unitId != null) 'unitId': _unitId,
        'type': template.inspectionType,
        'scheduledFor': _fmtIso(_scheduledFor),
        // Built-in template ids are negative — pass them back as-is.
        'templateId': template.id,
      });
      if (!mounted) return;
      Navigator.of(context).pop(detail.id);
    } on ApiException catch (e) {
      setState(() => _error = e.message);
      rethrow;
    } finally {
      if (mounted) setState(() => _creating = false);
    }
  }

  void _onPropertyChanged(int? value) {
    setState(() {
      _propertyId = value;
      _unitId = null;
      if (_error == 'Please choose a property.') {
        _error = null;
      }
    });
  }

  bool _validateTemplateStep() {
    if (_template != null) {
      if (_error == 'Please choose a checklist template.') {
        setState(() => _error = null);
      }
      return true;
    }

    setState(() => _error = 'Please choose a checklist template.');
    return false;
  }

  bool _validateLocationStep() {
    if (_propertyId != null) {
      if (_error == 'Please choose a property.') {
        setState(() => _error = null);
      }
      return true;
    }

    setState(() => _error = 'Please choose a property.');
    return false;
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final templatesAsync = ref.watch(inspectionTemplatesProvider);
    final propertiesAsync = ref.watch(inspectionPropertiesProvider);
    final propertyId = _propertyId;

    return Scaffold(
      body: TabbedFormSheet(
        title: 'New inspection',
        saveLabel: 'Start inspection',
        saving: _creating,
        error: _error,
        heightFactor: 0.94,
        onSave: _create,
        tabs: [
          TabbedFormStepSpec(
            label: 'Checklist',
            isComplete: () => _template != null,
            validate: _validateTemplateStep,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Text(
                  'Checklist',
                  style: theme.textTheme.titleSmall?.copyWith(
                    fontWeight: FontWeight.w700,
                  ),
                ),
                const SizedBox(height: 8),
                templatesAsync.when(
                  loading: () =>
                      const _FieldLoader(label: 'Loading templates…'),
                  error: (e, _) => _FieldError(
                    message: e is ApiException ? e.message : e.toString(),
                  ),
                  data: (templates) {
                    if (templates.isEmpty) {
                      return Text(
                        'No checklist templates are available.',
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: cs.onSurfaceVariant,
                        ),
                      );
                    }
                    return Column(
                      children: [
                        for (final t in templates)
                          _TemplateTile(
                            template: t,
                            selected: _template?.id == t.id,
                            onTap: () => setState(() {
                              _template = t;
                              if (_error ==
                                  'Please choose a checklist template.') {
                                _error = null;
                              }
                            }),
                          ),
                      ],
                    );
                  },
                ),
              ],
            ),
          ),
          TabbedFormStepSpec(
            label: 'Location',
            isComplete: () => _propertyId != null,
            validate: _validateLocationStep,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Text(
                  'Where',
                  style: theme.textTheme.titleSmall?.copyWith(
                    fontWeight: FontWeight.w700,
                  ),
                ),
                const SizedBox(height: 12),
                propertiesAsync.when(
                  loading: () => const _FieldLoader(label: 'Property'),
                  error: (e, _) => _FieldError(
                    message: e is ApiException ? e.message : e.toString(),
                  ),
                  data: (properties) => DropdownButtonFormField<int>(
                    initialValue: _propertyId,
                    isExpanded: true,
                    decoration: const InputDecoration(labelText: 'Property'),
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
                    onChanged: _onPropertyChanged,
                  ),
                ),
                const SizedBox(height: 16),
                if (propertyId != null)
                  _UnitPicker(
                    propertyId: propertyId,
                    value: _unitId,
                    onChanged: (v) => setState(() => _unitId = v),
                  ),
              ],
            ),
          ),
          TabbedFormStepSpec(
            label: 'Schedule',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Text(
                  'Schedule',
                  style: theme.textTheme.titleSmall?.copyWith(
                    fontWeight: FontWeight.w700,
                  ),
                ),
                const SizedBox(height: 12),
                _DateField(
                  label: 'Scheduled date',
                  date: _scheduledFor,
                  onTap: _pickDate,
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

class _TemplateTile extends StatelessWidget {
  const _TemplateTile({
    required this.template,
    required this.selected,
    required this.onTap,
  });

  final InspectionTemplate template;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final itemCount = template.items.length;
    return Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: Material(
        color: selected ? cs.primaryContainer : cs.surfaceContainerHighest,
        borderRadius: BorderRadius.circular(12),
        child: InkWell(
          onTap: onTap,
          borderRadius: BorderRadius.circular(12),
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 14),
            child: Row(
              children: [
                Icon(
                  selected
                      ? Icons.radio_button_checked
                      : Icons.radio_button_unchecked,
                  color: selected ? cs.primary : cs.onSurfaceVariant,
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        template.name,
                        style: theme.textTheme.titleSmall?.copyWith(
                          fontWeight: FontWeight.w600,
                          color: selected
                              ? cs.onPrimaryContainer
                              : cs.onSurface,
                        ),
                      ),
                      const SizedBox(height: 2),
                      Text(
                        '${friendlyInspectionType(template.inspectionType)}'
                        '  ·  $itemCount item${itemCount == 1 ? '' : 's'}'
                        '${template.isBuiltIn ? '  ·  Built-in' : ''}',
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: selected
                              ? cs.onPrimaryContainer
                              : cs.onSurfaceVariant,
                        ),
                      ),
                    ],
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

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
    final unitsAsync = ref.watch(inspectionUnitsProvider(propertyId));
    return unitsAsync.when(
      loading: () => const _FieldLoader(label: 'Unit (optional)'),
      error: (_, _) => const SizedBox.shrink(),
      data: (units) {
        if (units.isEmpty) return const SizedBox.shrink();
        final ids = units.map((u) => u.id).toSet();
        final current = ids.contains(value) ? value : null;
        return DropdownButtonFormField<int?>(
          initialValue: current,
          isExpanded: true,
          decoration: const InputDecoration(labelText: 'Unit (optional)'),
          items: [
            const DropdownMenuItem<int?>(
              value: null,
              child: Text('Whole property'),
            ),
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

class _DateField extends StatelessWidget {
  const _DateField({
    required this.label,
    required this.date,
    required this.onTap,
  });

  final String label;
  final DateTime date;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(4),
      child: InputDecorator(
        decoration: InputDecoration(
          labelText: label,
          suffixIcon: const Icon(Icons.calendar_today_outlined, size: 18),
        ),
        child: Text(fmtInspectionDate(date), style: theme.textTheme.bodyMedium),
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
    final cs = Theme.of(context).colorScheme;
    return Text(message, style: TextStyle(color: cs.error, fontSize: 13));
  }
}
