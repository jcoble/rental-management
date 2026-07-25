import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/scan/scan_repository.dart';
import 'package:rental_command/features/money/money_repository.dart';
import 'package:rental_command/features/payments/payments_repository.dart';
import 'package:rental_command/features/units/units_repository.dart';

void main() {
  test('UnitDashboard keeps account and relationship identity without an agreement', () {
    final dashboard = UnitDashboard.fromJson({
      'unit': {
        'id': 17,
        'propertyId': 8,
        'unitNumber': 'Left',
        'bedrooms': 2,
        'bathrooms': 1,
        'marketRent': 1250,
        'status': 'Occupied',
        'createdAt': '2026-01-01T00:00:00Z',
        'updatedAt': '2026-01-01T00:00:00Z',
      },
      'propertyName': 'Hilliard Duplex',
      'lifecycleStage': 'Active',
      'leaseManagementId': 23,
      'tenantAccountId': 41,
      'nextBestAction': {'label': 'Rent on track', 'href': '/units/17'},
      'header': {},
      'currentLease': null,
      'overview': {},
      'turnover': {},
    });

    expect(dashboard.leaseManagementId, 23);
    expect(dashboard.tenantAccountId, 41);
    expect(dashboard.currentLease, isNull);
  });

  test('UnitPaymentSummary requires canonical account and relationship ids', () {
    final receipt = UnitPaymentSummary.fromJson({
      'id': 901,
      'tenantAccountId': 41,
      'leaseManagementId': 23,
      'leaseAgreementId': 77,
      'type': 'Rent',
      'status': 'Paid',
      'amount': 1200,
      'dueDate': '2026-07-01T00:00:00Z',
    });

    expect(receipt.tenantAccountId, 41);
    expect(receipt.leaseManagementId, 23);
    expect(receipt.leaseAgreementId, 77);
    expect(
      () => UnitPaymentSummary.fromJson({
        'id': 902,
        'leaseId': 23,
        'type': 'Rent',
        'status': 'Paid',
        'amount': 1200,
        'dueDate': '2026-07-01T00:00:00Z',
      }),
      throwsA(isA<TypeError>()),
      reason: 'legacy leaseId must not be accepted as an identity fallback',
    );
  });

  test('Unit scan upload sends account and relationship context', () async {
    final adapter = _RecordingAdapter();
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repository = ScanRepository(dio: dio);

    await repository.uploadImage(
      Uint8List.fromList([1, 2, 3]),
      'rent-check.jpg',
      'image/jpeg',
      targetEntityType: 'Payment',
      propertyId: 7,
      unitId: 12,
      leaseManagementId: 23,
      tenantAccountId: 41,
    );

    final form = adapter.data! as FormData;
    expect(_field(form, 'propertyId'), '7');
    expect(_field(form, 'unitId'), '12');
    expect(_field(form, 'leaseManagementId'), '23');
    expect(_field(form, 'tenantAccountId'), '41');
    expect(_field(form, 'leaseId'), isNull);
  });

  test('Unit Money uses independent server pages and persisted SingleRental gating', () async {
    final source = File(
      'lib/features/units/unit_command_center_screen.dart',
    ).readAsStringSync();

    expect(source, contains('unitMoneyActivityPageProvider'));
    expect(source, contains('unitMoneyChargesPageProvider'));
    expect(source, contains('unitMoneyDepositsPageProvider'));
    expect(source, contains('expensesPageProvider'));
    expect(source, contains('unitMoneyFinancingPageProvider'));
    expect(
      source,
      contains(
        'propertyAsync.value?.rentalStructure == RentalStructure.singleRental',
      ),
    );
    expect(source, isNot(contains('unitExpensesProvider(')));
  });

  test('Money repository sends paging to canonical account owners', () async {
    final adapter = _RecordingAdapter(
      response: {
        'items': [
          {
            'tenantLedgerEntryId': 88,
            'description': 'July rent',
            'currency': 'USD',
            'originalAmount': 1200,
            'netAllocations': 800,
            'openAmount': 400,
            'isPastDue': false,
          },
        ],
        'totalCount': 21,
        'skip': 10,
        'take': 10,
      },
    );
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final page = await MoneyRepository(dio).chargePositionsPage((
      tenantAccountId: 41,
      propertyId: 8,
      unitId: 17,
      skip: 10,
      take: 10,
    ));

    expect(adapter.options!.path, '/tenant-accounts/41/charges/page');
    expect(adapter.options!.queryParameters['skip'], 10);
    expect(adapter.options!.queryParameters['take'], 10);
    expect(page.totalCount, 21);
    expect(page.items.single.openAmount, 400);
  });

  test('SingleRental financing pages without a tenant account', () async {
    final adapter = _RecordingAdapter(
      response: {
        'items': <Map<String, dynamic>>[],
        'totalCount': 0,
        'skip': 0,
        'take': 10,
      },
    );
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;

    await MoneyRepository(dio).financingPage((
      propertyId: 8,
      skip: 0,
      take: 10,
    ));

    expect(adapter.options!.path, '/loans/page');
    expect(adapter.options!.queryParameters['propertyId'], 8);
    expect(adapter.options!.queryParameters, isNot(contains('tenantAccountId')));

    final source = File(
      'lib/features/units/unit_command_center_screen.dart',
    ).readAsStringSync();
    final financingGate = RegExp(
      r'final financingAsync = isSingleRental.*?: null;',
      dotAll: true,
    ).firstMatch(source);
    expect(financingGate, isNotNull);
    expect(financingGate!.group(0), isNot(contains('accountId != null')));
    expect(financingGate.group(0), isNot(contains('tenantAccountId:')));
  });

  test('Payment correction uses canonical refund route and an idempotency key', () async {
    final adapter = _RecordingAdapter(
      response: {
        'value': {
          'applied': true,
          'outcome': 'Refunded',
          'paymentEntryId': 901,
          'refundEntryId': 902,
          'compensatedAllocationAmount': 1200,
          'compensatedAllocationCount': 2,
        },
        'replayed': false,
      },
    );
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final result = await PaymentsRepository(dio).correctPayment(
      41,
      CorrectTenantPaymentInput(
        paymentEntryId: 901,
        effectiveOn: DateTime(2026, 7, 24),
        reason: 'Correction of immutable payment receipt',
        paymentMethodSummary: 'Check',
        externalReference: 'refund-check-7',
      ),
    );

    expect(adapter.options!.path, '/tenant-accounts/41/refunds');
    expect(adapter.options!.path, isNot(contains('/reversals')));
    expect(adapter.options!.headers['Idempotency-Key'], isNotEmpty);
    expect(result.refundEntryId, 902);
    expect(result.compensatedAllocationCount, 2);
  });
}

String? _field(FormData data, String key) {
  for (final entry in data.fields) {
    if (entry.key == key) return entry.value;
  }
  return null;
}

class _RecordingAdapter implements HttpClientAdapter {
  _RecordingAdapter({this.response});

  final Map<String, dynamic>? response;
  Object? data;
  RequestOptions? options;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    this.options = options;
    data = options.data;
    return ResponseBody.fromString(
      jsonEncode(
        response ??
            {
              'draftId': 99,
              'status': 'Processing',
              'fileUrl': '/api/v1/scans/99/file',
            },
      ),
      201,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}
