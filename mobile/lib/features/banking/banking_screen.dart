import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/auth/auth_controller.dart';
import '../home/mobile_domain_chrome.dart';
import 'banking_models.dart';
import 'banking_repository.dart';

class BankingScreen extends ConsumerWidget {
  const BankingScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final authState = ref.watch(authControllerProvider);
    final capabilities = authState is AuthStateAuthenticated
        ? authState.capabilities
        : const <String>{};
    final canManageConnections = capabilities.contains(
      'bank-connections.manage',
    );
    final canOperate = capabilities.contains('money.reconciliation.operate');
    final canDestructivelyReconcile = capabilities.contains(
      'money.reconciliation.destructive',
    );
    final summaryAsync = canManageConnections
        ? ref.watch(bankingSummaryProvider)
        : null;
    final reviewAsync = canOperate
        ? ref.watch(bankingReviewQueueProvider)
        : null;
    final transactionsAsync = canManageConnections
        ? ref.watch(bankingTransactionsProvider)
        : null;
    final routingPropertiesAsync = canManageConnections
        ? ref.watch(bankingRoutingPropertiesProvider)
        : null;

    Future<void> refresh() async {
      if (canManageConnections) ref.invalidate(bankingSummaryProvider);
      if (canOperate) ref.invalidate(bankingReviewQueueProvider);
      if (canManageConnections) ref.invalidate(bankingTransactionsProvider);
      if (canManageConnections)
        ref.invalidate(bankingRoutingPropertiesProvider);
    }

    return Scaffold(
      appBar: mobileDomainRootAppBar(context, title: const Text('Banking')),
      body: RefreshIndicator(
        onRefresh: refresh,
        child: ListView(
          physics: const AlwaysScrollableScrollPhysics(),
          padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
          children: [
            if (canManageConnections) ...[
              summaryAsync!.when(
                loading: () =>
                    const _LoadingCard(label: 'Loading banking summary...'),
                error: (e, _) => _ErrorCard(message: _message(e)),
                data: (summary) => _SummaryGrid(summary: summary),
              ),
              const SizedBox(height: 18),
            ],
            if (canOperate)
              reviewAsync!.when(
                loading: () => const _LoadingCard(
                  label: 'Checking for possible duplicates...',
                ),
                error: (e, _) => Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    _SectionHeader(title: 'Review suggested matches'),
                    const SizedBox(height: 8),
                    _ErrorCard(message: _message(e)),
                  ],
                ),
                data: (queue) {
                  if (queue.items.isEmpty) {
                    return Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        _SectionHeader(title: 'Review suggested matches'),
                        const SizedBox(height: 8),
                        const _ReviewEmptyCard(),
                      ],
                    );
                  }
                  return Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      _SectionHeader(
                        title: 'Review suggested matches',
                        count: queue.count,
                      ),
                      const SizedBox(height: 4),
                      Text(
                        'These bank lines look like money already on record. '
                        'Confirm so we don\'t count it twice.',
                        style: Theme.of(context).textTheme.bodySmall?.copyWith(
                          color: Theme.of(context).colorScheme.onSurfaceVariant,
                        ),
                      ),
                      const SizedBox(height: 8),
                      for (final item in queue.items) ...[
                        _ReviewCard(
                          item: item,
                          canDismiss: canDestructivelyReconcile,
                        ),
                        const SizedBox(height: 8),
                      ],
                    ],
                  );
                },
              ),
            if (canManageConnections) ...[
              const SizedBox(height: 18),
              Text(
                'Recent bank lines',
                style: Theme.of(
                  context,
                ).textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w700),
              ),
              const SizedBox(height: 8),
              transactionsAsync!.when(
                loading: () =>
                    const _LoadingCard(label: 'Loading transactions...'),
                error: (e, _) => _ErrorCard(message: _message(e)),
                data: (transactions) {
                  if (transactions.isEmpty) {
                    return const _EmptyCard();
                  }
                  return Column(
                    children: [
                      for (final transaction in transactions) ...[
                        _TransactionCard(
                          transaction: transaction,
                          routingProperties:
                              routingPropertiesAsync?.value ?? const [],
                          canOperate: canOperate,
                          canClear: canDestructivelyReconcile,
                        ),
                        const SizedBox(height: 8),
                      ],
                    ],
                  );
                },
              ),
            ],
          ],
        ),
      ),
    );
  }

  static String _message(Object e) =>
      e is ApiException ? e.message : e.toString();
}

class _SummaryGrid extends StatelessWidget {
  const _SummaryGrid({required this.summary});

  final BankingSummary summary;

  @override
  Widget build(BuildContext context) {
    return GridView.count(
      crossAxisCount: 2,
      crossAxisSpacing: 10,
      mainAxisSpacing: 10,
      shrinkWrap: true,
      physics: const NeverScrollableScrollPhysics(),
      childAspectRatio: 1.65,
      children: [
        _MetricCard(label: 'Connections', value: '${summary.connectionCount}'),
        _MetricCard(
          label: 'Transactions',
          value: '${summary.transactionCount}',
        ),
        _MetricCard(label: 'Unmatched', value: '${summary.unmatchedCount}'),
        _MetricCard(
          label: 'Suggestions',
          value: '${summary.suggestedMatchCount}',
        ),
      ],
    );
  }
}

class _MetricCard extends StatelessWidget {
  const _MetricCard({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(14),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Text(label, style: theme.textTheme.bodySmall),
            const SizedBox(height: 4),
            Text(
              value,
              style: theme.textTheme.headlineSmall?.copyWith(
                fontWeight: FontWeight.w700,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _TransactionCard extends ConsumerWidget {
  const _TransactionCard({
    required this.transaction,
    required this.canOperate,
    required this.canClear,
    required this.routingProperties,
  });

  final BankTransaction transaction;
  final bool canOperate;
  final bool canClear;
  final List<BankRoutingProperty> routingProperties;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final suggestion = transaction.suggestedMatch;

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(14),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Expanded(
                  child: Text(
                    transaction.merchantName ?? transaction.description,
                    style: theme.textTheme.titleSmall?.copyWith(
                      fontWeight: FontWeight.w600,
                    ),
                  ),
                ),
                Text(
                  _money(transaction.amount),
                  style: theme.textTheme.titleSmall?.copyWith(
                    color: transaction.amount >= 0
                        ? Colors.green.shade700
                        : cs.error,
                    fontWeight: FontWeight.w700,
                  ),
                ),
              ],
            ),
            const SizedBox(height: 4),
            Text(
              '${transaction.institutionName} / ${transaction.accountName} / ${_date(transaction.postedAt)}',
              style: theme.textTheme.bodySmall?.copyWith(
                color: cs.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 10),
            DropdownButtonFormField<int?>(
              initialValue: transaction.propertyId,
              decoration: const InputDecoration(
                labelText: 'Property route',
                helperText: 'Only assigned managers can review routed lines.',
                border: OutlineInputBorder(),
                isDense: true,
              ),
              items: [
                const DropdownMenuItem<int?>(
                  value: null,
                  child: Text('Unassigned · admin only'),
                ),
                for (final property in routingProperties)
                  DropdownMenuItem<int?>(
                    value: property.id,
                    child: Text(property.name),
                  ),
              ],
              onChanged: (propertyId) async {
                try {
                  await ref
                      .read(bankingRepositoryProvider)
                      .routeTransaction(transaction, propertyId);
                  ref.invalidate(bankingSummaryProvider);
                  ref.invalidate(bankingTransactionsProvider);
                  ref.invalidate(bankingReviewQueueProvider);
                } catch (e) {
                  if (context.mounted) {
                    ScaffoldMessenger.of(context).showSnackBar(
                      SnackBar(content: Text(BankingScreen._message(e))),
                    );
                  }
                }
              },
            ),
            const SizedBox(height: 10),
            if (transaction.matchStatus == 'Matched')
              Row(
                children: [
                  const _StatusPill(label: 'Matched'),
                  const SizedBox(width: 8),
                  if (canClear)
                    TextButton(
                      onPressed: () async {
                        try {
                          await ref
                              .read(bankingRepositoryProvider)
                              .clearMatch(transaction);
                          ref.invalidate(bankingSummaryProvider);
                          ref.invalidate(bankingTransactionsProvider);
                          if (context.mounted) {
                            ScaffoldMessenger.of(context).showSnackBar(
                              const SnackBar(content: Text('Match cleared.')),
                            );
                          }
                        } catch (e) {
                          if (context.mounted) {
                            ScaffoldMessenger.of(context).showSnackBar(
                              SnackBar(
                                content: Text(BankingScreen._message(e)),
                              ),
                            );
                          }
                        }
                      },
                      child: const Text('Clear'),
                    ),
                ],
              )
            else if (suggestion != null && canOperate) ...[
              Text(
                suggestion.reason,
                style: theme.textTheme.bodySmall?.copyWith(
                  color: cs.onSurfaceVariant,
                ),
              ),
              const SizedBox(height: 8),
              FilledButton.tonal(
                onPressed: () async {
                  await ref.read(bankingRepositoryProvider).match(transaction);
                  ref.invalidate(bankingSummaryProvider);
                  ref.invalidate(bankingTransactionsProvider);
                },
                child: Text('Match ${suggestion.label}'),
              ),
            ] else
              Text(
                'No match suggestion yet',
                style: theme.textTheme.bodySmall?.copyWith(
                  color: cs.onSurfaceVariant,
                ),
              ),
          ],
        ),
      ),
    );
  }

  static String _money(double value) {
    final sign = value < 0 ? '-' : '';
    return '$sign\$${value.abs().toStringAsFixed(2)}';
  }

  static String _date(DateTime value) =>
      '${value.month}/${value.day}/${value.year}';
}

class _SectionHeader extends StatelessWidget {
  const _SectionHeader({required this.title, this.count});

  final String title;
  final int? count;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Row(
      children: [
        Text(
          title,
          style: theme.textTheme.titleMedium?.copyWith(
            fontWeight: FontWeight.w700,
          ),
        ),
        if (count != null && count! > 0) ...[
          const SizedBox(width: 8),
          Container(
            padding: const EdgeInsets.symmetric(horizontal: 9, vertical: 2),
            decoration: BoxDecoration(
              color: theme.colorScheme.primary.withValues(alpha: 0.14),
              borderRadius: BorderRadius.circular(999),
            ),
            child: Text(
              '$count',
              style: theme.textTheme.labelSmall?.copyWith(
                color: theme.colorScheme.primary,
                fontWeight: FontWeight.w700,
              ),
            ),
          ),
        ],
      ],
    );
  }
}

class _ReviewCard extends ConsumerWidget {
  const _ReviewCard({required this.item, required this.canDismiss});

  final BankReviewItem item;
  final bool canDismiss;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final transaction = item.transaction;
    final suggestion = item.suggestion;
    final confidencePct = (suggestion.confidence * 100).round();

    Future<void> run(Future<void> Function() action, String done) async {
      try {
        await action();
        ref.invalidate(bankingSummaryProvider);
        ref.invalidate(bankingReviewQueueProvider);
        ref.invalidate(bankingTransactionsProvider);
        if (context.mounted) {
          ScaffoldMessenger.of(
            context,
          ).showSnackBar(SnackBar(content: Text(done)));
        }
      } catch (e) {
        if (context.mounted) {
          ScaffoldMessenger.of(
            context,
          ).showSnackBar(SnackBar(content: Text(BankingScreen._message(e))));
        }
      }
    }

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(14),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            // Bank line
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Expanded(
                  child: Text(
                    transaction.merchantName?.isNotEmpty == true
                        ? transaction.merchantName!
                        : transaction.description,
                    style: theme.textTheme.titleSmall?.copyWith(
                      fontWeight: FontWeight.w600,
                    ),
                  ),
                ),
                Text(
                  _money(transaction.amount),
                  style: theme.textTheme.titleSmall?.copyWith(
                    color: transaction.amount >= 0
                        ? Colors.green.shade700
                        : cs.error,
                    fontWeight: FontWeight.w700,
                  ),
                ),
              ],
            ),
            const SizedBox(height: 2),
            Text(
              'Bank line / ${_date(transaction.postedAt)}',
              style: theme.textTheme.bodySmall?.copyWith(
                color: cs.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 10),
            // Suggested match
            Container(
              padding: const EdgeInsets.all(10),
              decoration: BoxDecoration(
                color: cs.surfaceContainerHighest.withValues(alpha: 0.5),
                borderRadius: BorderRadius.circular(10),
              ),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Expanded(
                        child: Text(
                          'Looks like: ${suggestion.label}',
                          style: theme.textTheme.bodyMedium?.copyWith(
                            fontWeight: FontWeight.w600,
                          ),
                        ),
                      ),
                      const SizedBox(width: 8),
                      _StatusPill(label: '$confidencePct% sure'),
                    ],
                  ),
                  if (suggestion.reason.isNotEmpty) ...[
                    const SizedBox(height: 4),
                    Text(
                      suggestion.reason,
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: cs.onSurfaceVariant,
                      ),
                    ),
                  ],
                ],
              ),
            ),
            const SizedBox(height: 12),
            Row(
              children: [
                Expanded(
                  child: FilledButton(
                    onPressed: () => run(
                      () => ref
                          .read(bankingRepositoryProvider)
                          .confirmMatch(
                            transaction.id,
                            expectedUpdatedAt: transaction.updatedAt,
                          ),
                      'Confirmed. We won\'t count it twice.',
                    ),
                    child: const Text('Confirm'),
                  ),
                ),
                if (canDismiss) ...[
                  const SizedBox(width: 10),
                  Expanded(
                    child: OutlinedButton(
                      onPressed: () => run(
                        () => ref
                            .read(bankingRepositoryProvider)
                            .dismissMatch(
                              transaction.id,
                              transaction.updatedAt,
                            ),
                        'Kept as a separate bank line.',
                      ),
                      child: const Text('Not a match'),
                    ),
                  ),
                ],
              ],
            ),
          ],
        ),
      ),
    );
  }

  static String _money(double value) {
    final sign = value < 0 ? '-' : '';
    return '$sign\$${value.abs().toStringAsFixed(2)}';
  }

  static String _date(DateTime value) =>
      '${value.month}/${value.day}/${value.year}';
}

class _ReviewEmptyCard extends StatelessWidget {
  const _ReviewEmptyCard();

  @override
  Widget build(BuildContext context) {
    return const Card(
      child: Padding(
        padding: EdgeInsets.all(16),
        child: Text(
          "Nothing to review. We'll flag any bank lines that look like a duplicate.",
        ),
      ),
    );
  }
}

class _StatusPill extends StatelessWidget {
  const _StatusPill({required this.label});

  final String label;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
      decoration: BoxDecoration(
        color: Colors.green.withValues(alpha: 0.14),
        borderRadius: BorderRadius.circular(999),
      ),
      child: Text(
        label,
        style: TextStyle(
          color: Colors.green.shade700,
          fontSize: 12,
          fontWeight: FontWeight.w700,
        ),
      ),
    );
  }
}

class _LoadingCard extends StatelessWidget {
  const _LoadingCard({required this.label});

  final String label;

  @override
  Widget build(BuildContext context) {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Row(
          children: [
            const SizedBox(
              width: 18,
              height: 18,
              child: CircularProgressIndicator(strokeWidth: 2),
            ),
            const SizedBox(width: 12),
            Text(label),
          ],
        ),
      ),
    );
  }
}

class _ErrorCard extends StatelessWidget {
  const _ErrorCard({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Text(
          message,
          style: TextStyle(color: Theme.of(context).colorScheme.error),
        ),
      ),
    );
  }
}

class _EmptyCard extends StatelessWidget {
  const _EmptyCard();

  @override
  Widget build(BuildContext context) {
    return const Card(
      child: Padding(
        padding: EdgeInsets.all(16),
        child: Text(
          'No bank transactions yet. Import or connect a read-only account on web.',
        ),
      ),
    );
  }
}
