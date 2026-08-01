import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/widgets/mobile_m3_list.dart';
import 'package:rental_command/features/deposits/deposits_screen.dart';
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
      expect(source, contains('.getDeposit(summary.tenantAccountId)'));
      expect(source, contains('SearchBar('));
      expect(
        source,
        contains("'Load more (\${page.items.length} of \${page.totalCount})'"),
      );
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
    'list sends canonical server-side search filters sort and paging',
    () async {
      final adapter = _DepositAdapter();
      final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter;
      final repository = DepositsRepository(dio);

      final page = await repository.listDepositsPage(
        const TenantAccountDepositQuery(
          skip: 20,
          take: 20,
          search: 'jordan',
          sort: '-heldBalance',
          tenantAccountId: 41,
          propertyId: 61,
          status: 'Held',
        ),
      );

      expect(adapter.method, 'GET');
      expect(adapter.path, '/tenant-accounts/deposits/page');
      expect(adapter.queryParameters, {
        'skip': 20,
        'take': 20,
        'sort': '-heldBalance',
        'search': 'jordan',
        'tenantAccountId': 41,
        'propertyId': 61,
        'status': 'Held',
      });
      expect(page.totalCount, 1);
      expect(page.items, hasLength(1));
      final account = page.items.single;
      expect(account.securityDepositAccountId, 31);
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
      expect(account.totalTransferredIn, 50);
      expect(account.totalTransferredOut, 25);
      expect(account.netAdjustments, -25);
      expect(account.heldBalance, 875);
      expect(account.status, 'Held');
    },
  );

  testWidgets(
    'deposit load more disables with compact progress and restores count label',
    (tester) async {
      final repository = _ControlledDepositsRepository();
      repository.queue(
        Future.value(_depositPage(items: [_depositAccount(1)], totalCount: 3)),
      );
      final nextPage = Completer<TenantAccountDepositPage>();
      repository.queue(nextPage.future);

      await tester.pumpWidget(
        ProviderScope(
          overrides: [depositsRepositoryProvider.overrideWithValue(repository)],
          child: const MaterialApp(home: DepositsScreen()),
        ),
      );
      await tester.pump();
      await tester.pump();

      expect(find.text('Load more (1 of 3)'), findsOneWidget);
      await tester.tap(find.text('Load more (1 of 3)'));
      await tester.pump();

      final pendingButton = tester.widget<OutlinedButton>(
        find.widgetWithText(OutlinedButton, 'Load more (1 of 3)'),
      );
      expect(pendingButton.onPressed, isNull);
      final progress = tester.widget<SizedBox>(
        find.byKey(const Key('deposits-load-more-progress')),
      );
      expect(progress.width, 18);
      expect(progress.height, 18);
      expect(repository.queries.last.skip, 1);
      expect(repository.queries.last.take, 20);

      nextPage.complete(
        _depositPage(items: [_depositAccount(2)], totalCount: 3, skip: 1),
      );
      await tester.pump();
      await tester.pump();

      expect(
        find.byKey(const Key('deposits-load-more-progress')),
        findsNothing,
      );
      expect(find.text('Load more (2 of 3)'), findsOneWidget);
      expect(
        tester
            .widget<OutlinedButton>(
              find.widgetWithText(OutlinedButton, 'Load more (2 of 3)'),
            )
            .onPressed,
        isNotNull,
      );
    },
  );

  testWidgets('deposit loading state is not shaped like an account row', (
    tester,
  ) async {
    final repository = _ControlledDepositsRepository();
    final pending = Completer<TenantAccountDepositPage>();
    repository.queue(pending.future);

    await tester.pumpWidget(
      ProviderScope(
        overrides: [depositsRepositoryProvider.overrideWithValue(repository)],
        child: const MaterialApp(home: DepositsScreen()),
      ),
    );
    await tester.pump();
    await tester.pump();

    expect(find.byKey(const Key('deposits-loading')), findsOneWidget);
    expect(find.byType(MobileM3ListItem), findsNothing);
    expect(find.text('Loading security deposits'), findsNothing);

    pending.complete(_depositPage(items: const [], totalCount: 0));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('deposits-loading')), findsNothing);
  });

  test('detail is resolved by the exact tenant account id', () async {
    final adapter = _DepositAdapter();
    final repository = DepositsRepository(
      Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter,
    );

    final account = await repository.getDeposit(41);

    expect(adapter.method, 'GET');
    expect(adapter.path, '/tenant-accounts/41/deposit');
    expect(adapter.queryParameters, isEmpty);
    expect(account.securityDepositAccountId, 31);
    expect(account.primaryTenantName, 'Jordan Lee');
  });

  test(
    'move-out statement is resolved by the exact tenant account id',
    () async {
      final adapter = _DepositAdapter();
      final repository = DepositsRepository(
        Dio(BaseOptions(baseUrl: 'https://example.test'))
          ..httpClientAdapter = adapter,
      );

      final bytes = await repository.moveOutStatementBytes(41);

      expect(adapter.path, '/tenant-accounts/41/deposit/move-out-statement');
      expect(bytes, [1, 2, 3]);
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

final _account = TenantAccountDeposit(
  securityDepositAccountId: 31,
  tenantAccountId: 41,
  leaseManagementId: 23,
  originatingAgreementId: 51,
  propertyId: 61,
  unitId: 71,
  accountNumber: 'TA-0041',
  relationshipNumber: 'LM-0023',
  primaryTenantName: 'Jordan Lee',
  propertyName: 'Mallard Point',
  unitNumber: '2B',
  currency: 'USD',
  totalReceived: 1500,
  totalDeductions: 125,
  totalRefunded: 500,
  totalTransferredIn: 50,
  totalTransferredOut: 25,
  netAdjustments: -25,
  heldBalance: 875,
  status: 'Held',
  createdAtUtc: DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
  effectiveNowUtc: DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
  businessDate: '2026-07-13',
);

class _ControlledDepositsRepository extends DepositsRepository {
  _ControlledDepositsRepository() : super(Dio());

  final queries = <TenantAccountDepositQuery>[];
  final _responses = <Future<TenantAccountDepositPage>>[];

  void queue(Future<TenantAccountDepositPage> response) {
    _responses.add(response);
  }

  @override
  Future<TenantAccountDepositPage> listDepositsPage([
    TenantAccountDepositQuery query = const TenantAccountDepositQuery(),
  ]) {
    queries.add(query);
    if (_responses.isEmpty) {
      throw StateError('No queued deposit page.');
    }
    return _responses.removeAt(0);
  }
}

TenantAccountDepositPage _depositPage({
  required List<TenantAccountDeposit> items,
  required int totalCount,
  int skip = 0,
}) {
  final query = TenantAccountDepositQuery(skip: skip);
  return TenantAccountDepositPage(
    items: items,
    totalCount: totalCount,
    skip: skip,
    take: 20,
    query: query,
  );
}

TenantAccountDeposit _depositAccount(int id) => TenantAccountDeposit(
  securityDepositAccountId: id,
  tenantAccountId: id,
  leaseManagementId: id,
  originatingAgreementId: id,
  propertyId: id,
  unitId: id,
  accountNumber: 'TA-$id',
  relationshipNumber: 'LM-$id',
  primaryTenantName: 'Tenant $id',
  propertyName: 'Mallard Point',
  unitNumber: '$id',
  currency: 'USD',
  totalReceived: 1500,
  totalDeductions: 125,
  totalRefunded: 500,
  totalTransferredIn: 50,
  totalTransferredOut: 25,
  netAdjustments: -25,
  heldBalance: 875,
  status: 'Held',
  createdAtUtc: DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
  effectiveNowUtc: DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
  businessDate: '2026-07-13',
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

    if (options.path.endsWith('/move-out-statement')) {
      return ResponseBody.fromBytes(
        [1, 2, 3],
        200,
        headers: {
          Headers.contentTypeHeader: ['application/pdf'],
        },
      );
    }

    final body = options.method == 'GET'
        ? options.path.endsWith('/deposit')
              ? _depositJson
              : {
                  'items': [_depositJson],
                  'totalCount': 1,
                  'skip': 20,
                  'take': 20,
                }
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

final _depositJson = <String, dynamic>{
  'securityDepositAccountId': 31,
  'tenantAccountId': 41,
  'leaseManagementId': 23,
  'originatingAgreementId': 51,
  'propertyId': 61,
  'unitId': 71,
  'accountNumber': 'TA-0041',
  'relationshipNumber': 'LM-0023',
  'primaryTenantName': 'Jordan Lee',
  'propertyName': 'Mallard Point',
  'unitNumber': '2B',
  'currency': 'USD',
  'totalReceived': 1500,
  'totalDeductions': 125,
  'totalRefunded': 500,
  'totalTransferredIn': 50,
  'totalTransferredOut': 25,
  'netAdjustments': -25,
  'heldBalance': 875,
  'status': 'Held',
  'createdAtUtc': '2026-07-01T12:00:00Z',
  'effectiveNowUtc': '2026-07-13T12:00:00Z',
  'businessDate': '2026-07-13',
};
