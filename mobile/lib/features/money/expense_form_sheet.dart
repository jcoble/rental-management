import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/work_order.dart';
import '../../core/time/app_clock.dart';
import '../../core/widgets/tabbed_form_sheet.dart';
import '../maintenance/work_orders_repository.dart';
import '../vendors/vendors_models.dart';
import '../vendors/vendors_repository.dart';
import 'expense_models.dart';
import 'money_format.dart';
import 'money_repository.dart';

final _expenseFormWorkOrdersProvider =
    FutureProvider.autoDispose<List<WorkOrder>>((ref) {
      return ref
          .watch(workOrdersRepositoryProvider)
          .listWorkOrders(take: 100, sort: '-updatedAt');
    });

Future<void> showCreateExpenseSheet(
  BuildContext context,
  WidgetRef ref, {
  VoidCallback? onSaved,
}) async {
  final saved = await showModalBottomSheet<bool>(
    context: context,
    isScrollControlled: true,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
    ),
    builder: (_) => const _CreateExpenseSheet(),
  );

  if (saved == true) {
    ref.invalidate(expensesListProvider);
    onSaved?.call();
  }
}

class _CreateExpenseSheet extends ConsumerStatefulWidget {
  const _CreateExpenseSheet();

  @override
  ConsumerState<_CreateExpenseSheet> createState() =>
      _CreateExpenseSheetState();
}

class _CreateExpenseSheetState extends ConsumerState<_CreateExpenseSheet> {
  final _formKey = GlobalKey<FormState>();
  final _descCtrl = TextEditingController();
  final _amountCtrl = TextEditingController();
  final _paymentMethodCtrl = TextEditingController();
  final _notesCtrl = TextEditingController();
  final _vendorAddressCtrl = TextEditingController();
  final _vendorPhoneCtrl = TextEditingController();
  final _vendorTaxIdCtrl = TextEditingController();

  int? _vendorId;
  WorkOrder? _selectedWorkOrder;
  ScheduleECategory _category = ScheduleECategory.repairs;
  ExpenseStatus _status = ExpenseStatus.paid;
  DateTime _incurredAt = DateTime.utc(2000);
  DateTime _paidAt = DateTime.utc(2000);
  bool _incurredDateTouched = false;
  bool _paidDateTouched = false;
  bool _datesInitialized = false;
  bool _billable = false;
  bool _saving = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    Future.microtask(_initializeDatesFromAppClock);
  }

  @override
  void dispose() {
    _descCtrl.dispose();
    _amountCtrl.dispose();
    _paymentMethodCtrl.dispose();
    _notesCtrl.dispose();
    _vendorAddressCtrl.dispose();
    _vendorPhoneCtrl.dispose();
    _vendorTaxIdCtrl.dispose();
    super.dispose();
  }

  Future<void> _initializeDatesFromAppClock() async {
    try {
      final today = _dateOnly(await ref.read(appNowProvider.future));
      if (!mounted) return;
      setState(() {
        if (!_incurredDateTouched) {
          _incurredAt = today;
        }
        if (!_paidDateTouched) {
          _paidAt = today;
        }
        _datesInitialized = true;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _error = 'The app clock could not be loaded.';
        _datesInitialized = false;
      });
    }
  }

  Future<void> _pickIncurredDate() async {
    final now = _dateOnly(await ref.read(appNowProvider.future));
    if (!mounted) return;
    final picked = await showDatePicker(
      context: context,
      initialDate: _datesInitialized ? _incurredAt : now,
      firstDate: DateTime(now.year - 5),
      lastDate: DateTime(now.year + 1),
    );
    if (picked != null) {
      setState(() {
        _incurredDateTouched = true;
        _incurredAt = picked;
        if (_status == ExpenseStatus.paid && !_paidDateTouched) {
          _paidAt = picked;
        }
      });
    }
  }

  Future<void> _pickPaidDate() async {
    final now = _dateOnly(await ref.read(appNowProvider.future));
    if (!mounted) return;
    final picked = await showDatePicker(
      context: context,
      initialDate: _datesInitialized ? _paidAt : now,
      firstDate: DateTime(now.year - 5),
      lastDate: DateTime(now.year + 1),
    );
    if (picked != null) {
      setState(() {
        _paidDateTouched = true;
        _paidAt = picked;
      });
    }
  }

  Vendor? _vendorById(List<Vendor> vendors, int? id) {
    if (id == null) return null;
    for (final vendor in vendors) {
      if (vendor.id == id) return vendor;
    }
    return null;
  }

  WorkOrder? _workOrderById(List<WorkOrder> workOrders, int? id) {
    if (id == null) return null;
    for (final order in workOrders) {
      if (order.id == id) return order;
    }
    return null;
  }

  String _workOrderLabel(WorkOrder workOrder) {
    final location = [
      workOrder.propertyName,
      if (workOrder.unitNumber != null) 'Unit ${workOrder.unitNumber}',
    ].whereType<String>().where((part) => part.trim().isNotEmpty).join(' · ');
    final prefix = '#${workOrder.id} ${workOrder.title}';
    return location.isEmpty ? prefix : '$prefix · $location';
  }

  String? _vendorSummary(Vendor vendor) {
    final details = [
      vendor.serviceType,
      vendor.phone,
      vendor.taxId == null ? null : 'Tax ID ${vendor.taxId}',
    ].whereType<String>().where((part) => part.trim().isNotEmpty).join(' · ');
    return details.isEmpty ? null : details;
  }

  String _vendorAddress(Vendor vendor) {
    final region = [
      vendor.state,
      vendor.postalCode,
    ].whereType<String>().where((part) => part.trim().isNotEmpty).join(' ');
    final cityLine = [
      vendor.city,
      region,
    ].whereType<String>().where((part) => part.trim().isNotEmpty).join(', ');
    return [
      vendor.addressLine1,
      cityLine,
    ].whereType<String>().where((part) => part.trim().isNotEmpty).join(', ');
  }

  void _fillBlank(TextEditingController controller, String? value) {
    final trimmed = value?.trim();
    if (trimmed == null || trimmed.isEmpty) return;
    if (controller.text.trim().isNotEmpty) return;
    controller.text = trimmed;
  }

  void _fillVendorReceiptDetails(Vendor? vendor) {
    if (vendor == null) return;
    _fillBlank(_vendorAddressCtrl, _vendorAddress(vendor));
    _fillBlank(_vendorPhoneCtrl, vendor.phone);
    _fillBlank(_vendorTaxIdCtrl, vendor.taxId);
  }

  DateTime _dateOnly(DateTime value) {
    return DateTime(value.year, value.month, value.day);
  }

  DateTime _workOrderExpenseDate(WorkOrder workOrder) {
    return _dateOnly(
      workOrder.completedAt ?? workOrder.scheduledFor ?? workOrder.requestedAt,
    );
  }

  String _moneyInput(double amount) {
    return amount == amount.roundToDouble()
        ? amount.toStringAsFixed(0)
        : amount.toStringAsFixed(2);
  }

  void _selectVendor(int? vendorId, List<Vendor> vendors) {
    final vendor = _vendorById(vendors, vendorId);
    setState(() {
      _vendorId = vendorId;
      _fillVendorReceiptDetails(vendor);
    });
  }

  void _selectWorkOrder(
    int? workOrderId,
    List<WorkOrder> workOrders,
    List<Vendor> vendors,
  ) {
    final workOrder = _workOrderById(workOrders, workOrderId);
    setState(() {
      _selectedWorkOrder = workOrder;
      if (workOrder == null) return;

      if (_descCtrl.text.trim().isEmpty) {
        _descCtrl.text = workOrder.title;
      }

      final cost = workOrder.actualCost ?? workOrder.estimatedCost;
      if (_amountCtrl.text.trim().isEmpty && cost != null && cost > 0) {
        _amountCtrl.text = _moneyInput(cost);
      }

      if (!_incurredDateTouched) {
        _incurredAt = _workOrderExpenseDate(workOrder);
        if (_status == ExpenseStatus.paid && !_paidDateTouched) {
          _paidAt = _incurredAt;
        }
      }

      if (_vendorId == null && workOrder.vendorId != null) {
        _vendorId = workOrder.vendorId;
        _fillVendorReceiptDetails(_vendorById(vendors, workOrder.vendorId));
      }
    });
  }

  Widget _workOrderField(
    AsyncValue<List<WorkOrder>> state,
    AsyncValue<List<Vendor>> vendorsState,
  ) {
    final vendors = vendorsState.asData?.value ?? const <Vendor>[];
    return state.when(
      data: (workOrders) => DropdownButtonFormField<int>(
        key: const Key('expense-work-order-field'),
        initialValue: _selectedWorkOrder?.id ?? 0,
        isExpanded: true,
        decoration: const InputDecoration(
          labelText: 'Link work order (optional)',
        ),
        items: [
          const DropdownMenuItem(value: 0, child: Text('No work order')),
          for (final workOrder in workOrders)
            DropdownMenuItem(
              value: workOrder.id,
              child: Text(_workOrderLabel(workOrder)),
            ),
        ],
        onChanged: (id) => _selectWorkOrder(
          id == null || id == 0 ? null : id,
          workOrders,
          vendors,
        ),
      ),
      loading: () => const InputDecorator(
        decoration: InputDecoration(labelText: 'Link work order (optional)'),
        child: Text('Loading work orders...'),
      ),
      error: (_, _) => const InputDecorator(
        decoration: InputDecoration(labelText: 'Link work order (optional)'),
        child: Text('Work orders could not be loaded.'),
      ),
    );
  }

  Widget _vendorField(AsyncValue<List<Vendor>> state) {
    return state.when(
      data: (vendors) {
        final selectedVendor = _vendorById(vendors, _vendorId);
        final selectedId = selectedVendor?.id ?? 0;
        final summary = selectedVendor == null
            ? null
            : _vendorSummary(selectedVendor);

        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            DropdownButtonFormField<int>(
              key: ValueKey('expense-vendor-field-$selectedId'),
              initialValue: selectedId,
              isExpanded: true,
              decoration: const InputDecoration(labelText: 'Vendor (optional)'),
              items: [
                const DropdownMenuItem(value: 0, child: Text('No vendor')),
                for (final vendor in vendors)
                  DropdownMenuItem(value: vendor.id, child: Text(vendor.name)),
              ],
              onChanged: (id) =>
                  _selectVendor(id == null || id == 0 ? null : id, vendors),
            ),
            if (summary != null) ...[
              const SizedBox(height: 6),
              Text(
                summary,
                style: Theme.of(context).textTheme.bodySmall?.copyWith(
                  color: Theme.of(context).colorScheme.onSurfaceVariant,
                ),
              ),
            ],
          ],
        );
      },
      loading: () => const InputDecorator(
        decoration: InputDecoration(labelText: 'Vendor (optional)'),
        child: Text('Loading vendors...'),
      ),
      error: (_, _) => const InputDecorator(
        decoration: InputDecoration(labelText: 'Vendor (optional)'),
        child: Text('Vendors could not be loaded.'),
      ),
    );
  }

  Future<void> _submit() async {
    if (!_datesInitialized) return;
    if (!(_formKey.currentState?.validate() ?? false)) return;
    setState(() {
      _saving = true;
      _error = null;
    });

    try {
      final paymentMethod = _paymentMethodCtrl.text.trim();
      final notes = _notesCtrl.text.trim();
      final receiptData = _receiptDataJson();
      await ref.read(moneyRepositoryProvider).createExpense({
        'description': _descCtrl.text.trim(),
        'amount': double.tryParse(_amountCtrl.text.trim()) ?? 0,
        if (_selectedWorkOrder != null)
          'propertyId': _selectedWorkOrder!.propertyId,
        if (_selectedWorkOrder?.unitId != null)
          'unitId': _selectedWorkOrder!.unitId,
        if (_vendorId != null) 'vendorId': _vendorId,
        if (_selectedWorkOrder != null) 'workOrderId': _selectedWorkOrder!.id,
        'category': _category.wire,
        'status': _status.wire,
        'incurredAt': _incurredAt.toIso8601String(),
        if (_status == ExpenseStatus.paid) 'paidAt': _paidAt.toIso8601String(),
        'billableToOwner': _billable,
        if (paymentMethod.isNotEmpty) 'paymentMethod': paymentMethod,
        if (notes.isNotEmpty) 'notes': notes,
        'receiptData': ?receiptData,
      });

      if (mounted) Navigator.of(context).pop(true);
    } on ApiException catch (e) {
      setState(() => _error = e.message);
      rethrow;
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  String? _receiptDataJson() {
    final vendorAddress = _vendorAddressCtrl.text.trim();
    final vendorPhone = _vendorPhoneCtrl.text.trim();
    final vendorTaxId = _vendorTaxIdCtrl.text.trim();
    final vendor = <String, String>{
      if (vendorAddress.isNotEmpty) 'address': vendorAddress,
      if (vendorPhone.isNotEmpty) 'phone': vendorPhone,
      if (vendorTaxId.isNotEmpty) 'taxId': vendorTaxId,
    };
    if (vendor.isEmpty) return null;
    return jsonEncode({'vendor': vendor});
  }

  @override
  Widget build(BuildContext context) {
    final vendorsState = ref.watch(vendorsProvider);
    final workOrdersState = ref.watch(_expenseFormWorkOrdersProvider);
    final isPaid = _status == ExpenseStatus.paid;
    const gap = SizedBox(height: 12);

    return Form(
      key: _formKey,
      child: TabbedFormSheet(
        title: 'Add expense',
        saveLabel: 'Save expense',
        saving: _saving || !_datesInitialized,
        error: _error,
        onSave: _submit,
        tabs: [
          TabbedFormStepSpec(
            label: 'Context',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                _workOrderField(workOrdersState, vendorsState),
                gap,
                _vendorField(vendorsState),
                gap,
                DropdownButtonFormField<ScheduleECategory>(
                  initialValue: _category,
                  isExpanded: true,
                  decoration: const InputDecoration(labelText: 'Category'),
                  items: ScheduleECategory.values
                      .map(
                        (c) => DropdownMenuItem(value: c, child: Text(c.label)),
                      )
                      .toList(),
                  onChanged: (v) {
                    if (v != null) setState(() => _category = v);
                  },
                ),
              ],
            ),
          ),
          TabbedFormStepSpec(
            label: 'Amount',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                TextFormField(
                  key: const Key('expense-description-field'),
                  controller: _descCtrl,
                  textInputAction: TextInputAction.next,
                  decoration: const InputDecoration(labelText: 'Description'),
                  validator: (v) => (v == null || v.trim().isEmpty)
                      ? 'Description is required'
                      : null,
                ),
                gap,
                TextFormField(
                  key: const Key('expense-amount-field'),
                  controller: _amountCtrl,
                  keyboardType: const TextInputType.numberWithOptions(
                    decimal: true,
                  ),
                  textInputAction: TextInputAction.next,
                  decoration: const InputDecoration(
                    labelText: 'Amount',
                    prefixText: '\$',
                  ),
                  validator: (v) {
                    if (v == null || v.trim().isEmpty) {
                      return 'Amount is required';
                    }
                    final amount = double.tryParse(v.trim());
                    if (amount == null) {
                      return 'Enter a valid number';
                    }
                    if (amount <= 0) {
                      return 'Amount must be greater than zero';
                    }
                    return null;
                  },
                ),
                gap,
                DropdownButtonFormField<ExpenseStatus>(
                  initialValue: _status,
                  decoration: const InputDecoration(labelText: 'Status'),
                  items: ExpenseStatus.values
                      .map(
                        (s) => DropdownMenuItem(value: s, child: Text(s.label)),
                      )
                      .toList(),
                  onChanged: (v) {
                    if (v == null) return;
                    setState(() {
                      _status = v;
                      if (v == ExpenseStatus.paid) {
                        _paidAt = _incurredAt;
                        _paidDateTouched = false;
                      }
                    });
                  },
                ),
              ],
            ),
          ),
          TabbedFormStepSpec(
            label: 'Dates',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                InkWell(
                  onTap: _pickIncurredDate,
                  borderRadius: BorderRadius.circular(4),
                  child: InputDecorator(
                    decoration: const InputDecoration(
                      labelText: 'Incurred date',
                      suffixIcon: Icon(Icons.calendar_today_outlined, size: 18),
                    ),
                    child: Text(dateFmt(_incurredAt)),
                  ),
                ),
                if (isPaid) ...[
                  gap,
                  InkWell(
                    onTap: _pickPaidDate,
                    borderRadius: BorderRadius.circular(4),
                    child: InputDecorator(
                      decoration: const InputDecoration(
                        labelText: 'Paid date',
                        suffixIcon: Icon(
                          Icons.event_available_outlined,
                          size: 18,
                        ),
                      ),
                      child: Text(dateFmt(_paidAt)),
                    ),
                  ),
                ],
              ],
            ),
          ),
          TabbedFormStepSpec(
            label: 'Notes',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                TextFormField(
                  controller: _paymentMethodCtrl,
                  textInputAction: TextInputAction.next,
                  decoration: const InputDecoration(
                    labelText: 'Paid with (optional)',
                  ),
                ),
                const SizedBox(height: 4),
                SwitchListTile(
                  contentPadding: EdgeInsets.zero,
                  title: const Text('Billable to owner'),
                  value: _billable,
                  onChanged: (v) => setState(() => _billable = v),
                ),
                TextFormField(
                  controller: _notesCtrl,
                  maxLines: 2,
                  textInputAction: TextInputAction.done,
                  decoration: const InputDecoration(labelText: 'Notes'),
                ),
                gap,
                Text(
                  'Vendor receipt details',
                  style: Theme.of(context).textTheme.titleSmall,
                ),
                const SizedBox(height: 8),
                TextFormField(
                  controller: _vendorAddressCtrl,
                  textInputAction: TextInputAction.next,
                  decoration: const InputDecoration(
                    labelText: 'Vendor address',
                  ),
                ),
                gap,
                TextFormField(
                  controller: _vendorPhoneCtrl,
                  keyboardType: TextInputType.phone,
                  textInputAction: TextInputAction.next,
                  decoration: const InputDecoration(labelText: 'Vendor phone'),
                ),
                gap,
                TextFormField(
                  controller: _vendorTaxIdCtrl,
                  textInputAction: TextInputAction.done,
                  decoration: const InputDecoration(labelText: 'Vendor tax ID'),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}
