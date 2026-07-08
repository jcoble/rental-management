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

  test('payments page sends due-period query params', () async {
    final adapter = _RecordingPageAdapter(_paymentPageJson());
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repo = PaymentsRepository(dio);

    final page = await repo.listPaymentsPage(
      const PaymentListQuery(dueFrom: '2026-07-01', dueTo: '2026-07-31'),
    );

    expect(adapter.path, '/payments/page');
    expect(adapter.queryParameters, containsPair('dueFrom', '2026-07-01'));
    expect(adapter.queryParameters, containsPair('dueTo', '2026-07-31'));
    expect(page.items.single.id, 12);
  });

  test('lease past-due mark-paid uses the server action endpoint', () async {
    final adapter = _RecordingWriteAdapter({
      'leaseId': 42,
      'markedPaidCount': 2,
      'paymentIds': [101, 102],
    });
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repo = PaymentsRepository(dio);

    final result = await repo.markLeasePastDuePaid(42, paidDate: '2026-07-08');

    expect(adapter.method, 'POST');
    expect(adapter.path, '/payments/leases/42/past-due/mark-paid');
    expect(adapter.data, containsPair('paidDate', '2026-07-08'));
    expect(result.markedPaidCount, 2);
    expect(result.paymentIds, [101, 102]);
  });

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

  test('leases page sends active-period query params', () async {
    final adapter = _RecordingPageAdapter(_leasePageJson());
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repo = LeasesRepository(dio);

    final page = await repo.listLeasesPage(
      const LeaseListQuery(activeFrom: '2026-07-01', activeTo: '2026-07-31'),
    );

    expect(adapter.path, '/leases/page');
    expect(adapter.queryParameters, containsPair('activeFrom', '2026-07-01'));
    expect(adapter.queryParameters, containsPair('activeTo', '2026-07-31'));
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

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    method = options.method;
    path = options.path;
    data = options.data;

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
      'id': 12,
      'portfolioId': 1,
      'leaseId': 7,
      'paymentType': 'Rent',
      'status': 'Scheduled',
      'amount': 1200,
      'dueDate': '2026-07-01',
      'createdAt': '2026-07-01T00:00:00Z',
      'updatedAt': '2026-07-01T00:00:00Z',
    },
  ],
  'totalCount': 1,
  'skip': 0,
  'take': 20,
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
      'id': 31,
      'portfolioId': 1,
      'propertyId': 2,
      'unitId': 3,
      'tenantId': 4,
      'leaseNumber': 'L-31',
      'status': 'Active',
      'startDate': '2026-01-01',
      'endDate': '2026-12-31',
      'monthlyRent': 1200,
      'securityDeposit': 1200,
      'lateFeeAmount': 50,
      'rentDueDay': 1,
      'createdAt': '2026-01-01T00:00:00Z',
      'updatedAt': '2026-07-01T00:00:00Z',
    },
  ],
  'totalCount': 1,
  'skip': 0,
  'take': 20,
};
