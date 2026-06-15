import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/models.dart';
import '../leases/lease_detail_screen.dart';
import '../leases/leases_repository.dart';
import '../money/money_format.dart';
import 'payments_repository.dart';
import 'record_payment_sheet.dart';

/// A single payment, fetched fresh by id so push deep-links and ledger taps can
/// open it without a preloaded model.
final paymentDetailProvider =
    FutureProvider.autoDispose.family<Payment, int>((ref, id) {
  return ref.read(paymentsRepositoryProvider).getPayment(id);
});

/// Detail page for one rent/charge payment — view, inline edit, mark-paid.
///
/// Follows the project's detail-page house rule (a real screen, not a modal),
/// with editing via an inline bottom-sheet.
class PaymentDetailScreen extends ConsumerWidget {
  const PaymentDetailScreen({super.key, required this.paymentId});

  final int paymentId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(paymentDetailProvider(paymentId));

    return Scaffold(
      appBar: AppBar(title: const Text('Payment')),
      body: async.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => _ErrorView(
          message: e is ApiException ? e.message : e.toString(),
          onRetry: () => ref.invalidate(paymentDetailProvider(paymentId)),
        ),
        data: (payment) => _PaymentBody(payment: payment),
      ),
    );
  }
}

class _PaymentBody extends ConsumerWidget {
  const _PaymentBody({required this.payment});

  final Payment payment;

  bool get _isPaid => payment.status.toLowerCase() == 'paid';

  Future<void> _markPaid(BuildContext context, WidgetRef ref) async {
    final messenger = ScaffoldMessenger.of(context);
    final updated =
        await showRecordPaymentSheet(context, ref, payment: payment);
    if (updated == null) return;
    ref.invalidate(paymentDetailProvider(payment.id));
    // Keep any lease payments section in sync after an inline record.
    ref.invalidate(leasePaymentsProvider(payment.leaseId));
    messenger
      ..hideCurrentSnackBar()
      ..showSnackBar(const SnackBar(content: Text('Payment recorded.')));
  }

  Future<void> _edit(BuildContext context, WidgetRef ref) async {
    final saved = await showModalBottomSheet<bool>(
      context: context,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => _EditPaymentSheet(payment: payment),
    );
    if (saved == true) ref.invalidate(paymentDetailProvider(payment.id));
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    return ListView(
      padding: const EdgeInsets.fromLTRB(20, 20, 20, 32),
      children: [
        Text(
          moneyFmt(payment.amount),
          style: theme.textTheme.displaySmall?.copyWith(
            fontWeight: FontWeight.w700,
          ),
        ),
        const SizedBox(height: 8),
        Wrap(
          spacing: 8,
          runSpacing: 8,
          children: [
            _chip(cs, payment.status, _statusBg(payment.status, cs),
                _statusFg(payment.status, cs)),
            if (payment.type.isNotEmpty)
              _chip(cs, payment.type, cs.secondaryContainer,
                  cs.onSecondaryContainer),
          ],
        ),
        const SizedBox(height: 20),
        _DetailRow(
          label: 'Tenant',
          value: payment.tenantName ?? '—',
        ),
        _DetailRow(
          label: 'Lease',
          value: payment.leaseNumber != null
              ? 'Lease ${payment.leaseNumber}'
              : 'Lease #${payment.leaseId}',
          onTap: () => _openLease(context, ref),
        ),
        _DetailRow(label: 'Due', value: dateFmt(payment.dueDate)),
        if (payment.paidDate != null)
          _DetailRow(label: 'Paid', value: dateFmt(payment.paidDate!)),
        if (payment.method != null && payment.method!.isNotEmpty)
          _DetailRow(label: 'Method', value: payment.method!),
        if (payment.externalReference != null &&
            payment.externalReference!.isNotEmpty)
          _DetailRow(label: 'Reference', value: payment.externalReference!),
        if (payment.notes != null && payment.notes!.isNotEmpty)
          _DetailRow(label: 'Notes', value: payment.notes!),
        const SizedBox(height: 24),
        Row(
          children: [
            if (!_isPaid)
              Expanded(
                child: FilledButton.icon(
                  onPressed: () => _markPaid(context, ref),
                  icon: const Icon(Icons.check_circle_outline),
                  label: const Text('Mark paid'),
                ),
              ),
            if (!_isPaid) const SizedBox(width: 12),
            Expanded(
              child: OutlinedButton.icon(
                onPressed: () => _edit(context, ref),
                icon: const Icon(Icons.edit_outlined),
                label: const Text('Edit'),
              ),
            ),
          ],
        ),
      ],
    );
  }

  Widget _chip(ColorScheme cs, String label, Color bg, Color fg) {
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

  Color _statusBg(String status, ColorScheme cs) {
    final lower = status.toLowerCase();
    if (lower == 'paid') return cs.primaryContainer;
    if (lower == 'late' || lower == 'overdue') return cs.errorContainer;
    return cs.surfaceContainerHighest;
  }

  Color _statusFg(String status, ColorScheme cs) {
    final lower = status.toLowerCase();
    if (lower == 'paid') return cs.onPrimaryContainer;
    if (lower == 'late' || lower == 'overdue') return cs.onErrorContainer;
    return cs.onSurfaceVariant;
  }

  /// Opens the lease this payment belongs to. We only carry the lease id on a
  /// payment, so push a screen that fetches the full lease via
  /// [leaseDetailProvider] and then shows the standard lease detail.
  void _openLease(BuildContext context, WidgetRef ref) {
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => LeaseDetailLoaderScreen(leaseId: payment.leaseId),
      ),
    );
  }
}

class _DetailRow extends StatelessWidget {
  const _DetailRow({required this.label, required this.value, this.onTap});

  final String label;
  final String value;

  /// When set, the row becomes tappable (drill-through) and shows a chevron.
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final row = Padding(
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
                color: onTap != null ? cs.primary : null,
              ),
            ),
          ),
          if (onTap != null)
            Icon(Icons.chevron_right, size: 18, color: cs.onSurfaceVariant),
        ],
      ),
    );
    if (onTap == null) return row;
    return InkWell(onTap: onTap, child: row);
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

/// Inline edit sheet for a payment (amount / due date / type / status / notes).
class _EditPaymentSheet extends ConsumerStatefulWidget {
  const _EditPaymentSheet({required this.payment});

  final Payment payment;

  @override
  ConsumerState<_EditPaymentSheet> createState() => _EditPaymentSheetState();
}

class _EditPaymentSheetState extends ConsumerState<_EditPaymentSheet> {
  final _formKey = GlobalKey<FormState>();
  late final TextEditingController _amountCtrl;
  late final TextEditingController _referenceCtrl;
  late final TextEditingController _notesCtrl;
  late DateTime _dueDate;
  late String _type;
  late String _status;
  String? _method;
  bool _saving = false;
  String? _error;

  static const _types = ['Rent', 'SecurityDeposit', 'LateFee', 'Utility', 'Other'];
  static const _statuses = ['Scheduled', 'Paid', 'Partial', 'Late', 'Waived'];
  static const _methods = [
    'Check',
    'Cash',
    'Bank transfer',
    'ACH',
    'Credit card',
    'Money order',
    'Online portal',
    'Other',
  ];

  /// Method dropdown options = the standard list plus any existing value not in
  /// it, so an imported / scanned method still shows.
  List<String> get _methodOptions {
    final options = [..._methods];
    final current = _method;
    if (current != null && current.isNotEmpty && !options.contains(current)) {
      options.insert(0, current);
    }
    return options;
  }

  @override
  void initState() {
    super.initState();
    final p = widget.payment;
    _amountCtrl = TextEditingController(text: p.amount.toStringAsFixed(2));
    _referenceCtrl = TextEditingController(text: p.externalReference ?? '');
    _notesCtrl = TextEditingController(text: p.notes ?? '');
    _dueDate = p.dueDate.year > 1 ? p.dueDate : DateTime.now();
    _type = _types.contains(p.type) ? p.type : 'Rent';
    _status = _statuses.contains(p.status) ? p.status : 'Scheduled';
    _method = (p.method != null && p.method!.isNotEmpty) ? p.method : null;
  }

  @override
  void dispose() {
    _amountCtrl.dispose();
    _referenceCtrl.dispose();
    _notesCtrl.dispose();
    super.dispose();
  }

  Future<void> _pickDate() async {
    final now = DateTime.now();
    final picked = await showDatePicker(
      context: context,
      initialDate: _dueDate,
      firstDate: DateTime(now.year - 3),
      lastDate: DateTime(now.year + 5),
    );
    if (picked != null) setState(() => _dueDate = picked);
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      final reference = _referenceCtrl.text.trim();
      await ref.read(paymentsRepositoryProvider).updatePayment(
        widget.payment.id,
        {
          'amount': double.tryParse(_amountCtrl.text.trim()) ?? 0,
          'dueDate': _dueDate.toIso8601String().split('T').first,
          'type': _type,
          'status': _status,
          if (_method != null) 'method': _method,
          'externalReference': reference,
          'notes': _notesCtrl.text.trim(),
        },
      );
      if (mounted) Navigator.of(context).pop(true);
    } on ApiException catch (e) {
      setState(() => _error = e.message);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final bottom = MediaQuery.viewInsetsOf(context).bottom;

    return Padding(
      padding: EdgeInsets.fromLTRB(20, 20, 20, 20 + bottom),
      child: Form(
        key: _formKey,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Row(
                children: [
                  Expanded(
                    child: Text(
                      'Edit payment',
                      style: theme.textTheme.titleLarge
                          ?.copyWith(fontWeight: FontWeight.w700),
                    ),
                  ),
                  IconButton(
                    icon: const Icon(Icons.close),
                    onPressed: () => Navigator.of(context).pop(false),
                  ),
                ],
              ),
              const SizedBox(height: 12),
              TextFormField(
                controller: _amountCtrl,
                keyboardType:
                    const TextInputType.numberWithOptions(decimal: true),
                decoration: const InputDecoration(
                  labelText: 'Amount',
                  prefixText: '\$',
                ),
                validator: (v) {
                  if (v == null || v.trim().isEmpty) return 'Amount is required';
                  if (double.tryParse(v.trim()) == null) {
                    return 'Enter a valid number';
                  }
                  return null;
                },
              ),
              const SizedBox(height: 12),
              InkWell(
                onTap: _pickDate,
                child: InputDecorator(
                  decoration: const InputDecoration(
                    labelText: 'Due date',
                    suffixIcon: Icon(Icons.calendar_today_outlined, size: 18),
                  ),
                  child: Text(dateFmt(_dueDate)),
                ),
              ),
              const SizedBox(height: 12),
              Row(
                children: [
                  Expanded(
                    child: DropdownButtonFormField<String>(
                      initialValue: _type,
                      decoration: const InputDecoration(labelText: 'Type'),
                      items: _types
                          .map((t) =>
                              DropdownMenuItem(value: t, child: Text(t)))
                          .toList(),
                      onChanged: (v) {
                        if (v != null) setState(() => _type = v);
                      },
                    ),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: DropdownButtonFormField<String>(
                      initialValue: _status,
                      decoration: const InputDecoration(labelText: 'Status'),
                      items: _statuses
                          .map((s) =>
                              DropdownMenuItem(value: s, child: Text(s)))
                          .toList(),
                      onChanged: (v) {
                        if (v != null) setState(() => _status = v);
                      },
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 12),
              DropdownButtonFormField<String>(
                initialValue: _method,
                isExpanded: true,
                decoration: const InputDecoration(labelText: 'Method'),
                items: _methodOptions
                    .map((m) => DropdownMenuItem(value: m, child: Text(m)))
                    .toList(),
                onChanged: (v) => setState(() => _method = v),
              ),
              const SizedBox(height: 12),
              TextFormField(
                controller: _referenceCtrl,
                decoration: const InputDecoration(
                  labelText: 'Reference / confirmation #',
                ),
              ),
              const SizedBox(height: 12),
              TextFormField(
                controller: _notesCtrl,
                maxLines: 2,
                decoration: const InputDecoration(labelText: 'Notes'),
              ),
              if (_error != null) ...[
                const SizedBox(height: 12),
                Text(
                  _error!,
                  style: TextStyle(color: cs.error, fontSize: 13),
                ),
              ],
              const SizedBox(height: 20),
              FilledButton(
                onPressed: _saving ? null : _submit,
                child: _saving
                    ? const SizedBox(
                        height: 20,
                        width: 20,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : const Text('Save'),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
