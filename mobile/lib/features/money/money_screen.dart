import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/auth/auth_controller.dart';
import '../../core/auth/auth_models.dart';
import '../../core/auth/mobile_access_policy.dart';
import '../../core/api/dio_client.dart';
import '../../core/presentation/plain_english_labels.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/widgets/mobile_m3_list.dart';
import '../accounting/accounting_book_models.dart';
import '../accounting/accounting_books_repository.dart';
import '../accounting/accounting_help.dart';
import '../accounting/accounting_help_tip.dart';
import '../accounting/accounting_models.dart';
import '../accounting/accounting_repository.dart';
import '../accounting/general_ledger_view.dart';
import '../accounting/journal_detail_sheet.dart';
import '../home/mobile_domain_chrome.dart';
import '../home/mobile_domain_navigation.dart';
import '../home/mobile_quick_action_fab.dart';
import '../home/mobile_quick_action_helpers.dart';
import '../payments/payment_detail_screen.dart';
import '../payments/tenant_account_receipt_flow.dart';
import 'expense_detail_screen.dart';
import 'expense_form_sheet.dart';
import 'money_format.dart';
import 'money_repository.dart';
import 'overdue_screen.dart';
import 'transaction_models.dart';
import 'transactions_controller.dart';

/// The legacy values remain public because shell destinations and existing
/// deep-link tests still use them. The visible Money surface now uses the four
/// M1 tabs below.
enum MoneyScreenView {
  overview,
  activity,
  ledger,
  reports,
  insights,
  payments,
  expenses,
}

enum MoneyTab { overview, activity, ledger, reports }

class MoneyPositionRequest {
  const MoneyPositionRequest({required this.label, this.from, this.to});

  final String label;
  final DateTime? from;
  final DateTime? to;

  @override
  bool operator ==(Object other) =>
      other is MoneyPositionRequest &&
      other.label == label &&
      other.from == from &&
      other.to == to;

  @override
  int get hashCode => Object.hash(label, from, to);
}

final moneyPositionProvider = FutureProvider.autoDispose
    .family<MoneyPositionResponse, MoneyPositionRequest>((ref, request) {
      return ref
          .watch(accountingBooksRepositoryProvider)
          .moneyPosition(from: request.from, to: request.to);
    });

final mobileIncomeStatementProvider =
    FutureProvider.autoDispose<FinancialStatementResponse>((ref) {
      return ref.watch(accountingBooksRepositoryProvider).incomeStatement();
    });

final mobileBalanceSheetProvider =
    FutureProvider.autoDispose<FinancialStatementResponse>((ref) {
      return ref.watch(accountingBooksRepositoryProvider).balanceSheet();
    });

final mobileTrialBalanceProvider =
    FutureProvider.autoDispose<TrialBalanceResponse>((ref) {
      return ref.watch(accountingBooksRepositoryProvider).trialBalance();
    });

final mobileCashFlowProvider =
    FutureProvider.autoDispose<CashFlowSummaryResponse>((ref) {
      return ref.watch(accountingBooksRepositoryProvider).cashFlow();
    });

class _ScheduleECategoryAmount {
  const _ScheduleECategoryAmount({
    required this.category,
    required this.amount,
  });

  final String category;
  final double amount;

  factory _ScheduleECategoryAmount.fromJson(Map<String, dynamic> json) =>
      _ScheduleECategoryAmount(
        category: json['category'] as String? ?? '',
        amount: (json['amount'] as num?)?.toDouble() ?? 0,
      );
}

class _ScheduleEPropertyReport {
  const _ScheduleEPropertyReport({
    required this.propertyName,
    required this.rentalIncome,
    required this.expensesByCategory,
    required this.totalExpenses,
    required this.netIncome,
  });

  final String propertyName;
  final double rentalIncome;
  final List<_ScheduleECategoryAmount> expensesByCategory;
  final double totalExpenses;
  final double netIncome;

  factory _ScheduleEPropertyReport.fromJson(Map<String, dynamic> json) =>
      _ScheduleEPropertyReport(
        propertyName: json['propertyName'] as String? ?? '',
        rentalIncome: (json['rentalIncome'] as num?)?.toDouble() ?? 0,
        expensesByCategory: ((json['expensesByCategory'] as List?) ?? const [])
            .whereType<Map<String, dynamic>>()
            .map(_ScheduleECategoryAmount.fromJson)
            .toList(growable: false),
        totalExpenses: (json['totalExpenses'] as num?)?.toDouble() ?? 0,
        netIncome: (json['netIncome'] as num?)?.toDouble() ?? 0,
      );
}

class _ScheduleEReport {
  const _ScheduleEReport({
    required this.year,
    required this.properties,
    required this.expensesByCategory,
    required this.totalRentalIncome,
    required this.totalExpenses,
    required this.netIncome,
    required this.warning,
  });

  final int year;
  final List<_ScheduleEPropertyReport> properties;
  final List<_ScheduleECategoryAmount> expensesByCategory;
  final double totalRentalIncome;
  final double totalExpenses;
  final double netIncome;
  final String warning;

  factory _ScheduleEReport.fromJson(Map<String, dynamic> json) {
    final unallocated = json['unallocatedActivity'];
    return _ScheduleEReport(
      year: (json['year'] as num?)?.toInt() ?? DateTime.now().year,
      properties: ((json['properties'] as List?) ?? const [])
          .whereType<Map<String, dynamic>>()
          .map(_ScheduleEPropertyReport.fromJson)
          .toList(growable: false),
      expensesByCategory: ((json['expensesByCategory'] as List?) ?? const [])
          .whereType<Map<String, dynamic>>()
          .map(_ScheduleECategoryAmount.fromJson)
          .toList(growable: false),
      totalRentalIncome: (json['totalRentalIncome'] as num?)?.toDouble() ?? 0,
      totalExpenses: (json['totalExpenses'] as num?)?.toDouble() ?? 0,
      netIncome: (json['netIncome'] as num?)?.toDouble() ?? 0,
      warning: unallocated is Map<String, dynamic>
          ? unallocated['warning'] as String? ?? ''
          : '',
    );
  }
}

final mobileScheduleEProvider = FutureProvider.autoDispose<_ScheduleEReport>((
  ref,
) async {
  try {
    final response = await ref
        .watch(dioProvider)
        .get<Map<String, dynamic>>(
          '/accounting/schedule-e',
          queryParameters: {'year': DateTime.now().year},
        );
    final data = response.data;
    if (data == null) {
      throw const ApiException(
        statusCode: 0,
        message: 'Empty response from server.',
      );
    }
    return _ScheduleEReport.fromJson(data);
  } on DioException catch (error) {
    throw ApiException.fromDioException(error);
  }
});

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
  late MoneyTab _tab;
  late MoneyScreenView _legacyActivityView;

  @override
  void initState() {
    super.initState();
    _tab = _tabForInitialView(widget.initialView);
    _legacyActivityView = widget.initialView == MoneyScreenView.expenses
        ? MoneyScreenView.expenses
        : MoneyScreenView.payments;
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
    ref.invalidate(moneyPositionProvider);
    ref.read(transactionsProvider.notifier).refresh();
  }

  void _addExpense() {
    showCreateExpenseSheet(context, ref, onSaved: _refreshAfterManualEntry);
  }

  Future<void> _addPayment() async {
    final result = await showGlobalRecordTenantReceiptFlow(context, ref);
    if (result != null && mounted) _refreshAfterManualEntry();
  }

  @override
  Widget build(BuildContext context) {
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

    final legacyActivityEntry =
        widget.showTransactionSelector &&
        (widget.initialView == MoneyScreenView.payments ||
            widget.initialView == MoneyScreenView.expenses);
    final legacyLedgerEntry =
        !widget.showTransactionSelector &&
        widget.initialView == MoneyScreenView.ledger;

    return Scaffold(
      appBar: mobileDomainRootAppBar(context, title: const Text('Money')),
      floatingActionButton: _tab == MoneyTab.overview
          ? null
          : MobileQuickActionFab(
              heroTag: 'money-ledger-actions-fab',
              primaryActions: manualActions,
              onChat: () => openMobileAssistant(context),
              onRecord: () => openMobileRecord(context),
              onScan: () => openMobileScan(context),
            ),
      body: legacyActivityEntry
          ? Column(
              children: [
                Padding(
                  padding: const EdgeInsets.fromLTRB(16, 12, 16, 10),
                  child: _MoneyViewSelector(
                    value: _legacyActivityView,
                    onChanged: (view) =>
                        setState(() => _legacyActivityView = view),
                  ),
                ),
                Expanded(child: _LedgerTab(view: _legacyActivityView)),
              ],
            )
          : legacyLedgerEntry
          ? const _LedgerTab(view: MoneyScreenView.ledger)
          : Column(
              children: [
                _MoneyTabBar(
                  selected: _tab,
                  onChanged: (tab) => setState(() => _tab = tab),
                ),
                Expanded(child: _tabBody()),
              ],
            ),
    );
  }

  Widget _tabBody() => switch (_tab) {
    MoneyTab.overview => _OverviewTab(
      onPastDueTap: _openOverdue,
      onLegacyRetry: () => ref.invalidate(moneySnapshotProvider),
    ),
    MoneyTab.activity => const _LedgerTab(view: MoneyScreenView.activity),
    MoneyTab.ledger => const GeneralLedgerView(),
    MoneyTab.reports => const _ReportsTab(),
  };
}

MoneyTab _tabForInitialView(MoneyScreenView view) => switch (view) {
  MoneyScreenView.overview || MoneyScreenView.insights => MoneyTab.overview,
  MoneyScreenView.activity ||
  MoneyScreenView.payments ||
  MoneyScreenView.expenses => MoneyTab.activity,
  MoneyScreenView.ledger => MoneyTab.ledger,
  MoneyScreenView.reports => MoneyTab.reports,
};

class _MoneyTabBar extends StatelessWidget {
  const _MoneyTabBar({required this.selected, required this.onChanged});

  final MoneyTab selected;
  final ValueChanged<MoneyTab> onChanged;

  @override
  Widget build(BuildContext context) {
    return SingleChildScrollView(
      scrollDirection: Axis.horizontal,
      padding: const EdgeInsets.fromLTRB(12, 8, 12, 4),
      child: Row(
        children: [
          _MoneyTabChip(
            tab: MoneyTab.overview,
            selected: selected,
            label: 'Overview',
            onChanged: onChanged,
          ),
          _MoneyTabChip(
            tab: MoneyTab.activity,
            selected: selected,
            label: 'Activity',
            onChanged: onChanged,
          ),
          _MoneyTabChip(
            tab: MoneyTab.ledger,
            selected: selected,
            label: 'Ledger',
            onChanged: onChanged,
          ),
          _MoneyTabChip(
            tab: MoneyTab.reports,
            selected: selected,
            label: 'Reports',
            onChanged: onChanged,
          ),
        ],
      ),
    );
  }
}

class _MoneyTabChip extends StatelessWidget {
  const _MoneyTabChip({
    required this.tab,
    required this.selected,
    required this.label,
    required this.onChanged,
  });

  final MoneyTab tab;
  final MoneyTab selected;
  final String label;
  final ValueChanged<MoneyTab> onChanged;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 4),
      child: ChoiceChip(
        key: Key('money-tab-${tab.name}'),
        label: Text(label),
        selected: selected == tab,
        onSelected: (_) => onChanged(tab),
      ),
    );
  }
}

class _OverviewTab extends ConsumerStatefulWidget {
  const _OverviewTab({required this.onPastDueTap, required this.onLegacyRetry});

  final VoidCallback onPastDueTap;
  final VoidCallback onLegacyRetry;

  @override
  ConsumerState<_OverviewTab> createState() => _OverviewTabState();
}

class _OverviewTabState extends ConsumerState<_OverviewTab> {
  late MoneyPositionRequest _request;

  @override
  void initState() {
    super.initState();
    _request = _thisMonthRequest();
  }

  @override
  Widget build(BuildContext context) {
    final positionAsync = ref.watch(moneyPositionProvider(_request));
    final legacyAsync = ref.watch(moneySnapshotProvider);

    if (positionAsync.hasValue && positionAsync.value != null) {
      final position = positionAsync.value!;
      return RefreshIndicator(
        onRefresh: () async => ref.invalidate(moneyPositionProvider(_request)),
        child: ListView(
          physics: const AlwaysScrollableScrollPhysics(),
          padding: const EdgeInsets.fromLTRB(16, 8, 16, 32),
          children: [
            _CashPositionCard(position: position),
            const SizedBox(height: 12),
            _PositionFacts(position: position),
            const SizedBox(height: 12),
            _PeriodFacts(
              position: position,
              request: _request,
              onRequestChanged: (request) => setState(() => _request = request),
            ),
          ],
        ),
      );
    }

    if (legacyAsync.hasValue || positionAsync.hasError) {
      return _MoneyInsightsTab(
        snapshotAsync: legacyAsync,
        onRetry: widget.onLegacyRetry,
        onDetails: () {},
        onPastDueTap: widget.onPastDueTap,
      );
    }

    if (positionAsync.isLoading) return const _OverviewLoading();
    return _OverviewError(
      onRetry: () => ref.invalidate(moneyPositionProvider(_request)),
    );
  }
}

MoneyPositionRequest _thisMonthRequest() {
  final now = DateTime.now().toUtc();
  return MoneyPositionRequest(
    label: 'This month',
    from: DateTime.utc(now.year, now.month),
    to: now,
  );
}

MoneyPositionRequest _lastMonthRequest() {
  final now = DateTime.now().toUtc();
  final currentMonth = DateTime.utc(now.year, now.month);
  return MoneyPositionRequest(
    label: 'Last month',
    from: DateTime.utc(now.year, now.month - 1),
    to: currentMonth.subtract(const Duration(days: 1)),
  );
}

MoneyPositionRequest _yearToDateRequest() {
  final now = DateTime.now().toUtc();
  return MoneyPositionRequest(
    label: 'Year to date',
    from: DateTime.utc(now.year),
    to: now,
  );
}

class _CashPositionCard extends StatelessWidget {
  const _CashPositionCard({required this.position});

  final MoneyPositionResponse position;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final depositsExceedCash =
        position.tenantDepositsHeld > position.totalCashOnHand;
    return Card(
      color: cs.primaryContainer,
      child: Padding(
        padding: const EdgeInsets.fromLTRB(18, 18, 18, 16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    'Cash on hand',
                    style: theme.textTheme.labelLarge?.copyWith(
                      color: cs.onPrimaryContainer,
                      fontWeight: FontWeight.w800,
                    ),
                  ),
                ),
                const AccountingHelpTipButton(
                  topic: AccountingHelpTopic.cashPosition,
                ),
              ],
            ),
            const SizedBox(height: 4),
            Text(
              moneyFmt(position.totalCashOnHand),
              style: theme.textTheme.displaySmall?.copyWith(
                color: cs.onPrimaryContainer,
                fontWeight: FontWeight.w800,
                fontFeatures: const [FontFeature.tabularFigures()],
              ),
            ),
            const SizedBox(height: 4),
            Text(
              'As of ${dateFmt(position.asOfUtc.toLocal())}',
              style: theme.textTheme.bodySmall?.copyWith(
                color: cs.onPrimaryContainer.withValues(alpha: 0.82),
              ),
            ),
            const SizedBox(height: 16),
            _CashBridgeRow(
              label: 'Total cash on hand',
              value: moneyFmt(position.totalCashOnHand),
              color: cs.onPrimaryContainer,
            ),
            _CashBridgeRow(
              label: 'Tenant deposits held',
              value: _signedMoney(-position.tenantDepositsHeld),
              color: cs.onPrimaryContainer,
            ),
            const Divider(height: 20),
            _CashBridgeRow(
              label: 'Cash after tenant deposits',
              value: moneyFmt(position.cashAfterTenantDeposits),
              color: cs.onPrimaryContainer,
              emphasized: true,
            ),
            if (depositsExceedCash) ...[
              const SizedBox(height: 12),
              Container(
                padding: const EdgeInsets.all(10),
                decoration: BoxDecoration(
                  color: cs.tertiaryContainer,
                  borderRadius: BorderRadius.circular(12),
                ),
                child: Text(
                  'Tenant deposits held (${moneyFmt(position.tenantDepositsHeld)}) exceed total cash on hand (${moneyFmt(position.totalCashOnHand)}).',
                  style: TextStyle(color: cs.onTertiaryContainer),
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }
}

class _CashBridgeRow extends StatelessWidget {
  const _CashBridgeRow({
    required this.label,
    required this.value,
    required this.color,
    this.emphasized = false,
  });

  final String label;
  final String value;
  final Color color;
  final bool emphasized;

  @override
  Widget build(BuildContext context) {
    final style = Theme.of(context).textTheme.bodyMedium?.copyWith(
      color: color,
      fontWeight: emphasized ? FontWeight.w800 : FontWeight.w600,
      fontFeatures: const [FontFeature.tabularFigures()],
    );
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 2),
      child: Row(
        children: [
          Expanded(child: Text(label, style: style)),
          Text(value, style: style),
        ],
      ),
    );
  }
}

class _PositionFacts extends ConsumerWidget {
  const _PositionFacts({required this.position});

  final MoneyPositionResponse position;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final mode = ref.watch(accountingDetailModeProvider);
    final facts = <({String label, String value, IconData icon})>[
      (
        label: 'Rent still owed',
        value: moneyFmt(position.rentStillOwed),
        icon: Icons.receipt_long_outlined,
      ),
      (
        label: 'Loan balance',
        value: moneyFmt(position.loanBalance),
        icon: Icons.account_balance_outlined,
      ),
      if (mode == AccountingDetailMode.advanced)
        (
          label: 'Book equity',
          value: moneyFmt(position.bookEquity),
          icon: Icons.insights_outlined,
        ),
    ];
    return GridView.builder(
      shrinkWrap: true,
      physics: const NeverScrollableScrollPhysics(),
      itemCount: facts.length,
      gridDelegate: const SliverGridDelegateWithFixedCrossAxisCount(
        crossAxisCount: 2,
        mainAxisSpacing: 8,
        crossAxisSpacing: 8,
        childAspectRatio: 1.8,
      ),
      itemBuilder: (_, index) {
        final fact = facts[index];
        return Card(
          child: Padding(
            padding: const EdgeInsets.all(12),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Icon(fact.icon, size: 18),
                const Spacer(),
                Text(fact.label, style: Theme.of(context).textTheme.labelSmall),
                const SizedBox(height: 2),
                Text(
                  fact.value,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: Theme.of(context).textTheme.titleSmall?.copyWith(
                    fontWeight: FontWeight.w800,
                    fontFeatures: const [FontFeature.tabularFigures()],
                  ),
                ),
              ],
            ),
          ),
        );
      },
    );
  }
}

class _PeriodFacts extends StatelessWidget {
  const _PeriodFacts({
    required this.position,
    required this.request,
    required this.onRequestChanged,
  });

  final MoneyPositionResponse position;
  final MoneyPositionRequest request;
  final ValueChanged<MoneyPositionRequest> onRequestChanged;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 14, 16, 12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            DropdownButtonFormField<String>(
              key: const Key('money-overview-period'),
              initialValue: request.label,
              decoration: const InputDecoration(
                labelText: 'Period facts',
                suffixIcon: AccountingHelpTipButton(
                  topic: AccountingHelpTopic.cashFlow,
                ),
              ),
              items: const [
                DropdownMenuItem(
                  value: 'This month',
                  child: Text('This month'),
                ),
                DropdownMenuItem(
                  value: 'Last month',
                  child: Text('Last month'),
                ),
                DropdownMenuItem(
                  value: 'Year to date',
                  child: Text('Year to date'),
                ),
                DropdownMenuItem(value: 'Custom', child: Text('Custom')),
              ],
              onChanged: (value) async {
                if (value == null) return;
                if (value == 'This month') {
                  onRequestChanged(_thisMonthRequest());
                }
                if (value == 'Last month') {
                  onRequestChanged(_lastMonthRequest());
                }
                if (value == 'Year to date') {
                  onRequestChanged(_yearToDateRequest());
                }
                if (value == 'Custom') {
                  final range = await showDateRangePicker(
                    context: context,
                    firstDate: DateTime(2000),
                    lastDate: DateTime(2100),
                    initialDateRange: request.from != null && request.to != null
                        ? DateTimeRange(start: request.from!, end: request.to!)
                        : null,
                  );
                  if (range != null) {
                    onRequestChanged(
                      MoneyPositionRequest(
                        label: 'Custom',
                        from: range.start,
                        to: range.end,
                      ),
                    );
                  }
                }
              },
            ),
            const SizedBox(height: 12),
            _FactLine(
              label: 'Cash received',
              value: moneyFmt(position.cashReceived),
            ),
            _FactLine(
              label: 'Cash paid',
              value: _signedMoney(-position.cashPaid),
            ),
            _FactLine(
              label: 'Net cash movement',
              value: _signedMoney(position.netCashMovement),
            ),
            _FactLine(
              label: 'Profit / loss',
              value: _signedMoney(position.profitOrLoss),
            ),
            const SizedBox(height: 6),
            Text(
              '${dateFmt(position.fromUtc.toLocal())} – ${dateFmt(position.toUtc.toLocal())}',
              style: theme.textTheme.bodySmall?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _FactLine extends StatelessWidget {
  const _FactLine({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 4),
    child: Row(
      children: [
        Expanded(child: Text(label)),
        Text(
          value,
          style: Theme.of(context).textTheme.bodyMedium?.copyWith(
            fontWeight: FontWeight.w700,
            fontFeatures: const [FontFeature.tabularFigures()],
          ),
        ),
      ],
    ),
  );
}

class _OverviewLoading extends StatelessWidget {
  const _OverviewLoading();

  @override
  Widget build(BuildContext context) {
    final color = Theme.of(context).colorScheme.surfaceContainerHigh;
    return ListView(
      padding: const EdgeInsets.fromLTRB(16, 8, 16, 24),
      children: [
        Container(height: 270, decoration: _skeletonDecoration(color)),
        const SizedBox(height: 12),
        Row(
          children: [
            Expanded(
              child: Container(
                height: 132,
                decoration: _skeletonDecoration(color),
              ),
            ),
            const SizedBox(width: 8),
            Expanded(
              child: Container(
                height: 132,
                decoration: _skeletonDecoration(color),
              ),
            ),
          ],
        ),
        const SizedBox(height: 12),
        Container(height: 220, decoration: _skeletonDecoration(color)),
      ],
    );
  }
}

class _OverviewError extends StatelessWidget {
  const _OverviewError({required this.onRetry});

  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) => ListView(
    padding: const EdgeInsets.fromLTRB(16, 80, 16, 24),
    children: [
      Icon(Icons.error_outline, color: Theme.of(context).colorScheme.error),
      const SizedBox(height: 12),
      const Center(child: Text("Couldn't load the money overview.")),
      const SizedBox(height: 12),
      Center(
        child: FilledButton.tonal(
          onPressed: onRetry,
          child: const Text('Retry'),
        ),
      ),
    ],
  );
}

BoxDecoration _skeletonDecoration(Color color) =>
    BoxDecoration(color: color, borderRadius: BorderRadius.circular(18));

String _signedMoney(num value) =>
    value < 0 ? '-${moneyFmt(value.abs())}' : moneyFmt(value);

enum _MobileReportKind {
  profitAndLoss,
  balanceSheet,
  trialBalance,
  cashFlow,
  scheduleE,
}

class _ReportsTab extends StatefulWidget {
  const _ReportsTab();

  @override
  State<_ReportsTab> createState() => _ReportsTabState();
}

class _ReportsTabState extends State<_ReportsTab> {
  _MobileReportKind? _selected;

  @override
  Widget build(BuildContext context) {
    final selected = _selected;
    if (selected != null) {
      return Column(
        children: [
          Align(
            alignment: Alignment.centerLeft,
            child: TextButton.icon(
              onPressed: () => setState(() => _selected = null),
              icon: const Icon(Icons.arrow_back),
              label: const Text('All reports'),
            ),
          ),
          Expanded(child: _ReportDetail(kind: selected)),
        ],
      );
    }

    return ListView(
      padding: const EdgeInsets.fromLTRB(16, 8, 16, 28),
      children: [
        const _ReportsIntro(),
        const SizedBox(height: 8),
        for (final report in const [
          (
            kind: _MobileReportKind.profitAndLoss,
            title: 'Profit & Loss',
            subtitle: 'Income and expenses from the server statement.',
            icon: Icons.bar_chart_outlined,
          ),
          (
            kind: _MobileReportKind.balanceSheet,
            title: 'Balance sheet',
            subtitle: 'Assets, liabilities, and equity as of the server date.',
            icon: Icons.account_balance_outlined,
          ),
          (
            kind: _MobileReportKind.trialBalance,
            title: 'Trial balance',
            subtitle: 'Read-only debit and credit balances.',
            icon: Icons.fact_check_outlined,
          ),
          (
            kind: _MobileReportKind.cashFlow,
            title: 'Cash flow',
            subtitle: 'Cash in, operating costs, debt service, and net cash.',
            icon: Icons.waterfall_chart_outlined,
          ),
          (
            kind: _MobileReportKind.scheduleE,
            title: 'Schedule E',
            subtitle: 'Server-calculated rental income and tax categories.',
            icon: Icons.receipt_long_outlined,
          ),
        ])
          Card(
            child: ListTile(
              key: Key('report-link-${report.kind.name}'),
              leading: Icon(report.icon),
              title: Text(report.title),
              subtitle: Text(report.subtitle),
              trailing: const Icon(Icons.chevron_right),
              onTap: () => setState(() => _selected = report.kind),
            ),
          ),
      ],
    );
  }
}

class _ReportsIntro extends StatelessWidget {
  const _ReportsIntro();

  @override
  Widget build(BuildContext context) => Card(
    child: Padding(
      padding: const EdgeInsets.all(16),
      child: Text(
        'Reports are read-only. Open one to view the statement fields returned by the server.',
        style: Theme.of(context).textTheme.bodyMedium,
      ),
    ),
  );
}

class _ReportDetail extends ConsumerWidget {
  const _ReportDetail({required this.kind});

  final _MobileReportKind kind;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    return switch (kind) {
      _MobileReportKind.profitAndLoss => _StatementReportBody(
        title: 'Profit & Loss',
        asyncValue: ref.watch(mobileIncomeStatementProvider),
      ),
      _MobileReportKind.balanceSheet => _StatementReportBody(
        title: 'Balance sheet',
        asyncValue: ref.watch(mobileBalanceSheetProvider),
      ),
      _MobileReportKind.trialBalance => _TrialBalanceReportBody(
        asyncValue: ref.watch(mobileTrialBalanceProvider),
      ),
      _MobileReportKind.cashFlow => _CashFlowReportBody(
        asyncValue: ref.watch(mobileCashFlowProvider),
      ),
      _MobileReportKind.scheduleE => _ScheduleEReportBody(
        asyncValue: ref.watch(mobileScheduleEProvider),
      ),
    };
  }
}

class _StatementReportBody extends StatelessWidget {
  const _StatementReportBody({required this.title, required this.asyncValue});

  final String title;
  final AsyncValue<FinancialStatementResponse> asyncValue;

  @override
  Widget build(BuildContext context) => asyncValue.when(
    loading: () => const _ReportLoading(),
    error: (error, _) => _ReportError(error: error),
    data: (statement) => ListView(
      padding: const EdgeInsets.fromLTRB(16, 4, 16, 28),
      children: [
        Text(title, style: Theme.of(context).textTheme.titleLarge),
        const SizedBox(height: 12),
        for (final section in statement.sections)
          _StatementSectionView(section: section),
        _ReportTotal(label: 'Total', value: statement.totals.total),
        if (statement.totals.isBalanced != null)
          _ReportStatus(
            label: statement.totals.isBalanced! ? 'Balanced ✓' : 'Not balanced',
            positive: statement.totals.isBalanced!,
          ),
      ],
    ),
  );
}

class _StatementSectionView extends StatelessWidget {
  const _StatementSectionView({required this.section});

  final StatementSection section;

  @override
  Widget build(BuildContext context) => Card(
    child: Padding(
      padding: const EdgeInsets.fromLTRB(16, 14, 16, 10),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(section.label, style: Theme.of(context).textTheme.titleSmall),
          const SizedBox(height: 8),
          for (final row in section.rows)
            _ReportLine(label: row.accountName, value: row.amount),
          const Divider(height: 18),
          _ReportLine(
            label: 'Section total',
            value: section.subtotal,
            emphasized: true,
          ),
        ],
      ),
    ),
  );
}

class _TrialBalanceReportBody extends StatelessWidget {
  const _TrialBalanceReportBody({required this.asyncValue});

  final AsyncValue<TrialBalanceResponse> asyncValue;

  @override
  Widget build(BuildContext context) => asyncValue.when(
    loading: () => const _ReportLoading(),
    error: (error, _) => _ReportError(error: error),
    data: (trial) => ListView(
      padding: const EdgeInsets.fromLTRB(16, 4, 16, 28),
      children: [
        Text('Trial balance', style: Theme.of(context).textTheme.titleLarge),
        const SizedBox(height: 12),
        for (final row in trial.rows)
          Card(
            child: Padding(
              padding: const EdgeInsets.all(14),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    row.accountName,
                    style: Theme.of(context).textTheme.titleSmall,
                  ),
                  const SizedBox(height: 6),
                  _ReportLine(label: 'Debit', value: row.debitBalance),
                  _ReportLine(label: 'Credit', value: row.creditBalance),
                ],
              ),
            ),
          ),
        _ReportLine(
          label: 'Total debits',
          value: trial.totalDebits,
          emphasized: true,
        ),
        _ReportLine(
          label: 'Total credits',
          value: trial.totalCredits,
          emphasized: true,
        ),
        _ReportStatus(
          label: trial.isBalanced ? 'Balanced ✓' : 'Not balanced',
          positive: trial.isBalanced,
        ),
      ],
    ),
  );
}

class _CashFlowReportBody extends StatelessWidget {
  const _CashFlowReportBody({required this.asyncValue});

  final AsyncValue<CashFlowSummaryResponse> asyncValue;

  @override
  Widget build(BuildContext context) => asyncValue.when(
    loading: () => const _ReportLoading(),
    error: (error, _) => _ReportError(error: error),
    data: (cashFlow) => ListView(
      padding: const EdgeInsets.fromLTRB(16, 4, 16, 28),
      children: [
        Text('Cash flow', style: Theme.of(context).textTheme.titleLarge),
        const SizedBox(height: 6),
        Text(
          '${dateFmt(cashFlow.from.toLocal())} – ${dateFmt(cashFlow.to.toLocal())}',
        ),
        const SizedBox(height: 12),
        _ReportLine(label: 'Operating cash in', value: cashFlow.totalIncome),
        _ReportLine(
          label: 'Operating expenses',
          value: cashFlow.totalOperatingExpenses,
        ),
        _ReportLine(
          label: 'Net operating income',
          value: cashFlow.totalNoi,
          emphasized: true,
        ),
        _ReportLine(label: 'Debt service', value: cashFlow.totalDebtService),
        _ReportLine(
          label: 'Net cash flow',
          value: cashFlow.totalCashFlow,
          emphasized: true,
        ),
        const SizedBox(height: 12),
        for (final property in cashFlow.properties)
          Card(
            child: Padding(
              padding: const EdgeInsets.all(14),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    property.propertyName,
                    style: Theme.of(context).textTheme.titleSmall,
                  ),
                  _ReportLine(label: 'Cash flow', value: property.cashFlow),
                ],
              ),
            ),
          ),
      ],
    ),
  );
}

class _ScheduleEReportBody extends StatelessWidget {
  const _ScheduleEReportBody({required this.asyncValue});

  final AsyncValue<_ScheduleEReport> asyncValue;

  @override
  Widget build(BuildContext context) => asyncValue.when(
    loading: () => const _ReportLoading(),
    error: (error, _) => _ReportError(error: error),
    data: (report) => ListView(
      padding: const EdgeInsets.fromLTRB(16, 4, 16, 28),
      children: [
        Text(
          'Schedule E · ${report.year}',
          style: Theme.of(context).textTheme.titleLarge,
        ),
        const SizedBox(height: 12),
        _ReportLine(label: 'Rental income', value: report.totalRentalIncome),
        _ReportLine(label: 'Total expenses', value: report.totalExpenses),
        _ReportLine(
          label: 'Net income',
          value: report.netIncome,
          emphasized: true,
        ),
        if (report.warning.trim().isNotEmpty)
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 12),
            child: Text(report.warning),
          ),
        for (final property in report.properties)
          Card(
            child: Padding(
              padding: const EdgeInsets.all(14),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    property.propertyName,
                    style: Theme.of(context).textTheme.titleSmall,
                  ),
                  _ReportLine(
                    label: 'Rental income',
                    value: property.rentalIncome,
                  ),
                  for (final category in property.expensesByCategory)
                    _ReportLine(
                      label: plainEnglishLabel(category.category),
                      value: category.amount,
                    ),
                  _ReportLine(
                    label: 'Net income',
                    value: property.netIncome,
                    emphasized: true,
                  ),
                ],
              ),
            ),
          ),
        if (report.expensesByCategory.isNotEmpty) ...[
          const SizedBox(height: 8),
          Text(
            'Expense categories',
            style: Theme.of(context).textTheme.titleSmall,
          ),
          for (final category in report.expensesByCategory)
            _ReportLine(
              label: plainEnglishLabel(category.category),
              value: category.amount,
            ),
        ],
      ],
    ),
  );
}

class _ReportLine extends StatelessWidget {
  const _ReportLine({
    required this.label,
    required this.value,
    this.emphasized = false,
  });

  final String label;
  final double value;
  final bool emphasized;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 4),
    child: Row(
      children: [
        Expanded(child: Text(label)),
        Text(
          _signedMoney(value),
          style: Theme.of(context).textTheme.bodyMedium?.copyWith(
            fontWeight: emphasized ? FontWeight.w800 : FontWeight.w600,
            fontFeatures: const [FontFeature.tabularFigures()],
          ),
        ),
      ],
    ),
  );
}

class _ReportTotal extends StatelessWidget {
  const _ReportTotal({required this.label, required this.value});

  final String label;
  final double value;

  @override
  Widget build(BuildContext context) =>
      _ReportLine(label: label, value: value, emphasized: true);
}

class _ReportStatus extends StatelessWidget {
  const _ReportStatus({required this.label, required this.positive});

  final String label;
  final bool positive;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(top: 12),
    child: Text(
      label,
      style: Theme.of(context).textTheme.titleSmall?.copyWith(
        color: positive
            ? Theme.of(context).colorScheme.primary
            : Theme.of(context).colorScheme.error,
        fontWeight: FontWeight.w800,
      ),
    ),
  );
}

class _ReportLoading extends StatelessWidget {
  const _ReportLoading();

  @override
  Widget build(BuildContext context) {
    final color = Theme.of(context).colorScheme.surfaceContainerHigh;
    return ListView(
      padding: const EdgeInsets.fromLTRB(16, 8, 16, 28),
      children: [
        for (var index = 0; index < 5; index++)
          Container(
            height: 48,
            margin: const EdgeInsets.only(bottom: 8),
            decoration: _skeletonDecoration(color),
          ),
      ],
    );
  }
}

class _ReportError extends StatelessWidget {
  const _ReportError({required this.error});

  final Object error;

  @override
  Widget build(BuildContext context) => ListView(
    padding: const EdgeInsets.fromLTRB(16, 80, 16, 24),
    children: [
      Center(
        child: Text(
          error is ApiException
              ? (error as ApiException).message
              : "Couldn't load this report.",
          textAlign: TextAlign.center,
        ),
      ),
    ],
  );
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
    MoneyScreenView.overview => null,
    MoneyScreenView.activity => null,
    MoneyScreenView.reports => null,
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
              itemCount: state.items.length + (state.loadingMore ? 1 : 0),
              separatorBuilder: (_, index) {
                if (index >= state.items.length - 1) {
                  return const SizedBox(height: 12);
                }
                return const MobileM3ListDivider();
              },
              itemBuilder: (_, i) {
                if (i >= state.items.length) {
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
                  tx: state.items[i],
                  position: MobileM3ListItemPositionForIndex.forIndex(
                    i,
                    state.items.length,
                  ),
                );
              },
            ),
    );
  }
}

class _LedgerLoadingBody extends StatelessWidget {
  const _LedgerLoadingBody();

  @override
  Widget build(BuildContext context) {
    final color = Theme.of(context).colorScheme.surfaceContainerHigh;
    return ListView(
      padding: const EdgeInsets.fromLTRB(16, 8, 16, 24),
      children: [
        for (var index = 0; index < 3; index++)
          Container(
            key: index == 0 ? const Key('ledger-loading') : null,
            height: 96,
            margin: const EdgeInsets.only(bottom: 8),
            decoration: _skeletonDecoration(color),
          ),
      ],
    );
  }
}

String _emptyMessageFor(MoneyScreenView view) => switch (view) {
  MoneyScreenView.overview => 'No activity yet.',
  MoneyScreenView.activity => 'No activity yet.',
  MoneyScreenView.reports => 'No report activity yet.',
  MoneyScreenView.insights => 'No insights yet.',
  MoneyScreenView.ledger => 'No transactions yet.',
  MoneyScreenView.payments => 'No payments yet.',
  MoneyScreenView.expenses => 'No expenses yet.',
};

class _TransactionCard extends StatelessWidget {
  const _TransactionCard({required this.tx, required this.position});

  final AccountingTransaction tx;
  final MobileM3ListItemPosition position;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final isPayment = tx.isPayment;
    final isTenantAccountEntry = tx.isTenantAccountEntry;
    final signColor = isPayment ? Colors.green.shade700 : cs.error;

    return MobileM3ListItem(
      position: position,
      onTap: () {
        final accountId = tx.tenantAccountId;
        if (isTenantAccountEntry && accountId == null) return;
        if (!isTenantAccountEntry && !tx.isExpense) return;
        final MobileDetailBuilder detailBuilder = isTenantAccountEntry
            ? (BuildContext _) => PaymentDetailScreen(
                tenantAccountId: accountId!,
                tenantLedgerEntryId: tx.id,
              )
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

        if (isTenantAccountEntry) {
          Navigator.of(context).push<void>(
            MaterialPageRoute<void>(
              builder: (_) => PaymentDetailScreen(
                tenantAccountId: accountId!,
                tenantLedgerEntryId: tx.id,
              ),
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
      leading: MobileM3LeadingIcon(
        icon: isPayment ? Icons.south_west : Icons.north_east,
        backgroundColor: isPayment ? cs.primaryContainer : cs.errorContainer,
        foregroundColor: isPayment
            ? cs.onPrimaryContainer
            : cs.onErrorContainer,
      ),
      title: Text(
        tx.description.isEmpty ? tx.kind : tx.description,
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
        style: theme.textTheme.titleSmall?.copyWith(
          fontWeight: FontWeight.w600,
        ),
      ),
      supporting: [
        Text(
          [
            if (tx.counterparty != null && tx.counterparty!.isNotEmpty)
              tx.counterparty!,
            if (tx.category.isNotEmpty) tx.category,
            shortDateFmt(tx.date),
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
    );
  }
}
