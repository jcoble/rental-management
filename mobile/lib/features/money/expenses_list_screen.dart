import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/auth/auth_controller.dart';
import '../../core/auth/auth_models.dart';
import '../../core/auth/mobile_access_policy.dart';
import '../../core/widgets/mobile_grid_controls.dart';
import '../../core/widgets/mobile_m3_list.dart';
import '../home/mobile_domain_chrome.dart';
import '../home/mobile_quick_action_fab.dart';
import '../home/mobile_quick_action_helpers.dart';
import 'expense_detail_screen.dart';
import 'expense_form_sheet.dart';
import 'expense_models.dart';
import '../../core/presentation/formatting.dart';
import 'money_repository.dart';

/// Lists the portfolio's expenses, newest first. Each row opens the expense
/// detail screen.
class ExpensesListScreen extends ConsumerStatefulWidget {
  const ExpensesListScreen({super.key});

  @override
  ConsumerState<ExpensesListScreen> createState() => _ExpensesListScreenState();
}

class _ExpensesListScreenState extends ConsumerState<ExpensesListScreen> {
  static const _pageSize = 20;

  final _searchCtrl = TextEditingController();
  String? _search;
  String _sort = '-incurredAt';
  String _period = MobileGridPeriod.all;
  int _skip = 0;

  ExpenseListQuery get _query {
    final dateRange = mobileGridDateRangeForPeriod(_period);
    return ExpenseListQuery(
      skip: _skip,
      take: _pageSize,
      search: _search,
      sort: _sort,
      incurredFrom: dateRange.from,
      incurredTo: dateRange.to,
    );
  }

  @override
  void dispose() {
    _searchCtrl.dispose();
    super.dispose();
  }

  Future<void> _refresh() async {
    ref.invalidate(expensesListProvider);
    return ref.refresh(expensesPageProvider(_query).future);
  }

  void _submitSearch([String? value]) {
    final next = (value ?? _searchCtrl.text).trim();
    setState(() {
      _search = next.isEmpty ? null : next;
      _skip = 0;
    });
  }

  void _clearSearch() {
    _searchCtrl.clear();
    _submitSearch('');
  }

  void _setSort(String? sort) {
    if (sort == null || sort == _sort) return;
    setState(() {
      _sort = sort;
      _skip = 0;
    });
  }

  void _setPeriod(String? period) {
    if (period == null || period == _period) return;
    setState(() {
      _period = period;
      _skip = 0;
    });
  }

  void _addExpense() {
    showCreateExpenseSheet(context, ref, onSaved: _refresh);
  }

  @override
  Widget build(BuildContext context) {
    final async = ref.watch(expensesPageProvider(_query));
    final auth = ref.watch(authControllerProvider);
    final canAddExpense =
        auth is AuthStateAuthenticated &&
        canUseMobileCapabilityAction(
          experience: auth.activeExperience,
          capabilities: auth.capabilities,
          capability: 'money.expenses.manage',
          experiences: const {WorkspaceExperience.management},
        );

    return Scaffold(
      appBar: mobileDomainRootAppBar(context, title: const Text('Expenses')),
      floatingActionButton: MobileQuickActionFab(
        heroTag: 'expenses-fab',
        primaryAction: canAddExpense
            ? MobileQuickAction(
                label: 'Add expense',
                icon: Icons.receipt_long_outlined,
                onPressed: _addExpense,
              )
            : null,
        onChat: () => openMobileAssistant(context),
        onRecord: () => openMobileRecord(context),
        onScan: () => openMobileScan(context),
      ),
      body: RefreshIndicator(
        onRefresh: _refresh,
        child: async.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (e, _) => ListView(
            children: [
              const SizedBox(height: 120),
              Center(
                child: Text(
                  e is ApiException ? e.message : "Couldn't load expenses.",
                ),
              ),
            ],
          ),
          data: (page) {
            if (page.items.isEmpty) {
              return ListView(
                physics: const AlwaysScrollableScrollPhysics(),
                padding: const EdgeInsets.fromLTRB(16, 8, 16, 24),
                children: [
                  _ExpensesGridControls(
                    searchController: _searchCtrl,
                    sort: _sort,
                    period: _period,
                    onSearch: _submitSearch,
                    onClearSearch: _clearSearch,
                    onSortChanged: _setSort,
                    onPeriodChanged: _setPeriod,
                  ),
                  const SizedBox(height: 120),
                  Center(
                    child: Text(
                      (_search ?? '').isNotEmpty
                          ? 'No matching expenses.'
                          : 'No expenses yet.',
                    ),
                  ),
                ],
              );
            }
            return ListView.separated(
              padding: const EdgeInsets.fromLTRB(16, 8, 16, 24),
              itemCount: page.items.length + 2,
              separatorBuilder: (_, index) {
                if (index == 0 || index == page.items.length) {
                  return const SizedBox(height: 8);
                }
                return const MobileM3ListDivider();
              },
              itemBuilder: (_, i) {
                if (i == 0) {
                  return _ExpensesGridControls(
                    searchController: _searchCtrl,
                    sort: _sort,
                    period: _period,
                    onSearch: _submitSearch,
                    onClearSearch: _clearSearch,
                    onSortChanged: _setSort,
                    onPeriodChanged: _setPeriod,
                  );
                }

                if (i == page.items.length + 1) {
                  return MobileGridPagingBar(
                    totalCount: page.totalCount,
                    skip: page.skip,
                    itemCount: page.items.length,
                    previousTooltip: 'Previous expenses page',
                    nextTooltip: 'Next expenses page',
                    onPrevious: page.hasPrevious
                        ? () => setState(() {
                            _skip = (_skip - _pageSize).clamp(0, _skip);
                          })
                        : null,
                    onNext: page.hasNext
                        ? () => setState(() => _skip += _pageSize)
                        : null,
                  );
                }

                final expenseIndex = i - 1;
                return ExpenseRowCard(
                  expense: page.items[expenseIndex],
                  position: MobileM3ListItemPositionForIndex.forIndex(
                    expenseIndex,
                    page.items.length,
                  ),
                );
              },
            );
          },
        ),
      ),
    );
  }
}

class _ExpensesGridControls extends StatelessWidget {
  const _ExpensesGridControls({
    required this.searchController,
    required this.sort,
    required this.period,
    required this.onSearch,
    required this.onClearSearch,
    required this.onSortChanged,
    required this.onPeriodChanged,
  });

  final TextEditingController searchController;
  final String sort;
  final String period;
  final ValueChanged<String> onSearch;
  final VoidCallback onClearSearch;
  final ValueChanged<String?> onSortChanged;
  final ValueChanged<String?> onPeriodChanged;

  @override
  Widget build(BuildContext context) {
    return MobileGridControlsBar(
      keyPrefix: 'expenses',
      padding: EdgeInsets.zero,
      searchController: searchController,
      searchLabel: 'Search expenses',
      onSearch: onSearch,
      onClearSearch: onClearSearch,
      sort: sort,
      defaultSort: '-incurredAt',
      sortLabel: 'Sort expenses',
      sortOptions: const [
        MobileGridControlOption(value: '-incurredAt', label: 'Incurred newest'),
        MobileGridControlOption(value: 'incurredAt', label: 'Incurred oldest'),
        MobileGridControlOption(value: '-amount', label: 'Amount high-low'),
        MobileGridControlOption(value: 'amount', label: 'Amount low-high'),
        MobileGridControlOption(value: 'category', label: 'Category'),
        MobileGridControlOption(value: 'status', label: 'Status'),
      ],
      onSortChanged: onSortChanged,
      period: period,
      defaultPeriod: MobileGridPeriod.all,
      periodLabel: 'Incurred period',
      onPeriodChanged: onPeriodChanged,
    );
  }
}

/// A single expense row, reused on the Money tab and the expenses list.
class ExpenseRowCard extends StatelessWidget {
  const ExpenseRowCard({
    super.key,
    required this.expense,
    this.position = MobileM3ListItemPosition.single,
  });

  final Expense expense;
  final MobileM3ListItemPosition position;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final title = expense.description.isEmpty
        ? expense.category.label
        : expense.description;
    final support = [
      expense.category.label,
      if (expense.vendorName != null) expense.vendorName!,
      shortDateFmt(expense.incurredAt),
    ].where((s) => s.isNotEmpty).join(' / ');

    return MobileM3ListItem(
      position: position,
      leading: MobileM3LeadingIcon(
        icon: Icons.receipt_long_outlined,
        backgroundColor: cs.errorContainer,
        foregroundColor: cs.onErrorContainer,
      ),
      title: Text(
        title,
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
        style: theme.textTheme.titleSmall?.copyWith(
          fontWeight: FontWeight.w700,
        ),
      ),
      supporting: [
        Text(
          support,
          maxLines: 1,
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
            moneyFmt(expense.amount),
            style: theme.textTheme.bodyMedium?.copyWith(
              fontWeight: FontWeight.w800,
            ),
          ),
          if (expense.hasReceipt)
            Icon(Icons.attachment, size: 14, color: cs.onSurfaceVariant),
        ],
      ),
      onTap: () => Navigator.of(context).push<void>(
        MaterialPageRoute<void>(
          builder: (_) => ExpenseDetailScreen(expenseId: expense.id),
        ),
      ),
    );
  }
}
