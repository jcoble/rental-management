import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/widgets/tabbed_form_sheet.dart';
import 'expense_models.dart';
import 'money_format.dart';
import 'money_repository.dart';

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
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
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
            label: 'Details',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                TextFormField(
                  controller: _descCtrl,
                  textInputAction: TextInputAction.next,
                  decoration: const InputDecoration(labelText: 'Description'),
                  validator: (v) => (v == null || v.trim().isEmpty)
                      ? 'Description is required'
                      : null,
                ),
                gap,
                TextFormField(
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
            label: 'Extra',
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
