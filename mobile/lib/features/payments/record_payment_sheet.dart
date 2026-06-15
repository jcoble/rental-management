import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/models.dart';
import '../money/money_format.dart';
import 'payments_repository.dart';

/// Remembers the most recently used payment method for the session so the
/// "Record payment" sheet can default to it (smart default — one-tap fast path).
class LastPaymentMethod extends Notifier<String?> {
  @override
  String? build() => null;

  void set(String? method) => state = method;
}

final lastPaymentMethodProvider =
    NotifierProvider<LastPaymentMethod, String?>(LastPaymentMethod.new);

/// Standard payment-method choices offered in the Record-payment sheet. The
/// API stores `method` as free text, so an unrecognised existing value is added
/// to the list dynamically (see [_RecordPaymentSheet._methodOptions]).
const _standardMethods = <String>[
  'Check',
  'Cash',
  'Bank transfer',
  'ACH',
  'Credit card',
  'Money order',
  'Online portal',
  'Other',
];

/// Opens the "Record payment" bottom-sheet for [payment] and marks it paid with
/// the chosen date / method / reference / notes. Returns the updated [Payment]
/// on success (so callers can refresh), or `null` if dismissed.
///
/// The fast path stays one tap: every field is pre-filled with a smart default
/// (date = today, method = last-used, amount = the payment's balance), so the
/// landlord can just hit "Record payment" — or expand the optional fields to
/// capture a reference number or note.
Future<Payment?> showRecordPaymentSheet(
  BuildContext context,
  WidgetRef ref, {
  required Payment payment,
}) {
  return showModalBottomSheet<Payment>(
    context: context,
    isScrollControlled: true,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
    ),
    builder: (_) => _RecordPaymentSheet(payment: payment),
  );
}

class _RecordPaymentSheet extends ConsumerStatefulWidget {
  const _RecordPaymentSheet({required this.payment});

  final Payment payment;

  @override
  ConsumerState<_RecordPaymentSheet> createState() =>
      _RecordPaymentSheetState();
}

class _RecordPaymentSheetState extends ConsumerState<_RecordPaymentSheet> {
  late DateTime _paidDate;
  String? _method;
  final _referenceCtrl = TextEditingController();
  final _notesCtrl = TextEditingController();

  bool _saving = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    _paidDate = DateTime.now();
    final p = widget.payment;
    // Smart default for method: this payment's own method, else the last one
    // the user picked this session.
    _method = (p.method != null && p.method!.isNotEmpty)
        ? p.method
        : ref.read(lastPaymentMethodProvider);
    _referenceCtrl.text = p.externalReference ?? '';
    _notesCtrl.text = p.notes ?? '';
  }

  @override
  void dispose() {
    _referenceCtrl.dispose();
    _notesCtrl.dispose();
    super.dispose();
  }

  /// Method options = the standard list plus the payment's existing method if it
  /// isn't already one of them (so an imported / unusual value still shows).
  List<String> get _methodOptions {
    final options = [..._standardMethods];
    final current = _method;
    if (current != null &&
        current.isNotEmpty &&
        !options.contains(current)) {
      options.insert(0, current);
    }
    return options;
  }

  Future<void> _pickDate() async {
    final now = DateTime.now();
    final picked = await showDatePicker(
      context: context,
      initialDate: _paidDate,
      firstDate: DateTime(now.year - 3),
      lastDate: DateTime(now.year + 1),
    );
    if (picked != null) setState(() => _paidDate = picked);
  }

  Future<void> _submit() async {
    setState(() {
      _saving = true;
      _error = null;
    });
    final reference = _referenceCtrl.text.trim();
    final notes = _notesCtrl.text.trim();
    try {
      final updated =
          await ref.read(paymentsRepositoryProvider).markPaid(
                widget.payment.id,
                paidDate: _paidDate.toIso8601String().split('T').first,
                method: _method,
                externalReference: reference.isEmpty ? null : reference,
                notes: notes.isEmpty ? null : notes,
              );
      // Remember the method so the next payment defaults to it.
      if (_method != null && _method!.isNotEmpty) {
        ref.read(lastPaymentMethodProvider.notifier).set(_method);
      }
      if (mounted) Navigator.of(context).pop(updated);
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
      child: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    'Record payment',
                    style: theme.textTheme.titleLarge
                        ?.copyWith(fontWeight: FontWeight.w700),
                  ),
                ),
                IconButton(
                  icon: const Icon(Icons.close),
                  onPressed: () => Navigator.of(context).pop(),
                ),
              ],
            ),
            const SizedBox(height: 4),
            // Amount being recorded (the payment balance) — shown for
            // confirmation; mark-paid records the full scheduled amount.
            Text(
              moneyFmt(widget.payment.amount),
              style: theme.textTheme.headlineSmall?.copyWith(
                fontWeight: FontWeight.w700,
                color: cs.primary,
              ),
            ),
            if (widget.payment.tenantName != null)
              Text(
                widget.payment.tenantName!,
                style: theme.textTheme.bodyMedium
                    ?.copyWith(color: cs.onSurfaceVariant),
              ),
            const SizedBox(height: 16),

            // Date (defaults to today).
            InkWell(
              onTap: _pickDate,
              borderRadius: BorderRadius.circular(4),
              child: InputDecorator(
                decoration: const InputDecoration(
                  labelText: 'Payment date',
                  suffixIcon: Icon(Icons.calendar_today_outlined, size: 18),
                ),
                child: Text(dateFmt(_paidDate)),
              ),
            ),
            const SizedBox(height: 12),

            // Method (defaults to last-used / this payment's method).
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

            // Reference / confirmation number (optional).
            TextField(
              controller: _referenceCtrl,
              textInputAction: TextInputAction.next,
              decoration: const InputDecoration(
                labelText: 'Reference / confirmation # (optional)',
              ),
            ),
            const SizedBox(height: 12),

            // Notes (optional).
            TextField(
              controller: _notesCtrl,
              maxLines: 2,
              textInputAction: TextInputAction.done,
              decoration: const InputDecoration(
                labelText: 'Notes (optional)',
              ),
            ),

            if (_error != null) ...[
              const SizedBox(height: 12),
              Container(
                padding: const EdgeInsets.symmetric(
                    horizontal: 12, vertical: 10),
                decoration: BoxDecoration(
                  color: cs.errorContainer,
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Text(
                  _error!,
                  style: TextStyle(
                      color: cs.onErrorContainer, fontSize: 13),
                ),
              ),
            ],

            const SizedBox(height: 20),
            FilledButton.icon(
              onPressed: _saving ? null : _submit,
              icon: _saving
                  ? const SizedBox(
                      height: 18,
                      width: 18,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : const Icon(Icons.check_circle_outline),
              label: const Text('Record payment'),
            ),
          ],
        ),
      ),
    );
  }
}
