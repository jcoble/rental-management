import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../accounting/accounting_repository.dart';
import '../payments/payment_detail_screen.dart';
import '../payments/payments_repository.dart';
import 'expense_detail_screen.dart';
import 'expenses_list_screen.dart';
import 'money_format.dart';
import 'money_repository.dart';
import 'money_snapshot_card.dart';
import 'overdue_screen.dart';
import 'transaction_models.dart';
import 'transactions_controller.dart';

/// The Money tab — full snapshot + a "Who's behind" drill-in, plus a unified
/// transactions ledger, expenses, and payments under a tab bar.
class MoneyScreen extends ConsumerStatefulWidget {
  const MoneyScreen({super.key});

  @override
  ConsumerState<MoneyScreen> createState() => _MoneyScreenState();
}

class _MoneyScreenState extends ConsumerState<MoneyScreen>
    with SingleTickerProviderStateMixin {
  late final TabController _tabs;

  @override
  void initState() {
    super.initState();
    _tabs = TabController(length: 3, vsync: this);
    Future.microtask(() {
      ref.read(transactionsProvider.notifier).load();
      ref.read(paymentsProvider.notifier).load();
    });
  }

  @override
  void dispose() {
    _tabs.dispose();
    super.dispose();
  }

  void _openOverdue() {
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(builder: (_) => const OverdueScreen()),
    );
  }

  @override
  Widget build(BuildContext context) {
    final moneyAsync = ref.watch(moneySnapshotProvider);

    return Scaffold(
      appBar: AppBar(
        title: const Text('Money'),
        bottom: TabBar(
          controller: _tabs,
          tabs: const [
            Tab(text: 'Ledger'),
            Tab(text: 'Expenses'),
            Tab(text: 'Payments'),
          ],
        ),
      ),
      body: Column(
        children: [
          // ── Snapshot headline (shared widget) ──────────────────────────
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 8),
            child: MoneySnapshotCard(
              snapshotAsync: moneyAsync,
              onRetry: () => ref.invalidate(moneySnapshotProvider),
              onPastDueTap: _openOverdue,
            ),
          ),
          Expanded(
            child: TabBarView(
              controller: _tabs,
              children: const [
                _LedgerTab(),
                _ExpensesTab(),
                _PaymentsTab(),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

// ── Ledger tab — paginated unified transactions ────────────────────────────

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
      if (_scroll.position.pixels >=
          _scroll.position.maxScrollExtent - 240) {
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
    ref.read(transactionsProvider.notifier).setFilter(
          current.copyWith(kind: kind, clearKind: kind == null),
        );
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
              _filterChip('Payments', kind == 'Payment',
                  () => _setKind('Payment')),
              const SizedBox(width: 8),
              _filterChip('Expenses', kind == 'Expense',
                  () => _setKind('Expense')),
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
                            padding:
                                const EdgeInsets.fromLTRB(16, 8, 16, 24),
                            itemCount:
                                state.items.length + (state.hasMore ? 1 : 0),
                            separatorBuilder: (_, _) =>
                                const SizedBox(height: 8),
                            itemBuilder: (_, i) {
                              if (i >= state.items.length) {
                                return const Padding(
                                  padding: EdgeInsets.all(16),
                                  child: Center(
                                    child: SizedBox(
                                      width: 22,
                                      height: 22,
                                      child: CircularProgressIndicator(
                                          strokeWidth: 2),
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
          backgroundColor:
              isPayment ? cs.primaryContainer : cs.errorContainer,
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

// ── Expenses tab ────────────────────────────────────────────────────────────

class _ExpensesTab extends ConsumerWidget {
  const _ExpensesTab();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(expensesListProvider);
    return RefreshIndicator(
      onRefresh: () async => ref.invalidate(expensesListProvider),
      child: async.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => ListView(
          children: const [
            SizedBox(height: 120),
            Center(child: Text("Couldn't load expenses.")),
          ],
        ),
        data: (expenses) {
          if (expenses.isEmpty) {
            return ListView(
              children: const [
                SizedBox(height: 120),
                Center(child: Text('No expenses yet.')),
              ],
            );
          }
          return ListView.separated(
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 24),
            itemCount: expenses.length,
            separatorBuilder: (_, _) => const SizedBox(height: 8),
            itemBuilder: (_, i) => ExpenseRowCard(expense: expenses[i]),
          );
        },
      ),
    );
  }
}

// ── Payments tab ────────────────────────────────────────────────────────────

class _PaymentsTab extends ConsumerWidget {
  const _PaymentsTab();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(paymentsProvider);
    return RefreshIndicator(
      onRefresh: () => ref.read(paymentsProvider.notifier).refresh(),
      child: async.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => ListView(
          children: const [
            SizedBox(height: 120),
            Center(child: Text("Couldn't load payments.")),
          ],
        ),
        data: (payments) {
          if (payments.isEmpty) {
            return ListView(
              children: const [
                SizedBox(height: 120),
                Center(child: Text('No payments yet.')),
              ],
            );
          }
          return ListView.separated(
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 24),
            itemCount: payments.length,
            separatorBuilder: (_, _) => const SizedBox(height: 8),
            itemBuilder: (_, i) {
              final p = payments[i];
              final cs = Theme.of(context).colorScheme;
              final isPaid = p.status.toLowerCase() == 'paid';
              return Card(
                child: ListTile(
                  onTap: () => Navigator.of(context).push<void>(
                    MaterialPageRoute<void>(
                      builder: (_) => PaymentDetailScreen(paymentId: p.id),
                    ),
                  ),
                  leading: CircleAvatar(
                    backgroundColor:
                        isPaid ? cs.primaryContainer : cs.secondaryContainer,
                    child: Icon(
                      Icons.payments_outlined,
                      color: isPaid
                          ? cs.onPrimaryContainer
                          : cs.onSecondaryContainer,
                      size: 18,
                    ),
                  ),
                  title: Text(
                    p.tenantName ??
                        (p.leaseNumber != null
                            ? 'Lease ${p.leaseNumber}'
                            : 'Payment #${p.id}'),
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: const TextStyle(fontWeight: FontWeight.w600),
                  ),
                  subtitle: Text(
                    '${p.type.isEmpty ? 'Payment' : p.type} · '
                    'Due ${shortDateFmt(p.dueDate)}',
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  ),
                  trailing: Text(
                    moneyFmt(p.amount),
                    style: const TextStyle(fontWeight: FontWeight.w700),
                  ),
                ),
              );
            },
          );
        },
      ),
    );
  }
}

