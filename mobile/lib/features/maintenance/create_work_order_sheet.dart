import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:image_picker/image_picker.dart';
import 'package:uuid/uuid.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/models.dart' hide Vendor;
import '../../core/utils/date_wire.dart';
import '../properties/properties_repository.dart';
import '../tenants/tenants_repository.dart';
import '../vendors/vendors_repository.dart';
import 'work_order_form_shell.dart';
import 'work_orders_repository.dart';

Future<void> showCreateWorkOrderSheet({
  required BuildContext context,
  required WidgetRef ref,
  required VoidCallback onSaved,
  int? initialPropertyId,
  int? initialUnitId,
  String? initialPropertyLabel,
  String? initialUnitLabel,
}) {
  ref.read(propertiesForWoProvider.notifier).load();

  return showModalBottomSheet<void>(
    context: context,
    isScrollControlled: true,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
    ),
    builder: (_) => _CreateWorkOrderSheet(
      onSaved: onSaved,
      initialPropertyId: initialPropertyId,
      initialUnitId: initialUnitId,
      initialPropertyLabel: initialPropertyLabel,
      initialUnitLabel: initialUnitLabel,
    ),
  );
}

class _CreateWorkOrderSheet extends ConsumerStatefulWidget {
  const _CreateWorkOrderSheet({
    required this.onSaved,
    this.initialPropertyId,
    this.initialUnitId,
    this.initialPropertyLabel,
    this.initialUnitLabel,
  });

  final VoidCallback onSaved;
  final int? initialPropertyId;
  final int? initialUnitId;
  final String? initialPropertyLabel;
  final String? initialUnitLabel;

  @override
  ConsumerState<_CreateWorkOrderSheet> createState() =>
      _CreateWorkOrderSheetState();
}

class _CreateWorkOrderSheetState extends ConsumerState<_CreateWorkOrderSheet> {
  final _formKey = GlobalKey<FormState>();
  final _titleCtrl = TextEditingController();
  final _descCtrl = TextEditingController();
  final _estCostCtrl = TextEditingController();

  int? _selectedPropertyId;
  int? _selectedUnitId;
  int? _selectedTenantId;
  int? _selectedVendorId;
  String _priority = 'Normal';
  String _category = 'General';

  DateTime? _scheduledDate;
  TimeOfDay? _startTime;
  TimeOfDay? _windowEndTime;

  Uint8List? _photoBytes;
  String? _photoName;
  String? _photoContentType;
  String? _photoUploadOperationId;

  bool _saving = false;
  String? _error;

  static const _priorities = ['Low', 'Normal', 'High', 'Emergency'];
  static const _categories = [
    'General',
    'Plumbing',
    'Electrical',
    'HVAC',
    'Appliance',
    'Structural',
    'Exterior',
    'Landscaping',
    'Cleaning',
    'Safety',
    'Other',
  ];

  bool get _propertyLocked => widget.initialPropertyId != null;
  bool get _unitLocked => widget.initialUnitId != null;

  @override
  void initState() {
    super.initState();
    _selectedPropertyId = widget.initialPropertyId;
    _selectedUnitId = widget.initialUnitId;
  }

  @override
  void dispose() {
    _titleCtrl.dispose();
    _descCtrl.dispose();
    _estCostCtrl.dispose();
    super.dispose();
  }

  DateTime? _combine(DateTime? date, TimeOfDay? time) {
    if (date == null || time == null) return null;
    return DateTime(date.year, date.month, date.day, time.hour, time.minute);
  }

  DateTime? _scheduledStart(DateTime? date, TimeOfDay? time) {
    if (date == null) return null;
    return _combine(date, time) ?? DateTime(date.year, date.month, date.day);
  }

  static String _fmtDate(DateTime d) {
    const months = [
      '',
      'Jan',
      'Feb',
      'Mar',
      'Apr',
      'May',
      'Jun',
      'Jul',
      'Aug',
      'Sep',
      'Oct',
      'Nov',
      'Dec',
    ];
    return '${months[d.month]} ${d.day}, ${d.year}';
  }

  Property? _findProperty(List<Property> properties) {
    final selectedId = _selectedPropertyId;
    if (selectedId == null) return null;
    for (final property in properties) {
      if (property.id == selectedId) return property;
    }
    return null;
  }

  Future<void> _pickScheduledDate() async {
    final now = DateTime.now();
    final picked = await showDatePicker(
      context: context,
      initialDate: _scheduledDate ?? now,
      firstDate: DateTime(now.year - 1),
      lastDate: DateTime(now.year + 5),
    );
    if (picked == null || !mounted) return;
    setState(
      () => _scheduledDate = DateTime(picked.year, picked.month, picked.day),
    );
  }

  Future<void> _pickTime({required bool isStart}) async {
    final picked = await showTimePicker(
      context: context,
      initialTime: (isStart ? _startTime : _windowEndTime) ?? TimeOfDay.now(),
    );
    if (picked == null || !mounted) return;
    setState(() {
      if (isStart) {
        _startTime = picked;
      } else {
        _windowEndTime = picked;
      }
    });
  }

  Future<void> _pickPhoto(ImageSource source) async {
    final picked = await ImagePicker().pickImage(
      source: source,
      imageQuality: 80,
      maxWidth: 1600,
      maxHeight: 1600,
    );
    if (picked == null) return;
    final bytes = Uint8List.fromList(await picked.readAsBytes());
    if (!mounted) return;
    setState(() {
      _photoBytes = bytes;
      _photoName = picked.name;
      _photoContentType = _mimeFromExtension(picked.name);
      _photoUploadOperationId = const Uuid().v4();
    });
  }

  String _mimeFromExtension(String filename) {
    final lower = filename.toLowerCase();
    if (lower.endsWith('.png')) return 'image/png';
    if (lower.endsWith('.webp')) return 'image/webp';
    if (lower.endsWith('.heic')) return 'image/heic';
    return 'image/jpeg';
  }

  String _tenantLabel(Tenant tenant) {
    final fullName = tenant.fullName?.trim();
    if (fullName != null && fullName.isNotEmpty) return fullName;
    final name = '${tenant.firstName} ${tenant.lastName}'.trim();
    return name.isEmpty ? 'Tenant #${tenant.id}' : name;
  }

  TenantListQuery? get _tenantQuery {
    final propertyId = _selectedPropertyId;
    if (propertyId == null) return null;
    return TenantListQuery(
      take: 200,
      sort: 'name',
      propertyId: propertyId,
      unitId: _selectedUnitId,
    );
  }

  void _clearSelectedTenantIfMissing(List<Tenant> tenants) {
    final tenantId = _selectedTenantId;
    if (tenantId == null || tenants.any((tenant) => tenant.id == tenantId)) {
      return;
    }

    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted && _selectedTenantId == tenantId) {
        setState(() => _selectedTenantId = null);
      }
    });
  }

  bool _validateArrivalWindow() {
    final scheduledFor = _scheduledStart(_scheduledDate, _startTime);
    final scheduledWindowEnd = _combine(_scheduledDate, _windowEndTime);

    if (scheduledFor != null &&
        scheduledWindowEnd != null &&
        !scheduledWindowEnd.isAfter(scheduledFor)) {
      setState(
        () => _error = 'Arrival window end must be after the start time.',
      );
      return false;
    }
    if (_error == 'Arrival window end must be after the start time.') {
      setState(() => _error = null);
    }
    return true;
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) {
      throw StateError('Work order form validation failed.');
    }

    final scheduledFor = _scheduledStart(_scheduledDate, _startTime);
    final scheduledWindowEnd = _combine(_scheduledDate, _windowEndTime);

    if (!_validateArrivalWindow()) {
      throw StateError('Work order arrival window validation failed.');
    }

    setState(() {
      _saving = true;
      _error = null;
    });

    try {
      final repo = ref.read(workOrdersRepositoryProvider);
      final estCost = double.tryParse(_estCostCtrl.text.trim());
      final created = await repo.createWorkOrder({
        'propertyId': _selectedPropertyId,
        'title': _titleCtrl.text.trim(),
        'description': _descCtrl.text.trim(),
        'priority': _priority,
        'category': _category,
        'unitId': ?_selectedUnitId,
        'tenantId': ?_selectedTenantId,
        'vendorId': ?_selectedVendorId,
        if (scheduledFor != null) 'scheduledFor': localToWireIso(scheduledFor),
        if (scheduledWindowEnd != null)
          'scheduledWindowEnd': localToWireIso(scheduledWindowEnd),
        'estimatedCost': ?estCost,
      });
      final photoBytes = _photoBytes;
      final photoName = _photoName;
      final photoContentType = _photoContentType;
      if (photoBytes != null && photoName != null && photoContentType != null) {
        await repo.uploadWorkOrderPhoto(
          workOrderId: created.id,
          bytes: photoBytes,
          fileName: photoName,
          contentType: photoContentType,
          clientOperationId: _photoUploadOperationId ??= const Uuid().v4(),
        );
      }

      widget.onSaved();
      if (mounted) Navigator.of(context).pop();
    } on ApiException catch (e) {
      setState(() => _error = e.message);
      rethrow;
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final propertiesAsync = ref.watch(propertiesForWoProvider);
    final tenantQuery = _tenantQuery;
    final tenantsAsync = tenantQuery == null
        ? null
        : ref.watch(tenantsPageProvider(tenantQuery));
    final vendorsAsync = ref.watch(vendorsProvider);
    final unitsAsync = _selectedPropertyId == null || _unitLocked
        ? null
        : ref.watch(unitsProvider(_selectedPropertyId!));
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    const gap = SizedBox(height: 12);

    final propertyField = propertiesAsync.when(
      loading: () => const Center(
        child: Padding(
          padding: EdgeInsets.symmetric(vertical: 12),
          child: CircularProgressIndicator(),
        ),
      ),
      error: (e, _) => Text(
        'Could not load properties: ${e is ApiException ? e.message : e}',
        style: TextStyle(color: colorScheme.error, fontSize: 13),
      ),
      data: (properties) {
        if (_propertyLocked) {
          final propertyName =
              widget.initialPropertyLabel ??
              _findProperty(properties)?.name ??
              'Property #${widget.initialPropertyId}';
          return TextFormField(
            key: const Key('work-order-property-field'),
            initialValue: propertyName,
            readOnly: true,
            decoration: const InputDecoration(labelText: 'Property'),
          );
        }
        return DropdownButtonFormField<int>(
          initialValue: _selectedPropertyId,
          decoration: const InputDecoration(labelText: 'Property'),
          items: properties
              .map(
                (p) => DropdownMenuItem(
                  value: p.id,
                  child: Text(p.name, overflow: TextOverflow.ellipsis),
                ),
              )
              .toList(),
          onChanged: (v) => setState(() {
            _selectedPropertyId = v;
            _selectedUnitId = null;
            _selectedTenantId = null;
          }),
          validator: (v) => v == null ? 'Please select a property' : null,
        );
      },
    );

    final unitField = _unitLocked
        ? TextFormField(
            key: const Key('work-order-unit-field'),
            initialValue:
                widget.initialUnitLabel ?? 'Unit ${widget.initialUnitId}',
            readOnly: true,
            decoration: const InputDecoration(labelText: 'Unit'),
          )
        : unitsAsync == null
        ? DropdownButtonFormField<int>(
            initialValue: null,
            decoration: const InputDecoration(
              labelText: 'Unit (optional)',
              hintText: 'Select a property first',
            ),
            items: const [],
            onChanged: null,
          )
        : unitsAsync.when(
            loading: () => const Padding(
              padding: EdgeInsets.symmetric(vertical: 8),
              child: LinearProgressIndicator(),
            ),
            error: (e, _) => Text(
              'Could not load units: ${e is ApiException ? e.message : e}',
              style: TextStyle(color: colorScheme.error, fontSize: 13),
            ),
            data: (units) => DropdownButtonFormField<int?>(
              initialValue: _selectedUnitId,
              decoration: const InputDecoration(labelText: 'Unit (optional)'),
              items: [
                const DropdownMenuItem<int?>(
                  value: null,
                  child: Text('No specific unit'),
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
              onChanged: (v) => setState(() {
                _selectedUnitId = v;
                _selectedTenantId = null;
              }),
            ),
          );

    final tenantField = tenantsAsync == null
        ? DropdownButtonFormField<int?>(
            initialValue: null,
            decoration: const InputDecoration(
              labelText: 'Tenant (optional)',
              hintText: 'Select a property first',
            ),
            items: const [],
            onChanged: null,
          )
        : tenantsAsync.when(
            loading: () => const Padding(
              padding: EdgeInsets.symmetric(vertical: 8),
              child: LinearProgressIndicator(),
            ),
            error: (e, _) => Text(
              'Could not load tenants: ${e is ApiException ? e.message : e}',
              style: TextStyle(color: colorScheme.error, fontSize: 13),
            ),
            data: (page) {
              final tenants = page.items;
              _clearSelectedTenantIfMissing(tenants);
              final visibleTenantId =
                  _selectedTenantId != null &&
                      tenants.any((tenant) => tenant.id == _selectedTenantId)
                  ? _selectedTenantId
                  : null;
              return DropdownButtonFormField<int?>(
                initialValue: visibleTenantId,
                decoration: const InputDecoration(
                  labelText: 'Tenant (optional)',
                ),
                items: [
                  const DropdownMenuItem<int?>(
                    value: null,
                    child: Text('No tenant'),
                  ),
                  ...tenants.map(
                    (t) => DropdownMenuItem<int?>(
                      value: t.id,
                      child: Text(
                        _tenantLabel(t),
                        overflow: TextOverflow.ellipsis,
                      ),
                    ),
                  ),
                ],
                onChanged: (v) => setState(() => _selectedTenantId = v),
              );
            },
          );

    final vendorField = vendorsAsync.when(
      loading: () => const Padding(
        padding: EdgeInsets.symmetric(vertical: 8),
        child: LinearProgressIndicator(),
      ),
      error: (e, _) => Text(
        'Could not load vendors: ${e is ApiException ? e.message : e}',
        style: TextStyle(color: colorScheme.error, fontSize: 13),
      ),
      data: (vendors) => DropdownButtonFormField<int?>(
        initialValue: _selectedVendorId,
        decoration: const InputDecoration(labelText: 'Vendor (optional)'),
        items: [
          const DropdownMenuItem<int?>(value: null, child: Text('No vendor')),
          ...vendors.map(
            (v) => DropdownMenuItem<int?>(
              value: v.id,
              child: Text(v.name, overflow: TextOverflow.ellipsis),
            ),
          ),
        ],
        onChanged: (v) => setState(() => _selectedVendorId = v),
      ),
    );

    return Form(
      key: _formKey,
      child: WorkOrderFormShell(
        title: 'New Work Order',
        saveLabel: 'Save Work Order',
        saving: _saving,
        error: _error,
        onSave: _submit,
        tabs: [
          WorkOrderFormTabSpec(
            label: 'Location',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [propertyField, gap, unitField],
            ),
          ),
          WorkOrderFormTabSpec(
            label: 'Issue',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                TextFormField(
                  key: const Key('work-order-title-field'),
                  controller: _titleCtrl,
                  textInputAction: TextInputAction.next,
                  decoration: const InputDecoration(labelText: 'Title'),
                  validator: (v) => (v == null || v.trim().isEmpty)
                      ? 'Title is required'
                      : null,
                ),
                gap,
                TextFormField(
                  key: const Key('work-order-description-field'),
                  controller: _descCtrl,
                  maxLines: 3,
                  textInputAction: TextInputAction.newline,
                  decoration: const InputDecoration(labelText: 'Description'),
                  validator: (v) => (v == null || v.trim().isEmpty)
                      ? 'Description is required'
                      : null,
                ),
              ],
            ),
          ),
          WorkOrderFormTabSpec(
            label: 'Schedule',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Row(
                  children: [
                    Expanded(
                      child: DropdownButtonFormField<String>(
                        initialValue: _priority,
                        decoration: const InputDecoration(
                          labelText: 'Priority',
                        ),
                        items: _priorities
                            .map(
                              (p) => DropdownMenuItem(value: p, child: Text(p)),
                            )
                            .toList(),
                        onChanged: (v) {
                          if (v != null) setState(() => _priority = v);
                        },
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: DropdownButtonFormField<String>(
                        initialValue: _category,
                        decoration: const InputDecoration(
                          labelText: 'Category',
                        ),
                        items: _categories
                            .map(
                              (c) => DropdownMenuItem(value: c, child: Text(c)),
                            )
                            .toList(),
                        onChanged: (v) {
                          if (v != null) setState(() => _category = v);
                        },
                      ),
                    ),
                  ],
                ),
                gap,
                InkWell(
                  onTap: _pickScheduledDate,
                  child: InputDecorator(
                    decoration: const InputDecoration(
                      labelText: 'Scheduled date (optional)',
                      suffixIcon: Icon(Icons.calendar_today_outlined),
                    ),
                    child: Text(
                      _scheduledDate == null
                          ? 'Not scheduled'
                          : _fmtDate(_scheduledDate!),
                      style: TextStyle(
                        color: _scheduledDate == null
                            ? colorScheme.onSurfaceVariant
                            : colorScheme.onSurface,
                      ),
                    ),
                  ),
                ),
                gap,
                Row(
                  children: [
                    Expanded(
                      child: InkWell(
                        onTap: _scheduledDate == null
                            ? null
                            : () => _pickTime(isStart: true),
                        child: InputDecorator(
                          decoration: const InputDecoration(
                            labelText: 'Start time',
                            suffixIcon: Icon(Icons.schedule_outlined),
                          ),
                          child: Text(
                            _startTime == null
                                ? '--:--'
                                : _startTime!.format(context),
                            style: TextStyle(
                              color: _startTime == null
                                  ? colorScheme.onSurfaceVariant
                                  : colorScheme.onSurface,
                            ),
                          ),
                        ),
                      ),
                    ),
                    const Spacer(),
                  ],
                ),
              ],
            ),
          ),
          WorkOrderFormTabSpec(
            label: 'Assign',
            validate: _validateArrivalWindow,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Row(
                  children: [
                    Expanded(
                      child: InkWell(
                        onTap: _scheduledDate == null
                            ? null
                            : () => _pickTime(isStart: false),
                        child: InputDecorator(
                          decoration: const InputDecoration(
                            labelText: 'Arrival window end',
                            suffixIcon: Icon(Icons.schedule_outlined),
                          ),
                          child: Text(
                            _windowEndTime == null
                                ? '--:--'
                                : _windowEndTime!.format(context),
                            style: TextStyle(
                              color: _windowEndTime == null
                                  ? colorScheme.onSurfaceVariant
                                  : colorScheme.onSurface,
                            ),
                          ),
                        ),
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: TextFormField(
                        key: const Key('work-order-estimated-cost-field'),
                        controller: _estCostCtrl,
                        keyboardType: const TextInputType.numberWithOptions(
                          decimal: true,
                        ),
                        decoration: const InputDecoration(
                          labelText: 'Estimated cost (optional)',
                          prefixText: '\$ ',
                        ),
                        validator: (v) {
                          final t = v?.trim() ?? '';
                          if (t.isEmpty) return null;
                          final parsed = double.tryParse(t);
                          if (parsed == null) return 'Enter a valid amount';
                          if (parsed < 0) return 'Cannot be negative';
                          return null;
                        },
                      ),
                    ),
                  ],
                ),
                gap,
                tenantField,
                gap,
                vendorField,
              ],
            ),
          ),
          WorkOrderFormTabSpec(
            label: 'Attach',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                if (_photoBytes != null) ...[
                  ClipRRect(
                    borderRadius: BorderRadius.circular(12),
                    child: Image.memory(
                      _photoBytes!,
                      height: 140,
                      fit: BoxFit.cover,
                      width: double.infinity,
                    ),
                  ),
                  const SizedBox(height: 8),
                ],
                Row(
                  children: [
                    Expanded(
                      child: OutlinedButton.icon(
                        onPressed: _saving
                            ? null
                            : () => _pickPhoto(ImageSource.camera),
                        icon: const Icon(Icons.camera_alt_outlined),
                        label: Text(
                          _photoBytes == null ? 'Take photo' : 'Retake',
                        ),
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: OutlinedButton.icon(
                        onPressed: _saving
                            ? null
                            : () => _pickPhoto(ImageSource.gallery),
                        icon: const Icon(Icons.photo_library_outlined),
                        label: const Text('Choose'),
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}
