import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/accounting/accounting_book_models.dart';
import 'package:rental_command/features/accounting/accounting_books_repository.dart';
import 'package:rental_command/features/money/expense_models.dart';

void main() {
  test(
    'general ledger sends server filters and binds running balance',
    () async {
      final adapter = _AccountingAdapter();
      final repository = AccountingBooksRepository(_dio(adapter));

      final page = await repository.generalLedger(
        query: GeneralLedgerQuery(
          skip: 20,
          take: 25,
          search: 'rent',
          sort: 'accountCode',
          accountId: 101,
          propertyId: 202,
          unitId: 303,
          sourceType: JournalSourceType.tenantCharge,
          effectiveFrom: DateTime.utc(2027, 1, 1),
          effectiveTo: DateTime.utc(2027, 1, 31),
        ),
      );

      final request = adapter.requests.single;
      expect(request.method, 'GET');
      expect(request.path, '/accounting/general-ledger');
      expect(request.queryParameters, {
        'skip': 20,
        'take': 25,
        'search': 'rent',
        'sort': 'accountCode',
        'accountId': 101,
        'propertyId': 202,
        'unitId': 303,
        'sourceType': 'TenantCharge',
        'effectiveFrom': '2027-01-01',
        'effectiveTo': '2027-01-31',
      });
      expect(page.totalCount, 1);
      expect(page.items.single.sourceType, JournalSourceType.tenantCharge);
      expect(page.items.single.accountType, AccountType.income);
      expect(page.items.single.normalBalance, NormalBalance.credit);
      expect(page.items.single.runningBalance, 1250.5);
    },
  );

  test(
    'invalid general-ledger sort falls back to the contract default',
    () async {
      final adapter = _AccountingAdapter();
      final repository = AccountingBooksRepository(_dio(adapter));

      await repository.generalLedger(
        query: const GeneralLedgerQuery(sort: 'runningBalance'),
      );

      expect(adapter.requests.single.queryParameters['sort'], '-effectiveOn');
    },
  );

  test(
    'accounting read endpoints verify routes, filters, and DTO parsing',
    () async {
      final adapter = _AccountingAdapter();
      final repository = AccountingBooksRepository(_dio(adapter));

      final chart = await repository.chartOfAccounts(
        query: ChartOfAccountsQuery(
          skip: 5,
          take: 10,
          search: 'rent',
          sort: 'code',
          from: DateTime.utc(2027, 1, 1),
          to: DateTime.utc(2027, 12, 31),
          activeOnly: true,
        ),
      );
      final journal = await repository.journalDetail(
        '11111111-1111-1111-1111-111111111111',
      );
      final sourceJournals = await repository.sourceJournals(
        sourceType: JournalSourceType.tenantReceipt,
        sourceId: 55,
      );
      final moneyPosition = await repository.moneyPosition(
        from: DateTime.utc(2027, 1, 1),
        to: DateTime.utc(2027, 1, 31),
      );
      final cashFlow = await repository.cashFlow(
        query: CashFlowQuery(
          from: DateTime.utc(2027, 1, 1),
          to: DateTime.utc(2027, 1, 31),
          propertyId: 7,
          propertyIds: [7, 8],
          skip: 10,
          take: 20,
          sort: 'propertyName',
        ),
      );
      final trialBalance = await repository.trialBalance(
        query: StatementQuery(
          from: DateTime.utc(2027, 1, 1),
          to: DateTime.utc(2027, 1, 31),
          currency: 'USD',
          propertyId: 7,
          unitId: 9,
        ),
      );
      final balanceSheet = await repository.balanceSheet(
        query: const StatementQuery(currency: 'USD'),
      );
      final incomeStatement = await repository.incomeStatement(
        query: const StatementQuery(currency: 'USD'),
      );

      expect(chart.items.single.scheduleECategory, ScheduleECategory.repairs);
      expect(journal.isBalanced, isTrue);
      expect(journal.lines.single.accountCode, '4100');
      expect(journal.bankReconciliationEvidence?.bankTransactionId, 88);
      expect(sourceJournals.single.isReversal, isFalse);
      expect(moneyPosition.cashAfterTenantDeposits, 9000);
      expect(cashFlow.totalCashFlow, 1200);
      expect(trialBalance.isBalanced, isTrue);
      expect(balanceSheet.totals.currentEarnings, 2500);
      expect(balanceSheet.totals.isBalanced, isTrue);
      expect(incomeStatement.sections.single.rows.single.amount, 2500);

      expect(adapter.requests.map((request) => request.path), [
        '/accounting/chart-of-accounts',
        '/accounting/journal-entries/11111111-1111-1111-1111-111111111111',
        '/accounting/source-journals',
        '/accounting/money-position',
        '/accounting/cash-flow',
        '/accounting/trial-balance',
        '/accounting/balance-sheet',
        '/accounting/income-statement',
      ]);
      expect(adapter.requests[0].queryParameters, {
        'skip': 5,
        'take': 10,
        'search': 'rent',
        'sort': 'code',
        'from': '2027-01-01',
        'to': '2027-12-31',
        'activeOnly': true,
      });
      expect(adapter.requests[2].queryParameters, {
        'sourceType': 'TenantReceipt',
        'sourceId': 55,
      });
      expect(adapter.requests[3].queryParameters, {
        'from': '2027-01-01',
        'to': '2027-01-31',
      });
      expect(adapter.requests[4].queryParameters, {
        'skip': 10,
        'take': 20,
        'sort': 'propertyName',
        'from': '2027-01-01',
        'to': '2027-01-31',
        'propertyId': 7,
        'propertyIds': [7, 8],
      });
      expect(adapter.requests[5].queryParameters, {
        'from': '2027-01-01',
        'to': '2027-01-31',
        'currency': 'USD',
        'propertyId': 7,
        'unitId': 9,
      });
    },
  );
}

Dio _dio(_AccountingAdapter adapter) =>
    Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;

class _RecordedRequest {
  const _RecordedRequest({
    required this.method,
    required this.path,
    required this.queryParameters,
    this.data,
  });

  final String method;
  final String path;
  final Map<String, dynamic> queryParameters;
  final Object? data;
}

class _AccountingAdapter implements HttpClientAdapter {
  final requests = <_RecordedRequest>[];

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    requests.add(
      _RecordedRequest(
        method: options.method,
        path: options.path,
        queryParameters: Map<String, dynamic>.from(options.queryParameters),
        data: options.data,
      ),
    );
    return ResponseBody.fromString(
      jsonEncode(_responseFor(options.path)),
      200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}

  Object _responseFor(String path) {
    switch (path) {
      case '/accounting/chart-of-accounts':
        return {
          'items': [
            {
              'id': 10,
              'publicId': 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
              'code': '4100',
              'name': 'Rent income',
              'accountType': 'Income',
              'normalBalance': 'Credit',
              'parentAccountId': null,
              'systemKey': 'rent_income',
              'scheduleECategory': 'Repairs',
              'isSystem': true,
              'isActive': true,
              'hasPostedLines': true,
            },
          ],
          'totalCount': 1,
          'skip': 5,
          'take': 10,
        };
      case '/accounting/general-ledger':
        return {
          'items': [
            {
              'journalEntryPublicId': 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
              'lineId': 1,
              'effectiveOn': '2027-01-15',
              'postedAtUtc': '2027-01-15T14:00:00Z',
              'sourceType': 'TenantCharge',
              'sourceId': 55,
              'sourceBusinessKey': 'tenant-charge:55',
              'description': 'January rent',
              'accountId': 101,
              'accountCode': '4100',
              'accountName': 'Rent income',
              'accountType': 'Income',
              'normalBalance': 'Credit',
              'debitAmount': 0,
              'creditAmount': 1250.5,
              'currency': 'USD',
              'propertyId': 202,
              'unitId': 303,
              'tenantAccountId': 404,
              'ownerEntityId': 505,
              'runningBalance': 1250.5,
            },
          ],
          'totalCount': 1,
          'skip': 20,
          'take': 25,
        };
      case '/accounting/journal-entries/11111111-1111-1111-1111-111111111111':
        return {
          'publicId': '11111111-1111-1111-1111-111111111111',
          'description': 'January receipt',
          'effectiveOn': '2027-01-15',
          'postedAtUtc': '2027-01-15T14:00:00Z',
          'sourceType': 'TenantReceipt',
          'sourceId': 55,
          'sourceBusinessKey': 'tenant-receipt:55',
          'actor': 'owner@example.test',
          'attemptId': '22222222-2222-2222-2222-222222222222',
          'atomicReceiptId': '33333333-3333-3333-3333-333333333333',
          'idempotencyDigest': 'digest',
          'currency': 'USD',
          'lines': [
            {
              'id': 8,
              'accountId': 101,
              'accountCode': '4100',
              'accountName': 'Rent income',
              'debitAmount': 0,
              'creditAmount': 1250,
              'memo': 'January rent',
              'propertyId': 202,
              'unitId': 303,
              'tenantAccountId': 404,
              'ownerEntityId': 505,
            },
          ],
          'totalDebits': 1250,
          'totalCredits': 1250,
          'isBalanced': true,
          'reversesJournalEntryPublicId': null,
          'reversalPublicIds': [],
          'auditLink': '/audit/8',
          'documentIds': [9],
          'bankReconciliationEvidence': {
            'bankTransactionId': 88,
            'bankAccountLabel': 'Operating',
            'matchedOn': '2027-01-16',
            'status': 'Matched',
          },
        };
      case '/accounting/source-journals':
        return [
          {
            'publicId': '44444444-4444-4444-4444-444444444444',
            'effectiveOn': '2027-01-15',
            'postedAtUtc': '2027-01-15T14:00:00Z',
            'sourceType': 'TenantReceipt',
            'description': 'January receipt',
            'totalDebits': 1250,
            'totalCredits': 1250,
            'isReversal': false,
            'reversesPublicId': null,
          },
        ];
      case '/accounting/money-position':
        return {
          'asOfUtc': '2027-01-31T23:59:59Z',
          'fromUtc': '2027-01-01T00:00:00Z',
          'toUtc': '2027-01-31T23:59:59Z',
          'totalCashOnHand': 10000,
          'tenantDepositsHeld': 1000,
          'cashAfterTenantDeposits': 9000,
          'rentStillOwed': 500,
          'loanBalance': 80000,
          'bookEquity': 20000,
          'cashReceived': 3000,
          'cashPaid': 1800,
          'netCashMovement': 1200,
          'profitOrLoss': 1100,
        };
      case '/accounting/cash-flow':
        return {
          'from': '2027-01-01T00:00:00Z',
          'to': '2027-01-31T00:00:00Z',
          'properties': [
            {
              'propertyId': 7,
              'propertyName': 'Oak House',
              'income': 3000,
              'operatingExpenses': 1000,
              'noi': 2000,
              'debtService': 800,
              'cashFlow': 1200,
            },
          ],
          'totalIncome': 3000,
          'totalOperatingExpenses': 1000,
          'totalNoi': 2000,
          'totalDebtService': 800,
          'totalCashFlow': 1200,
        };
      case '/accounting/trial-balance':
        return {
          'rows': [
            {
              'accountId': 101,
              'accountCode': '4100',
              'accountName': 'Rent income',
              'accountType': 'Income',
              'debitBalance': 0,
              'creditBalance': 2500,
              'currency': 'USD',
            },
          ],
          'totalDebits': 2500,
          'totalCredits': 2500,
          'isBalanced': true,
        };
      case '/accounting/balance-sheet':
        return {
          'sections': [],
          'totals': {
            'total': 25000,
            'netIncome': null,
            'assets': 50000,
            'liabilitiesAndEquity': 50000,
            'currentEarnings': 2500,
            'isBalanced': true,
          },
        };
      case '/accounting/income-statement':
        return {
          'sections': [
            {
              'label': 'Income',
              'rows': [
                {
                  'accountId': 101,
                  'accountCode': '4100',
                  'accountName': 'Rent income',
                  'amount': 2500,
                  'currency': 'USD',
                },
              ],
              'subtotal': 2500,
            },
          ],
          'totals': {
            'total': 2500,
            'netIncome': 2500,
            'assets': null,
            'liabilitiesAndEquity': null,
            'currentEarnings': null,
            'isBalanced': null,
          },
        };
      default:
        throw StateError('Unexpected accounting path: $path');
    }
  }
}
