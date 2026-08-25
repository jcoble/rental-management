import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../accounting/accounting_models.dart';
import '../../core/presentation/formatting.dart';

/// The plain-English money snapshot card ("money in / out / kept" + who's
/// behind), shared by the Today dashboard and the Money tab.
///
/// [onPastDueTap] makes the past-due block actionable (Today drills into the
/// Money overdue view). [compact] trims the card for the Today headline.
class MoneySnapshotCard extends StatelessWidget {
  const MoneySnapshotCard({
    super.key,
    required this.snapshotAsync,
    required this.onRetry,
    this.onPastDueTap,
    this.compact = false,
  });

  final AsyncValue<MoneySnapshot> snapshotAsync;
  final VoidCallback onRetry;
  final VoidCallback? onPastDueTap;
  final bool compact;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    return snapshotAsync.when(
      loading: () => const _MoneyLoadingCard(),
      error: (_, _) => Card(
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Row(
            children: [
              Icon(Icons.wifi_off_outlined, color: cs.error, size: 22),
              const SizedBox(width: 12),
              const Expanded(child: Text("Couldn't load your money.")),
              TextButton(onPressed: onRetry, child: const Text('Retry')),
            ],
          ),
        ),
      ),
      data: (snapshot) {
        final showPastDue =
            snapshot.pastDueCount > 0 || snapshot.pastDueAmount > 0;
        final pastDue = showPastDue
            ? _PastDueRow(
                count: snapshot.pastDueCount,
                amount: snapshot.pastDueAmount,
                explanation: snapshot.explanations.pastDue,
                onTap: onPastDueTap,
              )
            : null;

        return Card(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Icon(Icons.savings_outlined, color: cs.primary, size: 20),
                    const SizedBox(width: 8),
                    Text(
                      'Your money',
                      style: theme.textTheme.titleMedium?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                    const Spacer(),
                    if (snapshot.periodLabel.isNotEmpty)
                      Text(
                        snapshot.periodLabel,
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: cs.onSurfaceVariant,
                        ),
                      ),
                  ],
                ),
                const SizedBox(height: 16),
                _MoneyRow(
                  icon: Icons.south_west,
                  iconColor: Colors.green.shade700,
                  label: 'Collected',
                  amount: snapshot.collected,
                  explanation: compact ? '' : snapshot.explanations.collected,
                ),
                const SizedBox(height: 14),
                _MoneyRow(
                  icon: Icons.north_east,
                  iconColor: cs.error,
                  label: 'Spent',
                  amount: snapshot.spent,
                  explanation: compact ? '' : snapshot.explanations.spent,
                ),
                const SizedBox(height: 14),
                _MoneyRow(
                  icon: Icons.account_balance_wallet_outlined,
                  iconColor: cs.primary,
                  label: 'Kept',
                  amount: snapshot.net,
                  explanation: compact ? '' : snapshot.explanations.net,
                  emphasize: true,
                ),
                if (pastDue != null) ...[
                  const SizedBox(height: 14),
                  const Divider(height: 1),
                  const SizedBox(height: 14),
                  pastDue,
                ],
              ],
            ),
          ),
        );
      },
    );
  }
}

class _MoneyLoadingCard extends StatelessWidget {
  const _MoneyLoadingCard();

  @override
  Widget build(BuildContext context) {
    return const Card(
      child: Padding(
        padding: EdgeInsets.all(16),
        child: Row(
          children: [
            SizedBox(
              width: 18,
              height: 18,
              child: CircularProgressIndicator(strokeWidth: 2),
            ),
            SizedBox(width: 12),
            Text('Loading your money...'),
          ],
        ),
      ),
    );
  }
}

class _MoneyRow extends StatelessWidget {
  const _MoneyRow({
    required this.icon,
    required this.iconColor,
    required this.label,
    required this.amount,
    required this.explanation,
    this.emphasize = false,
  });

  final IconData icon;
  final Color iconColor;
  final String label;
  final double amount;
  final String explanation;
  final bool emphasize;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Padding(
          padding: const EdgeInsets.only(top: 2),
          child: Icon(icon, size: 18, color: iconColor),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                crossAxisAlignment: CrossAxisAlignment.baseline,
                textBaseline: TextBaseline.alphabetic,
                children: [
                  Expanded(
                    child: Text(
                      label,
                      style: theme.textTheme.bodyMedium?.copyWith(
                        fontWeight: FontWeight.w600,
                        color: cs.onSurface,
                      ),
                    ),
                  ),
                  const SizedBox(width: 8),
                  Text(
                    moneyFmt(amount),
                    style:
                        (emphasize
                                ? theme.textTheme.headlineSmall
                                : theme.textTheme.titleLarge)
                            ?.copyWith(
                              fontWeight: FontWeight.w700,
                              fontFeatures: const [
                                FontFeature.tabularFigures(),
                              ],
                              color: emphasize ? cs.primary : cs.onSurface,
                            ),
                  ),
                ],
              ),
              if (explanation.isNotEmpty) ...[
                const SizedBox(height: 2),
                Text(
                  explanation,
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: cs.onSurfaceVariant,
                    height: 1.4,
                  ),
                ),
              ],
            ],
          ),
        ),
      ],
    );
  }
}

class _PastDueRow extends StatelessWidget {
  const _PastDueRow({
    required this.count,
    required this.amount,
    required this.explanation,
    this.onTap,
  });

  final int count;
  final double amount;
  final String explanation;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final tenantWord = count == 1 ? 'tenant' : 'tenants';

    final row = Row(
      crossAxisAlignment: CrossAxisAlignment.center,
      children: [
        SizedBox.square(
          dimension: 40,
          child: Center(
            child: Container(
              padding: const EdgeInsets.all(6),
              decoration: BoxDecoration(
                color: cs.errorContainer,
                borderRadius: BorderRadius.circular(8),
              ),
              child: Icon(
                Icons.warning_amber_rounded,
                size: 16,
                color: cs.onErrorContainer,
              ),
            ),
          ),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                '$count $tenantWord behind, owing ${moneyFmt(amount)}',
                style: theme.textTheme.bodyMedium?.copyWith(
                  fontWeight: FontWeight.w700,
                  color: cs.onSurface,
                ),
              ),
              if (explanation.isNotEmpty) ...[
                const SizedBox(height: 2),
                Text(
                  explanation,
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: cs.onSurfaceVariant,
                    height: 1.4,
                  ),
                ),
              ],
            ],
          ),
        ),
        if (onTap != null)
          SizedBox.square(
            dimension: 40,
            child: Center(
              child: Icon(
                Icons.chevron_right,
                color: cs.onSurfaceVariant,
                size: 18,
              ),
            ),
          ),
      ],
    );

    if (onTap == null) return row;
    return InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(8),
      child: row,
    );
  }
}
