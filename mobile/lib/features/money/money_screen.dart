import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../accounting/accounting_repository.dart';
import '../home/mobile_domain_navigation.dart';
import '../payments/payment_detail_screen.dart';
import '../payments/payments_screen.dart';
import 'expense_detail_screen.dart';
import 'expense_form_sheet.dart';
import 'money_format.dart';
import 'money_repository.dart';
import 'money_snapshot_card.dart';
import 'overdue_screen.dart';
import 'transaction_models.dart';
import 'transactions_controller.dart';

/// Whether the Money screen's snapshot card is expanded. A session-scoped
/// Notifier so the choice is remembered while the app is open (collapsing it
/// gives the ledger room without losing the preference on every rebuild).
class MoneySnapshotExpanded extends Notifier<bool> {
  @override
  bool build() => true;

  void toggle() => state = !state;
}

final moneySnapshotExpandedProvider =
    NotifierProvider<MoneySnapshotExpanded, bool>(MoneySnapshotExpanded.new);

/// The Money tab — a collapsible plain-English snapshot plus a single unified
/// transactions ledger (with Payments / Expenses filter chips). The previously
/// separate Expenses and Payments tabs were redundant with the Ledger's own
/// kind filter, so they were consolidated here.
class MoneyScreen extends ConsumerStatefulWidget {
  const MoneyScreen({super.key});

  @override
  ConsumerState<MoneyScreen> createState() => _MoneyScreenState();
}

class _MoneyScreenState extends ConsumerState<MoneyScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(() {
      ref.read(transactionsProvider.notifier).load();
    });
  }

  void _openOverdue() {
    final shellNavigator = mobileShellNavigatorOf(context);
    if (shellNavigator != null) {
      shellNavigator.openTab(
        MobileShellTabId.money,
        destination: MobileDestinationId.moneyOverview,
        detailBuilder: (_) => const OverdueScreen(),
      );
      revealMobileShellIfDetached(context);
      return;
    }

    final domainNavigator = MobileDomainNavigation.maybeOf(context);
    if (domainNavigator != null) {
      domainNavigator.openDestination(
        MobileDestinationId.moneyOverview,
        detailBuilder: (_) => const OverdueScreen(),
      );
      return;
    }

    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(builder: (_) => const OverdueScreen()),
    );
  }

  void _refreshAfterManualEntry() {
    ref.invalidate(moneySnapshotProvider);
    ref.invalidate(expensesListProvider);
    ref.read(transactionsProvider.notifier).refresh();
  }

  void _addPayment() {
    showCreatePaymentSheet(context, ref, onSaved: _refreshAfterManualEntry);
  }

  void _addExpense() {
    showCreateExpenseSheet(context, ref, onSaved: _refreshAfterManualEntry);
  }

  @override
  Widget build(BuildContext context) {
    final moneyAsync = ref.watch(moneySnapshotProvider);
    final expanded = ref.watch(moneySnapshotExpandedProvider);

    return Scaffold(
      appBar: AppBar(title: const Text('Money')),
      body: Column(
        children: [
          // ── Collapsible snapshot headline ──────────────────────────────
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 8),
            child: _CollapsibleSnapshot(
              expanded: expanded,
              onToggle: () =>
                  ref.read(moneySnapshotExpandedProvider.notifier).toggle(),
              child: MoneySnapshotCard(
                snapshotAsync: moneyAsync,
                onRetry: () => ref.invalidate(moneySnapshotProvider),
                onPastDueTap: _openOverdue,
              ),
            ),
          ),
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 0, 16, 8),
            child: MoneyQuickActions(
              onAddPayment: _addPayment,
              onAddExpense: _addExpense,
            ),
          ),
          const Expanded(child: _LedgerTab()),
        ],
      ),
    );
  }
}

/// Wraps the snapshot card with a tappable header (title + expand arrow) so the
/// landlord can collapse it and give the ledger room. When collapsed it shows a
/// one-line summary (collected / kept / who's behind) instead of the full card.
class _CollapsibleSnapshot extends StatelessWidget {
  const _CollapsibleSnapshot({
    required this.expanded,
    required this.onToggle,
    required this.child,
  });

  final bool expanded;
  final VoidCallback onToggle;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        InkWell(
          onTap: onToggle,
          borderRadius: BorderRadius.circular(8),
          child: Padding(
            padding: const EdgeInsets.symmetric(vertical: 4, horizontal: 4),
            child: Row(
              children: [
                Icon(Icons.savings_outlined, size: 18, color: cs.primary),
                const SizedBox(width: 8),
                Text(
                  'Your money',
                  style: theme.textTheme.titleSmall?.copyWith(
                    fontWeight: FontWeight.w700,
                  ),
                ),
                const Spacer(),
                Text(
                  expanded ? 'Hide' : 'Show',
                  style: theme.textTheme.labelMedium?.copyWith(
                    color: cs.onSurfaceVariant,
                  ),
                ),
                Icon(
                  expanded ? Icons.expand_less : Icons.expand_more,
                  color: cs.onSurfaceVariant,
                ),
              ],
            ),
          ),
        ),
        AnimatedCrossFade(
          duration: const Duration(milliseconds: 180),
          crossFadeState: expanded
              ? CrossFadeState.showFirst
              : CrossFadeState.showSecond,
          firstChild: Padding(
            padding: const EdgeInsets.only(top: 8),
            child: child,
          ),
          secondChild: const SizedBox(width: double.infinity),
        ),
      ],
    );
  }
}

class MoneyQuickActions extends StatelessWidget {
  const MoneyQuickActions({
    super.key,
    required this.onAddPayment,
    required this.onAddExpense,
  });

  final VoidCallback onAddPayment;
  final VoidCallback onAddExpense;

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        Expanded(
          child: FilledButton.icon(
            onPressed: onAddPayment,
            icon: const Icon(Icons.add_card_outlined),
            label: const Text('Add payment'),
          ),
        ),
        const SizedBox(width: 8),
        Expanded(
          child: FilledButton.tonalIcon(
            onPressed: onAddExpense,
            icon: const Icon(Icons.receipt_long_outlined),
            label: const Text('Add expense'),
          ),
        ),
      ],
    );
  }
}

// ── Ledger — paginated unified transactions ─────────────────────────────────

class _LedgerTab extends ConsumerStatefulWidget {
  const _LedgerTab();

  @override
  ConsumerState<_LedgerTab> createState() => _LedgerTabState();
}

class _LedgerTabState extends ConsumerState<_LedgerTab> {
  final _scroll = ScrollController();

  @override
  void initState() {
    super.initState();
    _scroll.addListener(() {
      if (_scroll.position.pixels >= _scroll.position.maxScrollExtent - 240) {
        ref.read(transactionsProvider.notifier).loadMore();
      }
    });
  }

  @override
  void dispose() {
    _scroll.dispose();
    super.dispose();
  }

  void _setKind(String? kind) {
    final current = ref.read(transactionsProvider).filter;
    ref
        .read(transactionsProvider.notifier)
        .setFilter(current.copyWith(kind: kind, clearKind: kind == null));
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(transactionsProvider);
    final kind = state.filter.kind;

    return Column(
      children: [
        // Kind filter chips.
        SizedBox(
          height: 48,
          child: ListView(
            scrollDirection: Axis.horizontal,
            padding: const EdgeInsets.symmetric(horizontal: 16),
            children: [
              _filterChip('All', kind == null, () => _setKind(null)),
              const SizedBox(width: 8),
              _filterChip(
                'Payments',
                kind == 'Payment',
                () => _setKind('Payment'),
              ),
              const SizedBox(width: 8),
              _filterChip(
                'Expenses',
                kind == 'Expense',
                () => _setKind('Expense'),
              ),
            ],
          ),
        ),
        Expanded(
          child: RefreshIndicator(
            onRefresh: () => ref.read(transactionsProvider.notifier).refresh(),
            child: state.loading && state.items.isEmpty
                ? const Center(child: CircularProgressIndicator())
                : state.error != null && state.items.isEmpty
                ? ListView(
                    children: [
                      const SizedBox(height: 120),
                      Center(child: Text(state.error!)),
                    ],
                  )
                : state.items.isEmpty
                ? ListView(
                    children: const [
                      SizedBox(height: 120),
                      Center(child: Text('No transactions yet.')),
                    ],
                  )
                : ListView.separated(
                    controller: _scroll,
                    padding: const EdgeInsets.fromLTRB(16, 8, 16, 24),
                    itemCount: state.items.length + (state.hasMore ? 1 : 0),
                    separatorBuilder: (_, _) => const SizedBox(height: 8),
                    itemBuilder: (_, i) {
                      if (i >= state.items.length) {
                        return const Padding(
                          padding: EdgeInsets.all(16),
                          child: Center(
                            child: SizedBox(
                              width: 22,
                              height: 22,
                              child: CircularProgressIndicator(strokeWidth: 2),
                            ),
                          ),
                        );
                      }
                      return _TransactionCard(tx: state.items[i]);
                    },
                  ),
          ),
        ),
      ],
    );
  }

  Widget _filterChip(String label, bool selected, VoidCallback onTap) {
    return ChoiceChip(
      label: Text(label),
      selected: selected,
      onSelected: (_) => onTap(),
    );
  }
}

class _TransactionCard extends StatelessWidget {
  const _TransactionCard({required this.tx});

  final AccountingTransaction tx;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final isPayment = tx.isPayment;
    final signColor = isPayment ? Colors.green.shade700 : cs.error;

    return Card(
      child: ListTile(
        onTap: () {
          final MobileDetailBuilder detailBuilder = isPayment
              ? (BuildContext _) => PaymentDetailScreen(paymentId: tx.id)
              : (BuildContext _) => ExpenseDetailScreen(expenseId: tx.id);
          final shellNavigator = mobileShellNavigatorOf(context);
          if (shellNavigator != null) {
            shellNavigator.openTab(
              MobileShellTabId.money,
              destination: MobileDestinationId.moneyLedger,
              detailBuilder: detailBuilder,
            );
            revealMobileShellIfDetached(context);
            return;
          }

          final domainNavigator = MobileDomainNavigation.maybeOf(context);
          if (domainNavigator != null) {
            domainNavigator.openDestination(
              MobileDestinationId.moneyLedger,
              detailBuilder: detailBuilder,
            );
            return;
          }

          if (isPayment) {
            Navigator.of(context).push<void>(
              MaterialPageRoute<void>(
                builder: (_) => PaymentDetailScreen(paymentId: tx.id),
              ),
            );
          } else {
            Navigator.of(context).push<void>(
              MaterialPageRoute<void>(
                builder: (_) => ExpenseDetailScreen(expenseId: tx.id),
              ),
            );
          }
        },
        leading: CircleAvatar(
          backgroundColor: isPayment ? cs.primaryContainer : cs.errorContainer,
          child: Icon(
            isPayment ? Icons.south_west : Icons.north_east,
            color: isPayment ? cs.onPrimaryContainer : cs.onErrorContainer,
            size: 18,
          ),
        ),
        title: Text(
          tx.description.isEmpty ? tx.kind : tx.description,
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
          style: theme.textTheme.bodyMedium?.copyWith(
            fontWeight: FontWeight.w600,
          ),
        ),
        subtitle: Text(
          [
            if (tx.counterparty != null && tx.counterparty!.isNotEmpty)
              tx.counterparty!,
            if (tx.category.isNotEmpty) tx.category,
            shortDateFmt(tx.date),
          ].where((s) => s.isNotEmpty).join(' / '),
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
        ),
        trailing: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          crossAxisAlignment: CrossAxisAlignment.end,
          children: [
            Text(
              '${isPayment ? '+' : '-'}${moneyFmt(tx.amount)}',
              style: theme.textTheme.bodyMedium?.copyWith(
                fontWeight: FontWeight.w700,
                color: signColor,
              ),
            ),
            if (tx.reconciled)
              Text(
                'Cleared',
                style: theme.textTheme.labelSmall?.copyWith(
                  color: Colors.green.shade700,
                ),
              )
            else
              Text(
                tx.status,
                style: theme.textTheme.labelSmall?.copyWith(
                  color: cs.onSurfaceVariant,
                ),
              ),
          ],
        ),
      ),
    );
  }
}
