import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/work_order.dart';
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

  int? _vendorId;
  WorkOrder? _selectedWorkOrder;
  ScheduleECategory _category = ScheduleECategory.repairs;
  ExpenseStatus _status = ExpenseStatus.paid;
  DateTime _incurredAt = DateTime.now();
  DateTime _paidAt = DateTime.now();
  bool _billable = false;
  bool _saving = false;
  String? _error;

  @override
  void dispose() {
    _descCtrl.dispose();
    _amountCtrl.dispose();
    _paymentMethodCtrl.dispose();
    _notesCtrl.dispose();
    super.dispose();
  }

  Future<void> _pickIncurredDate() async {
    final now = DateTime.now();
    final picked = await showDatePicker(
      context: context,
      initialDate: _incurredAt,
      firstDate: DateTime(now.year - 5),
      lastDate: DateTime(now.year + 1),
    );
    if (picked != null) {
      setState(() {
        _incurredAt = picked;
        if (_status == ExpenseStatus.paid) _paidAt = picked;
      });
    }
  }

  Future<void> _pickPaidDate() async {
    final now = DateTime.now();
    final picked = await showDatePicker(
      context: context,
      initialDate: _paidAt,
      firstDate: DateTime(now.year - 5),
      lastDate: DateTime(now.year + 1),
    );
    if (picked != null) setState(() => _paidAt = picked);
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

  String _moneyInput(double amount) {
    return amount == amount.roundToDouble()
        ? amount.toStringAsFixed(0)
        : amount.toStringAsFixed(2);
  }

  void _selectVendor(int? vendorId) {
    setState(() => _vendorId = vendorId);
  }

  void _selectWorkOrder(int? workOrderId, List<WorkOrder> workOrders) {
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

      _vendorId ??= workOrder.vendorId;
    });
  }

  Widget _workOrderField(AsyncValue<List<WorkOrder>> state) {
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
        onChanged: (id) =>
            _selectWorkOrder(id == null || id == 0 ? null : id, workOrders),
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
                  _selectVendor(id == null || id == 0 ? null : id),
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
    if (!(_formKey.currentState?.validate() ?? false)) return;
    setState(() {
      _saving = true;
      _error = null;
    });

    try {
      final paymentMethod = _paymentMethodCtrl.text.trim();
      final notes = _notesCtrl.text.trim();
      await ref.read(moneyRepositoryProvider).createExpense({
        'description': _descCtrl.text.trim(),
        'amount': double.tryParse(_amountCtrl.text.trim()) ?? 0,
        if (_selectedWorkOrder != null)
          'propertyId': _selectedWorkOrder!.propertyId,
        if (_vendorId != null) 'vendorId': _vendorId,
        if (_selectedWorkOrder != null) 'workOrderId': _selectedWorkOrder!.id,
        'category': _category.wire,
        'status': _status.wire,
        'incurredAt': _incurredAt.toIso8601String(),
        if (_status == ExpenseStatus.paid) 'paidAt': _paidAt.toIso8601String(),
        'billableToOwner': _billable,
        if (paymentMethod.isNotEmpty) 'paymentMethod': paymentMethod,
        if (notes.isNotEmpty) 'notes': notes,
      });

      if (mounted) Navigator.of(context).pop(true);
    } on ApiException catch (e) {
      setState(() => _error = e.message);
      rethrow;
    } finally {
      if (mounted) setState(() => _saving = false);
    }
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
        saving: _saving,
        error: _error,
        onSave: _submit,
        tabs: [
          TabbedFormStepSpec(
            label: 'Context',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                _workOrderField(workOrdersState),
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
              ],
            ),
          ),
        ],
      ),
    );
  }
}
