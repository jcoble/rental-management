import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/widgets/tabbed_form_sheet.dart';
import '../home/mobile_domain_chrome.dart';
import '../home/mobile_quick_action_fab.dart';
import '../home/mobile_quick_action_helpers.dart';
import 'dispatch_vendor_sheet.dart' show VendorRatingSummary;
import 'vendor_detail_screen.dart';
import 'vendors_models.dart';
import 'vendors_repository.dart';

/// Landlord-facing list of vendors with their service type and rating summary.
class VendorsListScreen extends ConsumerWidget {
  const VendorsListScreen({super.key});

  void _showVendorForm(BuildContext context, WidgetRef ref, {Vendor? vendor}) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => VendorFormSheet(
        existing: vendor,
        onSaved: () => ref.invalidate(vendorsProvider),
      ),
    );
  }

  Future<void> _confirmDelete(
    BuildContext context,
    WidgetRef ref,
    Vendor vendor,
  ) async {
    final messenger = ScaffoldMessenger.of(context);
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (_) => AlertDialog(
        title: const Text('Delete vendor?'),
        content: Text('Delete ${vendor.name}? This cannot be undone.'),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(context).pop(false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(context).pop(true),
            child: const Text('Delete'),
          ),
        ],
      ),
    );

    if (confirmed != true || !context.mounted) return;

    try {
      await ref.read(vendorsRepositoryProvider).deleteVendor(vendor.id);
      ref.invalidate(vendorsProvider);
      if (!context.mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(const SnackBar(content: Text('Vendor deleted.')));
    } on ApiException catch (e) {
      if (!context.mounted) return;
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final vendorsAsync = ref.watch(vendorsProvider);

    return Scaffold(
      appBar: mobileDomainRootAppBar(context, title: const Text('Vendors')),
      floatingActionButton: MobileQuickActionFab(
        heroTag: 'vendors-fab',
        primaryAction: MobileQuickAction(
          label: 'Add vendor',
          icon: Icons.add,
          onPressed: () => _showVendorForm(context, ref),
        ),
        onChat: () => openMobileAssistant(context),
        onRecord: () => openMobileRecord(context),
        onScan: () => openMobileScan(context),
      ),
      body: RefreshIndicator(
        onRefresh: () async => ref.invalidate(vendorsProvider),
        child: vendorsAsync.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (e, _) => _ErrorBody(
            message: e is ApiException ? e.message : e.toString(),
            onRetry: () => ref.invalidate(vendorsProvider),
          ),
          data: (vendors) {
            if (vendors.isEmpty) {
              return _EmptyBody(onAdd: () => _showVendorForm(context, ref));
            }
            final sorted = List<Vendor>.from(vendors)
              ..sort((a, b) {
                // Preferred first, then by name.
                if (a.preferred != b.preferred) {
                  return a.preferred ? -1 : 1;
                }
                return a.name.toLowerCase().compareTo(b.name.toLowerCase());
              });
            return ListView.separated(
              physics: const AlwaysScrollableScrollPhysics(),
              padding: const EdgeInsets.fromLTRB(16, 12, 16, 32),
              itemCount: sorted.length,
              separatorBuilder: (_, _) => const SizedBox(height: 8),
              itemBuilder: (_, i) => _VendorCard(
                vendor: sorted[i],
                onEdit: () => _showVendorForm(context, ref, vendor: sorted[i]),
                onDelete: () => _confirmDelete(context, ref, sorted[i]),
                onTap: () => Navigator.of(context).push<void>(
                  MaterialPageRoute<void>(
                    builder: (_) => VendorDetailScreen(vendor: sorted[i]),
                  ),
                ),
              ),
            );
          },
        ),
      ),
    );
  }
}

class _VendorCard extends StatelessWidget {
  const _VendorCard({
    required this.vendor,
    required this.onTap,
    required this.onEdit,
    required this.onDelete,
  });

  final Vendor vendor;
  final VoidCallback onTap;
  final VoidCallback onEdit;
  final VoidCallback onDelete;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    return Card(
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.all(14),
          child: Row(
            children: [
              Container(
                padding: const EdgeInsets.all(10),
                decoration: BoxDecoration(
                  color: cs.tertiary.withValues(alpha: 0.12),
                  borderRadius: BorderRadius.circular(10),
                ),
                child: Icon(
                  Icons.handyman_outlined,
                  color: cs.tertiary,
                  size: 22,
                ),
              ),
              const SizedBox(width: 14),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        Flexible(
                          child: Text(
                            vendor.name,
                            style: theme.textTheme.titleSmall?.copyWith(
                              fontWeight: FontWeight.w700,
                            ),
                          ),
                        ),
                        if (vendor.preferred) ...[
                          const SizedBox(width: 6),
                          Icon(
                            Icons.star_rounded,
                            size: 16,
                            color: Colors.amber.shade600,
                          ),
                        ],
                      ],
                    ),
                    const SizedBox(height: 2),
                    Text(
                      vendor.serviceType,
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: cs.onSurfaceVariant,
                      ),
                    ),
                    const SizedBox(height: 6),
                    Row(
                      children: [
                        VendorRatingSummary(vendor: vendor),
                        const SizedBox(width: 10),
                        Text(
                          '${vendor.jobsCompleted} '
                          'job${vendor.jobsCompleted == 1 ? '' : 's'}',
                          style: theme.textTheme.bodySmall?.copyWith(
                            color: cs.onSurfaceVariant,
                          ),
                        ),
                      ],
                    ),
                  ],
                ),
              ),
              const SizedBox(width: 8),
              Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  IconButton(
                    icon: const Icon(Icons.edit_outlined),
                    tooltip: 'Edit vendor',
                    onPressed: onEdit,
                  ),
                  IconButton(
                    icon: const Icon(Icons.delete_outline),
                    tooltip: 'Delete vendor',
                    onPressed: onDelete,
                  ),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _EmptyBody extends StatelessWidget {
  const _EmptyBody({required this.onAdd});

  final VoidCallback onAdd;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(24, 80, 24, 24),
          child: Column(
            children: [
              Icon(
                Icons.handyman_outlined,
                size: 40,
                color: cs.onSurfaceVariant,
              ),
              const SizedBox(height: 12),
              Text(
                'No vendors yet.',
                textAlign: TextAlign.center,
                style: TextStyle(color: cs.onSurfaceVariant),
              ),
              const SizedBox(height: 6),
              Text(
                'Add service providers here, then assign or text them from '
                'work orders.',
                textAlign: TextAlign.center,
                style: Theme.of(
                  context,
                ).textTheme.bodySmall?.copyWith(color: cs.onSurfaceVariant),
              ),
              const SizedBox(height: 18),
              FilledButton.icon(
                onPressed: onAdd,
                icon: const Icon(Icons.add),
                label: const Text('Add your first vendor'),
              ),
            ],
          ),
        ),
      ],
    );
  }
}

class VendorFormSheet extends ConsumerStatefulWidget {
  const VendorFormSheet({super.key, required this.onSaved, this.existing});

  final VoidCallback onSaved;
  final Vendor? existing;

  @override
  ConsumerState<VendorFormSheet> createState() => _VendorFormSheetState();
}

class _VendorFormSheetState extends ConsumerState<VendorFormSheet> {
  final _formKey = GlobalKey<FormState>();

  late final TextEditingController _nameCtrl;
  late final TextEditingController _serviceCtrl;
  late final TextEditingController _emailCtrl;
  late final TextEditingController _phoneCtrl;
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
    _serviceCtrl = TextEditingController(text: v?.serviceType ?? 'Plumbing');
    _emailCtrl = TextEditingController(text: v?.email ?? '');
    _phoneCtrl = TextEditingController(text: v?.phone ?? '');
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
      if (_isEdit) {
        await repo.updateVendor(widget.existing!.id, body);
      } else {
        await repo.createVendor(body);
      }
      widget.onSaved();
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

class _ErrorBody extends StatelessWidget {
  const _ErrorBody({required this.message, required this.onRetry});

  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(24, 80, 24, 24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(Icons.error_outline, size: 40, color: cs.error),
              const SizedBox(height: 12),
              Text(
                message,
                textAlign: TextAlign.center,
                style: TextStyle(color: cs.error),
              ),
              const SizedBox(height: 16),
              FilledButton.tonal(
                onPressed: onRetry,
                child: const Text('Retry'),
              ),
            ],
          ),
        ),
      ],
    );
  }
}
