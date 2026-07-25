import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/accounting/accounting_models.dart';
import 'package:rental_command/features/accounting/accounting_repository.dart';
import 'package:rental_command/features/money/money_screen.dart';
import 'package:rental_command/features/money/money_repository.dart';
import 'package:rental_command/features/money/transaction_models.dart';
import 'package:rental_command/features/money/transactions_controller.dart';

void main() {
  test(
    'changing transaction kind clears stale rows while the page reloads',
    () async {
      final repo = _FakeMoneyRepository();
      final container = ProviderContainer(
        overrides: [moneyRepositoryProvider.overrideWithValue(repo)],
      );
      addTearDown(container.dispose);

      final notifier = container.read(transactionsProvider.notifier);
      repo.completeNext(_page(_tx(kind: 'Payment', id: 1)));
      await notifier.load();

      expect(container.read(transactionsProvider).items.single.kind, 'Payment');

      final nextPage = Completer<AccountingTransactionsPage>();
      repo.queue(nextPage.future);
      final pending = notifier.setFilter(
        const TransactionsFilter(kind: 'Expense'),
      );

      final pendingState = container.read(transactionsProvider);
      expect(pendingState.filter.kind, 'Expense');
      expect(pendingState.items, isEmpty);
      expect(pendingState.loading, isTrue);
      expect(repo.requests.last.kind, 'Expense');

      nextPage.complete(_page(_tx(kind: 'Expense', id: 2)));
      await pending;

      final loadedState = container.read(transactionsProvider);
      expect(loadedState.loading, isFalse);
      expect(loadedState.items.single.kind, 'Expense');
    },
  );

  test(
    'stale transaction responses cannot overwrite the selected view',
    () async {
      final repo = _FakeMoneyRepository();
      final container = ProviderContainer(
        overrides: [moneyRepositoryProvider.overrideWithValue(repo)],
      );
      addTearDown(container.dispose);

      final notifier = container.read(transactionsProvider.notifier);

      final ledgerPage = Completer<AccountingTransactionsPage>();
      repo.queue(ledgerPage.future);
      final ledgerLoad = notifier.load();

      final expensesPage = Completer<AccountingTransactionsPage>();
      repo.queue(expensesPage.future);
      final expensesLoad = notifier.setFilter(
        const TransactionsFilter(kind: 'Expense'),
      );

      expensesPage.complete(_page(_tx(kind: 'Expense', id: 2)));
      await expensesLoad;

      var state = container.read(transactionsProvider);
      expect(state.filter.kind, 'Expense');
      expect(state.items.single.kind, 'Expense');

      ledgerPage.complete(_page(_tx(kind: 'Payment', id: 1)));
      await ledgerLoad;

      state = container.read(transactionsProvider);
      expect(state.filter.kind, 'Expense');
      expect(state.items.single.kind, 'Expense');

      repo.completeNext(_page(_tx(kind: 'Payment', id: 3)));
      await notifier.setFilter(const TransactionsFilter());

      state = container.read(transactionsProvider);
      expect(state.filter.kind, isNull);
      expect(state.items.single.id, 3);
      expect(repo.requests.last.kind, isNull);
    },
  );

  test('tenant ledger rows preserve canonical tenant-account identity', () {
    final transaction = AccountingTransaction.fromJson({
      'kind': 'TenantLedger',
      'id': 88,
      'date': '2026-07-13T12:00:00Z',
      'tenantAccountId': 42,
      'detailHref': '/tenant-accounts/42/entries/88',
    });

    expect(transaction.isTenantLedger, isTrue);
    expect(transaction.isTenantAccountEntry, isTrue);
    expect(transaction.isExpense, isFalse);
    expect(transaction.tenantAccountId, 42);
    expect(transaction.detailHref, '/tenant-accounts/42/entries/88');
  });

  testWidgets('Money insights renders dashboard without transaction selector', (
    tester,
  ) async {
    final repo = _FakeMoneyRepository();

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          accountingRepositoryProvider.overrideWithValue(
            _FakeAccountingRepository(),
          ),
          moneyRepositoryProvider.overrideWithValue(repo),
        ],
        child: const MaterialApp(home: MoneyScreen()),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('Cash kept'), findsOneWidget);
    expect(find.text('Payments'), findsNothing);
    expect(find.text('Expenses'), findsNothing);
    expect(find.text('Payment row'), findsNothing);
    expect(repo.requests, isEmpty);
  });

  testWidgets('Money ledger toggles payments and expenses after build', (
    tester,
  ) async {
    final repo = _FakeMoneyRepository();
    repo.completeNext(_page(_tx(kind: 'Payment', id: 1)));

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          accountingRepositoryProvider.overrideWithValue(
            _FakeAccountingRepository(),
          ),
          moneyRepositoryProvider.overrideWithValue(repo),
        ],
        child: const MaterialApp(
          home: MoneyScreen(
            initialView: MoneyScreenView.payments,
            showTransactionSelector: true,
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('Payment row'), findsOneWidget);
    expect(repo.requests.last.kind, 'Payment');
    expect(find.text('Ledger'), findsNothing);
    expect(find.text('Payments'), findsOneWidget);
    expect(find.text('Expenses'), findsOneWidget);

    repo.completeNext(_page(_tx(kind: 'Expense', id: 2)));
    await tester.tap(find.text('Expenses'));
    await tester.pumpAndSettle();

    expect(repo.requests.last.kind, 'Expense');
    expect(find.text('Expense row'), findsOneWidget);

    repo.completeNext(_page(_tx(kind: 'Payment', id: 3)));
    await tester.tap(find.text('Payments'));
    await tester.pumpAndSettle();

    expect(repo.requests.last.kind, 'Payment');
    expect(find.text('Payment row'), findsOneWidget);
  });
}

class _FakeAccountingRepository extends AccountingRepository {
  _FakeAccountingRepository() : super(Dio());

  @override
  Future<MoneySnapshot> snapshot() async {
    return const MoneySnapshot(
      periodLabel: 'July 2026',
      collected: 0,
      spent: 0,
      net: 0,
      pastDueAmount: 0,
      pastDueCount: 0,
      collectedLast30Days: 0,
      spentLast30Days: 0,
      netLast30Days: 0,
      explanations: MoneySnapshotExplanations(
        collected: '',
        spent: '',
        net: '',
        pastDue: '',
      ),
    );
  }
}

class _FakeMoneyRepository extends MoneyRepository {
  _FakeMoneyRepository() : super(Dio());

  final requests = <TransactionsFilter>[];
  final _responses = <Future<AccountingTransactionsPage>>[];

  void completeNext(AccountingTransactionsPage page) {
    _responses.add(Future.value(page));
  }

  void queue(Future<AccountingTransactionsPage> page) {
    _responses.add(page);
  }

  @override
  Future<AccountingTransactionsPage> transactions({
    required int skip,
    required int take,
    TransactionsFilter filter = const TransactionsFilter(),
  }) {
    requests.add(filter);
    if (_responses.isEmpty) {
      throw StateError('No queued transaction page.');
    }
    return _responses.removeAt(0);
  }
}

AccountingTransactionsPage _page(AccountingTransaction tx) {
  return AccountingTransactionsPage(
    items: [tx],
    totalCount: 1,
    skip: 0,
    take: 40,
  );
}

AccountingTransaction _tx({required String kind, required int id}) {
  return AccountingTransaction(
    kind: kind,
    id: id,
    date: DateTime(2026, 7, 8),
    description: '$kind row',
    category: kind,
    status: 'Scheduled',
    amount: 75,
    hasReceipt: false,
    receiptIsImage: false,
    reconciled: false,
  );
}
