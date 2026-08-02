import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/auth/auth_controller.dart';
import '../../core/auth/auth_models.dart';
import '../../core/auth/mobile_access_policy.dart';
import '../accounting/accounting_book_models.dart';
import '../accounting/accounting_impact_card.dart';
import '../activity/activity_history_screen.dart';
import '../home/mobile_quick_action_fab.dart';
import '../money/money_format.dart';
import 'payments_repository.dart';

final paymentDetailProvider = FutureProvider.autoDispose
    .family<StaffTenantLedgerEntryDetail, ({int accountId, int entryId})>((
      ref,
      key,
    ) {
      return ref
          .read(paymentsRepositoryProvider)
          .getTenantLedgerEntry(key.accountId, key.entryId);
    });

/// Read-only detail for one permanent tenant-account receipt.
class PaymentDetailScreen extends ConsumerWidget {
  const PaymentDetailScreen({
    super.key,
    required this.tenantAccountId,
    required this.tenantLedgerEntryId,
  });

  final int tenantAccountId;
  final int tenantLedgerEntryId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final key = (accountId: tenantAccountId, entryId: tenantLedgerEntryId);
    final async = ref.watch(paymentDetailProvider(key));
    final auth = ref.watch(authControllerProvider);
    final canCorrect =
        auth is AuthStateAuthenticated &&
        canUseMobileCapabilityAction(
          experience: auth.activeExperience,
          capabilities: auth.capabilities,
          capability: 'money.payments.manage',
          experiences: const {WorkspaceExperience.management},
        );
    return Scaffold(
      appBar: AppBar(
        title: const Text('Receipt'),
        actions: [
          IconButton(
            icon: const Icon(Icons.history_outlined),
            tooltip: 'View receipt activity',
            onPressed: () => Navigator.of(context).push<void>(
              MaterialPageRoute<void>(
                builder: (_) => ActivityHistoryScreen(
                  entityType: 'TenantLedgerEntry',
                  entityId: tenantLedgerEntryId,
                  title: 'Receipt activity',
                ),
              ),
            ),
          ),
        ],
      ),
      body: async.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (error, _) => _ErrorView(
          message: error is ApiException ? error.message : error.toString(),
          onRetry: () => ref.invalidate(paymentDetailProvider(key)),
        ),
        data: (receipt) => _ReceiptBody(
          receipt: receipt,
          onCorrect: canCorrect && receipt.entryType == 'PaymentReceipt'
              ? () => showModalBottomSheet<void>(
                  context: context,
                  isScrollControlled: true,
                  builder: (_) => _PaymentCorrectionSheet(receipt: receipt),
                )
              : null,
        ),
      ),
    );
  }
}

class _ReceiptBody extends StatelessWidget {
  const _ReceiptBody({required this.receipt, this.onCorrect});

  final StaffTenantLedgerEntryDetail receipt;
  final VoidCallback? onCorrect;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final home = [
      receipt.propertyName,
      if (receipt.unitNumber.trim().isNotEmpty) 'Unit ${receipt.unitNumber}',
    ].where((value) => value.trim().isNotEmpty).join(' · ');

    return ListView(
      padding: const EdgeInsets.fromLTRB(20, 20, 20, 32),
      children: [
        Text(
          moneyFmt(receipt.amount),
          style: theme.textTheme.displaySmall?.copyWith(
            fontWeight: FontWeight.w700,
          ),
        ),
        const SizedBox(height: 8),
        Align(
          alignment: Alignment.centerLeft,
          child: Chip(
            avatar: Icon(Icons.check_circle_outline, color: cs.primary),
            label: const Text('Received'),
          ),
        ),
        const SizedBox(height: 20),
        _DetailRow(label: 'Received', value: dateFmt(receipt.effectiveOn)),
        _DetailRow(
          label: 'Tenant',
          value: receipt.primaryTenantName?.trim().isNotEmpty == true
              ? receipt.primaryTenantName!
              : '—',
        ),
        _DetailRow(label: 'Rental', value: home.isEmpty ? '—' : home),
        _DetailRow(label: 'Description', value: receipt.description),
        _DetailRow(label: 'Account number', value: receipt.accountNumber),
        _DetailRow(label: 'Lease number', value: receipt.relationshipNumber),
        if (receipt.paymentMethodSummary?.trim().isNotEmpty == true)
          _DetailRow(label: 'Method', value: receipt.paymentMethodSummary!),
        if (receipt.providerReference?.trim().isNotEmpty == true)
          _DetailRow(label: 'Reference', value: receipt.providerReference!),
        if (receipt.payerName?.trim().isNotEmpty == true)
          _DetailRow(label: 'Payer', value: receipt.payerName!),
        if (receipt.checkNumber?.trim().isNotEmpty == true)
          _DetailRow(label: 'Check', value: receipt.checkNumber!),
        if (receipt.bankName?.trim().isNotEmpty == true)
          _DetailRow(label: 'Bank', value: receipt.bankName!),
        AccountingImpactCard(
          sourceType: JournalSourceType.tenantReceipt,
          sourceId: receipt.tenantLedgerEntryId,
        ),
        const SizedBox(height: 20),
        Text(
          'This receipt is a permanent account record. If it needs a '
          'correction, Rental Command creates a linked refund and keeps both '
          'records in the account history.',
          style: theme.textTheme.bodySmall?.copyWith(
            color: cs.onSurfaceVariant,
          ),
        ),
        if (onCorrect != null) ...[
          const SizedBox(height: 16),
          FilledButton.tonalIcon(
            key: const Key('correct-payment-action'),
            icon: const Icon(Icons.undo_rounded),
            label: const Text('Correct payment'),
            onPressed: onCorrect,
          ),
        ],
      ],
    );
  }
}

class _PaymentCorrectionSheet extends ConsumerStatefulWidget {
  const _PaymentCorrectionSheet({required this.receipt});

  final StaffTenantLedgerEntryDetail receipt;

  @override
  ConsumerState<_PaymentCorrectionSheet> createState() =>
      _PaymentCorrectionSheetState();
}

class _PaymentCorrectionSheetState
    extends ConsumerState<_PaymentCorrectionSheet> {
  late final TextEditingController _reason;
  late final TextEditingController _method;
  late final TextEditingController _paymentReference;
  DateTime _effectiveOn = DateTime.now();
  bool _submitting = false;
  String? _error;
  CorrectTenantPaymentResult? _result;

  @override
  void initState() {
    super.initState();
    final receipt = widget.receipt;
    _reason = TextEditingController(
      text: 'Correction for ${receipt.description}',
    );
    _method = TextEditingController(
      text: receipt.paymentMethodSummary?.trim() ?? '',
    );
    _paymentReference = TextEditingController(
      text: receipt.providerReference?.trim() ?? '',
    );
  }

  @override
  void dispose() {
    _reason.dispose();
    _method.dispose();
    _paymentReference.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (_reason.text.trim().isEmpty ||
        _method.text.trim().isEmpty ||
        _paymentReference.text.trim().isEmpty) {
      setState(
        () => _error =
            'Reason, payment method, and payment reference are required.',
      );
      return;
    }
    setState(() {
      _submitting = true;
      _error = null;
      _result = null;
    });
    try {
      final result = await ref
          .read(paymentsRepositoryProvider)
          .correctPayment(
            widget.receipt.tenantAccountId,
            CorrectTenantPaymentInput(
              paymentEntryId: widget.receipt.tenantLedgerEntryId,
              effectiveOn: _effectiveOn,
              reason: _reason.text,
              paymentMethodSummary: _method.text,
              externalReference: _paymentReference.text,
              sourceStoredFileId: widget.receipt.sourceStoredFileId,
            ),
          );
      if (!mounted) return;
      setState(() => _result = result);
      ref.invalidate(
        paymentDetailProvider((
          accountId: widget.receipt.tenantAccountId,
          entryId: widget.receipt.tenantLedgerEntryId,
        )),
      );
    } on ApiException catch (error) {
      if (!mounted) return;
      setState(() {
        _error = '${error.message} Nothing was changed.';
      });
    } finally {
      if (mounted) setState(() => _submitting = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final receipt = widget.receipt;
    final tenant = receipt.primaryTenantName?.trim().isNotEmpty == true
        ? receipt.primaryTenantName!
        : 'Tenant not named';
    return MobileQuickActionHider(
      child: SafeArea(
        child: SingleChildScrollView(
          padding: EdgeInsets.fromLTRB(
            20,
            20,
            20,
            MediaQuery.viewInsetsOf(context).bottom + 24,
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            mainAxisSize: MainAxisSize.min,
            children: [
              Text(
                'Correct payment',
                style: Theme.of(context).textTheme.titleLarge,
              ),
              const SizedBox(height: 6),
              const Text(
                'The original receipt stays in the account history. Saving '
                'this correction creates a linked refund.',
              ),
              const SizedBox(height: 16),
              _DetailRow(label: 'Account number', value: receipt.accountNumber),
              _DetailRow(
                label: 'Unit',
                value: '${receipt.propertyName} · Unit ${receipt.unitNumber}',
              ),
              _DetailRow(label: 'Tenant', value: tenant),
              _DetailRow(label: 'Payment', value: moneyFmt(receipt.amount)),
              const SizedBox(height: 12),
              TextField(
                controller: _reason,
                decoration: const InputDecoration(labelText: 'Reason'),
              ),
              const SizedBox(height: 12),
              TextField(
                controller: _method,
                decoration: const InputDecoration(labelText: 'Payment method'),
              ),
              const SizedBox(height: 12),
              TextField(
                controller: _paymentReference,
                decoration: const InputDecoration(
                  labelText: 'Payment reference',
                ),
              ),
              const SizedBox(height: 12),
              OutlinedButton(
                onPressed: () async {
                  final selected = await showDatePicker(
                    context: context,
                    firstDate: DateTime(2000),
                    lastDate: DateTime(2100),
                    initialDate: _effectiveOn,
                  );
                  if (selected != null) setState(() => _effectiveOn = selected);
                },
                child: Text('Correction date · ${dateFmt(_effectiveOn)}'),
              ),
              if (_error != null) ...[
                const SizedBox(height: 12),
                Text(
                  _error!,
                  key: const Key('payment-correction-conflict'),
                  style: TextStyle(color: Theme.of(context).colorScheme.error),
                ),
              ],
              if (_result != null) ...[
                const SizedBox(height: 12),
                Text(
                  'Refund recorded. '
                  '${_result!.compensatedAllocationCount == 1 ? '1 related balance was updated.' : '${_result!.compensatedAllocationCount} related balances were updated.'}',
                  key: const Key('payment-correction-result'),
                ),
              ],
              const SizedBox(height: 16),
              FilledButton(
                key: const Key('payment-correction-submit'),
                onPressed: _submitting ? null : _submit,
                child: Text(_submitting ? 'Correcting…' : 'Append correction'),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _DetailRow extends StatelessWidget {
  const _DetailRow({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(
            width: 108,
            child: Text(
              label,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
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
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const Icon(Icons.error_outline, size: 40),
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
