import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/models.dart';
import '../activity/activity_history_screen.dart';
import '../money/money_format.dart';
import 'payments_repository.dart';

final paymentDetailProvider = FutureProvider.autoDispose
    .family<PaymentReceipt, int>((ref, id) {
      return ref.read(paymentsRepositoryProvider).getPayment(id);
    });

/// Read-only detail for one immutable tenant-account receipt.
class PaymentDetailScreen extends ConsumerWidget {
  const PaymentDetailScreen({super.key, required this.paymentId});

  final int paymentId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(paymentDetailProvider(paymentId));
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
                  entityId: paymentId,
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
          onRetry: () => ref.invalidate(paymentDetailProvider(paymentId)),
        ),
        data: (receipt) => _ReceiptBody(receipt: receipt),
      ),
    );
  }
}

class _ReceiptBody extends StatelessWidget {
  const _ReceiptBody({required this.receipt});

  final PaymentReceipt receipt;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final home = [
      receipt.propertyName,
      if (receipt.unitNumber?.trim().isNotEmpty == true)
        'Unit ${receipt.unitNumber}',
    ].whereType<String>().where((value) => value.trim().isNotEmpty).join(' · ');

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
        _DetailRow(label: 'Received', value: dateFmt(receipt.receivedOn)),
        _DetailRow(
          label: 'Tenant',
          value: receipt.tenantName?.trim().isNotEmpty == true
              ? receipt.tenantName!
              : '—',
        ),
        _DetailRow(label: 'Rental', value: home.isEmpty ? '—' : home),
        _DetailRow(label: 'Description', value: receipt.description),
        _DetailRow(label: 'Account', value: receipt.accountNumber),
        _DetailRow(label: 'Relationship', value: receipt.relationshipNumber),
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
        const SizedBox(height: 20),
        Text(
          'This receipt is an immutable posted record. Corrections are made '
          'with a separate reversal or adjustment.',
          style: theme.textTheme.bodySmall?.copyWith(
            color: cs.onSurfaceVariant,
          ),
        ),
      ],
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
