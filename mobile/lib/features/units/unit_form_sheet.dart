import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/unit.dart';
import '../../core/widgets/tabbed_form_sheet.dart';
import '../properties/properties_repository.dart';

Future<Unit?> showUnitFormSheet(
  BuildContext context, {
  required int propertyId,
  Unit? unit,
  ValueChanged<Unit>? onSaved,
}) {
  return showModalBottomSheet<Unit>(
    context: context,
    isScrollControlled: true,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
    ),
    builder: (_) => UnitFormSheet(
      propertyId: propertyId,
      unit: unit,
      onSaved: onSaved,
    ),
  );
}

class UnitFormSheet extends ConsumerStatefulWidget {
  const UnitFormSheet({
    super.key,
    required this.propertyId,
    this.unit,
    this.onSaved,
  });

  final int propertyId;
  final Unit? unit;
  final ValueChanged<Unit>? onSaved;

  @override
  ConsumerState<UnitFormSheet> createState() => _UnitFormSheetState();
}

class _UnitFormSheetState extends ConsumerState<UnitFormSheet> {
  final _formKey = GlobalKey<FormState>();

  late final TextEditingController _numberCtrl;
  late final TextEditingController _floorPlanCtrl;
  late final TextEditingController _bedsCtrl;
  late final TextEditingController _bathsCtrl;
  late final TextEditingController _squareFeetCtrl;
  late final TextEditingController _rentCtrl;
  late final TextEditingController _notesCtrl;

  bool _saving = false;
  String? _error;

  bool get _isEdit => widget.unit != null;

  @override
  void initState() {
    super.initState();
    final unit = widget.unit;
    _numberCtrl = TextEditingController(text: unit?.unitNumber ?? '');
    _floorPlanCtrl = TextEditingController(text: unit?.floorPlan ?? '');
    _bedsCtrl = TextEditingController(text: (unit?.bedrooms ?? 1).toString());
    _bathsCtrl = TextEditingController(
      text: _numberToText(unit?.bathrooms ?? 1),
    );
    _squareFeetCtrl = TextEditingController(
      text: unit?.squareFeet == null
          ? ''
          : unit!.squareFeet!.round().toString(),
    );
    _rentCtrl = TextEditingController(
      text: _numberToText(unit?.marketRent ?? 1200),
    );
    _notesCtrl = TextEditingController(text: unit?.notes ?? '');
  }

  @override
  void dispose() {
    _numberCtrl.dispose();
    _floorPlanCtrl.dispose();
    _bedsCtrl.dispose();
    _bathsCtrl.dispose();
    _squareFeetCtrl.dispose();
    _rentCtrl.dispose();
    _notesCtrl.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;

    setState(() {
      _saving = true;
      _error = null;
    });

    try {
      final saved = _isEdit
          ? await ref
                .read(propertiesRepositoryProvider)
                .updateUnit(widget.unit!.id, _buildPayload())
          : await ref
                .read(propertiesRepositoryProvider)
                .createUnit(widget.propertyId, _buildPayload());

      widget.onSaved?.call(saved);
      if (mounted) Navigator.of(context).pop(saved);
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  Map<String, dynamic> _buildPayload() {
    final squareFeetText = _squareFeetCtrl.text.trim();
    return {
      'unitNumber': _numberCtrl.text.trim(),
      'floorPlan': _floorPlanCtrl.text.trim(),
      'bedrooms': int.tryParse(_bedsCtrl.text) ?? widget.unit?.bedrooms ?? 1,
      'bathrooms':
          double.tryParse(_bathsCtrl.text) ?? widget.unit?.bathrooms ?? 1.0,
      'squareFeet': squareFeetText.isEmpty
          ? null
          : int.tryParse(squareFeetText),
      'marketRent':
          double.tryParse(_rentCtrl.text) ?? widget.unit?.marketRent ?? 0.0,
      'notes': _notesCtrl.text.trim(),
    };
  }

  @override
  Widget build(BuildContext context) {
    const gap = SizedBox(height: 12);

    return Form(
      key: _formKey,
      child: TabbedFormSheet(
        title: _isEdit ? 'Edit Unit ${widget.unit!.unitNumber}' : 'Add Unit',
        saveLabel: _isEdit ? 'Save Changes' : 'Add Unit',
        saving: _saving,
        error: _error,
        onSave: _submit,
        tabs: [
          TabbedFormStepSpec(
            label: 'Details',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                TextFormField(
                  controller: _numberCtrl,
                  textInputAction: TextInputAction.next,
                  decoration: const InputDecoration(labelText: 'Unit number'),
                  validator: (v) => (v == null || v.trim().isEmpty)
                      ? 'Unit number is required'
                      : null,
                ),
                gap,
                TextFormField(
                  controller: _floorPlanCtrl,
                  textInputAction: TextInputAction.next,
                  decoration: const InputDecoration(labelText: 'Floor plan'),
                ),
                gap,
                Row(
                  children: [
                    Expanded(
                      child: TextFormField(
                        controller: _bedsCtrl,
                        keyboardType: TextInputType.number,
                        textInputAction: TextInputAction.next,
                        decoration: const InputDecoration(labelText: 'Beds'),
                        validator: (v) => (v == null || int.tryParse(v) == null)
                            ? 'Enter a number'
                            : null,
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: TextFormField(
                        controller: _bathsCtrl,
                        keyboardType: const TextInputType.numberWithOptions(
                          decimal: true,
                        ),
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
                gap,
                TextFormField(
                  controller: _squareFeetCtrl,
                  keyboardType: TextInputType.number,
                  textInputAction: TextInputAction.next,
                  decoration: const InputDecoration(labelText: 'Square feet'),
                  validator: (v) {
                    if (v == null || v.trim().isEmpty) return null;
                    return int.tryParse(v) == null ? 'Enter a number' : null;
                  },
                ),
              ],
            ),
          ),
          TabbedFormStepSpec(
            label: 'Rent',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                TextFormField(
                  controller: _rentCtrl,
                  keyboardType: const TextInputType.numberWithOptions(
                    decimal: true,
                  ),
                  textInputAction: TextInputAction.next,
                  decoration: const InputDecoration(
                    labelText: 'Market rent (\$)',
                  ),
                  validator: (v) => (v == null || double.tryParse(v) == null)
                      ? 'Enter an amount'
                      : null,
                ),
              ],
            ),
          ),
          TabbedFormStepSpec(
            label: 'Notes',
            child: TextFormField(
              controller: _notesCtrl,
              minLines: 4,
              maxLines: 8,
              decoration: const InputDecoration(labelText: 'Notes'),
            ),
          ),
        ],
      ),
    );
  }

  String _numberToText(num value) {
    final asDouble = value.toDouble();
    if (asDouble == asDouble.roundToDouble()) {
      return asDouble.round().toString();
    }
    return value.toString();
  }
}
