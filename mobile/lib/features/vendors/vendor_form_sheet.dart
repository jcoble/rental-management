import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/widgets/tabbed_form_sheet.dart';
import 'vendors_models.dart';
import 'vendors_repository.dart';

class VendorFormSheet extends ConsumerStatefulWidget {
  const VendorFormSheet({
    super.key,
    required this.onSaved,
    this.existing,
    this.onVendorSaved,
  });

  final VoidCallback onSaved;
  final Vendor? existing;
  final ValueChanged<Vendor>? onVendorSaved;

  @override
  ConsumerState<VendorFormSheet> createState() => _VendorFormSheetState();
}

class _VendorFormSheetState extends ConsumerState<VendorFormSheet> {
  final _formKey = GlobalKey<FormState>();

  late final TextEditingController _nameCtrl;
  late final TextEditingController _serviceCtrl;
  late final TextEditingController _emailCtrl;
  late final TextEditingController _phoneCtrl;
  late final TextEditingController _websiteCtrl;
  late final TextEditingController _addressCtrl;
  late final TextEditingController _cityCtrl;
  late final TextEditingController _stateCtrl;
  late final TextEditingController _postalCtrl;
  late final TextEditingController _taxIdCtrl;
  late final TextEditingController _notesCtrl;

  late bool _is1099Eligible;
  late bool _w9OnFile;
  late bool _preferred;
  bool _saving = false;
  String? _error;

  bool get _isEdit => widget.existing != null;

  @override
  void initState() {
    super.initState();
    final v = widget.existing;
    _nameCtrl = TextEditingController(text: v?.name ?? '');
    _serviceCtrl = TextEditingController(text: v?.serviceType ?? '');
    _emailCtrl = TextEditingController(text: v?.email ?? '');
    _phoneCtrl = TextEditingController(text: v?.phone ?? '');
    _websiteCtrl = TextEditingController(text: v?.website ?? '');
    _addressCtrl = TextEditingController(text: v?.addressLine1 ?? '');
    _cityCtrl = TextEditingController(text: v?.city ?? '');
    _stateCtrl = TextEditingController(text: v?.state ?? '');
    _postalCtrl = TextEditingController(text: v?.postalCode ?? '');
    _taxIdCtrl = TextEditingController(text: v?.taxId ?? '');
    _notesCtrl = TextEditingController(text: v?.notes ?? '');
    _is1099Eligible = v?.is1099Eligible ?? true;
    _w9OnFile = v?.w9OnFile ?? false;
    _preferred = v?.preferred ?? false;
  }

  @override
  void dispose() {
    _nameCtrl.dispose();
    _serviceCtrl.dispose();
    _emailCtrl.dispose();
    _phoneCtrl.dispose();
    _websiteCtrl.dispose();
    _addressCtrl.dispose();
    _cityCtrl.dispose();
    _stateCtrl.dispose();
    _postalCtrl.dispose();
    _taxIdCtrl.dispose();
    _notesCtrl.dispose();
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
      'name': _text(_nameCtrl),
      'serviceType': _text(_serviceCtrl),
      'email': _text(_emailCtrl),
      'phone': _text(_phoneCtrl),
      'website': _text(_websiteCtrl),
      'taxId': _text(_taxIdCtrl),
      'addressLine1': _text(_addressCtrl),
      'city': _text(_cityCtrl),
      'state': _text(_stateCtrl),
      'postalCode': _text(_postalCtrl),
      'is1099Eligible': _is1099Eligible,
      'w9OnFile': _w9OnFile,
      'preferred': _preferred,
      'notes': _text(_notesCtrl),
    };

    try {
      final repo = ref.read(vendorsRepositoryProvider);
      final saved = _isEdit
          ? await repo.updateVendor(widget.existing!.id, body)
          : await repo.createVendor(body);
      widget.onSaved();
      widget.onVendorSaved?.call(saved);
      if (!mounted) return;
      Navigator.of(context).pop();
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(
          SnackBar(
            content: Text(_isEdit ? 'Vendor updated.' : 'Vendor created.'),
          ),
        );
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
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
        title: _isEdit ? 'Edit Vendor' : 'New Vendor',
        saveLabel: _isEdit ? 'Save Vendor' : 'Add Vendor',
        saving: _saving,
        error: _error,
        onSave: _submit,
        tabs: [
          TabbedFormStepSpec(
            label: 'Contact',
            isComplete: () =>
                _text(_nameCtrl).isNotEmpty && _text(_serviceCtrl).isNotEmpty,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                TextFormField(
                  key: const Key('vendor-name-field'),
                  controller: _nameCtrl,
                  textInputAction: TextInputAction.next,
                  textCapitalization: TextCapitalization.words,
                  decoration: const InputDecoration(labelText: 'Vendor name'),
                  validator: (v) => (v == null || v.trim().isEmpty)
                      ? 'Vendor name is required'
                      : null,
                ),
                gap,
                TextFormField(
                  key: const Key('vendor-service-field'),
                  controller: _serviceCtrl,
                  textInputAction: TextInputAction.next,
                  textCapitalization: TextCapitalization.words,
                  decoration: const InputDecoration(labelText: 'Service type'),
                  validator: (v) => (v == null || v.trim().isEmpty)
                      ? 'Service type is required'
                      : null,
                ),
                gap,
                TextFormField(
                  key: const Key('vendor-email-field'),
                  controller: _emailCtrl,
                  textInputAction: TextInputAction.next,
                  keyboardType: TextInputType.emailAddress,
                  decoration: const InputDecoration(
                    labelText: 'Email (optional)',
                  ),
                  validator: (v) {
                    final value = v?.trim() ?? '';
                    if (value.isEmpty || value.contains('@')) return null;
                    return 'Enter a valid email';
                  },
                ),
                gap,
                TextFormField(
                  key: const Key('vendor-phone-field'),
                  controller: _phoneCtrl,
                  textInputAction: TextInputAction.next,
                  keyboardType: TextInputType.phone,
                  decoration: const InputDecoration(
                    labelText: 'Phone (optional)',
                  ),
                ),
                gap,
                TextFormField(
                  key: const Key('vendor-website-field'),
                  controller: _websiteCtrl,
                  textInputAction: TextInputAction.next,
                  keyboardType: TextInputType.url,
                  decoration: const InputDecoration(
                    labelText: 'Website (optional)',
                    hintText: 'https://example.com',
                  ),
                  validator: (v) {
                    final value = v?.trim() ?? '';
                    if (value.isEmpty) return null;
                    final uri = Uri.tryParse(value);
                    if (uri != null &&
                        (uri.scheme == 'http' || uri.scheme == 'https') &&
                        uri.host.isNotEmpty) {
                      return null;
                    }
                    return 'Enter a full website URL';
                  },
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
                  key: const Key('vendor-address-field'),
                  controller: _addressCtrl,
                  textInputAction: TextInputAction.next,
                  textCapitalization: TextCapitalization.words,
                  decoration: const InputDecoration(
                    labelText: 'Address (optional)',
                  ),
                ),
                gap,
                TextFormField(
                  key: const Key('vendor-city-field'),
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
                        key: const Key('vendor-state-field'),
                        controller: _stateCtrl,
                        textInputAction: TextInputAction.next,
                        textCapitalization: TextCapitalization.characters,
                        decoration: const InputDecoration(labelText: 'State'),
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: TextFormField(
                        key: const Key('vendor-postal-field'),
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
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                TextFormField(
                  key: const Key('vendor-tax-id-field'),
                  controller: _taxIdCtrl,
                  textInputAction: TextInputAction.next,
                  decoration: const InputDecoration(
                    labelText: 'Tax ID (optional)',
                  ),
                ),
                gap,
                SwitchListTile.adaptive(
                  contentPadding: EdgeInsets.zero,
                  title: const Text('1099 eligible'),
                  value: _is1099Eligible,
                  onChanged: (value) => setState(() => _is1099Eligible = value),
                ),
                SwitchListTile.adaptive(
                  contentPadding: EdgeInsets.zero,
                  title: const Text('W-9 on file'),
                  value: _w9OnFile,
                  onChanged: (value) => setState(() => _w9OnFile = value),
                ),
                SwitchListTile.adaptive(
                  contentPadding: EdgeInsets.zero,
                  title: const Text('Preferred vendor'),
                  value: _preferred,
                  onChanged: (value) => setState(() => _preferred = value),
                ),
                gap,
                TextFormField(
                  key: const Key('vendor-notes-field'),
                  controller: _notesCtrl,
                  maxLines: 3,
                  textInputAction: TextInputAction.newline,
                  decoration: const InputDecoration(
                    labelText: 'Notes (optional)',
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}
