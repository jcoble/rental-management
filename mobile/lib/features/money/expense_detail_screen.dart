import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/widgets/tabbed_form_sheet.dart';
import '../activity/activity_history_screen.dart';
import '../accounting/accounting_book_models.dart';
import '../accounting/accounting_impact_card.dart';
import '../properties/capital_assets_repository.dart';
import 'expense_models.dart';
import '../../core/presentation/formatting.dart';
import 'money_repository.dart';
import 'receipt_attachment_repository.dart';
import 'receipt_upload_sheet.dart';
import 'receipt_viewer_screen.dart';
import 'transactions_controller.dart';

/// Detail page for one expense — view, inline edit, delete, and receipt viewer.
///
/// Follows the project's detail-page house rule (a real screen, not a modal),
/// with editing via an inline bottom-sheet.
class ExpenseDetailScreen extends ConsumerWidget {
  const ExpenseDetailScreen({super.key, required this.expenseId});

  final int expenseId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(expenseDetailProvider(expenseId));

    return Scaffold(
      appBar: AppBar(
        title: const Text('Expense'),
        actions: [
          IconButton(
            icon: const Icon(Icons.history_outlined),
            tooltip: 'View expense activity',
            onPressed: () => Navigator.of(context).push<void>(
              MaterialPageRoute<void>(
                builder: (_) => ActivityHistoryScreen(
                  entityType: 'Expense',
                  entityId: expenseId,
                  title: 'Expense activity',
                ),
              ),
            ),
          ),
          async.maybeWhen(
            data: (expense) => IconButton(
              icon: const Icon(Icons.delete_outline),
              tooltip: 'Delete expense',
              onPressed: () => _confirmDelete(context, ref, expense),
            ),
            orElse: () => const SizedBox.shrink(),
          ),
        ],
      ),
      body: async.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => _ErrorView(
          message: e is ApiException ? e.message : e.toString(),
          onRetry: () => ref.invalidate(expenseDetailProvider(expenseId)),
        ),
        data: (expense) => _ExpenseBody(expense: expense),
      ),
    );
  }

  Future<void> _confirmDelete(
    BuildContext context,
    WidgetRef ref,
    Expense expense,
  ) async {
    final messenger = ScaffoldMessenger.of(context);
    final navigator = Navigator.of(context);
    final ok = await showDialog<bool>(
      context: context,
      builder: (_) => AlertDialog(
        title: const Text('Delete expense?'),
        content: Text(
          'This will permanently remove "${expense.description}" '
          '(${moneyFmt(expense.amount)}).',
        ),
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
    if (ok != true) return;

    try {
      await ref.read(moneyRepositoryProvider).deleteExpense(expense.id);
      ref.invalidate(expensesListProvider);
      ref.invalidate(expensesPageProvider);
      ref.invalidate(transactionsProvider);
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(const SnackBar(content: Text('Expense deleted.')));
      navigator.pop();
    } on ApiException catch (e) {
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(e.message)));
    }
  }
}

class _ExpenseBody extends ConsumerWidget {
  const _ExpenseBody({required this.expense});

  final Expense expense;

  Future<void> _edit(BuildContext context, WidgetRef ref) async {
    final saved = await showModalBottomSheet<bool>(
      context: context,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => _EditExpenseSheet(expense: expense),
    );
    if (saved == true) {
      ref.invalidate(expenseDetailProvider(expense.id));
      ref.invalidate(expensesListProvider);
      ref.invalidate(expensesPageProvider);
      ref.invalidate(transactionsProvider);
    }
  }

  void _viewReceipt(BuildContext context) {
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => ReceiptViewerScreen(
          expenseId: expense.id,
          isImage: expense.receiptIsImage,
        ),
      ),
    );
  }

  Future<void> _uploadReceipt(BuildContext context, WidgetRef ref) async {
    final messenger = ScaffoldMessenger.of(context);
    final uploaded = await showReceiptUploadSheet(
      context: context,
      entityType: ReceiptEntityType.expense,
      entityId: expense.id,
    );
    if (!uploaded) return;
    ref.invalidate(expenseDetailProvider(expense.id));
    ref.invalidate(expenseReceiptProvider(expense.id));
    ref.invalidate(expensesListProvider);
    ref.invalidate(expensesPageProvider);
    ref.invalidate(transactionsProvider);
    messenger
      ..hideCurrentSnackBar()
      ..showSnackBar(const SnackBar(content: Text('Receipt uploaded.')));
  }

  Future<void> _capitalize(BuildContext context, WidgetRef ref) async {
    final messenger = ScaffoldMessenger.of(context);
    final saved = await showModalBottomSheet<bool>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => _CapitalizeExpenseSheet(expense: expense),
    );
    if (saved != true) return;

    ref.invalidate(expenseDetailProvider(expense.id));
    ref.invalidate(expensesListProvider);
    ref.invalidate(expensesPageProvider);
    ref.invalidate(transactionsProvider);
    final propertyId = expense.propertyId;
    if (propertyId != null) {
      ref.invalidate(propertyCapitalAssetsProvider(propertyId));
    }
    if (!context.mounted) return;
    messenger
      ..hideCurrentSnackBar()
      ..showSnackBar(const SnackBar(content: Text('Expense capitalized.')));
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    return ListView(
      padding: const EdgeInsets.fromLTRB(20, 20, 20, 32),
      children: [
        Text(
          moneyFmt(expense.amount),
          style: theme.textTheme.displaySmall?.copyWith(
            fontWeight: FontWeight.w700,
          ),
        ),
        const SizedBox(height: 4),
        Text(expense.description, style: theme.textTheme.titleMedium),
        const SizedBox(height: 12),
        Wrap(
          spacing: 8,
          runSpacing: 8,
          children: [
            _chip(expense.status.label, _statusBg(cs), _statusFg(cs)),
            _chip(
              expense.category.label,
              cs.secondaryContainer,
              cs.onSecondaryContainer,
            ),
            if (expense.billableToOwner)
              _chip(
                'Billable to owner',
                cs.tertiaryContainer,
                cs.onTertiaryContainer,
              ),
            if (expense.capitalizedAssetId != null)
              _chip('Capitalized', cs.primaryContainer, cs.onPrimaryContainer),
          ],
        ),
        const SizedBox(height: 20),
        _DetailRow(label: 'Incurred', value: dateFmt(expense.incurredAt)),
        if (expense.dueDate != null)
          _DetailRow(label: 'Due', value: dateFmt(expense.dueDate!)),
        if (expense.paidAt != null)
          _DetailRow(label: 'Paid', value: dateFmt(expense.paidAt!)),
        if (expense.propertyName != null)
          _DetailRow(label: 'Property', value: expense.propertyName!),
        if (expense.vendorName != null)
          _DetailRow(label: 'Vendor', value: expense.vendorName!),
        if (expense.paymentMethod != null && expense.paymentMethod!.isNotEmpty)
          _DetailRow(
            label: 'Paid with',
            value: [
              expense.paymentMethod!,
              if (expense.cardLast4 != null && expense.cardLast4!.isNotEmpty)
                '••${expense.cardLast4}',
            ].join(' '),
          ),
        if (expense.subtotal != null)
          _DetailRow(label: 'Subtotal', value: moneyFmt(expense.subtotal!)),
        if (expense.taxAmount != null)
          _DetailRow(label: 'Tax', value: moneyFmt(expense.taxAmount!)),
        if (expense.notes != null && expense.notes!.isNotEmpty)
          _DetailRow(label: 'Notes', value: expense.notes!),

        // ── Line items (from scanned receipt) ─────────────────────────────
        if (expense.lineItems.isNotEmpty) ...[
          const SizedBox(height: 20),
          Text(
            'Items',
            style: theme.textTheme.titleSmall?.copyWith(
              fontWeight: FontWeight.w700,
            ),
          ),
          const SizedBox(height: 8),
          for (final li in expense.lineItems)
            Padding(
              padding: const EdgeInsets.symmetric(vertical: 4),
              child: Row(
                children: [
                  Expanded(child: Text(li.description)),
                  if (li.amount != null)
                    Text(
                      moneyFmt(li.amount!),
                      style: const TextStyle(fontWeight: FontWeight.w600),
                    ),
                ],
              ),
            ),
        ],

        AccountingImpactCard(
          sourceId: expense.id,
          sourceTypes: const [
            JournalSourceType.billIncurred,
            JournalSourceType.expensePayment,
          ],
        ),

        const SizedBox(height: 24),
        if (expense.hasReceipt)
          OutlinedButton.icon(
            onPressed: () => _viewReceipt(context),
            icon: Icon(
              expense.receiptIsImage
                  ? Icons.image_outlined
                  : Icons.picture_as_pdf_outlined,
            ),
            label: const Text('View receipt'),
          ),
        if (expense.hasReceipt) const SizedBox(height: 12),
        OutlinedButton.icon(
          onPressed: () => _uploadReceipt(context, ref),
          icon: const Icon(Icons.upload_file_outlined),
          label: const Text('Upload receipt'),
        ),
        const SizedBox(height: 12),
        if (expense.capitalizedAssetId != null) ...[
          OutlinedButton.icon(
            onPressed: null,
            icon: const Icon(Icons.account_balance_wallet_outlined),
            label: const Text('Capitalized'),
          ),
          const SizedBox(height: 12),
        ] else if (expense.propertyId != null) ...[
          OutlinedButton.icon(
            onPressed: () => _capitalize(context, ref),
            icon: const Icon(Icons.account_balance_wallet_outlined),
            label: const Text('Capitalize expense'),
          ),
          const SizedBox(height: 12),
        ],
        FilledButton.icon(
          onPressed: () => _edit(context, ref),
          icon: const Icon(Icons.edit_outlined),
          label: const Text('Edit expense'),
        ),
      ],
    );
  }

  Widget _chip(String label, Color bg, Color fg) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 5),
      decoration: BoxDecoration(
        color: bg,
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        label,
        style: TextStyle(fontSize: 12, fontWeight: FontWeight.w600, color: fg),
      ),
    );
  }

  Color _statusBg(ColorScheme cs) {
    switch (expense.status) {
      case ExpenseStatus.paid:
        return cs.primaryContainer;
      case ExpenseStatus.rejected:
        return cs.errorContainer;
      default:
        return cs.surfaceContainerHighest;
    }
  }

  Color _statusFg(ColorScheme cs) {
    switch (expense.status) {
      case ExpenseStatus.paid:
        return cs.onPrimaryContainer;
      case ExpenseStatus.rejected:
        return cs.onErrorContainer;
      default:
        return cs.onSurfaceVariant;
    }
  }
}

class _DetailRow extends StatelessWidget {
  const _DetailRow({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(
            width: 96,
            child: Text(
              label,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: cs.onSurfaceVariant,
              ),
            ),
          ),
          Expanded(
            child: Text(
              value,
              style: theme.textTheme.bodyMedium?.copyWith(
                fontWeight: FontWeight.w600,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _ErrorView extends StatelessWidget {
  const _ErrorView({required this.message, required this.onRetry});

  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.error_outline, size: 40, color: cs.error),
            const SizedBox(height: 12),
            Text(message, textAlign: TextAlign.center),
            const SizedBox(height: 16),
            FilledButton.tonal(onPressed: onRetry, child: const Text('Retry')),
          ],
        ),
      ),
    );
  }
}

class _CapitalizeExpenseSheet extends ConsumerStatefulWidget {
  const _CapitalizeExpenseSheet({required this.expense});

  final Expense expense;

  @override
  ConsumerState<_CapitalizeExpenseSheet> createState() =>
      _CapitalizeExpenseSheetState();
}

class _CapitalizeExpenseSheetState
    extends ConsumerState<_CapitalizeExpenseSheet> {
  final _descriptionCtrl = TextEditingController();
  final _recoveryYearsCtrl = TextEditingController(text: '27.5');
  late DateTime _inServiceDate;
  DepreciationMethod _method = DepreciationMethod.straightLine;
  DepreciationConvention _convention = DepreciationConvention.midMonth;
  bool _saving = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    final expense = widget.expense;
    _descriptionCtrl.text = expense.description;
    _inServiceDate = expense.incurredAt.year > 1
        ? expense.incurredAt
        : DateTime.now();
  }

  @override
  void dispose() {
    _descriptionCtrl.dispose();
    _recoveryYearsCtrl.dispose();
    super.dispose();
  }

  Future<void> _pickDate() async {
    final now = DateTime.now();
    final picked = await showDatePicker(
      context: context,
      initialDate: _inServiceDate,
      firstDate: DateTime(now.year - 40),
      lastDate: DateTime(now.year + 1),
    );
    if (picked != null) setState(() => _inServiceDate = picked);
  }

  Future<void> _submit() async {
    setState(() {
      _saving = true;
      _error = null;
    });

    final description = _descriptionCtrl.text.trim();
    try {
      await ref.read(moneyRepositoryProvider).capitalizeExpense(
        widget.expense.id,
        {
          'inServiceDate': _inServiceDate.toIso8601String(),
          'method': _method.wire,
          'recoveryYears': double.tryParse(_recoveryYearsCtrl.text.trim()) ?? 0,
          'convention': _convention.wire,
          if (description.isNotEmpty) 'description': description,
        },
      );
      if (mounted) Navigator.of(context).pop(true);
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    const gap = SizedBox(height: 12);
    return TabbedFormSheet(
      title: 'Capitalize Expense',
      saveLabel: 'Capitalize',
      saving: _saving,
      error: _error,
      onSave: _submit,
      tabs: [
        TabbedFormStepSpec(
          label: 'Asset',
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Text(
                'Cost basis: ${moneyFmt(widget.expense.amount)}',
                style: Theme.of(context).textTheme.labelLarge,
              ),
              gap,
              TextFormField(
                key: const Key('expense-capitalize-description-field'),
                controller: _descriptionCtrl,
                textInputAction: TextInputAction.next,
                textCapitalization: TextCapitalization.sentences,
                decoration: const InputDecoration(labelText: 'Description'),
                validator: (value) =>
                    (value != null && value.trim().length > 500)
                    ? 'Description must be 500 characters or fewer'
                    : null,
              ),
              gap,
              InkWell(
                onTap: _pickDate,
                child: InputDecorator(
                  decoration: const InputDecoration(
                    labelText: 'In-service date',
                    suffixIcon: Icon(Icons.calendar_today_outlined, size: 18),
                  ),
                  child: Text(dateFmt(_inServiceDate)),
                ),
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
                key: const Key('expense-capitalize-method-field'),
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
                key: const Key('expense-capitalize-recovery-years-field'),
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
              gap,
              DropdownButtonFormField<DepreciationConvention>(
                key: const Key('expense-capitalize-convention-field'),
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
          ),
        ),
      ],
    );
  }
}

/// Inline edit sheet for an expense (amount / category / status / dates / notes).
class _EditExpenseSheet extends ConsumerStatefulWidget {
  const _EditExpenseSheet({required this.expense});

  final Expense expense;

  @override
  ConsumerState<_EditExpenseSheet> createState() => _EditExpenseSheetState();
}

class _EditExpenseSheetState extends ConsumerState<_EditExpenseSheet> {
  final _formKey = GlobalKey<FormState>();
  late final TextEditingController _amountCtrl;
  late final TextEditingController _descCtrl;
  late final TextEditingController _notesCtrl;
  late ScheduleECategory _category;
  late ExpenseStatus _status;
  late DateTime _incurredAt;
  late bool _billable;
  bool _saving = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    final e = widget.expense;
    _amountCtrl = TextEditingController(text: e.amount.toStringAsFixed(2));
    _descCtrl = TextEditingController(text: e.description);
    _notesCtrl = TextEditingController(text: e.notes ?? '');
    _category = e.category;
    _status = e.status;
    _incurredAt = e.incurredAt.year > 1 ? e.incurredAt : DateTime.now();
    _billable = e.billableToOwner;
  }

  @override
  void dispose() {
    _amountCtrl.dispose();
    _descCtrl.dispose();
    _notesCtrl.dispose();
    super.dispose();
  }

  Future<void> _pickDate() async {
    final now = DateTime.now();
    final picked = await showDatePicker(
      context: context,
      initialDate: _incurredAt,
      firstDate: DateTime(now.year - 5),
      lastDate: DateTime(now.year + 1),
    );
    if (picked != null) setState(() => _incurredAt = picked);
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      await ref.read(moneyRepositoryProvider).updateExpense(widget.expense.id, {
        'amount': double.tryParse(_amountCtrl.text.trim()) ?? 0,
        'description': _descCtrl.text.trim(),
        'category': _category.wire,
        'status': _status.wire,
        'incurredAt': _incurredAt.toIso8601String(),
        'billableToOwner': _billable,
        'notes': _notesCtrl.text.trim(),
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
    const gap = SizedBox(height: 12);

    return Form(
      key: _formKey,
      child: TabbedFormSheet(
        title: 'Edit expense',
        saveLabel: 'Save',
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
                  decoration: const InputDecoration(
                    labelText: 'Amount',
                    prefixText: '\$',
                  ),
                  validator: (v) {
                    if (v == null || v.trim().isEmpty) {
                      return 'Amount is required';
                    }
                    if (double.tryParse(v.trim()) == null) {
                      return 'Enter a valid number';
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
                    if (v != null) setState(() => _status = v);
                  },
                ),
              ],
            ),
          ),
          TabbedFormStepSpec(
            label: 'Extra',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                InkWell(
                  onTap: _pickDate,
                  child: InputDecorator(
                    decoration: const InputDecoration(
                      labelText: 'Incurred date',
                      suffixIcon: Icon(Icons.calendar_today_outlined, size: 18),
                    ),
                    child: Text(dateFmt(_incurredAt)),
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

String _rangeLabel(double value) =>
    value == value.roundToDouble() ? value.toStringAsFixed(0) : '$value';
