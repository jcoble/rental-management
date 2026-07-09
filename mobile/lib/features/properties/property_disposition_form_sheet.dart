import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/widgets/tabbed_form_sheet.dart';
import 'property_dispositions_repository.dart';

Future<bool> showPropertyDispositionFormSheet(
  BuildContext context, {
  required int propertyId,
  PropertyDisposition? disposition,
}) async {
  final saved = await showModalBottomSheet<bool>(
    context: context,
    isScrollControlled: true,
    useSafeArea: true,
    useRootNavigator: true,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
    ),
    builder: (_) => _PropertyDispositionFormSheet(
      propertyId: propertyId,
      disposition: disposition,
    ),
  );
  return saved == true;
}

class _PropertyDispositionFormSheet extends ConsumerStatefulWidget {
  const _PropertyDispositionFormSheet({
    required this.propertyId,
    this.disposition,
  });

  final int propertyId;
  final PropertyDisposition? disposition;

  @override
  ConsumerState<_PropertyDispositionFormSheet> createState() =>
      _PropertyDispositionFormSheetState();
}

class _PropertyDispositionFormSheetState
    extends ConsumerState<_PropertyDispositionFormSheet> {
  final _closedOnCtrl = TextEditingController();
  final _salePriceCtrl = TextEditingController();
  final _sellingCostsCtrl = TextEditingController();
  final _buyerCtrl = TextEditingController();
  final _memoCtrl = TextEditingController();

  bool _saving = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    final disposition = widget.disposition;
    if (disposition == null) {
      _closedOnCtrl.text = _dateInput(DateTime.now());
      return;
    }

    _closedOnCtrl.text = _dateInput(disposition.closedOnDate);
    _salePriceCtrl.text = _moneyInput(disposition.salePrice);
    _sellingCostsCtrl.text = _moneyInput(disposition.sellingCosts);
    _buyerCtrl.text = disposition.buyerName ?? '';
    _memoCtrl.text = disposition.memo ?? '';
  }

  @override
  void dispose() {
    _closedOnCtrl.dispose();
    _salePriceCtrl.dispose();
    _sellingCostsCtrl.dispose();
    _buyerCtrl.dispose();
    _memoCtrl.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    setState(() {
      _saving = true;
      _error = null;
    });

    final repo = ref.read(propertyDispositionsRepositoryProvider);
    final payload = _payload();

    try {
      final disposition = widget.disposition;
      if (disposition == null) {
        await repo.createDisposition(
          propertyId: widget.propertyId,
          data: payload,
        );
      } else {
        await repo.updateDisposition(disposition.id, payload);
      }
      if (mounted) Navigator.of(context).pop(true);
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
      rethrow;
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  Map<String, dynamic> _payload() {
    return {
      'closedOnDate': _closedOnCtrl.text.trim(),
      'salePrice': _requiredDouble(_salePriceCtrl.text),
      'sellingCosts': _optionalDouble(_sellingCostsCtrl.text) ?? 0,
      'buyerName': _optionalText(_buyerCtrl.text),
      'memo': _optionalText(_memoCtrl.text),
    };
  }

  @override
  Widget build(BuildContext context) {
    const gap = SizedBox(height: 12);
    return TabbedFormSheet(
      title: widget.disposition == null
          ? 'Record Property Sale'
          : 'Edit Property Sale',
      saveLabel: widget.disposition == null ? 'Record Sale' : 'Save Sale',
      saving: _saving,
      error: _error,
      onSave: _save,
      tabs: [
        TabbedFormStepSpec(
          label: 'Sale',
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              TextFormField(
                key: const Key('property-disposition-closed-on-field'),
                controller: _closedOnCtrl,
                keyboardType: TextInputType.datetime,
                textInputAction: TextInputAction.next,
                decoration: const InputDecoration(
                  labelText: 'Close date',
                  helperText: 'YYYY-MM-DD',
                ),
                validator: (value) =>
                    _dateValid(value) ? null : 'Enter YYYY-MM-DD',
              ),
              gap,
              _MoneyField(
                keyName: 'property-disposition-sale-price-field',
                controller: _salePriceCtrl,
                label: 'Sale price',
                required: true,
              ),
              gap,
              _MoneyField(
                keyName: 'property-disposition-selling-costs-field',
                controller: _sellingCostsCtrl,
                label: 'Selling costs',
              ),
              gap,
              TextFormField(
                key: const Key('property-disposition-buyer-field'),
                controller: _buyerCtrl,
                textInputAction: TextInputAction.next,
                textCapitalization: TextCapitalization.words,
                decoration: const InputDecoration(labelText: 'Buyer'),
                validator: (value) => _maxLength(value, 200, 'Buyer'),
              ),
            ],
          ),
        ),
        TabbedFormStepSpec(
          label: 'Notes',
          child: TextFormField(
            key: const Key('property-disposition-memo-field'),
            controller: _memoCtrl,
            minLines: 4,
            maxLines: 8,
            textCapitalization: TextCapitalization.sentences,
            decoration: const InputDecoration(labelText: 'Memo'),
            validator: (value) => _maxLength(value, 1000, 'Memo'),
          ),
        ),
      ],
    );
  }
}

class _MoneyField extends StatelessWidget {
  const _MoneyField({
    required this.keyName,
    required this.controller,
    required this.label,
    this.required = false,
  });

  final String keyName;
  final TextEditingController controller;
  final String label;
  final bool required;

  @override
  Widget build(BuildContext context) {
    return TextFormField(
      key: Key(keyName),
      controller: controller,
      keyboardType: const TextInputType.numberWithOptions(decimal: true),
      textInputAction: TextInputAction.next,
      decoration: InputDecoration(labelText: label, prefixText: '\$'),
      validator: (value) {
        if (!required && (value == null || value.trim().isEmpty)) return null;
        return _numberInRange(
          value,
          min: required ? 0.01 : 0,
          max: 999999999,
          label: label,
        );
      },
    );
  }
}

double _requiredDouble(String value) => double.tryParse(value.trim()) ?? 0;

double? _optionalDouble(String value) {
  final trimmed = value.trim();
  if (trimmed.isEmpty) return null;
  return double.tryParse(trimmed);
}

String? _optionalText(String value) {
  final trimmed = value.trim();
  return trimmed.isEmpty ? null : trimmed;
}

String? _numberInRange(
  String? value, {
  required double min,
  required double max,
  required String label,
}) {
  final parsed = double.tryParse(value?.trim() ?? '');
  if (parsed == null) return 'Enter a number';
  if (parsed < min || parsed > max) {
    return '$label must be between ${_rangeLabel(min)} and ${_rangeLabel(max)}';
  }
  return null;
}

String? _maxLength(String? value, int max, String label) {
  final trimmed = value?.trim() ?? '';
  if (trimmed.length > max) return '$label must be $max characters or fewer';
  return null;
}

bool _dateValid(String? value) {
  final trimmed = value?.trim() ?? '';
  if (!RegExp(r'^\d{4}-\d{2}-\d{2}$').hasMatch(trimmed)) return false;
  return DateTime.tryParse(trimmed) != null;
}

String _rangeLabel(double value) =>
    value == value.roundToDouble() ? value.toStringAsFixed(0) : '$value';

String _dateInput(DateTime value) {
  final local = value.toLocal();
  return '${local.year.toString().padLeft(4, '0')}-'
      '${local.month.toString().padLeft(2, '0')}-'
      '${local.day.toString().padLeft(2, '0')}';
}

String _moneyInput(double value) => value == value.roundToDouble()
    ? value.toStringAsFixed(0)
    : value.toStringAsFixed(2);
