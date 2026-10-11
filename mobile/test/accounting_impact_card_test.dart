import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/accounting/accounting_book_models.dart';
import 'package:rental_command/features/accounting/accounting_books_repository.dart';
import 'package:rental_command/features/accounting/accounting_impact_card.dart';
import 'package:rental_command/features/accounting/journal_detail_sheet.dart';

void main() {
  testWidgets('renders the server-backed posted lines for one source', (
    tester,
  ) async {
    final repository = _FakeAccountingBooksRepository(
      summaries: {
        JournalSourceType.expensePayment: [_summary()],
      },
      details: {'journal-1': _expenseJournal()},
    );

    await tester.pumpWidget(_harness(repository));
    await tester.pumpAndSettle();

    expect(find.byKey(const Key('accounting-impact-card')), findsOneWidget);
    expect(find.textContaining('4100 · Repairs expense'), findsOneWidget);
    expect(find.textContaining('1000 · Operating cash'), findsOneWidget);
    expect(find.text(r'Debit $225.00'), findsOneWidget);
    expect(find.text(r'Credit $225.00'), findsOneWidget);
    expect(find.text('View accounting record →'), findsOneWidget);
    expect(repository.requestedSourceIds, [13]);
    expect(repository.requestedSourceTypes, [JournalSourceType.expensePayment]);
    expect(repository.requestedJournalIds, ['journal-1']);
  });

  testWidgets('renders one group per source journal without client totals', (
    tester,
  ) async {
    final repository = _FakeAccountingBooksRepository(
      summaries: {
        JournalSourceType.billIncurred: [
          _summary(
            publicId: 'journal-bill',
            sourceType: JournalSourceType.billIncurred,
            effectiveOn: DateTime.utc(2027, 1, 10),
          ),
        ],
        JournalSourceType.expensePayment: [
          _summary(
            publicId: 'journal-payment',
            effectiveOn: DateTime.utc(2027, 1, 12),
          ),
        ],
      },
      details: {
        'journal-bill': _expenseJournal(
          publicId: 'journal-bill',
          sourceType: JournalSourceType.billIncurred,
          amount: 225,
        ),
        'journal-payment': _expenseJournal(
          publicId: 'journal-payment',
          amount: 225,
        ),
      },
    );

    await tester.pumpWidget(
      _harness(
        repository,
        sourceTypes: const [
          JournalSourceType.billIncurred,
          JournalSourceType.expensePayment,
        ],
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('View accounting record →'), findsNWidgets(2));
    expect(find.text(r'Debit $225.00'), findsNWidgets(2));
    expect(
      repository.requestedSourceTypes,
      containsAllInOrder([
        JournalSourceType.billIncurred,
        JournalSourceType.expensePayment,
      ]),
    );
  });

  testWidgets('advanced mode shows server debit and credit fields', (
    tester,
  ) async {
    final repository = _FakeAccountingBooksRepository(
      summaries: {
        JournalSourceType.expensePayment: [_summary()],
      },
      details: {'journal-1': _expenseJournal()},
    );

    await tester.pumpWidget(_harness(repository));
    await tester.pumpAndSettle();

    expect(find.textContaining('4100 · Repairs expense'), findsOneWidget);
    expect(find.text(r'Debit $225.00'), findsOneWidget);
    expect(find.text(r'Credit $225.00'), findsOneWidget);
    expect(find.text('Repairs expense increased'), findsNothing);
  });

  testWidgets('view accounting record opens the internal journal fallback', (
    tester,
  ) async {
    final repository = _FakeAccountingBooksRepository(
      summaries: {
        JournalSourceType.expensePayment: [_summary()],
      },
      details: {'journal-1': _expenseJournal()},
    );

    await tester.pumpWidget(_harness(repository));
    await tester.pumpAndSettle();
    await tester.tap(find.text('View accounting record →'));
    await tester.pumpAndSettle();

    expect(find.text('Accounting record'), findsOneWidget);
    expect(find.text('Balanced'), findsOneWidget);
    expect(find.text('Posted expense payment'), findsOneWidget);
  });

  testWidgets('hides the card and makes no request when unauthorized', (
    tester,
  ) async {
    final repository = _FakeAccountingBooksRepository(
      summaries: {
        JournalSourceType.expensePayment: [_summary()],
      },
      details: {'journal-1': _expenseJournal()},
    );

    await tester.pumpWidget(_harness(repository, authorized: false));
    await tester.pump();

    expect(find.byKey(const Key('accounting-impact-card')), findsNothing);
    expect(find.byKey(const Key('accounting-impact-loading')), findsNothing);
    expect(repository.sourceCalls, 0);
  });

  testWidgets('hides the card when the server returns no journal', (
    tester,
  ) async {
    final repository = _FakeAccountingBooksRepository(
      summaries: {
        JournalSourceType.expensePayment: const <SourceJournalSummary>[],
      },
      details: const {},
    );

    await tester.pumpWidget(_harness(repository));
    await tester.pumpAndSettle();

    expect(find.byKey(const Key('accounting-impact-card')), findsNothing);
    expect(find.byKey(const Key('accounting-impact-loading')), findsNothing);
  });
}

Widget _harness(
  _FakeAccountingBooksRepository repository, {
  List<JournalSourceType> sourceTypes = const [
    JournalSourceType.expensePayment,
  ],
  bool? authorized = true,
}) => ProviderScope(
  overrides: [
    accountingBooksRepositoryProvider.overrideWithValue(repository),
    accountingDetailModeProvider.overrideWith(_AdvancedDetailMode.new),
  ],
  child: MaterialApp(
    home: Scaffold(
      body: AccountingImpactCard(
        sourceId: 13,
        sourceTypes: sourceTypes,
        authorized: authorized,
      ),
    ),
  ),
);

class _AdvancedDetailMode extends AccountingDetailModeNotifier {
  @override
  AccountingDetailMode build() => AccountingDetailMode.advanced;
}

SourceJournalSummary _summary({
  String publicId = 'journal-1',
  JournalSourceType sourceType = JournalSourceType.expensePayment,
  DateTime? effectiveOn,
}) => SourceJournalSummary(
  publicId: publicId,
  effectiveOn: effectiveOn ?? DateTime.utc(2027, 1, 15),
  postedAtUtc: DateTime.utc(2027, 1, 15, 12),
  sourceType: sourceType,
  description: 'Posted expense payment',
  totalDebits: 225,
  totalCredits: 225,
  isReversal: false,
);

JournalDetail _expenseJournal({
  String publicId = 'journal-1',
  JournalSourceType sourceType = JournalSourceType.expensePayment,
  double amount = 225,
}) => JournalDetail(
  publicId: publicId,
  description: 'Posted expense payment',
  effectiveOn: DateTime.utc(2027, 1, 15),
  postedAtUtc: DateTime.utc(2027, 1, 15, 12),
  sourceType: sourceType,
  sourceId: 13,
  sourceBusinessKey: 'expense:13',
  attemptId: 'attempt-1',
  atomicReceiptId: 'receipt-1',
  idempotencyDigest: 'digest-1',
  currency: 'USD',
  lines: [
    JournalDetailLine(
      id: 1,
      accountId: 101,
      accountCode: '4100',
      accountName: 'Repairs expense',
      debitAmount: amount,
      creditAmount: 0,
    ),
    JournalDetailLine(
      id: 2,
      accountId: 102,
      accountCode: '1000',
      accountName: 'Operating cash',
      debitAmount: 0,
      creditAmount: amount,
    ),
  ],
  totalDebits: amount,
  totalCredits: amount,
  isBalanced: true,
  reversalPublicIds: const [],
  documentIds: const [],
);

class _FakeAccountingBooksRepository extends AccountingBooksRepository {
  _FakeAccountingBooksRepository({
    required this.summaries,
    required this.details,
  }) : super(Dio());

  final Map<JournalSourceType, List<SourceJournalSummary>> summaries;
  final Map<String, JournalDetail> details;
  final requestedSourceTypes = <JournalSourceType>[];
  final requestedSourceIds = <int>[];
  final requestedJournalIds = <String>[];
  var sourceCalls = 0;

  @override
  Future<List<SourceJournalSummary>> sourceJournals({
    required JournalSourceType sourceType,
    required int sourceId,
  }) async {
    sourceCalls++;
    requestedSourceTypes.add(sourceType);
    requestedSourceIds.add(sourceId);
    return summaries[sourceType] ?? const <SourceJournalSummary>[];
  }

  @override
  Future<JournalDetail> journalDetail(String publicId) async {
    requestedJournalIds.add(publicId);
    return details[publicId]!;
  }
}
