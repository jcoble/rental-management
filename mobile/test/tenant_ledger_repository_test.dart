import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/money/tenant_ledger_models.dart';
import 'package:rental_command/features/money/tenant_ledger_repository.dart';

void main() {
  test(
    'tenant ledger sends server filters and binds linked read-model fields',
    () async {
      final adapter = _TenantLedgerAdapter();
      final repository = TenantLedgerRepository(_dio(adapter));

      final page = await repository.ledger(
        41,
        query: TenantLedgerQuery(
          skip: 20,
          take: 25,
          search: 'January',
          sort: 'postedAtUtc',
          entryType: TenantLedgerEntryType.rentCharge,
          effectiveFrom: DateTime.utc(2027, 1, 1),
          effectiveTo: DateTime.utc(2027, 1, 31),
          openOnly: true,
          settledOnly: false,
        ),
      );

      final request = adapter.requests.single;
      expect(request.method, 'GET');
      expect(request.path, '/tenant-accounts/41/ledger');
      expect(request.queryParameters, {
        'skip': 20,
        'take': 25,
        'sort': 'postedAtUtc',
        'search': 'January',
        'entryType': 'RentCharge',
        'effectiveFrom': '2027-01-01',
        'effectiveTo': '2027-01-31',
        'openOnly': true,
        'settledOnly': false,
      });
      final row = page.items.single;
      expect(row.type, TenantLedgerEntryType.rentCharge);
      expect(row.runningAmountOwed, 1250);
      expect(row.allocations.single.targetSourceId, 100);
      expect(row.relatedTenantLedgerEntryId, 98);
      expect(row.relatedEntryDescription, 'December charge');
      expect(row.categoryName, 'Rent');
      expect(row.servicePeriodStartOn, DateTime.parse('2027-01-01'));
      expect(row.servicePeriodEndOn, DateTime.parse('2027-01-31'));
    },
  );

  test(
    'tenant summaries and recurring-charge reads use canonical routes',
    () async {
      final adapter = _TenantLedgerAdapter();
      final repository = TenantLedgerRepository(_dio(adapter));

      final months = await repository.monthSummary(
        41,
        query: TenantMonthSummaryQuery(
          from: DateTime.utc(2027, 1, 1),
          to: DateTime.utc(2027, 3, 31),
        ),
      );
      final summary = await repository.ledgerSummary(
        41,
        query: const TenantLedgerPeriodSummaryQuery(months: 6),
      );
      final recurring = await repository.recurringCharges(
        41,
        query: RecurringTenantChargeQuery(
          skip: 10,
          take: 20,
          search: 'rent',
          sort: 'nextRunDate',
          from: DateTime.utc(2027, 1, 1),
          to: DateTime.utc(2027, 12, 31),
        ),
      );

      expect(months.single.closingBalance, 1250);
      expect(summary.periodMonths, 6);
      expect(summary.aging1To30, 100);
      expect(recurring.items.single.displayName, 'Monthly rent');
      expect(recurring.items.single.nextRunDate, DateTime.parse('2027-02-01'));
      expect(adapter.requests.map((request) => request.path), [
        '/tenant-accounts/41/month-summary',
        '/tenant-accounts/41/ledger-summary',
        '/tenant-accounts/41/recurring-charges',
      ]);
      expect(adapter.requests[0].queryParameters, {
        'from': '2027-01-01',
        'to': '2027-03-31',
      });
      expect(adapter.requests[1].queryParameters, {'months': 6});
      expect(adapter.requests[2].queryParameters, {
        'skip': 10,
        'take': 20,
        'search': 'rent',
        'sort': 'nextRunDate',
        'from': '2027-01-01',
        'to': '2027-12-31',
      });
    },
  );

  test(
    'tenant money mutations bind DTO bodies, envelopes, and idempotency keys',
    () async {
      final adapter = _TenantLedgerAdapter();
      final repository = TenantLedgerRepository(_dio(adapter));

      final receipt = await repository.recordReceipt(
        41,
        RecordTenantReceiptInput(
          amount: 1250,
          effectiveOn: DateTime.utc(2027, 1, 15),
          description: 'January receipt',
          paymentMethodSummary: 'ACH',
          externalReference: 'ACH-1',
          payerName: 'Jordan Lee',
          checkNumber: 'not-used',
          bankName: 'Example Bank',
          sourceStoredFileId: 7,
          targetChargeEntryId: 98,
          allocateOldestCharges: false,
        ),
        operationKey: 'receipt-key',
      );
      final charge = await repository.postCharge(
        41,
        PostTenantChargeInput(
          amount: 1300,
          effectiveOn: DateTime.utc(2027, 2, 1),
          dueOn: DateTime.utc(2027, 2, 5),
          description: 'February rent',
          sourceStoredFileId: 8,
          incomeLedgerAccountId: 4100,
          servicePeriodStartOn: DateTime.utc(2027, 2, 1),
          servicePeriodEndOn: DateTime.utc(2027, 2, 28),
        ),
        operationKey: 'charge-key',
      );
      final credit = await repository.postCredit(
        41,
        PostTenantCreditInput(
          amount: 100,
          effectiveOn: DateTime.utc(2027, 2, 10),
          description: 'Rent concession',
          sourceStoredFileId: 9,
          allocateOldestCharges: true,
          targetChargeEntryId: 99,
          incomeLedgerAccountId: 4100,
        ),
        operationKey: 'credit-key',
      );
      final chargeReversal = await repository.reverseCharge(
        41,
        99,
        ReverseTenantChargeInput(
          effectiveOn: DateTime.utc(2027, 2, 11),
          reason: 'Duplicate charge',
          sourceStoredFileId: 10,
        ),
        operationKey: 'charge-reversal-key',
      );
      final ledgerReversal = await repository.reverseLedgerEntry(
        41,
        ReverseTenantLedgerEntryInput(
          reversesEntryId: 99,
          effectiveOn: DateTime.utc(2027, 2, 12),
          reason: 'Correcting entry',
          sourceStoredFileId: 11,
        ),
        operationKey: 'ledger-reversal-key',
      );

      expect(receipt.allocatedAmount, 1250);
      expect(receipt.replayed, isTrue);
      expect(charge.applied, isTrue);
      expect(credit.entryType, TenantLedgerEntryType.credit);
      expect(credit.direction, TenantLedgerDirection.credit);
      expect(chargeReversal.reversesEntryId, 99);
      expect(ledgerReversal.direction, TenantLedgerDirection.debit);

      expect(adapter.requests.map((request) => request.path), [
        '/tenant-accounts/41/receipts',
        '/tenant-accounts/41/charges',
        '/tenant-accounts/41/credits',
        '/tenant-accounts/41/charges/99/reversals',
        '/tenant-accounts/41/reversals',
      ]);
      expect(adapter.requests[0].data, {
        'amount': 1250.0,
        'effectiveOn': '2027-01-15',
        'description': 'January receipt',
        'paymentMethodSummary': 'ACH',
        'externalReference': 'ACH-1',
        'payerName': 'Jordan Lee',
        'checkNumber': 'not-used',
        'bankName': 'Example Bank',
        'sourceStoredFileId': 7,
        'targetChargeEntryId': 98,
        'allocateOldestCharges': false,
      });
      expect(adapter.requests[1].data, {
        'amount': 1300.0,
        'effectiveOn': '2027-02-01',
        'dueOn': '2027-02-05',
        'description': 'February rent',
        'sourceStoredFileId': 8,
        'incomeLedgerAccountId': 4100,
        'servicePeriodStartOn': '2027-02-01',
        'servicePeriodEndOn': '2027-02-28',
      });
      expect(adapter.requests[2].data, {
        'amount': 100.0,
        'effectiveOn': '2027-02-10',
        'description': 'Rent concession',
        'sourceStoredFileId': 9,
        'allocateOldestCharges': true,
        'targetChargeEntryId': 99,
        'incomeLedgerAccountId': 4100,
      });
      expect(adapter.requests[3].data, {
        'effectiveOn': '2027-02-11',
        'reason': 'Duplicate charge',
        'sourceStoredFileId': 10,
      });
      expect(adapter.requests[4].data, {
        'reversesEntryId': 99,
        'effectiveOn': '2027-02-12',
        'reason': 'Correcting entry',
        'sourceStoredFileId': 11,
      });
      expect(
        adapter.requests.map((request) => request.headers['Idempotency-Key']),
        [
          'receipt-key',
          'charge-key',
          'credit-key',
          'charge-reversal-key',
          'ledger-reversal-key',
        ],
      );
    },
  );

  test(
    'recurring charge mutations use direct row responses and canonical bodies',
    () async {
      final adapter = _TenantLedgerAdapter();
      final repository = TenantLedgerRepository(_dio(adapter));

      final created = await repository.createRecurringCharge(
        41,
        CreateRecurringTenantChargeInput(
          displayName: 'Monthly rent',
          amount: 1300,
          ledgerAccountId: 4100,
          leaseAgreementId: 77,
          effectiveStartOn: DateTime.utc(2027, 1, 1),
          effectiveEndOn: DateTime.utc(2027, 12, 31),
          monthlyDueDay: 1,
          nextRunDate: DateTime.utc(2027, 2, 1),
          propertyId: 7,
          unitId: 8,
        ),
        operationKey: 'recurring-create-key',
      );
      final updated = await repository.updateRecurringCharge(
        41,
        12,
        UpdateRecurringTenantChargeInput(
          displayName: 'Updated rent',
          amount: 1350,
          ledgerAccountId: 4101,
          effectiveStartOn: DateTime.utc(2027, 1, 2),
          effectiveEndOn: DateTime.utc(2027, 12, 30),
          monthlyDueDay: 2,
          nextRunDate: DateTime.utc(2027, 3, 2),
          isActive: true,
          propertyId: 9,
          unitId: 10,
        ),
        operationKey: 'recurring-update-key',
      );
      final deactivated = await repository.deactivateRecurringCharge(
        41,
        12,
        operationKey: 'recurring-deactivate-key',
      );

      expect(created.id, 12);
      expect(updated.displayName, 'Updated rent');
      expect(deactivated.isActive, isFalse);
      expect(adapter.requests.map((request) => request.path), [
        '/tenant-accounts/41/recurring-charges',
        '/tenant-accounts/41/recurring-charges/12',
        '/tenant-accounts/41/recurring-charges/12/deactivate',
      ]);
      expect(adapter.requests.map((request) => request.method), [
        'POST',
        'PATCH',
        'POST',
      ]);
      expect(adapter.requests[0].data, {
        'displayName': 'Monthly rent',
        'amount': 1300.0,
        'ledgerAccountId': 4100,
        'leaseAgreementId': 77,
        'effectiveStartOn': '2027-01-01',
        'effectiveEndOn': '2027-12-31',
        'monthlyDueDay': 1,
        'nextRunDate': '2027-02-01',
        'propertyId': 7,
        'unitId': 8,
      });
      expect(adapter.requests[1].data, {
        'displayName': 'Updated rent',
        'amount': 1350.0,
        'ledgerAccountId': 4101,
        'effectiveStartOn': '2027-01-02',
        'effectiveEndOn': '2027-12-30',
        'monthlyDueDay': 2,
        'nextRunDate': '2027-03-02',
        'isActive': true,
        'propertyId': 9,
        'unitId': 10,
      });
      expect(adapter.requests[2].data, isEmpty);
      expect(
        adapter.requests.map((request) => request.headers['Idempotency-Key']),
        [
          'recurring-create-key',
          'recurring-update-key',
          'recurring-deactivate-key',
        ],
      );
    },
  );
}

Dio _dio(_TenantLedgerAdapter adapter) =>
    Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;

class _RecordedRequest {
  const _RecordedRequest({
    required this.method,
    required this.path,
    required this.queryParameters,
    required this.headers,
    this.data,
  });

  final String method;
  final String path;
  final Map<String, dynamic> queryParameters;
  final Map<String, dynamic> headers;
  final Object? data;
}

class _TenantLedgerAdapter implements HttpClientAdapter {
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
        headers: Map<String, dynamic>.from(options.headers),
        data: options.data,
      ),
    );
    return ResponseBody.fromString(
      jsonEncode(
        options.method == 'POST' &&
                options.path == '/tenant-accounts/41/recurring-charges'
            ? _recurringRow()
            : _responseFor(options.path),
      ),
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
      case '/tenant-accounts/41/ledger':
        return {
          'items': [
            {
              'tenantLedgerEntryId': 99,
              'publicId': 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
              'sourceType': 'LeaseCharge',
              'sourceId': 99,
              'sourcePublicId': 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
              'effectiveOn': '2027-01-15',
              'postedAtUtc': '2027-01-15T14:00:00Z',
              'type': 'RentCharge',
              'description': 'January rent',
              'chargeAmount': 1250,
              'paymentAmount': 0,
              'creditAmount': 0,
              'runningAmountOwed': 1250,
              'dueOn': '2027-01-20',
              'openAmount': 1250,
              'status': 'Open',
              'paymentMethod': null,
              'reference': 'INV-1',
              'accountLabel': 'Rent',
              'recurringScheduleContext': 'Monthly rent',
              'sourceDocumentContext': 'Lease agreement',
              'allocations': [
                {
                  'targetSourceId': 100,
                  'targetPublicId': 'cccccccc-cccc-cccc-cccc-cccccccccccc',
                  'targetDescription': 'January receipt',
                  'amount': 1250,
                  'effectiveOn': '2027-01-20',
                },
              ],
              'reversesEntryId': null,
              'replacedByEntryId': null,
              'journalEntryPublicId': 'dddddddd-dddd-dddd-dddd-dddddddddddd',
              'currency': 'USD',
              'relatedTenantLedgerEntryId': 98,
              'relatedEntryDescription': 'December charge',
              'categoryName': 'Rent',
              'servicePeriodStartOn': '2027-01-01',
              'servicePeriodEndOn': '2027-01-31',
            },
          ],
          'totalCount': 1,
          'skip': 20,
          'take': 25,
        };
      case '/tenant-accounts/41/month-summary':
        return [
          {
            'year': 2027,
            'month': 1,
            'currency': 'USD',
            'openingBalance': 0,
            'chargeAmount': 1250,
            'paymentAmount': 0,
            'creditAmount': 0,
            'closingBalance': 1250,
          },
        ];
      case '/tenant-accounts/41/ledger-summary':
        return {
          'periodMonths': 6,
          'currency': 'USD',
          'chargeAmount': 7500,
          'paymentAmount': 6250,
          'creditAmount': 0,
          'endingBalance': 1250,
          'agingCurrent': 1150,
          'aging1To30': 100,
          'aging31To60': 0,
          'aging61To90': 0,
          'aging90Plus': 0,
        };
      case '/tenant-accounts/41/recurring-charges':
        return {
          'items': [_recurringRow()],
          'totalCount': 1,
          'skip': 10,
          'take': 20,
        };
      case '/tenant-accounts/41/receipts':
        return {
          'value': {
            'found': true,
            'tenantAccountId': 41,
            'ledgerEntryId': 100,
            'paymentAttemptId': 200,
            'amount': 1250,
            'allocatedAmount': 1250,
            'allocationCount': 1,
          },
          'replayed': true,
        };
      case '/tenant-accounts/41/charges':
        return {
          'value': {
            'found': true,
            'applied': true,
            'tenantAccountId': 41,
            'ledgerEntryId': 101,
            'reversesEntryId': null,
            'amount': 1300,
            'error': null,
          },
          'replayed': false,
        };
      case '/tenant-accounts/41/credits':
        return {
          'value': {
            'found': true,
            'applied': true,
            'tenantAccountId': 41,
            'ledgerEntryId': 102,
            'reversesEntryId': null,
            'entryType': 'Credit',
            'direction': 'Credit',
            'amount': 100,
            'allocatedAmount': 100,
            'allocationCount': 1,
            'error': null,
          },
          'replayed': false,
        };
      case '/tenant-accounts/41/charges/99/reversals':
        return {
          'value': {
            'found': true,
            'applied': true,
            'tenantAccountId': 41,
            'ledgerEntryId': 103,
            'reversesEntryId': 99,
            'amount': 1300,
            'error': null,
          },
          'replayed': false,
        };
      case '/tenant-accounts/41/reversals':
        return {
          'value': {
            'found': true,
            'applied': true,
            'tenantAccountId': 41,
            'ledgerEntryId': 104,
            'reversesEntryId': 99,
            'entryType': 'Reversal',
            'direction': 'Debit',
            'amount': 1300,
            'allocatedAmount': 0,
            'allocationCount': 0,
            'error': null,
          },
          'replayed': false,
        };
      case '/tenant-accounts/41/recurring-charges/12':
        return {
          ..._recurringRow(),
          'displayName': 'Updated rent',
          'amount': 1350,
        };
      case '/tenant-accounts/41/recurring-charges/12/deactivate':
        return {..._recurringRow(), 'isActive': false};
      default:
        throw StateError('Unexpected tenant-ledger path: $path');
    }
  }
}

Map<String, dynamic> _recurringRow() => {
  'id': 12,
  'publicId': 'eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee',
  'tenantAccountId': 41,
  'leaseAgreementId': 77,
  'displayName': 'Monthly rent',
  'amount': 1300,
  'currency': 'USD',
  'ledgerAccountId': 4100,
  'effectiveStartOn': '2027-01-01',
  'effectiveEndOn': '2027-12-31',
  'monthlyDueDay': 1,
  'nextRunDate': '2027-02-01',
  'isActive': true,
  'propertyId': 7,
  'unitId': 8,
};
