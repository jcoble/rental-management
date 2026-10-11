import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/widgets/tabbed_form_sheet.dart';
import '../accounting/journal_detail_sheet.dart';
import 'capital_assets_repository.dart';

Future<bool> showPropertyCapitalAssetFormSheet(
  BuildContext context, {
  required int propertyId,
  CapitalAsset? asset,
}) async {
  final saved = await showModalBottomSheet<bool>(
    context: context,
    isScrollControlled: true,
    useSafeArea: true,
    useRootNavigator: true,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
    ),
    builder: (_) =>
        _PropertyCapitalAssetFormSheet(propertyId: propertyId, asset: asset),
  );
  return saved == true;
}

class _PropertyCapitalAssetFormSheet extends ConsumerStatefulWidget {
  const _PropertyCapitalAssetFormSheet({required this.propertyId, this.asset});

  final int propertyId;
  final CapitalAsset? asset;

  @override
  ConsumerState<_PropertyCapitalAssetFormSheet> createState() =>
      _PropertyCapitalAssetFormSheetState();
}

class _PropertyCapitalAssetFormSheetState
    extends ConsumerState<_PropertyCapitalAssetFormSheet> {
  final _descriptionCtrl = TextEditingController();
  final _costBasisCtrl = TextEditingController();
  final _inServiceDateCtrl = TextEditingController();
  final _recoveryYearsCtrl = TextEditingController(text: '27.5');
  final _accumulatedCtrl = TextEditingController();

  DepreciationMethod _method = DepreciationMethod.straightLine;
  DepreciationConvention _convention = DepreciationConvention.midMonth;
  bool _saving = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    final asset = widget.asset;
    if (asset == null) {
      _inServiceDateCtrl.text = _dateInput(DateTime.now());
      return;
    }

    _descriptionCtrl.text = asset.description;
    _costBasisCtrl.text = _moneyInput(asset.costBasis);
    _inServiceDateCtrl.text = _dateInput(asset.inServiceDate);
    _method = asset.method;
    _recoveryYearsCtrl.text = _decimalInput(asset.recoveryYears);
    _convention = asset.convention == DepreciationConvention.midQuarter
        ? DepreciationConvention.halfYear
        : asset.convention;
    _accumulatedCtrl.text = asset.accumulatedDepreciation == 0
        ? ''
        : _moneyInput(asset.accumulatedDepreciation);
  }

  @override
  void dispose() {
    _descriptionCtrl.dispose();
    _costBasisCtrl.dispose();
    _inServiceDateCtrl.dispose();
    _recoveryYearsCtrl.dispose();
    _accumulatedCtrl.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    setState(() {
      _saving = true;
      _error = null;
    });

    final repo = ref.read(capitalAssetsRepositoryProvider);
    final payload = _payload();

    try {
      final asset = widget.asset;
      if (asset == null) {
        await repo.createAsset(propertyId: widget.propertyId, data: payload);
      } else {
        await repo.updateAsset(asset.id, payload);
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
    final accumulated = _optionalDouble(_accumulatedCtrl.text);
    return {
      'description': _descriptionCtrl.text.trim(),
      'costBasis': _requiredDouble(_costBasisCtrl.text),
      'inServiceDate': _inServiceDateCtrl.text.trim(),
      'method': _method.wire,
      'recoveryYears': _requiredDouble(_recoveryYearsCtrl.text),
      'convention': _convention.wire,
      'accumulatedDepreciation': accumulated ?? 0,
    };
  }

  @override
  Widget build(BuildContext context) {
    const gap = SizedBox(height: 12);
    final showAdvanced =
        ref.watch(accountingDetailModeProvider) ==
        AccountingDetailMode.advanced;
    return TabbedFormSheet(
      title: widget.asset == null ? 'Add Capital Asset' : 'Edit Capital Asset',
      saveLabel: widget.asset == null ? 'Add Asset' : 'Save Asset',
      saving: _saving,
      error: _error,
      onSave: _save,
      tabs: [
        TabbedFormStepSpec(
          label: 'Basics',
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              TextFormField(
                key: const Key('capital-asset-description-field'),
                controller: _descriptionCtrl,
                textInputAction: TextInputAction.next,
                textCapitalization: TextCapitalization.sentences,
                decoration: const InputDecoration(labelText: 'Description'),
                validator: (value) {
                  final trimmed = value?.trim() ?? '';
                  if (trimmed.isEmpty) return 'Description is required';
                  if (trimmed.length > 500) {
                    return 'Description must be 500 characters or fewer';
                  }
                  return null;
                },
              ),
              gap,
              _MoneyField(
                keyName: 'capital-asset-cost-basis-field',
                controller: _costBasisCtrl,
                label: 'Cost basis',
                required: true,
              ),
              gap,
              TextFormField(
                key: const Key('capital-asset-in-service-date-field'),
                controller: _inServiceDateCtrl,
                keyboardType: TextInputType.datetime,
                textInputAction: TextInputAction.next,
                decoration: const InputDecoration(
                  labelText: 'In-service date',
                  helperText: 'YYYY-MM-DD',
                ),
                validator: (value) =>
                    _dateValid(value) ? null : 'Enter YYYY-MM-DD',
              ),
            ],
          ),
        ),
        TabbedFormStepSpec(
          label: 'Depreciation',
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              DropdownButtonFormField<DepreciationMethod>(
                key: const Key('capital-asset-method-field'),
                initialValue: _method,
                decoration: const InputDecoration(labelText: 'Method'),
                items: DepreciationMethod.values
                    .map(
                      (method) => DropdownMenuItem(
                        value: method,
                        child: Text(method.label),
                      ),
                    )
                    .toList(),
                onChanged: (value) {
                  if (value != null) setState(() => _method = value);
                },
              ),
              gap,
              TextFormField(
                key: const Key('capital-asset-recovery-years-field'),
                controller: _recoveryYearsCtrl,
                keyboardType: const TextInputType.numberWithOptions(
                  decimal: true,
                ),
                textInputAction: TextInputAction.next,
                decoration: const InputDecoration(
                  labelText: 'Recovery life',
                  suffixText: 'years',
                ),
                validator: (value) => _numberInRange(
                  value,
                  min: 1,
                  max: 40,
                  label: 'Recovery life',
                ),
              ),
              if (showAdvanced) ...[
                gap,
                DropdownButtonFormField<DepreciationConvention>(
                  key: const Key('capital-asset-convention-field'),
                  initialValue: _convention,
                  decoration: const InputDecoration(labelText: 'Convention'),
                  items:
                      const [
                            DepreciationConvention.midMonth,
                            DepreciationConvention.halfYear,
                          ]
                          .map(
                            (convention) => DropdownMenuItem(
                              value: convention,
                              child: Text(convention.label),
                            ),
                          )
                          .toList(),
                  onChanged: (value) {
                    if (value != null) setState(() => _convention = value);
                  },
                ),
              ],
              gap,
              _MoneyField(
                keyName: 'capital-asset-accumulated-field',
                controller: _accumulatedCtrl,
                label: 'Prior depreciation',
              ),
            ],
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
        return _numberInRange(value, min: 0, max: 999999999, label: label);
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

String _decimalInput(double value) => value == value.roundToDouble()
    ? value.toStringAsFixed(0)
    : value.toString();
