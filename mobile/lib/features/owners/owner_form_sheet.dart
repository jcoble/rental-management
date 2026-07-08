import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/widgets/tabbed_form_sheet.dart';
import 'owners_models.dart';
import 'owners_repository.dart';

class OwnerFormSheet extends ConsumerStatefulWidget {
  const OwnerFormSheet({
    super.key,
    required this.onSaved,
    this.onSavedOwner,
    this.existing,
  });

  final VoidCallback onSaved;
  final ValueChanged<OwnerEntity>? onSavedOwner;
  final OwnerEntity? existing;

  @override
  ConsumerState<OwnerFormSheet> createState() => _OwnerFormSheetState();
}

class _OwnerFormSheetState extends ConsumerState<OwnerFormSheet> {
  final _formKey = GlobalKey<FormState>();

  late final TextEditingController _nameCtrl;
  late final TextEditingController _emailCtrl;
  late final TextEditingController _phoneCtrl;
  late final TextEditingController _addressLine1Ctrl;
  late final TextEditingController _addressLine2Ctrl;
  late final TextEditingController _cityCtrl;
  late final TextEditingController _stateCtrl;
  late final TextEditingController _postalCtrl;
  late final TextEditingController _taxIdCtrl;
  late OwnerEntityType _type;
  bool _saving = false;
  String? _error;

  bool get _isEdit => widget.existing != null;

  @override
  void initState() {
    super.initState();
    final owner = widget.existing;
    _type = owner?.ownerEntityType ?? OwnerEntityType.person;
    _nameCtrl = TextEditingController(text: owner?.name ?? '');
    _emailCtrl = TextEditingController(text: owner?.email ?? '');
    _phoneCtrl = TextEditingController(text: owner?.phone ?? '');
    _addressLine1Ctrl = TextEditingController(text: owner?.addressLine1 ?? '');
    _addressLine2Ctrl = TextEditingController(text: owner?.addressLine2 ?? '');
    _cityCtrl = TextEditingController(text: owner?.city ?? '');
    _stateCtrl = TextEditingController(text: owner?.state ?? '');
    _postalCtrl = TextEditingController(text: owner?.postalCode ?? '');
    _taxIdCtrl = TextEditingController(text: owner?.taxId ?? '');
  }

  @override
  void dispose() {
    _nameCtrl.dispose();
    _emailCtrl.dispose();
    _phoneCtrl.dispose();
    _addressLine1Ctrl.dispose();
    _addressLine2Ctrl.dispose();
    _cityCtrl.dispose();
    _stateCtrl.dispose();
    _postalCtrl.dispose();
    _taxIdCtrl.dispose();
    super.dispose();
  }

  String _text(TextEditingController controller) => controller.text.trim();

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;

    setState(() {
      _saving = true;
      _error = null;
    });

    final body = <String, dynamic>{
      'ownerEntityType': _type.wireName,
      'name': _text(_nameCtrl),
      'email': _text(_emailCtrl),
      'phone': _text(_phoneCtrl),
      'taxId': _text(_taxIdCtrl),
      'addressLine1': _text(_addressLine1Ctrl),
      'addressLine2': _text(_addressLine2Ctrl),
      'city': _text(_cityCtrl),
      'state': _text(_stateCtrl),
      'postalCode': _text(_postalCtrl),
    };

    try {
      final repo = ref.read(ownersRepositoryProvider);
      final OwnerEntity saved;
      if (_isEdit) {
        saved = await repo.updateOwner(widget.existing!.id, body);
      } else {
        saved = await repo.createOwner(body);
      }
      widget.onSaved();
      widget.onSavedOwner?.call(saved);
      if (!mounted) return;
      Navigator.of(context).pop(saved);
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(
          SnackBar(
            content: Text(_isEdit ? 'Owner updated.' : 'Owner created.'),
          ),
        );
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
      rethrow;
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    const gap = SizedBox(height: 12);

    return Form(
      key: _formKey,
      child: TabbedFormSheet(
        title: _isEdit ? 'Edit Owner' : 'New Owner',
        saveLabel: _isEdit ? 'Save Owner' : 'Add Owner',
        saving: _saving,
        error: _error,
        onSave: _submit,
        tabs: [
          TabbedFormStepSpec(
            label: 'Contact',
            isComplete: () => _text(_nameCtrl).isNotEmpty,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                DropdownButtonFormField<OwnerEntityType>(
                  key: const Key('owner-type-field'),
                  initialValue: _type,
                  decoration: const InputDecoration(labelText: 'Owner type'),
                  items: [
                    for (final type in OwnerEntityType.values)
                      DropdownMenuItem(value: type, child: Text(type.label)),
                  ],
                  onChanged: (value) {
                    if (value != null) setState(() => _type = value);
                  },
                ),
                gap,
                TextFormField(
                  key: const Key('owner-name-field'),
                  controller: _nameCtrl,
                  textInputAction: TextInputAction.next,
                  textCapitalization: TextCapitalization.words,
                  decoration: const InputDecoration(labelText: 'Owner name'),
                  validator: (value) => (value == null || value.trim().isEmpty)
                      ? 'Owner name is required'
                      : null,
                ),
                gap,
                TextFormField(
                  key: const Key('owner-email-field'),
                  controller: _emailCtrl,
                  textInputAction: TextInputAction.next,
                  keyboardType: TextInputType.emailAddress,
                  decoration: const InputDecoration(
                    labelText: 'Email (optional)',
                  ),
                  validator: (value) {
                    final email = value?.trim() ?? '';
                    if (email.isEmpty || email.contains('@')) return null;
                    return 'Enter a valid email';
                  },
                ),
                gap,
                TextFormField(
                  key: const Key('owner-phone-field'),
                  controller: _phoneCtrl,
                  textInputAction: TextInputAction.next,
                  keyboardType: TextInputType.phone,
                  decoration: const InputDecoration(
                    labelText: 'Phone (optional)',
                  ),
                ),
              ],
            ),
          ),
          TabbedFormStepSpec(
            label: 'Address',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                TextFormField(
                  key: const Key('owner-address-line-1-field'),
                  controller: _addressLine1Ctrl,
                  textInputAction: TextInputAction.next,
                  textCapitalization: TextCapitalization.words,
                  decoration: const InputDecoration(
                    labelText: 'Address line 1',
                  ),
                ),
                gap,
                TextFormField(
                  key: const Key('owner-address-line-2-field'),
                  controller: _addressLine2Ctrl,
                  textInputAction: TextInputAction.next,
                  textCapitalization: TextCapitalization.words,
                  decoration: const InputDecoration(
                    labelText: 'Address line 2',
                  ),
                ),
                gap,
                TextFormField(
                  key: const Key('owner-city-field'),
                  controller: _cityCtrl,
                  textInputAction: TextInputAction.next,
                  textCapitalization: TextCapitalization.words,
                  decoration: const InputDecoration(labelText: 'City'),
                ),
                gap,
                Row(
                  children: [
                    Expanded(
                      child: TextFormField(
                        key: const Key('owner-state-field'),
                        controller: _stateCtrl,
                        textInputAction: TextInputAction.next,
                        textCapitalization: TextCapitalization.characters,
                        decoration: const InputDecoration(labelText: 'State'),
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: TextFormField(
                        key: const Key('owner-postal-field'),
                        controller: _postalCtrl,
                        textInputAction: TextInputAction.next,
                        keyboardType: TextInputType.number,
                        decoration: const InputDecoration(labelText: 'ZIP'),
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
          TabbedFormStepSpec(
            label: 'Tax',
            child: TextFormField(
              key: const Key('owner-tax-id-field'),
              controller: _taxIdCtrl,
              textInputAction: TextInputAction.done,
              decoration: const InputDecoration(labelText: 'Tax ID (optional)'),
            ),
          ),
        ],
      ),
    );
  }
}
