import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import 'leases_repository.dart';
import '../../core/presentation/formatting.dart';

/// A self-contained, scrollable "account history" for one lease: the running
/// totals card followed by every charge/payment with its plain-English "why".
///
/// Used both on the tenant portal (transparency) and the landlord lease detail.
class LeaseLedgerView extends ConsumerWidget {
  const LeaseLedgerView({super.key, required this.leaseManagementId});

  final int leaseManagementId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final ledgerAsync = ref.watch(leaseLedgerProvider(leaseManagementId));

    return RefreshIndicator(
      onRefresh: () async =>
          ref.invalidate(leaseLedgerProvider(leaseManagementId)),
      child: ledgerAsync.when(
        loading: () => ListView(
          children: const [
            SizedBox(height: 120),
            Center(child: CircularProgressIndicator()),
          ],
        ),
        error: (err, _) => ListView(
          padding: const EdgeInsets.all(20),
          children: [
            const SizedBox(height: 40),
            Icon(
              Icons.receipt_long_outlined,
              size: 40,
              color: theme.colorScheme.onSurfaceVariant,
            ),
            const SizedBox(height: 12),
            Text(
              "Couldn't load account history.",
              textAlign: TextAlign.center,
              style: theme.textTheme.titleMedium,
            ),
            const SizedBox(height: 6),
            Text(
              err is ApiException ? err.message : 'Please try again.',
              textAlign: TextAlign.center,
              style: theme.textTheme.bodySmall?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 16),
            Center(
              child: FilledButton.tonal(
                onPressed: () =>
                    ref.invalidate(leaseLedgerProvider(leaseManagementId)),
                child: const Text('Retry'),
              ),
            ),
          ],
        ),
        data: (ledger) => ListView(
          padding: const EdgeInsets.all(16),
          children: [
            _BalanceCard(ledger: ledger),
            const SizedBox(height: 16),
            Text(
              'Account history',
              style: theme.textTheme.titleSmall?.copyWith(
                fontWeight: FontWeight.w700,
              ),
            ),
            const SizedBox(height: 8),
            if (ledger.entries.isEmpty)
              Card(
                child: Padding(
                  padding: const EdgeInsets.all(16),
                  child: Row(
                    children: [
                      Icon(
                        Icons.check_circle_outline,
                        color: theme.colorScheme.onSurfaceVariant,
                        size: 20,
                      ),
                      const SizedBox(width: 12),
                      const Expanded(
                        child: Text('No charges or payments yet.'),
                      ),
                    ],
                  ),
                ),
              )
            else
              for (final entry in ledger.entries) ...[
                _LedgerEntryCard(entry: entry),
                const SizedBox(height: 8),
              ],
            const SizedBox(height: 24),
          ],
        ),
      ),
    );
  }
}

class _BalanceCard extends StatelessWidget {
  const _BalanceCard({required this.ledger});

  final LeaseManagementLedger ledger;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final owes = ledger.balance > 0;
    final credit = ledger.balance < 0;

    final String balanceLabel;
    final Color balanceColor;
    if (credit) {
      balanceLabel = 'Credit';
      balanceColor = Colors.green.shade700;
    } else if (owes) {
      balanceLabel = 'Balance due';
      balanceColor = cs.error;
    } else {
      balanceLabel = 'Balance';
      balanceColor = Colors.green.shade700;
    }

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              balanceLabel,
              style: theme.textTheme.labelLarge?.copyWith(
                color: cs.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 2),
            Text(
              owes || credit ? moneyFmt(ledger.balance.abs()) : moneyFmt(0),
              style: theme.textTheme.headlineMedium?.copyWith(
                fontFeatures: const [FontFeature.tabularFigures()],
                fontWeight: FontWeight.w700,
                color: balanceColor,
              ),
            ),
            const SizedBox(height: 12),
            const Divider(height: 1),
            const SizedBox(height: 12),
            Row(
              children: [
                Expanded(
                  child: _Total(
                    label: 'Charged',
                    value: moneyFmt(ledger.totalCharged),
                  ),
                ),
                Expanded(
                  child: _Total(
                    label: 'Paid',
                    value: moneyFmt(ledger.totalPaid),
                  ),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

class _Total extends StatelessWidget {
  const _Total({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          label,
          style: theme.textTheme.bodySmall?.copyWith(
            color: theme.colorScheme.onSurfaceVariant,
          ),
        ),
        const SizedBox(height: 2),
        Text(
          value,
          style: theme.textTheme.titleMedium?.copyWith(
            fontFeatures: const [FontFeature.tabularFigures()],
            fontWeight: FontWeight.w700,
          ),
        ),
      ],
    );
  }
}

class _LedgerEntryCard extends StatelessWidget {
  const _LedgerEntryCard({required this.entry});

  final LeaseLedgerEntry entry;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    // A payment reduces what's owed; show it green and signed.
    final isPayment = entry.type.toLowerCase() == 'payment';
    final amountColor = isPayment ? Colors.green.shade700 : cs.onSurface;
    final signedAmount =
        '${isPayment ? '-' : ''}${moneyFmt(entry.amount.abs())}';

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(14),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Container(
                  padding: const EdgeInsets.all(6),
                  decoration: BoxDecoration(
                    color: isPayment
                        ? Colors.green.shade50
                        : cs.surfaceContainerHighest,
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: Icon(
                    isPayment
                        ? Icons.payments_outlined
                        : Icons.request_quote_outlined,
                    size: 16,
                    color: isPayment ? Colors.green.shade700 : cs.primary,
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        entry.description.isEmpty
                            ? entry.type
                            : entry.description,
                        style: theme.textTheme.bodyMedium?.copyWith(
                          fontWeight: FontWeight.w600,
                        ),
                      ),
                      if (entry.isProrated) ...[
                        const SizedBox(height: 4),
                        Align(
                          alignment: Alignment.centerLeft,
                          child: Container(
                            padding: const EdgeInsets.symmetric(
                              horizontal: 8,
                              vertical: 3,
                            ),
                            decoration: BoxDecoration(
                              color: cs.primaryContainer,
                              borderRadius: BorderRadius.circular(999),
                            ),
                            child: Text(
                              'Prorated',
                              style: theme.textTheme.labelSmall?.copyWith(
                                color: cs.onPrimaryContainer,
                                fontWeight: FontWeight.w700,
                              ),
                            ),
                          ),
                        ),
                      ],
                      if (dateFmt(entry.date).isNotEmpty) ...[
                        const SizedBox(height: 2),
                        Text(
                          [
                            dateFmt(entry.date),
                            if (entry.status.isNotEmpty) entry.status,
                          ].join(' · '),
                          style: theme.textTheme.bodySmall?.copyWith(
                            color: cs.onSurfaceVariant,
                          ),
                        ),
                      ],
                    ],
                  ),
                ),
                const SizedBox(width: 8),
                Text(
                  signedAmount,
                  style: theme.textTheme.titleMedium?.copyWith(
                    fontFeatures: const [FontFeature.tabularFigures()],
                    fontWeight: FontWeight.w700,
                    color: amountColor,
                  ),
                ),
              ],
            ),
            if (entry.explanation.isNotEmpty) ...[
              const SizedBox(height: 10),
              Container(
                padding: const EdgeInsets.all(10),
                decoration: BoxDecoration(
                  color: cs.surfaceContainerHighest,
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Icon(
                      Icons.info_outline,
                      size: 15,
                      color: cs.onSurfaceVariant,
                    ),
                    const SizedBox(width: 8),
                    Expanded(
                      child: Text(
                        entry.explanation,
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: cs.onSurface,
                          height: 1.4,
                        ),
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }
}
