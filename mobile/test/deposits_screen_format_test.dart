import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/deposits/deposits_repository.dart';
import 'package:rental_command/features/deposits/deposits_screen.dart';

void main() {
  testWidgets('deposit amounts use thousands separators', (tester) async {
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          depositsRepositoryProvider.overrideWithValue(
            _StubDepositsRepository(),
          ),
        ],
        child: const MaterialApp(home: DepositsScreen()),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.textContaining(r'$1,050.00'), findsOneWidget);
  });
}

class _StubDepositsRepository extends DepositsRepository {
  _StubDepositsRepository() : super(Dio());

  @override
  Future<TenantAccountDepositPage> listDepositsPage([
    TenantAccountDepositQuery query = const TenantAccountDepositQuery(),
  ]) async => TenantAccountDepositPage(
    items: [
      TenantAccountDeposit(
        securityDepositAccountId: 1,
        tenantAccountId: 1,
        leaseManagementId: 1,
        originatingAgreementId: 1,
        propertyId: 1,
        unitId: 1,
        accountNumber: 'TA-0001',
        relationshipNumber: 'LM-0001',
        primaryTenantName: 'Jordan Lee',
        propertyName: 'Mallard Point',
        unitNumber: '2B',
        currency: 'USD',
        totalReceived: 1050,
        totalDeductions: 0,
        totalRefunded: 0,
        totalTransferredIn: 0,
        totalTransferredOut: 0,
        netAdjustments: 0,
        heldBalance: 1050,
        status: 'Held',
        createdAtUtc: DateTime.utc(2026),
        effectiveNowUtc: DateTime.utc(2026),
        businessDate: '2026-01-01',
      ),
    ],
    totalCount: 1,
    skip: query.skip,
    take: query.take,
    query: query,
  );
}
