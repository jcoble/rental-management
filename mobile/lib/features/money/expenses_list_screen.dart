import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import 'expense_detail_screen.dart';
import 'expense_models.dart';
import 'money_format.dart';
import 'money_repository.dart';

/// Lists the portfolio's expenses, newest first. Each row opens the expense
/// detail screen.
class ExpensesListScreen extends ConsumerWidget {
  const ExpensesListScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(expensesListProvider);

    return Scaffold(
      appBar: AppBar(title: const Text('Expenses')),
      body: RefreshIndicator(
        onRefresh: () async => ref.invalidate(expensesListProvider),
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
              padding: const EdgeInsets.fromLTRB(16, 8, 16, 24),
              itemCount: expenses.length,
              separatorBuilder: (_, _) => const SizedBox(height: 8),
              itemBuilder: (_, i) => ExpenseRowCard(expense: expenses[i]),
            );
          },
        ),
      ),
    );
  }
}

/// A single expense row, reused on the Money tab and the expenses list.
class ExpenseRowCard extends StatelessWidget {
  const ExpenseRowCard({super.key, required this.expense});

  final Expense expense;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    return Card(
      child: ListTile(
        onTap: () => Navigator.of(context).push<void>(
          MaterialPageRoute<void>(
            builder: (_) => ExpenseDetailScreen(expenseId: expense.id),
          ),
        ),
        leading: CircleAvatar(
          backgroundColor: cs.errorContainer,
          child: Icon(
            Icons.receipt_long_outlined,
            color: cs.onErrorContainer,
            size: 18,
          ),
        ),
        title: Text(
          expense.description.isEmpty
              ? expense.category.label
              : expense.description,
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
          style: theme.textTheme.bodyMedium?.copyWith(
            fontWeight: FontWeight.w600,
          ),
        ),
        subtitle: Text(
          [
            expense.category.label,
            if (expense.vendorName != null) expense.vendorName!,
            shortDateFmt(expense.incurredAt),
          ].where((s) => s.isNotEmpty).join(' / '),
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
        ),
        trailing: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          crossAxisAlignment: CrossAxisAlignment.end,
          children: [
            Text(
              moneyFmt(expense.amount),
              style: theme.textTheme.bodyMedium?.copyWith(
                fontWeight: FontWeight.w700,
              ),
            ),
            if (expense.hasReceipt)
              Icon(Icons.attachment, size: 14, color: cs.onSurfaceVariant),
          ],
        ),
      ),
    );
  }
}
