import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/accounting/accounting_book_models.dart';
import 'package:rental_command/features/accounting/accounting_books_repository.dart';
import 'package:rental_command/features/accounting/accounting_impact_card.dart';
import 'package:rental_command/features/accounting/journal_detail_sheet.dart';
import 'package:rental_command/features/properties/capital_assets_repository.dart';
import 'package:rental_command/features/properties/property_capital_asset_form_sheet.dart';

void main() {
  testWidgets('accounting impact card is hidden in simple mode', (
    tester,
  ) async {
    final repository = _FakeBooksRepository();

    await tester.pumpWidget(
      _cardHarness(repository, AccountingDetailMode.simple),
    );
    await tester.pumpAndSettle();

    expect(find.byKey(const Key('accounting-impact-card')), findsNothing);
    expect(find.text('View accounting record →'), findsNothing);
    expect(repository.sourceCalls, 0);
  });

  testWidgets('accounting impact card renders in advanced mode', (
    tester,
  ) async {
    final repository = _FakeBooksRepository();

    await tester.pumpWidget(
      _cardHarness(repository, AccountingDetailMode.advanced),
    );
    await tester.pumpAndSettle();

    expect(find.byKey(const Key('accounting-impact-card')), findsOneWidget);
    expect(find.text('View accounting record →'), findsOneWidget);
    expect(repository.sourceCalls, 1);
  });

  testWidgets('section 1250 and convention hidden in simple mode', (
    tester,
  ) async {
    final simpleRepository = _FakeCapitalAssetsRepository();

    await tester.pumpWidget(
      _capitalAssetHarness(simpleRepository, AccountingDetailMode.simple),
    );
    await _openDepreciationStep(tester);

    expect(find.text('Convention'), findsNothing);
    expect(find.text('Recovery life'), findsOneWidget);

    await tester.tap(find.text('Add Asset'));
    await tester.pumpAndSettle();
    expect(simpleRepository.payloads.single['convention'], 'MidMonth');

  });

  testWidgets('convention is shown in advanced mode', (tester) async {
    final repository = _FakeCapitalAssetsRepository();

    await tester.pumpWidget(
      _capitalAssetHarness(repository, AccountingDetailMode.advanced),
    );
    await _openDepreciationStep(tester);

    expect(find.text('Convention'), findsOneWidget);
  });
}

Future<void> _openDepreciationStep(WidgetTester tester) async {
  await tester.pumpAndSettle();
  await tester.tap(find.text('open'));
  await tester.pumpAndSettle();
  await tester.enterText(
    find.byKey(const Key('capital-asset-description-field')),
    'New roof',
  );
  await tester.enterText(
    find.byKey(const Key('capital-asset-cost-basis-field')),
    '18000',
  );
  await tester.tap(find.text('Next'));
  await tester.pumpAndSettle();
}

Widget _cardHarness(
  _FakeBooksRepository repository,
  AccountingDetailMode mode,
) => ProviderScope(
  overrides: [
    accountingBooksRepositoryProvider.overrideWithValue(repository),
    accountingDetailModeProvider.overrideWith(() => _StubDetailMode(mode)),
  ],
  child: MaterialApp(
    home: Scaffold(
      body: AccountingImpactCard(
        sourceId: 13,
        sourceTypes: const [JournalSourceType.expensePayment],
        authorized: true,
      ),
    ),
  ),
);

Widget _capitalAssetHarness(
  _FakeCapitalAssetsRepository repository,
  AccountingDetailMode mode,
) => ProviderScope(
  overrides: [
    capitalAssetsRepositoryProvider.overrideWithValue(repository),
    accountingDetailModeProvider.overrideWith(() => _StubDetailMode(mode)),
  ],
  child: MaterialApp(
    home: Scaffold(
      body: Builder(
        builder: (context) => Center(
          child: ElevatedButton(
            onPressed: () =>
                showPropertyCapitalAssetFormSheet(context, propertyId: 7),
            child: const Text('open'),
          ),
        ),
      ),
    ),
  ),
);

class _StubDetailMode extends AccountingDetailModeNotifier {
  _StubDetailMode(this.mode);

  final AccountingDetailMode mode;

  @override
  AccountingDetailMode build() => mode;
}

class _FakeBooksRepository extends AccountingBooksRepository {
  _FakeBooksRepository() : super(Dio());

  var sourceCalls = 0;

  @override
  Future<List<SourceJournalSummary>> sourceJournals({
    required JournalSourceType sourceType,
    required int sourceId,
  }) async {
    sourceCalls++;
    return [
      SourceJournalSummary(
        publicId: 'journal-1',
        effectiveOn: DateTime.utc(2027, 1, 15),
        postedAtUtc: DateTime.utc(2027, 1, 15, 12),
        sourceType: sourceType,
        description: 'Posted expense payment',
        totalDebits: 225,
        totalCredits: 225,
        isReversal: false,
      ),
    ];
  }

  @override
  Future<JournalDetail> journalDetail(String publicId) async => JournalDetail(
    publicId: publicId,
    description: 'Posted expense payment',
    effectiveOn: DateTime.utc(2027, 1, 15),
    postedAtUtc: DateTime.utc(2027, 1, 15, 12),
    sourceType: JournalSourceType.expensePayment,
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
        debitAmount: 225,
        creditAmount: 0,
      ),
      JournalDetailLine(
        id: 2,
        accountId: 102,
        accountCode: '1000',
        accountName: 'Operating cash',
        debitAmount: 0,
        creditAmount: 225,
      ),
    ],
    totalDebits: 225,
    totalCredits: 225,
    isBalanced: true,
    reversalPublicIds: const [],
    documentIds: const [],
  );
}

class _FakeCapitalAssetsRepository extends CapitalAssetsRepository {
  _FakeCapitalAssetsRepository() : super(Dio());

  final payloads = <Map<String, dynamic>>[];

  @override
  Future<CapitalAsset> createAsset({
    required int propertyId,
    required Map<String, dynamic> data,
  }) async {
    payloads.add(data);
    return CapitalAsset(
      id: 1,
      portfolioId: 1,
      propertyId: propertyId,
      description: data['description'] as String,
      costBasis: (data['costBasis'] as num).toDouble(),
      inServiceDate: DateTime.utc(2027, 1, 1),
      method: DepreciationMethod.straightLine,
      recoveryYears: (data['recoveryYears'] as num).toDouble(),
      convention: DepreciationConvention.fromWire(
        data['convention'] as String?,
      ),
      accumulatedDepreciation: 0,
      depreciationYear: 2027,
      annualDepreciation: 0,
      isFirstYearEstimate: false,
      createdAt: DateTime.utc(2027, 1, 1),
      updatedAt: DateTime.utc(2027, 1, 1),
      testId: 'capital-asset-1',
    );
  }
}
