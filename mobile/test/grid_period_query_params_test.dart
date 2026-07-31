import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/widgets/mobile_grid_controls.dart';
import 'package:rental_command/features/leases/leases_repository.dart';
import 'package:rental_command/features/money/money_repository.dart';
import 'package:rental_command/features/payments/payments_repository.dart';

void main() {
  test('mobile grid period helper emits inclusive date-only bounds', () {
    final now = DateTime(2026, 7);

    expect(
      mobileGridDateRangeForPeriod(MobileGridPeriod.thisMonth, now: now),
      _range('2026-07-01', '2026-07-31'),
    );
    expect(
      mobileGridDateRangeForPeriod(MobileGridPeriod.lastMonth, now: now),
      _range('2026-06-01', '2026-06-30'),
    );
    expect(
      mobileGridDateRangeForPeriod(MobileGridPeriod.next30Days, now: now),
      _range('2026-07-01', '2026-07-31'),
    );
    expect(
      mobileGridDateRangeForPeriod(MobileGridPeriod.thisYear, now: now),
      _range('2026-01-01', '2026-12-31'),
    );
  });

  test('receipts page sends canonical tenant relationship filters', () async {
    final adapter = _RecordingPageAdapter(_paymentPageJson());
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repo = PaymentsRepository(dio);

    final page = await repo.listTenantLedgerEntriesPage(
      const TenantLedgerEntryListQuery(
        tenantAccountId: 42,
        from: '2026-07-01',
        to: '2026-07-31',
      ),
    );

    expect(adapter.path, '/tenant-accounts/entries/page');
    expect(adapter.queryParameters, containsPair('tenantAccountId', 42));
    expect(
      adapter.queryParameters,
      containsPair('entryType', 'PaymentReceipt'),
    );
    expect(adapter.queryParameters, containsPair('from', '2026-07-01'));
    expect(adapter.queryParameters, containsPair('to', '2026-07-31'));
    expect(page.items.single.tenantLedgerEntryId, 12);
  });

  test('receipt detail carries the canonical account and entry ids', () async {
    final adapter = _RecordingPageAdapter({
      ..._paymentPageJson()['items']!.first as Map<String, dynamic>,
      'portfolioId': 1,
      'tenantName': 'Jesse Coble',
      'providerAttempt': {
        'paymentMethodSummary': 'Check',
        'providerReference': 'RCPT-12',
      },
    });
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repo = PaymentsRepository(dio);

    final detail = await repo.getTenantLedgerEntry(42, 12);

    expect(adapter.path, '/tenant-accounts/42/entries/12');
    expect(detail.tenantAccountId, 42);
    expect(detail.tenantLedgerEntryId, 12);
    expect(detail.paymentMethodSummary, 'Check');
  });

  test('tenant charges page uses canonical server-paged endpoint', () async {
    final adapter = _RecordingPageAdapter(_chargePageJson());
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repo = PaymentsRepository(dio);

    final page = await repo.listTenantChargesPage(
      42,
      const TenantChargeListQuery(skip: 20, take: 10, sort: 'dueOn'),
    );

    expect(adapter.path, '/tenant-accounts/42/charges/page');
    expect(adapter.queryParameters, containsPair('skip', 20));
    expect(adapter.queryParameters, containsPair('take', 10));
    expect(adapter.queryParameters, containsPair('sort', 'dueOn'));
    expect(page.tenantAccountId, 42);
    expect(page.items.single.tenantLedgerEntryId, 9001);
    expect(page.items.single.openAmount, 875);
  });

  test(
    'record receipt uses the tenant-account command and idempotency key',
    () async {
      final adapter = _RecordingWriteAdapter({
        'value': {
          'tenantAccountId': 42,
          'ledgerEntryId': 101,
          'paymentAttemptId': 102,
          'amount': 1200,
          'allocatedAmount': 1200,
          'allocationCount': 1,
        },
        'replayed': false,
      });
      final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter;
      final repo = PaymentsRepository(dio);

      final result = await repo.recordReceipt(
        42,
        RecordTenantReceiptInput(
          amount: 1200,
          effectiveOn: DateTime(2026, 7, 8),
          description: 'July rent',
          paymentMethodSummary: 'Check',
          targetChargeEntryId: 9001,
        ),
        operationKey: 'receipt-key-42',
      );

      expect(adapter.method, 'POST');
      expect(adapter.path, '/tenant-accounts/42/receipts');
      expect(adapter.data, containsPair('effectiveOn', '2026-07-08'));
      expect(adapter.data, containsPair('targetChargeEntryId', 9001));
      expect(adapter.data, isNot(contains('allocateOldestCharges')));
      expect(
        adapter.headers,
        containsPair('Idempotency-Key', 'receipt-key-42'),
      );
      expect(result.ledgerEntryId, 101);
    },
  );

  test('expenses page sends incurred-period query params', () async {
    final adapter = _RecordingPageAdapter(_expensePageJson());
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repo = MoneyRepository(dio);

    final page = await repo.listExpensesPage(
      const ExpenseListQuery(
        incurredFrom: '2026-07-01',
        incurredTo: '2026-07-31',
      ),
    );

    expect(adapter.path, '/expenses/page');
    expect(adapter.queryParameters, containsPair('incurredFrom', '2026-07-01'));
    expect(adapter.queryParameters, containsPair('incurredTo', '2026-07-31'));
    expect(page.items.single.id, 21);
  });

  test('rental relationships page sends contextual query params', () async {
    final adapter = _RecordingPageAdapter(_leasePageJson());
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repo = LeaseManagementsRepository(dio);

    final page = await repo.listPage(
      const LeaseManagementListQuery(propertyId: 2, lifecycle: 'Occupied'),
    );

    expect(adapter.path, '/lease-managements/page');
    expect(adapter.queryParameters, containsPair('propertyId', 2));
    expect(adapter.queryParameters, containsPair('lifecycle', 'Occupied'));
    expect(page.items.single.id, 31);
  });
}

Matcher _range(String? from, String? to) {
  return isA<MobileGridDateRange>()
      .having((range) => range.from, 'from', from)
      .having((range) => range.to, 'to', to);
}

class _RecordingPageAdapter implements HttpClientAdapter {
  _RecordingPageAdapter(this.body);

  final Map<String, dynamic> body;
  String? path;
  Map<String, dynamic>? queryParameters;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    path = options.path;
    queryParameters = Map<String, dynamic>.from(options.queryParameters);

    return ResponseBody.fromString(
      jsonEncode(body),
      200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

class _RecordingWriteAdapter implements HttpClientAdapter {
  _RecordingWriteAdapter(this.body);

  final Map<String, dynamic> body;
  String? method;
  String? path;
  Object? data;
  Map<String, dynamic>? headers;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    method = options.method;
    path = options.path;
    data = options.data;
    headers = Map<String, dynamic>.from(options.headers);

    return ResponseBody.fromString(
      jsonEncode(body),
      200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

Map<String, dynamic> _paymentPageJson() => {
  'items': [
    {
      'tenantLedgerEntryId': 12,
      'publicId': '7b5b31ec-a3ea-4d87-b515-01b76f42a34c',
      'tenantAccountId': 42,
      'leaseManagementId': 7,
      'propertyId': 2,
      'propertyName': 'Maple Ridge',
      'unitId': 3,
      'unitNumber': '2B',
      'accountNumber': 'TA-42',
      'relationshipNumber': 'LM-7',
      'entryType': 'PaymentReceipt',
      'direction': 'Credit',
      'amount': 1200,
      'currency': 'USD',
      'effectiveOn': '2026-07-01',
      'postedAtUtc': '2026-07-01T00:00:00Z',
      'description': 'July rent',
    },
  ],
  'totalCount': 1,
  'skip': 0,
  'take': 20,
};

Map<String, dynamic> _chargePageJson() => {
  'tenantAccountId': 42,
  'leaseManagementId': 7,
  'items': [
    {
      'tenantAccountId': 42,
      'leaseManagementId': 7,
      'tenantLedgerEntryId': 9001,
      'publicId': '7b5b31ec-a3ea-4d87-b515-01b76f42a34c',
      'entryType': 'RentCharge',
      'direction': 'Debit',
      'currency': 'USD',
      'effectiveOn': '2026-07-01',
      'dueOn': '2026-07-01',
      'postedAtUtc': '2026-07-01T00:00:00Z',
      'description': 'July rent',
      'originalAmount': 1200,
      'reversedAmount': 0,
      'netAllocations': 325,
      'openAmount': 875,
      'isPastDue': true,
    },
  ],
  'totalCount': 1,
  'skip': 0,
  'take': 10,
};

Map<String, dynamic> _expensePageJson() => {
  'items': [
    {
      'id': 21,
      'portfolioId': 1,
      'category': 'Repairs',
      'description': 'Paint',
      'status': 'Pending',
      'amount': 80,
      'incurredAt': '2026-07-02T00:00:00Z',
      'billableToOwner': false,
      'hasReceipt': false,
      'receiptIsImage': false,
      'lineItems': [],
      'createdAt': '2026-07-02T00:00:00Z',
      'updatedAt': '2026-07-02T00:00:00Z',
    },
  ],
  'totalCount': 1,
  'skip': 0,
  'take': 20,
};

Map<String, dynamic> _leasePageJson() => {
  'items': [
    {
      'leaseManagementId': 31,
      'leaseManagementPublicId': '7b5b31ec-a3ea-4d87-b515-01b76f42a34c',
      'relationshipNumber': 'LM-31',
      'propertyId': 2,
      'propertyName': 'Maple Ridge',
      'unitId': 3,
      'unitNumber': '2B',
      'lifecycle': 'Occupied',
      'leaseAgreementId': 52,
      'agreementNumber': 'L-31',
      'agreementStatus': 'Executed',
      'termStartOn': '2026-01-01',
      'termEndOn': '2026-12-31',
      'baseRentAmount': 1200,
      'primaryTenantId': 4,
      'primaryTenantName': 'Verify Tenant',
      'currentPartyCount': 1,
      'currentResidentCount': 1,
      'hasReconciliationException': false,
      'endingDisposition': 'Undecided',
      'updatedAtUtc': '2026-07-01T00:00:00Z',
    },
  ],
  'totalCount': 1,
  'skip': 0,
  'take': 20,
};
