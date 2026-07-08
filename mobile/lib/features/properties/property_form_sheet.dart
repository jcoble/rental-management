import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/models.dart';
import '../../core/widgets/tabbed_form_sheet.dart';
import '../owners/owner_form_sheet.dart';
import '../owners/owners_models.dart';
import '../owners/owners_repository.dart';
import '../places/address_autocomplete_field.dart';
import 'properties_repository.dart';
import 'property_labels.dart';

Future<Property?> showPropertyFormSheet(
  BuildContext context, {
  Property? property,
  ValueChanged<Property>? onSaved,
}) {
  return showModalBottomSheet<Property>(
    context: context,
    isScrollControlled: true,
    useSafeArea: true,
    useRootNavigator: true,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
    ),
    builder: (_) => _PropertyFormSheet(property: property, onSaved: onSaved),
  );
}

class _PropertyOption {
  const _PropertyOption(this.value, this.label);

  final String value;
  final String label;
}

const _propertyTypes = [
  _PropertyOption('SingleFamily', 'Single-family'),
  _PropertyOption('MultiFamily', 'Multi-family'),
  _PropertyOption('Condo', 'Condo'),
  _PropertyOption('Townhome', 'Townhome'),
  _PropertyOption('Commercial', 'Commercial'),
  _PropertyOption('MixedUse', 'Mixed-use'),
];

const _propertyStatuses = [
  _PropertyOption('Active', 'Active'),
  _PropertyOption('UnderMaintenance', 'Under maintenance'),
  _PropertyOption('Inactive', 'Inactive'),
];

const _defaultCreatePropertyType = 'SingleFamily';
const _unitNumberMaxLength = 50;

class _PropertyFormSheet extends ConsumerStatefulWidget {
  const _PropertyFormSheet({this.property, this.onSaved});

  final Property? property;
  final ValueChanged<Property>? onSaved;

  @override
  ConsumerState<_PropertyFormSheet> createState() => _PropertyFormSheetState();
}

class _PropertyFormSheetState extends ConsumerState<_PropertyFormSheet> {
  final _nameCtrl = TextEditingController();
  final _addressCtrl = TextEditingController();
  final _address2Ctrl = TextEditingController();
  final _cityCtrl = TextEditingController();
  final _stateCtrl = TextEditingController();
  final _zipCtrl = TextEditingController();
  final _yearBuiltCtrl = TextEditingController();
  final _managementFeeCtrl = TextEditingController();
  final _notesCtrl = TextEditingController();
  final _purchasePriceCtrl = TextEditingController();
  final _landValueCtrl = TextEditingController();
  final _inServiceDateCtrl = TextEditingController();
  final _manualDepreciationCtrl = TextEditingController();

  String _selectedType = _defaultCreatePropertyType;
  String _selectedStatus = 'Active';
  int? _selectedOwnerEntityId;
  List<PropertyOwnerOption> _owners = const [];
  bool _ownersLoading = false;
  bool _ownerActionLoading = false;
  bool _saving = false;
  String? _error;
  String? _addressError;

  bool get _isEditing => widget.property != null;

  @override
  void initState() {
    super.initState();
    final property = widget.property;
    if (property != null) {
      _nameCtrl.text = property.name;
      _addressCtrl.text = property.addressLine1;
      _address2Ctrl.text = property.addressLine2 ?? '';
      _cityCtrl.text = property.city;
      _stateCtrl.text = property.state;
      _zipCtrl.text = property.postalCode;
      _yearBuiltCtrl.text = property.yearBuilt?.toString() ?? '';
      _managementFeeCtrl.text = property.managementFeePercent?.toString() ?? '';
      _notesCtrl.text = property.notes ?? '';
      _purchasePriceCtrl.text = property.purchasePrice?.toString() ?? '';
      _landValueCtrl.text = property.landValue?.toString() ?? '';
      _inServiceDateCtrl.text = _dateOnly(property.inServiceDate);
      _manualDepreciationCtrl.text =
          property.manualAnnualDepreciation?.toString() ?? '';
      _selectedType = _knownType(property.type);
      _selectedStatus = _knownStatus(property.status);
      _selectedOwnerEntityId = property.ownerEntityId;
    }
    Future.microtask(_loadOwners);
  }

  @override
  void dispose() {
    _nameCtrl.dispose();
    _addressCtrl.dispose();
    _address2Ctrl.dispose();
    _cityCtrl.dispose();
    _stateCtrl.dispose();
    _zipCtrl.dispose();
    _yearBuiltCtrl.dispose();
    _managementFeeCtrl.dispose();
    _notesCtrl.dispose();
    _purchasePriceCtrl.dispose();
    _landValueCtrl.dispose();
    _inServiceDateCtrl.dispose();
    _manualDepreciationCtrl.dispose();
    super.dispose();
  }

  static String _dateOnly(String? value) {
    if (value == null || value.isEmpty) return '';
    return value.length >= 10 ? value.substring(0, 10) : value;
  }

  static String _knownType(String value) {
    return _propertyTypes.any((option) => option.value == value)
        ? value
        : 'MultiFamily';
  }

  static String _knownStatus(String value) {
    return _propertyStatuses.any((option) => option.value == value)
        ? value
        : 'Active';
  }

  Future<void> _loadOwners({
    int? selectOwnerId,
    PropertyOwnerOption? fallbackOwner,
  }) async {
    setState(() => _ownersLoading = true);
    try {
      final owners = [
        ...await ref.read(propertiesRepositoryProvider).listOwnerOptions(),
      ];
      if (fallbackOwner != null &&
          !owners.any((owner) => owner.id == fallbackOwner.id)) {
        owners.insert(0, fallbackOwner);
      }
      if (!mounted) return;
      setState(() {
        _owners = owners;
        if (selectOwnerId != null) _selectedOwnerEntityId = selectOwnerId;
      });
    } on ApiException catch (e) {
      if (!mounted) return;
      setState(() => _error = e.message);
    } finally {
      if (mounted) setState(() => _ownersLoading = false);
    }
  }

  List<PropertyOwnerOption> get _ownerItems {
    final items = [..._owners];
    final selected = _selectedOwnerEntityId;
    if (selected != null && !items.any((owner) => owner.id == selected)) {
      items.insert(
        0,
        PropertyOwnerOption(
          id: selected,
          name: widget.property?.ownerName ?? 'Current owner',
        ),
      );
    }
    return items;
  }

  Future<void> _showOwnerForm({OwnerEntity? owner}) async {
    final saved = await showModalBottomSheet<OwnerEntity>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      useRootNavigator: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => OwnerFormSheet(existing: owner, onSaved: () {}),
    );
    if (saved == null || !mounted) return;
    await _loadOwners(
      selectOwnerId: saved.id,
      fallbackOwner: PropertyOwnerOption(id: saved.id, name: saved.name),
    );
  }

  Future<void> _editSelectedOwner() async {
    final ownerId = _selectedOwnerEntityId;
    if (ownerId == null || _ownerActionLoading) return;

    setState(() {
      _ownerActionLoading = true;
      _error = null;
    });

    try {
      final owner = await ref.read(ownersRepositoryProvider).getOwner(ownerId);
      if (!mounted) return;
      setState(() => _ownerActionLoading = false);
      await _showOwnerForm(owner: owner);
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
    } catch (e) {
      if (mounted) setState(() => _error = e.toString());
    } finally {
      if (mounted) setState(() => _ownerActionLoading = false);
    }
  }

  bool _validateAddressStep() {
    final valid = _addressCtrl.text.trim().isNotEmpty;
    setState(() => _addressError = valid ? null : 'Address is required');
    return valid;
  }

  String? _required(String label, String? value) {
    return value == null || value.trim().isEmpty ? '$label is required' : null;
  }

  String? _optionalIntRange(
    String label,
    String? value, {
    required int min,
    required int max,
  }) {
    final text = value?.trim() ?? '';
    if (text.isEmpty) return null;
    final parsed = int.tryParse(text);
    if (parsed == null) return '$label must be a whole number';
    if (parsed < min || parsed > max) return '$label must be $min-$max';
    return null;
  }

  String? _optionalNumberRange(
    String label,
    String? value, {
    required double min,
    required double max,
  }) {
    final text = value?.trim() ?? '';
    if (text.isEmpty) return null;
    final parsed = double.tryParse(text);
    if (parsed == null) return '$label must be a number';
    if (parsed < min || parsed > max) return '$label must be $min-$max';
    return null;
  }

  String? _optionalNonNegative(String label, String? value) {
    final text = value?.trim() ?? '';
    if (text.isEmpty) return null;
    final parsed = double.tryParse(text);
    if (parsed == null) return '$label must be a number';
    if (parsed < 0) return '$label cannot be negative';
    return null;
  }

  String? _landValueValidator(String? value) {
    final baseError = _optionalNonNegative('Land value', value);
    if (baseError != null) return baseError;

    final purchaseText = _purchasePriceCtrl.text.trim();
    final landText = value?.trim() ?? '';
    if (purchaseText.isEmpty || landText.isEmpty) return null;

    final purchase = double.tryParse(purchaseText);
    final land = double.tryParse(landText);
    if (purchase != null && land != null && land > purchase) {
      return 'Land value cannot exceed purchase price';
    }
    return null;
  }

  String? _optionalDate(String label, String? value) {
    final text = value?.trim() ?? '';
    if (text.isEmpty) return null;
    final parsed = DateTime.tryParse(text);
    if (parsed == null) return '$label must use YYYY-MM-DD';
    return null;
  }

  int? _optionalInt(TextEditingController controller) {
    final text = controller.text.trim();
    return text.isEmpty ? null : int.parse(text);
  }

  double? _optionalDouble(TextEditingController controller) {
    final text = controller.text.trim();
    return text.isEmpty ? null : double.parse(text);
  }

  void _putOptionalText(
    Map<String, dynamic> data,
    String key,
    TextEditingController controller,
  ) {
    final text = controller.text.trim();
    if (text.isNotEmpty) data[key] = text;
  }

  Map<String, dynamic> _payload() {
    final data = <String, dynamic>{
      'name': _nameCtrl.text.trim(),
      'type': _selectedType,
      'status': _selectedStatus,
      'addressLine1': _addressCtrl.text.trim(),
      'city': _cityCtrl.text.trim(),
      'state': _stateCtrl.text.trim(),
      'postalCode': _zipCtrl.text.trim(),
    };

    if (_selectedOwnerEntityId != null) {
      data['ownerEntityId'] = _selectedOwnerEntityId;
    }
    _putOptionalText(data, 'addressLine2', _address2Ctrl);
    _putOptionalText(data, 'notes', _notesCtrl);

    final yearBuilt = _optionalInt(_yearBuiltCtrl);
    if (yearBuilt != null) data['yearBuilt'] = yearBuilt;

    final managementFee = _optionalDouble(_managementFeeCtrl);
    if (managementFee != null) data['managementFeePercent'] = managementFee;

    final purchasePrice = _optionalDouble(_purchasePriceCtrl);
    if (purchasePrice != null) data['purchasePrice'] = purchasePrice;

    final landValue = _optionalDouble(_landValueCtrl);
    if (landValue != null) data['landValue'] = landValue;

    _putOptionalText(data, 'inServiceDate', _inServiceDateCtrl);

    final manualDepreciation = _optionalDouble(_manualDepreciationCtrl);
    if (manualDepreciation != null) {
      data['manualAnnualDepreciation'] = manualDepreciation;
    }

    return data;
  }

  Future<Property> _createProperty(
    PropertiesRepository repo,
    Map<String, dynamic> payload,
  ) async {
    final saved = await repo.createProperty(payload);
    final type = payload['type'] as String? ?? saved.type;
    if (!isPropertyUnitType(type)) return saved;

    if ((saved.unitCount ?? 0) > 0) return saved;

    final existingUnits = await repo.listUnits(saved.id);
    if (existingUnits.isNotEmpty) return saved;

    await repo.createUnit(saved.id, {
      'unitNumber': _canonicalUnitNumber(saved.name),
      'bedrooms': 0,
      'bathrooms': 0,
      'marketRent': 0,
      'status': 'Vacant',
    });
    return saved;
  }

  String _canonicalUnitNumber(String propertyName) {
    final value = propertyName.trim().isEmpty
        ? 'Property'
        : propertyName.trim();
    return value.length <= _unitNumberMaxLength
        ? value
        : value.substring(0, _unitNumberMaxLength);
  }

  Future<void> _submit() async {
    setState(() {
      _saving = true;
      _error = null;
    });

    try {
      final repo = ref.read(propertiesRepositoryProvider);
      final saved = _isEditing
          ? await repo.updateProperty(widget.property!.id, _payload())
          : await _createProperty(repo, _payload());

      widget.onSaved?.call(saved);
      if (mounted) Navigator.of(context).pop(saved);
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
      rethrow;
    } catch (e) {
      if (mounted) setState(() => _error = e.toString());
      rethrow;
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    const gap = SizedBox(height: 12);

    return TabbedFormSheet(
      title: _isEditing ? 'Edit Property' : 'New Property',
      saveLabel: _isEditing ? 'Save Property' : 'Save Property',
      saving: _saving,
      error: _error,
      onSave: _submit,
      tabs: [
        TabbedFormStepSpec(
          label: 'Identity',
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              TextFormField(
                key: const Key('property-name-field'),
                controller: _nameCtrl,
                textInputAction: TextInputAction.next,
                decoration: const InputDecoration(labelText: 'Property name'),
                validator: (v) => _required('Name', v),
              ),
              gap,
              DropdownButtonFormField<String>(
                key: const Key('property-type-field'),
                initialValue: _selectedType,
                decoration: const InputDecoration(labelText: 'Type'),
                items: _propertyTypes
                    .map(
                      (option) => DropdownMenuItem(
                        value: option.value,
                        child: Text(option.label),
                      ),
                    )
                    .toList(),
                onChanged: (value) {
                  if (value != null) setState(() => _selectedType = value);
                },
              ),
              gap,
              DropdownButtonFormField<String>(
                key: const Key('property-status-field'),
                initialValue: _selectedStatus,
                decoration: const InputDecoration(labelText: 'Status'),
                items: _propertyStatuses
                    .map(
                      (option) => DropdownMenuItem(
                        value: option.value,
                        child: Text(option.label),
                      ),
                    )
                    .toList(),
                onChanged: (value) {
                  if (value != null) setState(() => _selectedStatus = value);
                },
              ),
              gap,
              Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Expanded(
                    child: DropdownButtonFormField<int?>(
                      key: const Key('property-owner-field'),
                      initialValue: _selectedOwnerEntityId,
                      decoration: InputDecoration(
                        labelText: 'Owner',
                        helperText: _ownersLoading ? 'Loading owners...' : null,
                      ),
                      items: [
                        const DropdownMenuItem<int?>(
                          value: null,
                          child: Text('No owner assigned'),
                        ),
                        for (final owner in _ownerItems)
                          DropdownMenuItem<int?>(
                            value: owner.id,
                            child: Text(owner.name),
                          ),
                      ],
                      onChanged: _ownersLoading
                          ? null
                          : (value) =>
                                setState(() => _selectedOwnerEntityId = value),
                    ),
                  ),
                  const SizedBox(width: 8),
                  Padding(
                    padding: const EdgeInsets.only(top: 4),
                    child: IconButton.filledTonal(
                      key: const Key('property-owner-add-button'),
                      tooltip: 'Add owner',
                      icon: const Icon(Icons.person_add_alt_1_outlined),
                      onPressed: _ownersLoading || _ownerActionLoading
                          ? null
                          : () => _showOwnerForm(),
                    ),
                  ),
                  Padding(
                    padding: const EdgeInsets.only(top: 4),
                    child: IconButton.outlined(
                      key: const Key('property-owner-edit-button'),
                      tooltip: 'Edit selected owner',
                      icon: _ownerActionLoading
                          ? const SizedBox(
                              width: 18,
                              height: 18,
                              child: CircularProgressIndicator(strokeWidth: 2),
                            )
                          : const Icon(Icons.edit_outlined),
                      onPressed:
                          _ownersLoading ||
                              _ownerActionLoading ||
                              _selectedOwnerEntityId == null
                          ? null
                          : _editSelectedOwner,
                    ),
                  ),
                ],
              ),
            ],
          ),
        ),
        TabbedFormStepSpec(
          label: 'Address',
          validate: _validateAddressStep,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              AddressAutocompleteField(
                controller: _addressCtrl,
                label: 'Address',
                testKey: 'property-address-field',
                onResolved: (a) {
                  if (a.city.isNotEmpty) _cityCtrl.text = a.city;
                  if (a.state.isNotEmpty) _stateCtrl.text = a.state;
                  if (a.zip.isNotEmpty) _zipCtrl.text = a.zip;
                },
              ),
              if (_addressError != null) ...[
                const SizedBox(height: 6),
                Text(
                  _addressError!,
                  style: TextStyle(
                    color: Theme.of(context).colorScheme.error,
                    fontSize: 12,
                  ),
                ),
              ],
              gap,
              TextFormField(
                key: const Key('property-address2-field'),
                controller: _address2Ctrl,
                textInputAction: TextInputAction.next,
                decoration: const InputDecoration(
                  labelText: 'Apt / Suite / Unit #',
                ),
              ),
              gap,
              TextFormField(
                key: const Key('property-city-field'),
                controller: _cityCtrl,
                textInputAction: TextInputAction.next,
                decoration: const InputDecoration(labelText: 'City'),
                validator: (v) => _required('City', v),
              ),
              gap,
              Row(
                children: [
                  Expanded(
                    child: TextFormField(
                      key: const Key('property-state-field'),
                      controller: _stateCtrl,
                      textInputAction: TextInputAction.next,
                      textCapitalization: TextCapitalization.characters,
                      decoration: const InputDecoration(labelText: 'State'),
                      validator: (v) => _required('State', v),
                    ),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: TextFormField(
                      key: const Key('property-zip-field'),
                      controller: _zipCtrl,
                      textInputAction: TextInputAction.done,
                      keyboardType: TextInputType.number,
                      decoration: const InputDecoration(labelText: 'ZIP'),
                      validator: (v) => _required('ZIP', v),
                    ),
                  ),
                ],
              ),
            ],
          ),
        ),
        TabbedFormStepSpec(
          label: 'Operations',
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              TextFormField(
                key: const Key('property-year-built-field'),
                controller: _yearBuiltCtrl,
                textInputAction: TextInputAction.next,
                keyboardType: TextInputType.number,
                decoration: const InputDecoration(labelText: 'Year built'),
                validator: (v) =>
                    _optionalIntRange('Year built', v, min: 1800, max: 2200),
              ),
              gap,
              TextFormField(
                key: const Key('property-management-fee-field'),
                controller: _managementFeeCtrl,
                textInputAction: TextInputAction.next,
                keyboardType: const TextInputType.numberWithOptions(
                  decimal: true,
                ),
                decoration: const InputDecoration(
                  labelText: 'Management fee %',
                ),
                validator: (v) =>
                    _optionalNumberRange('Management fee', v, min: 0, max: 100),
              ),
              gap,
              TextFormField(
                key: const Key('property-notes-field'),
                controller: _notesCtrl,
                minLines: 3,
                maxLines: 5,
                decoration: const InputDecoration(labelText: 'Notes'),
              ),
            ],
          ),
        ),
        TabbedFormStepSpec(
          label: 'Tax Basis',
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              TextFormField(
                key: const Key('property-purchase-price-field'),
                controller: _purchasePriceCtrl,
                textInputAction: TextInputAction.next,
                keyboardType: const TextInputType.numberWithOptions(
                  decimal: true,
                ),
                decoration: const InputDecoration(labelText: 'Purchase price'),
                validator: (v) => _optionalNonNegative('Purchase price', v),
              ),
              gap,
              TextFormField(
                key: const Key('property-land-value-field'),
                controller: _landValueCtrl,
                textInputAction: TextInputAction.next,
                keyboardType: const TextInputType.numberWithOptions(
                  decimal: true,
                ),
                decoration: const InputDecoration(labelText: 'Land value'),
                validator: _landValueValidator,
              ),
              gap,
              TextFormField(
                key: const Key('property-in-service-date-field'),
                controller: _inServiceDateCtrl,
                textInputAction: TextInputAction.next,
                keyboardType: TextInputType.datetime,
                decoration: const InputDecoration(
                  labelText: 'In-service date',
                  hintText: 'YYYY-MM-DD',
                ),
                validator: (v) => _optionalDate('In-service date', v),
              ),
              gap,
              TextFormField(
                key: const Key('property-manual-depreciation-field'),
                controller: _manualDepreciationCtrl,
                keyboardType: const TextInputType.numberWithOptions(
                  decimal: true,
                ),
                decoration: const InputDecoration(
                  labelText: 'Manual annual depreciation',
                ),
                validator: (v) =>
                    _optionalNonNegative('Manual annual depreciation', v),
              ),
            ],
          ),
        ),
      ],
    );
  }
}
