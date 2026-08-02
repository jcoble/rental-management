import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/auth/auth_models.dart';
import '../../core/auth/mobile_access_policy.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/widgets/mobile_m3_list.dart';
import '../accounting/accounting_models.dart';
import '../accounting/accounting_repository.dart';
import '../home/mobile_domain_chrome.dart';
import '../home/mobile_domain_navigation.dart';
import '../home/mobile_quick_action_fab.dart';
import '../home/mobile_quick_action_helpers.dart';
import '../payments/tenant_account_receipt_flow.dart';
import 'expense_form_sheet.dart';
import 'money_format.dart';
import 'money_repository.dart';
import 'money_snapshot_card.dart';
import 'overdue_screen.dart';
import 'transaction_models.dart';
import 'transactions_controller.dart';
import 'widgets/accounting_impact_block.dart';
import 'widgets/journal_entry_screen.dart';
import 'widgets/ledger_type_badge.dart';

enum MoneyScreenView { insights, ledger, payments, expenses }

/// The Money tab starts as a working ledger. The snapshot stays visible as a
/// compact KPI strip, with the full plain-English card one tap away.
class MoneyScreen extends ConsumerStatefulWidget {
  const MoneyScreen({
    super.key,
    this.initialView = MoneyScreenView.insights,
    this.showTransactionSelector = false,
  });

  final MoneyScreenView initialView;
  final bool showTransactionSelector;

  @override
  ConsumerState<MoneyScreen> createState() => _MoneyScreenState();
}

class _MoneyScreenState extends ConsumerState<MoneyScreen> {
  late MoneyScreenView _view;

  @override
  void initState() {
    super.initState();
    _view =
        widget.showTransactionSelector &&
            widget.initialView == MoneyScreenView.ledger
        ? MoneyScreenView.payments
        : widget.initialView;
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

  void _addExpense() {
    showCreateExpenseSheet(context, ref, onSaved: _refreshAfterManualEntry);
  }

  Future<void> _addPayment() async {
    final result = await showGlobalRecordTenantReceiptFlow(context, ref);
    if (result != null && mounted) _refreshAfterManualEntry();
  }

  void _showSnapshotDetails(AsyncValue<MoneySnapshot> snapshotAsync) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      showDragHandle: true,
      useSafeArea: true,
      builder: (_) => Padding(
        padding: const EdgeInsets.fromLTRB(16, 0, 16, 24),
        child: MoneySnapshotCard(
          snapshotAsync: snapshotAsync,
          onRetry: () => ref.invalidate(moneySnapshotProvider),
          onPastDueTap: () {
            Navigator.of(context).pop();
            _openOverdue();
          },
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final moneyAsync = ref.watch(moneySnapshotProvider);
    final auth = ref.watch(authControllerProvider);
    final canAddPayment =
        auth is AuthStateAuthenticated &&
        canUseMobileCapabilityAction(
          experience: auth.activeExperience,
          capabilities: auth.capabilities,
          capability: 'money.payments.manage',
          experiences: const {WorkspaceExperience.management},
        );
    final canAddExpense =
        auth is AuthStateAuthenticated &&
        canUseMobileCapabilityAction(
          experience: auth.activeExperience,
          capabilities: auth.capabilities,
          capability: 'money.expenses.manage',
          experiences: const {WorkspaceExperience.management},
        );
    final manualActions = <MobileQuickAction>[
      if (canAddPayment)
        MobileQuickAction(
          label: 'Add payment',
          icon: Icons.add_card_outlined,
          onPressed: _addPayment,
        ),
      if (canAddExpense)
        MobileQuickAction(
          label: 'Add expense',
          icon: Icons.receipt_long_outlined,
          onPressed: _addExpense,
        ),
    ];

    return Scaffold(
      appBar: mobileDomainRootAppBar(context, title: const Text('Money')),
      floatingActionButton: _view == MoneyScreenView.insights
          ? null
          : MobileQuickActionFab(
              heroTag: 'money-ledger-actions-fab',
              primaryActions: manualActions,
              onChat: () => openMobileAssistant(context),
              onRecord: () => openMobileRecord(context),
              onScan: () => openMobileScan(context),
            ),
      body: Column(
        children: [
          if (_view == MoneyScreenView.insights)
            Expanded(
              child: _MoneyInsightsTab(
                snapshotAsync: moneyAsync,
                onRetry: () => ref.invalidate(moneySnapshotProvider),
                onDetails: () => _showSnapshotDetails(moneyAsync),
                onPastDueTap: _openOverdue,
              ),
            )
          else ...[
            if (widget.showTransactionSelector)
              Padding(
                padding: const EdgeInsets.fromLTRB(16, 12, 16, 10),
                child: _MoneyViewSelector(
                  value: _view,
                  onChanged: (view) => setState(() => _view = view),
                ),
              ),
            Expanded(child: _LedgerTab(view: _view)),
          ],
        ],
      ),
    );
  }
}

class _MoneyInsightsTab extends StatelessWidget {
  const _MoneyInsightsTab({
    required this.snapshotAsync,
    required this.onRetry,
    required this.onDetails,
    required this.onPastDueTap,
  });

  final AsyncValue<MoneySnapshot> snapshotAsync;
  final VoidCallback onRetry;
  final VoidCallback onDetails;
  final VoidCallback onPastDueTap;

  @override
  Widget build(BuildContext context) {
    return RefreshIndicator(
      onRefresh: () async => onRetry(),
      child: snapshotAsync.when(
        loading: () => ListView(
          physics: const AlwaysScrollableScrollPhysics(),
          children: const [
            SizedBox(height: 180),
            Center(child: CircularProgressIndicator()),
          ],
        ),
        error: (_, _) => ListView(
          physics: const AlwaysScrollableScrollPhysics(),
          padding: const EdgeInsets.fromLTRB(16, 96, 16, 32),
          children: [_MoneyInsightError(onRetry: onRetry)],
        ),
        data: (snapshot) => ListView(
          physics: const AlwaysScrollableScrollPhysics(),
          padding: const EdgeInsets.fromLTRB(16, 0, 16, 96),
          children: [
            _MoneyInsightHeader(
              snapshot: snapshot,
              onDetails: onDetails,
              onPastDueTap: onPastDueTap,
            ),
            const SizedBox(height: 12),
            _MoneyInsightList(snapshot: snapshot, onPastDueTap: onPastDueTap),
          ],
        ),
      ),
    );
  }
}

class _MoneyInsightHeader extends StatelessWidget {
  const _MoneyInsightHeader({
    required this.snapshot,
    required this.onDetails,
    required this.onPastDueTap,
  });

  final MoneySnapshot snapshot;
  final VoidCallback onDetails;
  final VoidCallback onPastDueTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final netPositive = snapshot.net >= 0;

    return Material(
      color: cs.primaryContainer,
      borderRadius: M3Shape.radiusLargeIncreased,
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onDetails,
        child: Padding(
          padding: const EdgeInsets.fromLTRB(18, 16, 14, 16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Icon(Icons.insights_outlined, color: cs.onPrimaryContainer),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      snapshot.periodLabel.isEmpty
                          ? 'Money insights'
                          : snapshot.periodLabel,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: theme.textTheme.titleSmall?.copyWith(
                        color: cs.onPrimaryContainer,
                        fontWeight: FontWeight.w800,
                      ),
                    ),
                  ),
                  IconButton(
                    tooltip: 'Money details',
                    icon: Icon(
                      Icons.open_in_full,
                      color: cs.onPrimaryContainer,
                    ),
                    onPressed: onDetails,
                  ),
                ],
              ),
              const SizedBox(height: 14),
              Text(
                netPositive ? 'Cash kept' : 'Cash shortfall',
                style: theme.textTheme.labelLarge?.copyWith(
                  color: cs.onPrimaryContainer.withValues(alpha: 0.82),
                  fontWeight: FontWeight.w700,
                ),
              ),
              const SizedBox(height: 4),
              Text(
                moneyFmt(snapshot.net),
                style: theme.textTheme.displaySmall?.copyWith(
                  color: cs.onPrimaryContainer,
                  fontWeight: FontWeight.w800,
                  fontFeatures: const [FontFeature.tabularFigures()],
                ),
              ),
              const SizedBox(height: 12),
              Text(
                snapshot.explanations.net.isEmpty
                    ? 'Collected minus property spending for this period.'
                    : snapshot.explanations.net,
                style: theme.textTheme.bodyMedium?.copyWith(
                  color: cs.onPrimaryContainer.withValues(alpha: 0.86),
                ),
              ),
              if (snapshot.pastDueAmount > 0) ...[
                const SizedBox(height: 14),
                FilledButton.tonalIcon(
                  onPressed: onPastDueTap,
                  icon: const Icon(Icons.warning_amber_rounded),
                  label: Text('${snapshot.pastDueCount} behind'),
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}

class _MoneyInsightList extends StatelessWidget {
  const _MoneyInsightList({required this.snapshot, required this.onPastDueTap});

  final MoneySnapshot snapshot;
  final VoidCallback onPastDueTap;

  @override
  Widget build(BuildContext context) {
    final items = [
      _MoneyInsightItemData(
        icon: Icons.south_west,
        title: 'Collections',
        value: moneyFmt(snapshot.collected),
        supporting: snapshot.explanations.collected,
        meta: 'Last 30 days ${moneyFmt(snapshot.collectedLast30Days)}',
        tone: _MoneyInsightTone.positive,
      ),
      _MoneyInsightItemData(
        icon: Icons.north_east,
        title: 'Property spending',
        value: moneyFmt(snapshot.spent),
        supporting: snapshot.explanations.spent,
        meta: 'Last 30 days ${moneyFmt(snapshot.spentLast30Days)}',
        tone: _MoneyInsightTone.warning,
      ),
      _MoneyInsightItemData(
        icon: Icons.account_balance_wallet_outlined,
        title: 'Rolling cash kept',
        value: moneyFmt(snapshot.netLast30Days),
        supporting: 'Trailing 30-day net after property spending.',
        meta: 'This period ${moneyFmt(snapshot.net)}',
        tone: snapshot.netLast30Days >= 0
            ? _MoneyInsightTone.primary
            : _MoneyInsightTone.error,
      ),
      _MoneyInsightItemData(
        icon: Icons.warning_amber_rounded,
        title: 'Past-due exposure',
        value: moneyFmt(snapshot.pastDueAmount),
        supporting: snapshot.explanations.pastDue,
        meta:
            '${snapshot.pastDueCount} tenant${snapshot.pastDueCount == 1 ? '' : 's'} behind',
        tone: snapshot.pastDueAmount > 0
            ? _MoneyInsightTone.error
            : _MoneyInsightTone.neutral,
        onTap: snapshot.pastDueAmount > 0 ? onPastDueTap : null,
      ),
    ];

    return Column(
      children: [
        for (var i = 0; i < items.length; i++) ...[
          _MoneyInsightItem(
            data: items[i],
            position: MobileM3ListItemPositionForIndex.forIndex(
              i,
              items.length,
            ),
          ),
          if (i < items.length - 1) const MobileM3ListDivider(),
        ],
      ],
    );
  }
}

enum _MoneyInsightTone { primary, positive, warning, error, neutral }

class _MoneyInsightItemData {
  const _MoneyInsightItemData({
    required this.icon,
    required this.title,
    required this.value,
    required this.supporting,
    required this.meta,
    required this.tone,
    this.onTap,
  });

  final IconData icon;
  final String title;
  final String value;
  final String supporting;
  final String meta;
  final _MoneyInsightTone tone;
  final VoidCallback? onTap;
}

class _MoneyInsightItem extends StatelessWidget {
  const _MoneyInsightItem({required this.data, required this.position});

  final _MoneyInsightItemData data;
  final MobileM3ListItemPosition position;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final colors = switch (data.tone) {
      _MoneyInsightTone.primary => (cs.primaryContainer, cs.onPrimaryContainer),
      _MoneyInsightTone.positive => (
        cs.secondaryContainer,
        cs.onSecondaryContainer,
      ),
      _MoneyInsightTone.warning => (
        cs.tertiaryContainer,
        cs.onTertiaryContainer,
      ),
      _MoneyInsightTone.error => (cs.errorContainer, cs.onErrorContainer),
      _MoneyInsightTone.neutral => (
        cs.surfaceContainerHighest,
        cs.onSurfaceVariant,
      ),
    };

    return MobileM3ListItem(
      position: position,
      onTap: data.onTap,
      leading: MobileM3LeadingIcon(
        icon: data.icon,
        backgroundColor: colors.$1,
        foregroundColor: colors.$2,
      ),
      title: Row(
        children: [
          Expanded(
            child: Text(
              data.title,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: theme.textTheme.titleSmall?.copyWith(
                fontWeight: FontWeight.w700,
              ),
            ),
          ),
          Text(
            data.value,
            style: theme.textTheme.titleSmall?.copyWith(
              fontWeight: FontWeight.w800,
              fontFeatures: const [FontFeature.tabularFigures()],
            ),
          ),
        ],
      ),
      supporting: [
        Text(
          data.supporting.isEmpty
              ? 'No explanation available yet.'
              : data.supporting,
          maxLines: 2,
          overflow: TextOverflow.ellipsis,
          style: theme.textTheme.bodySmall?.copyWith(
            color: cs.onSurfaceVariant,
          ),
        ),
      ],
      meta: Text(
        data.meta,
        style: theme.textTheme.bodySmall?.copyWith(color: cs.onSurfaceVariant),
      ),
      trailing: data.onTap == null
          ? null
          : Icon(Icons.chevron_right, color: cs.onSurfaceVariant),
    );
  }
}

class _MoneyInsightError extends StatelessWidget {
  const _MoneyInsightError({required this.onRetry});

  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Material(
      color: cs.surfaceContainerHigh,
      borderRadius: M3Shape.radiusLargeIncreased,
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          children: [
            Icon(Icons.wifi_off_outlined, size: 36, color: cs.error),
            const SizedBox(height: 10),
            Text(
              "Couldn't load money insights.",
              textAlign: TextAlign.center,
              style: Theme.of(context).textTheme.titleSmall,
            ),
            const SizedBox(height: 14),
            FilledButton.tonal(onPressed: onRetry, child: const Text('Retry')),
          ],
        ),
      ),
    );
  }
}

class _MoneyViewSelector extends StatelessWidget {
  const _MoneyViewSelector({required this.value, required this.onChanged});

  final MoneyScreenView value;
  final ValueChanged<MoneyScreenView> onChanged;

  @override
  Widget build(BuildContext context) {
    final selected = value == MoneyScreenView.expenses
        ? MoneyScreenView.expenses
        : MoneyScreenView.payments;
    return SegmentedButton<MoneyScreenView>(
      showSelectedIcon: false,
      segments: const [
        ButtonSegment(
          value: MoneyScreenView.payments,
          label: Text('Payments', maxLines: 1, softWrap: false),
        ),
        ButtonSegment(
          value: MoneyScreenView.expenses,
          label: Text('Expenses', maxLines: 1, softWrap: false),
        ),
      ],
      selected: {selected},
      onSelectionChanged: (selection) => onChanged(selection.single),
    );
  }
}

// ── Ledger — paginated unified transactions ─────────────────────────────────

class _LedgerTab extends ConsumerStatefulWidget {
  const _LedgerTab({required this.view});

  final MoneyScreenView view;

  @override
  ConsumerState<_LedgerTab> createState() => _LedgerTabState();
}

class _LedgerTabState extends ConsumerState<_LedgerTab> {
  final _scroll = ScrollController();
  int _syncGeneration = 0;

  @override
  void initState() {
    super.initState();
    _scroll.addListener(() {
      if (_scroll.position.pixels >= _scroll.position.maxScrollExtent - 240) {
        ref.read(transactionsProvider.notifier).loadMore();
      }
    });
    _scheduleFilterSync();
  }

  @override
  void didUpdateWidget(covariant _LedgerTab oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.view == widget.view) return;
    _scheduleFilterSync();
  }

  @override
  void dispose() {
    _syncGeneration++;
    _scroll.dispose();
    super.dispose();
  }

  String? get _kind => switch (widget.view) {
    MoneyScreenView.insights => null,
    MoneyScreenView.ledger => null,
    MoneyScreenView.payments => 'Payment',
    MoneyScreenView.expenses => 'Expense',
  };

  void _scheduleFilterSync() {
    final generation = ++_syncGeneration;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted || generation != _syncGeneration) return;
      unawaited(_syncFilter());
    });
  }

  Future<void> _syncFilter() async {
    final state = ref.read(transactionsProvider);
    final current = state.filter;
    final nextKind = _kind;
    if (current.kind == nextKind) {
      if (state.items.isEmpty && !state.loading) {
        await ref.read(transactionsProvider.notifier).load();
      }
      return;
    }
    await ref
        .read(transactionsProvider.notifier)
        .setFilter(
          current.copyWith(kind: nextKind, clearKind: nextKind == null),
        );
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(transactionsProvider);
    final accounts = ref.watch(chartOfAccountsProvider);

    return RefreshIndicator(
      onRefresh: () => ref.read(transactionsProvider.notifier).refresh(),
      child: state.loading && state.items.isEmpty
          ? const _LedgerLoadingBody()
          : state.error != null && state.items.isEmpty
          ? ListView(
              children: [
                const SizedBox(height: 120),
                Center(child: Text(state.error!)),
              ],
            )
          : state.items.isEmpty
          ? ListView(
              children: [
                const SizedBox(height: 120),
                Center(child: Text(_emptyMessageFor(widget.view))),
              ],
            )
          : ListView.separated(
              controller: _scroll,
              padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
              itemCount: state.items.length + (state.loadingMore ? 1 : 0) + 1,
              separatorBuilder: (_, index) {
                if (index >= state.items.length) {
                  return const SizedBox(height: 12);
                }
                return const MobileM3ListDivider();
              },
              itemBuilder: (_, i) {
                if (i == 0) {
                  return _LedgerFilters(
                    filter: state.filter,
                    accounts: accounts.value ?? const [],
                    onChanged: ref
                        .read(transactionsProvider.notifier)
                        .setFilter,
                  );
                }
                final rowIndex = i - 1;
                if (rowIndex >= state.items.length) {
                  return const Padding(
                    padding: EdgeInsets.all(16),
                    child: Center(
                      child: SizedBox(
                        key: Key('ledger-append-progress'),
                        width: 22,
                        height: 22,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      ),
                    ),
                  );
                }
                return _TransactionCard(
                  tx: state.items[rowIndex],
                  position: MobileM3ListItemPositionForIndex.forIndex(
                    rowIndex,
                    state.items.length,
                  ),
                );
              },
            ),
    );
  }
}

class _LedgerFilters extends StatelessWidget {
  const _LedgerFilters({
    required this.filter,
    required this.accounts,
    required this.onChanged,
  });
  final TransactionsFilter filter;
  final List<ChartOfAccountsRow> accounts;
  final ValueChanged<TransactionsFilter> onChanged;
  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) {
      final typeFilter = DropdownButtonFormField<String?>(
        isExpanded: true,
        initialValue: filter.displayType,
        decoration: const InputDecoration(
          labelText: 'Type',
          border: OutlineInputBorder(),
        ),
        items: const [
          DropdownMenuItem(value: null, child: Text('All types')),
          DropdownMenuItem(value: 'Charge', child: Text('Charge')),
          DropdownMenuItem(
            value: 'PaymentReceived',
            child: Text('Payment received'),
          ),
          DropdownMenuItem(value: 'Credit', child: Text('Credit')),
          DropdownMenuItem(value: 'Expense', child: Text('Expense')),
          DropdownMenuItem(value: 'Bill', child: Text('Bill')),
          DropdownMenuItem(value: 'BankTransfer', child: Text('Bank transfer')),
          DropdownMenuItem(value: 'LoanPayment', child: Text('Loan payment')),
          DropdownMenuItem(
            value: 'OwnerActivity',
            child: Text('Owner activity'),
          ),
          DropdownMenuItem(value: 'Reversal', child: Text('Reversal')),
        ],
        onChanged: (value) => onChanged(
          filter.copyWith(displayType: value, clearDisplayType: value == null),
        ),
      );
      final accountFilter = DropdownButtonFormField<int?>(
        isExpanded: true,
        initialValue: filter.accountId,
        decoration: const InputDecoration(
          labelText: 'Account',
          border: OutlineInputBorder(),
        ),
        items: [
          const DropdownMenuItem(value: null, child: Text('All accounts')),
          for (final account in accounts)
            DropdownMenuItem(
              value: account.id,
              child: Text(
                '${account.code} ${account.name}',
                overflow: TextOverflow.ellipsis,
              ),
            ),
        ],
        onChanged: (value) => onChanged(
          filter.copyWith(accountId: value, clearAccountId: value == null),
        ),
      );

      return Padding(
        padding: const EdgeInsets.only(bottom: 12),
        child: constraints.maxWidth < 520
            ? Column(
                children: [
                  typeFilter,
                  const SizedBox(height: 8),
                  accountFilter,
                ],
              )
            : Row(
                children: [
                  Expanded(child: typeFilter),
                  const SizedBox(width: 8),
                  Expanded(child: accountFilter),
                ],
              ),
      );
    },
  );
}

class _LedgerLoadingBody extends StatelessWidget {
  const _LedgerLoadingBody();

  @override
  Widget build(BuildContext context) => const Center(
    child: SizedBox.square(
      key: Key('ledger-loading'),
      dimension: 32,
      child: CircularProgressIndicator(strokeWidth: 3),
    ),
  );
}

String _emptyMessageFor(MoneyScreenView view) => switch (view) {
  MoneyScreenView.insights => 'No insights yet.',
  MoneyScreenView.ledger => 'No transactions yet.',
  MoneyScreenView.payments => 'No payments yet.',
  MoneyScreenView.expenses => 'No expenses yet.',
};

class _TransactionCard extends ConsumerWidget {
  const _TransactionCard({required this.tx, required this.position});

  final AccountingTransaction tx;
  final MobileM3ListItemPosition position;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final isPayment = tx.isPayment;
    final signColor = isPayment
        ? Colors.green.shade700
        : Theme.of(context).colorScheme.error;

    return MobileM3ListItem(
      position: position,
      onTap: () {
        final auth = ref.read(authControllerProvider);
        final canSeeAccounting =
            auth is AuthStateAuthenticated &&
            canUseMobileCapabilityAction(
              experience: auth.activeExperience,
              capabilities: auth.capabilities,
              capability: 'money.balances.read',
              experiences: const {WorkspaceExperience.management},
            );
        Navigator.of(context).push<void>(
          MaterialPageRoute(
            builder: (_) => _LedgerTransactionDetailScreen(
              tx: tx,
              showAccounting: canSeeAccounting,
            ),
          ),
        );
      },
      title: Text(
        tx.title.isEmpty ? tx.description : tx.title,
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
        style: theme.textTheme.titleSmall?.copyWith(
          fontWeight: FontWeight.w600,
        ),
      ),
      supporting: [
        Align(
          alignment: Alignment.centerLeft,
          child: LedgerTypeBadge(type: tx.displayType),
        ),
        Text(
          [
            if (tx.counterparty != null && tx.counterparty!.isNotEmpty)
              tx.counterparty!,
            if (tx.category.isNotEmpty) tx.category,
            if (tx.sourceContext?.isNotEmpty == true) tx.sourceContext!,
            'Effective ${shortDateFmt(tx.date)}',
            'Entered ${shortDateFmt(tx.enteredAtUtc)}',
          ].where((s) => s.isNotEmpty).join(' / '),
          maxLines: 2,
          overflow: TextOverflow.ellipsis,
          style: theme.textTheme.bodySmall?.copyWith(
            color: cs.onSurfaceVariant,
          ),
        ),
      ],
      trailing: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.end,
        children: [
          Text(
            '${isPayment ? '+' : '-'}${moneyFmt(tx.amount.abs())}',
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
            ),
        ],
      ),
    );
  }
}

class _LedgerTransactionDetailScreen extends ConsumerWidget {
  const _LedgerTransactionDetailScreen({
    required this.tx,
    required this.showAccounting,
  });
  final AccountingTransaction tx;
  final bool showAccounting;
  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final publicId = showAccounting ? tx.journalEntryPublicId : null;
    final journal = publicId == null
        ? null
        : ref.watch(journalEntryProvider(publicId));
    return Scaffold(
      appBar: AppBar(title: const Text('Money details')),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          LedgerTypeBadge(type: tx.displayType),
          const SizedBox(height: 12),
          Text(tx.title, style: Theme.of(context).textTheme.headlineSmall),
          if (tx.sourceContext?.isNotEmpty == true) Text(tx.sourceContext!),
          if (tx.paidByOrTo?.isNotEmpty == true) Text(tx.paidByOrTo!),
          const SizedBox(height: 16),
          ListTile(
            title: const Text('Effective date'),
            subtitle: Text(shortDateFmt(tx.date)),
          ),
          ListTile(
            title: const Text('Entered date'),
            subtitle: Text(shortDateFmt(tx.enteredAtUtc)),
          ),
          ListTile(
            title: const Text('Amount'),
            trailing: Text(moneyFmt(tx.amount)),
          ),
          if (tx.accountName != null)
            ListTile(
              title: const Text('Account'),
              subtitle: Text('${tx.accountCode ?? ''} ${tx.accountName}'),
            ),
          if (journal != null)
            journal.when(
              loading: () => const Center(child: CircularProgressIndicator()),
              error: (error, _) =>
                  Text("Couldn't load accounting impact: $error"),
              data: (value) => AccountingImpactBlock(
                journal: value,
                onOpenJournal: () => Navigator.of(context).push<void>(
                  MaterialPageRoute(
                    builder: (_) => JournalEntryScreen(publicId: publicId!),
                  ),
                ),
              ),
            ),
        ],
      ),
    );
  }
}
