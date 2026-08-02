import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/accounting/accounting_book_models.dart';
import 'package:rental_command/features/accounting/accounting_books_repository.dart';
import 'package:rental_command/features/accounting/accounting_repository.dart';
import 'package:rental_command/features/accounting/journal_detail_sheet.dart';
import 'package:rental_command/features/money/money_repository.dart';
import 'package:rental_command/features/money/money_screen.dart';
import 'package:rental_command/features/money/transaction_models.dart';

void main() {
  testWidgets('Overview renders server facts and the deposits warning', (
    tester,
  ) async {
    final books = _FakeAccountingBooksRepository();
    books.position = _moneyPosition(
      totalCashOnHand: 5000,
      tenantDepositsHeld: 6000,
      cashAfterTenantDeposits: -1000,
      rentStillOwed: 850,
      loanBalance: 12000,
      bookEquity: 9000,
      cashReceived: 2400,
      cashPaid: 900,
      netCashMovement: 1500,
      profitOrLoss: 1300,
    );

    await tester.pumpWidget(_moneyApp(books));
    await tester.pumpAndSettle();

    expect(find.byKey(const Key('money-tab-overview')), findsOneWidget);
    expect(find.text('Cash on hand'), findsOneWidget);
    expect(find.text(r'$5,000.00'), findsAtLeastNWidgets(1));
    expect(
      find.text(
        r'Tenant deposits held ($6,000.00) exceed total cash on hand ($5,000.00).',
      ),
      findsOneWidget,
    );
    expect(find.text('Rent still owed'), findsOneWidget);
    expect(find.text('Book equity'), findsNothing);
  });

  testWidgets('Ledger shows the no-account banner and server running balance', (
    tester,
  ) async {
    final books = _FakeAccountingBooksRepository();
    await tester.pumpWidget(_moneyApp(books));
    await tester.pumpAndSettle();

    await tester.tap(find.byKey(const Key('money-tab-ledger')));
    await tester.pumpAndSettle();

    expect(
      find.byKey(const Key('general-ledger-no-account-banner')),
      findsOneWidget,
    );
    expect(find.text('Rent payment received'), findsOneWidget);
    expect(find.textContaining('Balance'), findsNothing);

    await tester.tap(find.byKey(const Key('general-ledger-account-selector')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Operating cash').last);
    await tester.pumpAndSettle();

    expect(
      find.byKey(const Key('general-ledger-no-account-banner')),
      findsNothing,
    );
    expect(find.textContaining(r'Balance $1,400.00'), findsOneWidget);
    expect(books.lastLedgerQuery?.accountId, 1000);
  });

  testWidgets('Activity uses the existing feed without a running balance', (
    tester,
  ) async {
    final activity = _FakeMoneyRepository(
      items: [
        AccountingTransaction(
          kind: 'Payment',
          id: 55,
          date: DateTime.utc(2027, 2, 14),
          description: 'Rent payment received',
          category: 'Rent',
          status: 'Posted',
          amount: 1400,
          hasReceipt: false,
          receiptIsImage: false,
          reconciled: true,
        ),
      ],
    );
    final books = _FakeAccountingBooksRepository();

    await tester.pumpWidget(_moneyApp(books, activity: activity));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('money-tab-activity')));
    await tester.pumpAndSettle();

    expect(find.text('Rent payment received'), findsOneWidget);
    expect(find.textContaining('Balance'), findsNothing);
  });

  testWidgets('Reports are read-only links backed by statement fields', (
    tester,
  ) async {
    final books = _FakeAccountingBooksRepository();
    await tester.pumpWidget(_moneyApp(books));
    await tester.pumpAndSettle();

    await tester.tap(find.byKey(const Key('money-tab-reports')));
    await tester.pumpAndSettle();

    expect(find.byKey(const Key('report-link-profitAndLoss')), findsOneWidget);
    expect(find.byKey(const Key('report-link-balanceSheet')), findsOneWidget);
    expect(find.byKey(const Key('report-link-trialBalance')), findsOneWidget);
    expect(find.byKey(const Key('report-link-cashFlow')), findsOneWidget);
    expect(find.byKey(const Key('report-link-scheduleE')), findsOneWidget);

    await tester.tap(find.byKey(const Key('report-link-profitAndLoss')));
    await tester.pumpAndSettle();

    expect(find.text('Rental income'), findsOneWidget);
    expect(find.text(r'$2,500.00'), findsAtLeastNWidgets(1));
  });

  testWidgets('Journal sheet renders simple and advanced detail modes', (
    tester,
  ) async {
    final books = _FakeAccountingBooksRepository();
    await tester.pumpWidget(
      ProviderScope(
        overrides: [accountingBooksRepositoryProvider.overrideWithValue(books)],
        child: const MaterialApp(
          home: JournalDetailSheet(journalPublicId: 'journal-1'),
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('What this did'), findsOneWidget);
    expect(find.text('Operating cash increased'), findsOneWidget);
    expect(find.text('Balanced ✓'), findsOneWidget);
    expect(find.text('Source record'), findsOneWidget);
    expect(find.text('tenant-receipt:55'), findsOneWidget);
    expect(find.text('Entered by'), findsOneWidget);

    final container = ProviderScope.containerOf(
      tester.element(find.byType(JournalDetailSheet)),
    );
    container.read(accountingDetailModeProvider.notifier).state =
        AccountingDetailMode.advanced;
    await tester.pump();

    expect(find.text('Detail'), findsOneWidget);
    expect(find.text('1000 Operating cash'), findsOneWidget);
    expect(find.textContaining('Dr'), findsOneWidget);
  });
}

Widget _moneyApp(
  _FakeAccountingBooksRepository books, {
  _FakeMoneyRepository? activity,
}) {
  return ProviderScope(
    overrides: [
      accountingBooksRepositoryProvider.overrideWithValue(books),
      moneySnapshotProvider.overrideWith(
        (ref) async => throw StateError('legacy snapshot is not used'),
      ),
      moneyRepositoryProvider.overrideWithValue(
        activity ?? _FakeMoneyRepository(),
      ),
    ],
    child: const MaterialApp(home: MoneyScreen()),
  );
}

MoneyPositionResponse _moneyPosition({
  required double totalCashOnHand,
  required double tenantDepositsHeld,
  required double cashAfterTenantDeposits,
  required double rentStillOwed,
  required double loanBalance,
  required double bookEquity,
  required double cashReceived,
  required double cashPaid,
  required double netCashMovement,
  required double profitOrLoss,
}) => MoneyPositionResponse(
  asOfUtc: DateTime.utc(2027, 2, 15),
  fromUtc: DateTime.utc(2027, 2),
  toUtc: DateTime.utc(2027, 2, 15),
  totalCashOnHand: totalCashOnHand,
  tenantDepositsHeld: tenantDepositsHeld,
  cashAfterTenantDeposits: cashAfterTenantDeposits,
  rentStillOwed: rentStillOwed,
  loanBalance: loanBalance,
  bookEquity: bookEquity,
  cashReceived: cashReceived,
  cashPaid: cashPaid,
  netCashMovement: netCashMovement,
  profitOrLoss: profitOrLoss,
);

class _FakeAccountingBooksRepository extends AccountingBooksRepository {
  _FakeAccountingBooksRepository() : super(Dio());

  final account = const ChartOfAccountsRow(
    id: 1000,
    publicId: 'cash-account',
    code: '1000',
    name: 'Operating cash',
    accountType: AccountType.asset,
    normalBalance: NormalBalance.debit,
    isSystem: true,
    isActive: true,
    hasPostedLines: true,
  );

  MoneyPositionResponse position = _moneyPosition(
    totalCashOnHand: 5000,
    tenantDepositsHeld: 500,
    cashAfterTenantDeposits: 4500,
    rentStillOwed: 200,
    loanBalance: 12000,
    bookEquity: 9000,
    cashReceived: 2400,
    cashPaid: 900,
    netCashMovement: 1500,
    profitOrLoss: 1300,
  );

  GeneralLedgerQuery? lastLedgerQuery;

  @override
  Future<MoneyPositionResponse> moneyPosition({
    DateTime? from,
    DateTime? to,
  }) async => position;

  @override
  Future<AccountingPage<ChartOfAccountsRow>> chartOfAccounts({
    ChartOfAccountsQuery query = const ChartOfAccountsQuery(),
  }) async => AccountingPage(
    items: [account],
    totalCount: 1,
    skip: 0,
    take: query.take,
  );

  @override
  Future<AccountingPage<GeneralLedgerRow>> generalLedger({
    GeneralLedgerQuery query = const GeneralLedgerQuery(),
  }) async {
    lastLedgerQuery = query;
    return AccountingPage(
      items: [_ledgerRow(query.accountId != null)],
      totalCount: 1,
      skip: 0,
      take: query.take,
    );
  }

  @override
  Future<JournalDetail> journalDetail(String publicId) async => _journalDetail;

  @override
  Future<FinancialStatementResponse> incomeStatement({
    StatementQuery query = const StatementQuery(),
  }) async => _incomeStatement;

  @override
  Future<FinancialStatementResponse> balanceSheet({
    StatementQuery query = const StatementQuery(),
  }) async => _incomeStatement;

  @override
  Future<TrialBalanceResponse> trialBalance({
    StatementQuery query = const StatementQuery(),
  }) async => const TrialBalanceResponse(
    rows: [],
    totalDebits: 0,
    totalCredits: 0,
    isBalanced: true,
  );

  @override
  Future<CashFlowSummaryResponse> cashFlow({
    CashFlowQuery query = const CashFlowQuery(),
  }) async => CashFlowSummaryResponse(
    from: DateTime.utc(2027, 2),
    to: DateTime.utc(2027, 2, 15),
    properties: const [],
    totalIncome: 0,
    totalOperatingExpenses: 0,
    totalNoi: 0,
    totalDebtService: 0,
    totalCashFlow: 0,
  );

  GeneralLedgerRow _ledgerRow(bool selected) => GeneralLedgerRow(
    journalEntryPublicId: 'journal-1',
    lineId: 1,
    effectiveOn: DateTime.utc(2027, 2, 14),
    postedAtUtc: DateTime.utc(2027, 2, 14, 12),
    sourceType: JournalSourceType.tenantReceipt,
    sourceId: 55,
    sourceBusinessKey: 'tenant-receipt:55',
    description: 'Rent payment received',
    accountId: account.id,
    accountCode: account.code,
    accountName: account.name,
    accountType: account.accountType,
    normalBalance: account.normalBalance,
    debitAmount: 1400,
    creditAmount: 0,
    currency: 'USD',
    runningBalance: selected ? 1400 : null,
  );
}

class _FakeMoneyRepository extends MoneyRepository {
  _FakeMoneyRepository({this.items = const []}) : super(Dio());

  final List<AccountingTransaction> items;

  @override
  Future<AccountingTransactionsPage> transactions({
    required int skip,
    required int take,
    TransactionsFilter filter = const TransactionsFilter(),
  }) async => AccountingTransactionsPage(
    items: items,
    totalCount: items.length,
    skip: 0,
    take: 40,
  );
}

final _incomeStatement = FinancialStatementResponse(
  sections: [
    StatementSection(
      label: 'Income',
      rows: [
        const FinancialStatementRow(
          accountId: 4100,
          accountCode: '4100',
          accountName: 'Rental income',
          amount: 2500,
          currency: 'USD',
        ),
      ],
      subtotal: 2500,
    ),
  ],
  totals: const StatementTotals(total: 2500, netIncome: 2500),
);

final _journalDetail = JournalDetail(
  publicId: 'journal-1',
  description: 'Rent payment received',
  effectiveOn: DateTime.utc(2027, 2, 14),
  postedAtUtc: DateTime.utc(2027, 2, 14, 12),
  sourceType: JournalSourceType.tenantReceipt,
  sourceId: 55,
  sourceBusinessKey: 'tenant-receipt:55',
  actor: 'Jordan Lee',
  attemptId: 'attempt-1',
  atomicReceiptId: 'receipt-1',
  idempotencyDigest: 'digest-1',
  currency: 'USD',
  lines: [
    const JournalDetailLine(
      id: 1,
      accountId: 1000,
      accountCode: '1000',
      accountName: 'Operating cash',
      debitAmount: 1400,
      creditAmount: 0,
    ),
  ],
  totalDebits: 1400,
  totalCredits: 1400,
  isBalanced: true,
  reversalPublicIds: const [],
  documentIds: const [],
);
