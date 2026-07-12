import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/deposits/deposits_repository.dart';

void main() {
  test(
    'deposit screen exposes canonical account actions without create holding',
    () {
      final source = File(
        'lib/features/deposits/deposits_screen.dart',
      ).readAsStringSync();

      expect(source, contains("label: 'Fund deposit'"));
      expect(source, contains("title: 'Record deduction'"));
      expect(source, contains("title: 'Record refund'"));
      expect(source, contains('showModalBottomSheet<void>'));
      expect(
        RegExp(
          r"_operationKey = const Uuid\(\)\.v4\(\);",
        ).allMatches(source).length,
        3,
      );
      expect(source, isNot(contains('Record Security Deposit')));
      expect(source, isNot(contains('_CreateDepositSheet')));
    },
  );

  test(
    'list parses the canonical security deposit account projection',
    () async {
      final adapter = _DepositAdapter();
      final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter;
      final repository = DepositsRepository(dio);

      final accounts = await repository.listDeposits(leaseManagementId: 23);

      expect(adapter.method, 'GET');
      expect(adapter.path, '/security-deposits');
      expect(adapter.queryParameters, {'leaseManagementId': 23});
      expect(accounts, hasLength(1));
      final account = accounts.single;
      expect(account.id, 31);
      expect(account.tenantAccountId, 41);
      expect(account.leaseManagementId, 23);
      expect(account.originatingAgreementId, 51);
      expect(account.propertyId, 61);
      expect(account.unitId, 71);
      expect(account.propertyName, 'Mallard Point');
      expect(account.unitNumber, '2B');
      expect(account.totalReceived, 1500);
      expect(account.totalDeductions, 125);
      expect(account.totalRefunded, 500);
      expect(account.heldBalance, 875);
      expect(account.status, 'Held');
    },
  );

  test(
    'fund posts canonical tenant-account command with caller operation key',
    () async {
      final adapter = _DepositAdapter();
      final repository = DepositsRepository(
        Dio(BaseOptions(baseUrl: 'https://example.test'))
          ..httpClientAdapter = adapter,
      );

      await repository.fundDeposit(
        _account,
        FundSecurityDepositInput(
          amount: 250,
          effectiveOn: DateTime(2026, 7, 12),
          description: 'Security deposit received',
          paymentMethodSummary: 'Check',
          externalReference: 'CHK-100',
        ),
        operationKey: 'fund-form-unchanged-on-retry',
      );

      expect(adapter.method, 'POST');
      expect(adapter.path, '/tenant-accounts/41/deposit/fund');
      expect(
        adapter.headers['Idempotency-Key'],
        'fund-form-unchanged-on-retry',
      );
      expect(adapter.data, {
        'securityDepositAccountId': 31,
        'amount': 250.0,
        'effectiveOn': '2026-07-12',
        'description': 'Security deposit received',
        'paymentMethodSummary': 'Check',
        'externalReference': 'CHK-100',
      });
    },
  );

  test(
    'deduction and refund use canonical routes and DTO field names',
    () async {
      final adapter = _DepositAdapter();
      final repository = DepositsRepository(
        Dio(BaseOptions(baseUrl: 'https://example.test'))
          ..httpClientAdapter = adapter,
      );

      await repository.deductDeposit(
        _account,
        DeductSecurityDepositInput(
          amount: 75,
          effectiveOn: DateTime(2026, 7, 13),
          reason: 'Cleaning',
          notes: 'Invoice attached',
        ),
        operationKey: 'deduction-form-key',
      );
      expect(adapter.path, '/tenant-accounts/41/deposit/deductions');
      expect(adapter.headers['Idempotency-Key'], 'deduction-form-key');
      expect(adapter.data, {
        'securityDepositAccountId': 31,
        'amount': 75.0,
        'effectiveOn': '2026-07-13',
        'reason': 'Cleaning',
        'notes': 'Invoice attached',
      });

      await repository.refundDeposit(
        _account,
        RefundSecurityDepositInput(
          amount: 800,
          effectiveOn: DateTime(2026, 7, 14),
          description: 'Final deposit refund',
          externalReference: 'ACH-200',
        ),
        operationKey: 'refund-form-key',
      );
      expect(adapter.path, '/tenant-accounts/41/deposit/refunds');
      expect(adapter.headers['Idempotency-Key'], 'refund-form-key');
      expect(adapter.data, {
        'securityDepositAccountId': 31,
        'amount': 800.0,
        'effectiveOn': '2026-07-14',
        'description': 'Final deposit refund',
        'externalReference': 'ACH-200',
      });
    },
  );
}

final _account = SecurityDepositAccount(
  id: 31,
  tenantAccountId: 41,
  leaseManagementId: 23,
  originatingAgreementId: 51,
  propertyId: 61,
  unitId: 71,
  accountNumber: 'TA-0041',
  relationshipNumber: 'LM-0023',
  tenantName: 'Jordan Lee',
  propertyName: 'Mallard Point',
  unitNumber: '2B',
  currency: 'USD',
  totalReceived: 1500,
  totalDeductions: 125,
  totalRefunded: 500,
  heldBalance: 875,
  status: 'Held',
  createdAtUtc: DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
);

class _DepositAdapter implements HttpClientAdapter {
  String? method;
  String? path;
  Map<String, dynamic> queryParameters = {};
  Map<String, dynamic> headers = {};
  Object? data;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    method = options.method;
    path = options.path;
    queryParameters = Map<String, dynamic>.from(options.queryParameters);
    headers = Map<String, dynamic>.from(options.headers);
    data = options.data;

    final body = options.method == 'GET'
        ? [
            {
              'id': 31,
              'tenantAccountId': 41,
              'leaseManagementId': 23,
              'originatingAgreementId': 51,
              'propertyId': 61,
              'unitId': 71,
              'accountNumber': 'TA-0041',
              'relationshipNumber': 'LM-0023',
              'tenantName': 'Jordan Lee',
              'propertyName': 'Mallard Point',
              'unitNumber': '2B',
              'currency': 'USD',
              'totalReceived': 1500,
              'totalDeductions': 125,
              'totalRefunded': 500,
              'heldBalance': 875,
              'status': 'Held',
              'createdAtUtc': '2026-07-01T12:00:00Z',
            },
          ]
        : {
            'value': {'applied': true},
            'replayed': false,
          };
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
