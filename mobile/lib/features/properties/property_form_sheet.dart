import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/models.dart';
import '../../core/widgets/tabbed_form_sheet.dart';
import '../owners/owner_form_sheet.dart';
import '../owners/owners_models.dart';
import '../owners/owners_repository.dart';
import '../places/address_autocomplete_field.dart';
import 'properties_repository.dart';

Future<Property?> showPropertyFormSheet(
  BuildContext context, {
  Property? property,
  ValueChanged<Property>? onSaved,
  ValueChanged<PropertySetupResult>? onSetupSaved,
}) {
  return showModalBottomSheet<Property>(
    context: context,
    isScrollControlled: true,
    useSafeArea: true,
    useRootNavigator: true,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
    ),
    builder: (_) => _PropertyFormSheet(
      property: property,
      onSaved: onSaved,
      onSetupSaved: onSetupSaved,
    ),
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
  const _PropertyFormSheet({this.property, this.onSaved, this.onSetupSaved});

  final Property? property;
  final ValueChanged<Property>? onSaved;
  final ValueChanged<PropertySetupResult>? onSetupSaved;

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
  final _unitNumbersCtrl = TextEditingController();
  final _bedroomsCtrl = TextEditingController(text: '0');
  final _bathroomsCtrl = TextEditingController(text: '0');
  final _marketRentCtrl = TextEditingController(text: '0');

  String _selectedType = _defaultCreatePropertyType;
  RentalStructure _rentalStructure = RentalStructure.singleRental;
  String _selectedStatus = 'Active';
  int? _selectedOwnerEntityId;
  int? _initialOwnerEntityId;
  List<PropertyOwnerOption> _owners = const [];
  bool _ownersLoading = false;
  bool _ownerActionLoading = false;
  bool _saving = false;
  String? _error;
  String? _addressError;
  String? _rentalsError;

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
      _rentalStructure = property.rentalStructure;
      _selectedStatus = _knownStatus(property.status);
      _selectedOwnerEntityId = property.ownerships.length == 1
          ? property.ownerships.first.ownerEntityId
          : null;
      _initialOwnerEntityId = _selectedOwnerEntityId;
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
    _unitNumbersCtrl.dispose();
    _bedroomsCtrl.dispose();
    _bathroomsCtrl.dispose();
    _marketRentCtrl.dispose();
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
          name: widget.property?.ownerships.length == 1
              ? widget.property!.ownerships.first.ownerName
              : 'Current owner',
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

  List<String> get _unitNumbers => _unitNumbersCtrl.text
      .split(RegExp(r'[\n,]'))
      .map((value) => value.trim())
      .where((value) => value.isNotEmpty)
      .toList(growable: false);

  bool _validateRentalsStep() {
    String? error;
    if (_rentalStructure == RentalStructure.multiRental) {
      final numbers = _unitNumbers;
      if (numbers.isEmpty) {
        error = 'Enter at least one unit name or number.';
      } else if (numbers.toSet().length != numbers.length) {
        error = 'Each unit name or number must be unique.';
      } else if (numbers.any(
        (number) => number.length > _unitNumberMaxLength,
      )) {
        error =
            'Unit names or numbers cannot exceed $_unitNumberMaxLength characters.';
      }
    }
    setState(() => _rentalsError = error);
    return error == null;
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
      if (!_isEditing) 'rentalStructure': _rentalStructure.wireValue,
      'status': _selectedStatus,
      'addressLine1': _addressCtrl.text.trim(),
      'city': _cityCtrl.text.trim(),
      'state': _stateCtrl.text.trim(),
      'postalCode': _zipCtrl.text.trim(),
    };

    if (!_isEditing || _selectedOwnerEntityId != _initialOwnerEntityId) {
      data['ownerships'] = _selectedOwnerEntityId == null
          ? <Map<String, dynamic>>[]
          : [
              {
                'ownerEntityId': _selectedOwnerEntityId,
                'ownershipSharePercent': 100,
              },
            ];
      data['clearOwnership'] = _selectedOwnerEntityId == null;
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

  List<Map<String, dynamic>> _initialUnitsPayload() {
    if (_rentalStructure == RentalStructure.singleRental) {
      return [
        {
          'unitNumber': '1',
          'bedrooms': int.tryParse(_bedroomsCtrl.text.trim()) ?? 0,
          'bathrooms': double.tryParse(_bathroomsCtrl.text.trim()) ?? 0,
          'marketRent': double.tryParse(_marketRentCtrl.text.trim()) ?? 0,
        },
      ];
    }

    return [
      for (final number in _unitNumbers)
        {'unitNumber': number, 'bedrooms': 0, 'bathrooms': 0, 'marketRent': 0},
    ];
  }

  Future<PropertySetupResult> _createProperty(
    PropertiesRepository repo,
    Map<String, dynamic> payload,
  ) async {
    return repo.setupProperty(property: payload, units: _initialUnitsPayload());
  }

  Future<void> _submit() async {
    setState(() {
      _saving = true;
      _error = null;
    });

    try {
      final repo = ref.read(propertiesRepositoryProvider);
      late final Property saved;
      if (_isEditing) {
        saved = await repo.updateProperty(widget.property!.id, _payload());
      } else {
        final setup = await _createProperty(repo, _payload());
        saved = setup.property;
        widget.onSetupSaved?.call(setup);
      }

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

  bool _validateCreateSheet() {
    final addressValid = _validateAddressStep();
    final rentalsValid = _validateRentalsStep();
    return addressValid && rentalsValid;
  }

  static const _gap = SizedBox(height: 12);

  Widget _nameField() {
    return TextFormField(
      key: const Key('property-name-field'),
      controller: _nameCtrl,
      textInputAction: TextInputAction.next,
      decoration: const InputDecoration(labelText: 'Property name'),
      validator: (v) => _required('Name', v),
    );
  }

  Widget _typeField() {
    return DropdownButtonFormField<String>(
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
    );
  }

  Widget _statusField() {
    return DropdownButtonFormField<String>(
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
    );
  }

  Widget _ownerRow() {
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Expanded(
          child: DropdownButtonFormField<int?>(
            key: const Key('property-owner-field'),
            initialValue: _selectedOwnerEntityId,
            isExpanded: true,
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
                  child: Text(
                    owner.name,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
            ],
            onChanged: _ownersLoading
                ? null
                : (value) => setState(() => _selectedOwnerEntityId = value),
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
    );
  }

  Widget _addressField() {
    return Column(
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
      ],
    );
  }

  Widget _address2Field() {
    return TextFormField(
      key: const Key('property-address2-field'),
      controller: _address2Ctrl,
      textInputAction: TextInputAction.next,
      decoration: const InputDecoration(labelText: 'Apt / Suite / Unit #'),
    );
  }

  Widget _cityField() {
    return TextFormField(
      key: const Key('property-city-field'),
      controller: _cityCtrl,
      textInputAction: TextInputAction.next,
      decoration: const InputDecoration(labelText: 'City'),
      validator: (v) => _required('City', v),
    );
  }

  Widget _stateZipRow() {
    return Row(
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
    );
  }

  Widget _monthlyRentField() {
    return TextFormField(
      key: const Key('property-rental-market-rent-field'),
      controller: _marketRentCtrl,
      keyboardType: const TextInputType.numberWithOptions(decimal: true),
      decoration: const InputDecoration(
        labelText: 'Monthly rent',
        prefixText: r'$ ',
      ),
      validator: (value) => _optionalNonNegative('Monthly rent', value),
    );
  }

  Widget _bedsBathsRow() {
    return Row(
      children: [
        Expanded(
          child: TextFormField(
            key: const Key('property-rental-bedrooms-field'),
            controller: _bedroomsCtrl,
            keyboardType: TextInputType.number,
            decoration: const InputDecoration(labelText: 'Bedrooms'),
            validator: (value) =>
                _optionalIntRange('Bedrooms', value, min: 0, max: 100),
          ),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: TextFormField(
            key: const Key('property-rental-bathrooms-field'),
            controller: _bathroomsCtrl,
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            decoration: const InputDecoration(labelText: 'Bathrooms'),
            validator: (value) =>
                _optionalNumberRange('Bathrooms', value, min: 0, max: 100),
          ),
        ),
      ],
    );
  }

  Widget _unitNumbersField() {
    return TextFormField(
      key: const Key('property-unit-numbers-field'),
      controller: _unitNumbersCtrl,
      minLines: 4,
      maxLines: 8,
      decoration: InputDecoration(
        labelText: 'Unit names or numbers',
        hintText: '1A\n1B\n2A\n2B',
        helperText: 'One per line. You can add the remaining units later.',
        errorText: _rentalsError,
        alignLabelWithHint: true,
      ),
    );
  }

  Widget _multiRentalQuestion() {
    return SwitchListTile(
      key: const Key('property-multi-rental-field'),
      contentPadding: EdgeInsets.zero,
      value: _rentalStructure == RentalStructure.multiRental,
      title: Text(
        'Does this address have more than one rental?',
        style: Theme.of(
          context,
        ).textTheme.titleSmall?.copyWith(fontWeight: FontWeight.w600),
      ),
      onChanged: (isMulti) => setState(() {
        _rentalStructure = isMulti
            ? RentalStructure.multiRental
            : RentalStructure.singleRental;
        _rentalsError = null;
      }),
    );
  }

  Widget _yearBuiltField() {
    return TextFormField(
      key: const Key('property-year-built-field'),
      controller: _yearBuiltCtrl,
      textInputAction: TextInputAction.next,
      keyboardType: TextInputType.number,
      decoration: const InputDecoration(labelText: 'Year built'),
      validator: (v) =>
          _optionalIntRange('Year built', v, min: 1800, max: 2200),
    );
  }

  Widget _managementFeeRow() {
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Expanded(
          child: TextFormField(
            key: const Key('property-management-fee-field'),
            controller: _managementFeeCtrl,
            textInputAction: TextInputAction.next,
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            decoration: const InputDecoration(labelText: 'Management fee %'),
            validator: (v) =>
                _optionalNumberRange('Management fee', v, min: 0, max: 100),
          ),
        ),
        const SizedBox(width: 4),
        const Padding(
          padding: EdgeInsets.only(top: 4),
          child: _ManagementFeeHelpButton(),
        ),
      ],
    );
  }

  Widget _notesField() {
    return TextFormField(
      key: const Key('property-notes-field'),
      controller: _notesCtrl,
      minLines: 3,
      maxLines: 5,
      decoration: const InputDecoration(labelText: 'Notes'),
    );
  }

  List<Widget> _taxBasisFields() {
    return [
      TextFormField(
        key: const Key('property-purchase-price-field'),
        controller: _purchasePriceCtrl,
        textInputAction: TextInputAction.next,
        keyboardType: const TextInputType.numberWithOptions(decimal: true),
        decoration: const InputDecoration(labelText: 'Purchase price'),
        validator: (v) => _optionalNonNegative('Purchase price', v),
      ),
      _gap,
      TextFormField(
        key: const Key('property-land-value-field'),
        controller: _landValueCtrl,
        textInputAction: TextInputAction.next,
        keyboardType: const TextInputType.numberWithOptions(decimal: true),
        decoration: const InputDecoration(labelText: 'Land value'),
        validator: _landValueValidator,
      ),
      _gap,
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
      _gap,
      TextFormField(
        key: const Key('property-manual-depreciation-field'),
        controller: _manualDepreciationCtrl,
        keyboardType: const TextInputType.numberWithOptions(decimal: true),
        decoration: const InputDecoration(
          labelText: 'Manual annual depreciation',
        ),
        validator: (v) => _optionalNonNegative('Manual annual depreciation', v),
      ),
    ];
  }

  Widget _buildCreateSheet() {
    return TabbedFormSheet(
      title: 'Add a rental',
      saveLabel: 'Add rental',
      saving: _saving,
      error: _error,
      onSave: _submit,
      tabs: [
        TabbedFormStepSpec(
          label: 'Rental',
          validate: _validateCreateSheet,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              _nameField(),
              _gap,
              _addressField(),
              _gap,
              _cityField(),
              _gap,
              _stateZipRow(),
              _gap,
              if (_rentalStructure == RentalStructure.singleRental) ...[
                _monthlyRentField(),
                _gap,
                _bedsBathsRow(),
              ] else
                _unitNumbersField(),
              const SizedBox(height: 4),
              _multiRentalQuestion(),
              const SizedBox(height: 4),
              MoreDetailsSection(
                children: [
                  _address2Field(),
                  _gap,
                  _typeField(),
                  _gap,
                  _statusField(),
                  _gap,
                  _ownerRow(),
                  _gap,
                  _yearBuiltField(),
                  _gap,
                  _managementFeeRow(),
                  _gap,
                  _notesField(),
                  _gap,
                  ..._taxBasisFields(),
                ],
              ),
            ],
          ),
        ),
      ],
    );
  }

  Widget _buildEditSheet() {
    return TabbedFormSheet(
      title: 'Edit Property',
      saveLabel: 'Save Property',
      saving: _saving,
      error: _error,
      onSave: _submit,
      tabs: [
        TabbedFormStepSpec(
          label: 'Identity',
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              _nameField(),
              _gap,
              _typeField(),
              _gap,
              Text(
                'How is this address rented?',
                style: Theme.of(
                  context,
                ).textTheme.titleSmall?.copyWith(fontWeight: FontWeight.w700),
              ),
              const SizedBox(height: 4),
              Text(
                'Rental structure is set when the property is created.',
                style: Theme.of(context).textTheme.bodySmall?.copyWith(
                  color: Theme.of(context).colorScheme.onSurfaceVariant,
                ),
              ),
              const SizedBox(height: 8),
              SegmentedButton<RentalStructure>(
                key: const Key('property-rental-structure-field'),
                segments: const [
                  ButtonSegment(
                    value: RentalStructure.singleRental,
                    icon: Icon(Icons.home_outlined),
                    label: Text('One rental'),
                  ),
                  ButtonSegment(
                    value: RentalStructure.multiRental,
                    icon: Icon(Icons.apartment_outlined),
                    label: Text('Building with units'),
                  ),
                ],
                selected: {_rentalStructure},
                onSelectionChanged: null,
              ),
              _gap,
              _statusField(),
              _gap,
              _ownerRow(),
            ],
          ),
        ),
        TabbedFormStepSpec(
          label: 'Address',
          validate: _validateAddressStep,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              _addressField(),
              _gap,
              _address2Field(),
              _gap,
              _cityField(),
              _gap,
              _stateZipRow(),
            ],
          ),
        ),
        TabbedFormStepSpec(
          label: 'Operations',
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              _yearBuiltField(),
              _gap,
              _managementFeeRow(),
              _gap,
              _notesField(),
            ],
          ),
        ),
        TabbedFormStepSpec(
          label: 'Tax Basis',
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: _taxBasisFields(),
          ),
        ),
      ],
    );
  }

  @override
  Widget build(BuildContext context) {
    return _isEditing ? _buildEditSheet() : _buildCreateSheet();
  }
}

const _managementFeeHelpUri = 'https://rentalcommand.net/docs/properties';

class _ManagementFeeHelpButton extends StatelessWidget {
  const _ManagementFeeHelpButton();

  @override
  Widget build(BuildContext context) {
    return IconButton(
      key: const Key('property-management-fee-help'),
      tooltip: 'What is a management fee?',
      visualDensity: VisualDensity.compact,
      icon: const Icon(Icons.help_outline, size: 20),
      onPressed: () => _showTip(context),
    );
  }

  Future<void> _showTip(BuildContext context) async {
    await showModalBottomSheet<void>(
      context: context,
      showDragHandle: true,
      useSafeArea: true,
      builder: (sheetContext) => Padding(
        padding: const EdgeInsets.fromLTRB(20, 4, 20, 24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              'Management fee',
              style: Theme.of(
                sheetContext,
              ).textTheme.titleLarge?.copyWith(fontWeight: FontWeight.w700),
            ),
            const SizedBox(height: 10),
            const Text(
              'Only used if a management company takes a cut of the rent. '
              'Leave it blank when you manage this rental yourself.',
            ),
            const SizedBox(height: 16),
            Align(
              alignment: Alignment.centerLeft,
              child: FilledButton.tonalIcon(
                key: const Key('property-management-fee-learn-more'),
                icon: const Icon(Icons.open_in_new, size: 18),
                label: const Text('Learn more'),
                onPressed: () => _openGuide(sheetContext),
              ),
            ),
          ],
        ),
      ),
    );
  }

  Future<void> _openGuide(BuildContext context) async {
    var opened = false;
    try {
      opened = await launchUrl(
        Uri.parse(_managementFeeHelpUri),
        mode: LaunchMode.externalApplication,
      );
    } catch (_) {
      opened = false;
    }
    if (opened || !context.mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(
      const SnackBar(content: Text('Could not open the properties guide.')),
    );
  }
}
