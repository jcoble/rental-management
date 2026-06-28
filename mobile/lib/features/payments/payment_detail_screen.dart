import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/models.dart';
import '../../core/widgets/tabbed_form_sheet.dart';
import '../home/mobile_domain_navigation.dart';
import '../leases/lease_detail_screen.dart';
import '../leases/leases_repository.dart';
import '../money/money_format.dart';
import '../money/receipt_attachment_repository.dart';
import '../money/receipt_upload_sheet.dart';
import 'payment_lease_labels.dart';
import 'payment_receipt_viewer_screen.dart';
import 'payments_repository.dart';
import 'record_payment_sheet.dart';

/// A single payment, fetched fresh by id so push deep-links and ledger taps can
/// open it without a preloaded model.
final paymentDetailProvider = FutureProvider.autoDispose.family<Payment, int>((
  ref,
  id,
) {
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
    final updated = await showRecordPaymentSheet(
      context,
      ref,
      payment: payment,
    );
    if (updated == null) return;
    ref.invalidate(paymentDetailProvider(payment.id));
    // Keep any lease payments section in sync after an inline record.
    ref.invalidate(leasePaymentsProvider(payment.leaseId));
    messenger
      ..hideCurrentSnackBar()
      ..showSnackBar(const SnackBar(content: Text('Payment recorded.')));
  }

  Future<void> _edit(BuildContext context, WidgetRef ref) async {
    final newLeaseId = await showModalBottomSheet<int?>(
      context: context,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => _EditPaymentSheet(payment: payment),
    );
    if (newLeaseId == null) return;
    ref.invalidate(paymentDetailProvider(payment.id));
    ref.invalidate(leasePaymentsProvider(payment.leaseId));
    if (newLeaseId != payment.leaseId) {
      ref.invalidate(leasePaymentsProvider(newLeaseId));
    }
  }

  Future<void> _uploadReceipt(BuildContext context, WidgetRef ref) async {
    final messenger = ScaffoldMessenger.of(context);
    final uploaded = await showReceiptUploadSheet(
      context: context,
      entityType: ReceiptEntityType.payment,
      entityId: payment.id,
    );
    if (!uploaded) return;
    ref.invalidate(paymentDetailProvider(payment.id));
    ref.invalidate(paymentReceiptProvider(payment.id));
    ref.invalidate(leasePaymentsProvider(payment.leaseId));
    messenger
      ..hideCurrentSnackBar()
      ..showSnackBar(const SnackBar(content: Text('Receipt uploaded.')));
  }

  void _viewReceipt(BuildContext context) {
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => PaymentReceiptViewerScreen(
          paymentId: payment.id,
          isImage: payment.scanIsImage,
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final leaseDisplay = formatPaymentLeaseDisplay(payment);

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
            _chip(
              cs,
              payment.status,
              _statusBg(payment.status, cs),
              _statusFg(payment.status, cs),
            ),
            if (payment.type.isNotEmpty)
              _chip(
                cs,
                payment.type,
                cs.secondaryContainer,
                cs.onSecondaryContainer,
              ),
          ],
        ),
        const SizedBox(height: 20),
        _DetailRow(label: 'Tenant', value: payment.tenantName ?? '—'),
        _DetailRow(
          label: 'Lease',
          value: leaseDisplay.isNotEmpty
              ? leaseDisplay
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
        if (payment.hasScan) ...[
          OutlinedButton.icon(
            onPressed: () => _viewReceipt(context),
            icon: Icon(
              payment.scanIsImage
                  ? Icons.image_outlined
                  : Icons.picture_as_pdf_outlined,
            ),
            label: const Text('View receipt'),
          ),
          const SizedBox(height: 12),
        ],
        OutlinedButton.icon(
          onPressed: () => _uploadReceipt(context, ref),
          icon: const Icon(Icons.upload_file_outlined),
          label: const Text('Upload receipt'),
        ),
        const SizedBox(height: 12),
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
    final shellNavigator = mobileShellNavigatorOf(context);
    if (shellNavigator != null) {
      shellNavigator.openTab(
        MobileShellTabId.rentals,
        destination: MobileDestinationId.units,
        detailBuilder: (_) => LeaseDetailLoaderScreen(leaseId: payment.leaseId),
      );
      revealMobileShellIfDetached(context);
      return;
    }

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
  late final TextEditingController _amountPaidCtrl;
  late final TextEditingController _referenceCtrl;
  late final TextEditingController _notesCtrl;
  late DateTime _dueDate;
  late int? _leaseId;
  late String _type;
  late String _status;
  String? _method;
  bool _saving = false;
  String? _error;

  static const _types = [
    'Rent',
    'SecurityDeposit',
    'LateFee',
    'Utility',
    'Other',
  ];
  static const _statuses = ['Scheduled', 'Paid', 'Partial', 'Late', 'Waived'];

  /// Method dropdown options = the canonical [kPaymentMethods] list (shared with
  /// the Record-payment sheet + web, I2) plus any existing value not in it, so an
  /// imported / scanned / legacy method still shows.
  List<String> get _methodOptions {
    final options = [...kPaymentMethods];
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
    _amountPaidCtrl = TextEditingController(
      text: p.amountPaid != null ? p.amountPaid!.toStringAsFixed(2) : '',
    );
    _referenceCtrl = TextEditingController(text: p.externalReference ?? '');
    _notesCtrl = TextEditingController(text: p.notes ?? '');
    _dueDate = p.dueDate.year > 1 ? p.dueDate : DateTime.now();
    _leaseId = p.leaseId;
    _type = _types.contains(p.type) ? p.type : 'Rent';
    _status = _statuses.contains(p.status) ? p.status : 'Scheduled';
    _method = (p.method != null && p.method!.isNotEmpty) ? p.method : null;
    Future.microtask(() => ref.read(leasesForPaymentProvider.notifier).load());
  }

  @override
  void dispose() {
    _amountCtrl.dispose();
    _amountPaidCtrl.dispose();
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

    final amount = double.tryParse(_amountCtrl.text.trim()) ?? 0;
    // Partial-payment split: the server requires an Amount paid strictly between
    // 0 and the full Amount (mirrors web paymentSchema.superRefine /
    // PaymentService.NormalizeAmountPaid). Surface it inline before submit.
    final isPartial = _status == 'Partial';
    final amountPaid = isPartial
        ? double.tryParse(_amountPaidCtrl.text.trim())
        : null;
    if (isPartial) {
      if (amountPaid == null) {
        setState(
          () => _error = 'Amount paid is required for a partial payment',
        );
        return;
      }
      if (amountPaid <= 0) {
        setState(() => _error = 'Amount paid must be greater than zero');
        return;
      }
      if (amountPaid >= amount) {
        setState(
          () => _error = 'Amount paid must be less than the full amount',
        );
        return;
      }
    }

    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      final reference = _referenceCtrl.text.trim();
      final updated = await ref.read(paymentsRepositoryProvider).updatePayment(
        widget.payment.id,
        {
          'amount': amount,
          // amountPaid only travels for a Partial payment; null for any other
          // status so the server clears a stale collected-so-far when editing
          // away from Partial (matches the web client + server normalization).
          'amountPaid': isPartial ? amountPaid : null,
          'leaseId': _leaseId,
          'dueDate': _dueDate.toIso8601String().split('T').first,
          'type': _type,
          'status': _status,
          if (_method != null) 'method': _method,
          'externalReference': reference,
          'notes': _notesCtrl.text.trim(),
        },
      );
      if (mounted) Navigator.of(context).pop(updated.leaseId);
    } on ApiException catch (e) {
      setState(() => _error = e.message);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final leasesAsync = ref.watch(leasesForPaymentProvider);
    const gap = SizedBox(height: 12);

    return Form(
      key: _formKey,
      child: TabbedFormSheet(
        title: 'Edit payment',
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
                leasesAsync.when(
                  loading: () => const LinearProgressIndicator(),
                  error: (e, _) => Text(
                    'Could not load leases: ${e is ApiException ? e.message : e}',
                    style: TextStyle(color: cs.error, fontSize: 13),
                  ),
                  data: (leases) {
                    final paymentLeaseLabel = formatPaymentLeaseDisplay(
                      widget.payment,
                    );
                    final items = [
                      if (_leaseId != null &&
                          leases.every((lease) => lease.id != _leaseId))
                        DropdownMenuItem<int>(
                          value: _leaseId,
                          child: Text(
                            paymentLeaseLabel.isNotEmpty
                                ? paymentLeaseLabel
                                : 'Lease #$_leaseId',
                            overflow: TextOverflow.ellipsis,
                          ),
                        ),
                      for (final lease in leases)
                        DropdownMenuItem<int>(
                          value: lease.id,
                          child: Text(
                            formatLeasePickerLabel(lease),
                            overflow: TextOverflow.ellipsis,
                          ),
                        ),
                    ];
                    return DropdownButtonFormField<int>(
                      initialValue: _leaseId,
                      isExpanded: true,
                      decoration: const InputDecoration(labelText: 'Lease'),
                      items: items,
                      onChanged: (value) => setState(() => _leaseId = value),
                      validator: (value) =>
                          value == null ? 'Please select a lease' : null,
                    );
                  },
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
                gap,
                DropdownButtonFormField<String>(
                  initialValue: _type,
                  decoration: const InputDecoration(labelText: 'Type'),
                  items: _types
                      .map((t) => DropdownMenuItem(value: t, child: Text(t)))
                      .toList(),
                  onChanged: (v) {
                    if (v != null) setState(() => _type = v);
                  },
                ),
              ],
            ),
          ),
          TabbedFormStepSpec(
            label: 'Settlement',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                DropdownButtonFormField<String>(
                  initialValue: _status,
                  decoration: const InputDecoration(labelText: 'Status'),
                  items: _statuses
                      .map((s) => DropdownMenuItem(value: s, child: Text(s)))
                      .toList(),
                  onChanged: (v) {
                    if (v != null) setState(() => _status = v);
                  },
                ),
                if (_status == 'Partial') ...[
                  gap,
                  TextFormField(
                    controller: _amountPaidCtrl,
                    keyboardType: const TextInputType.numberWithOptions(
                      decimal: true,
                    ),
                    decoration: const InputDecoration(
                      labelText: 'Amount paid (so far)',
                      prefixText: '\$',
                      helperText:
                          'How much was collected. The rest stays owed.',
                    ),
                  ),
                ],
                gap,
                DropdownButtonFormField<String>(
                  initialValue: _method,
                  isExpanded: true,
                  decoration: const InputDecoration(labelText: 'Method'),
                  items: _methodOptions
                      .map((m) => DropdownMenuItem(value: m, child: Text(m)))
                      .toList(),
                  onChanged: (v) => setState(() => _method = v),
                ),
                gap,
                TextFormField(
                  controller: _referenceCtrl,
                  decoration: const InputDecoration(
                    labelText: 'Reference / confirmation #',
                  ),
                ),
              ],
            ),
          ),
          TabbedFormStepSpec(
            label: 'Notes',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
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
