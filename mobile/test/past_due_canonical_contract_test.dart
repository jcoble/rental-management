import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/accounting/accounting_models.dart';

void main() {
  test('past-due page parses canonical account and ledger identity', () {
    final page = PastDueResult.fromJson({
      'items': [
        {
          'leaseManagementId': 12,
          'tenantAccountId': 34,
          'currentAgreementId': 56,
          'unitId': 78,
          'tenantName': 'Jordan Lee',
          'tenantPhone': '614-555-0130',
          'relationshipNumber': 'LM-12',
          'propertyName': 'Mallard Point',
          'unitNumber': '2B',
          'pastDueAmount': 975.50,
          'overduePaymentCount': 2,
          'oldestDueOn': '2026-06-01',
          'oldestLedgerEntryId': 901,
        },
      ],
      'totalCount': 25,
      'totalPastDueAmount': 12345.67,
      'businessDate': '2026-07-13',
      'skip': 20,
      'take': 20,
    });

    expect(page.skip, 20);
    expect(page.take, 20);
    expect(page.hasMore, isTrue);
    expect(page.businessDate, DateTime(2026, 7, 13));
    final account = page.items.single;
    expect(account.leaseManagementId, 12);
    expect(account.tenantAccountId, 34);
    expect(account.currentAgreementId, 56);
    expect(account.unitId, 78);
    expect(account.relationshipNumber, 'LM-12');
    expect(account.oldestDueOn, DateTime(2026, 6, 1));
    expect(account.oldestLedgerEntryId, 901);
    expect(account.displayName, 'Jordan Lee');
  });

  test('missing canonical identity fails instead of becoming zero', () {
    expect(
      () => PastDueLease.fromJson({
        'pastDueAmount': 100,
        'overduePaymentCount': 1,
        'oldestDueOn': '2026-06-01',
        'oldestLedgerEntryId': 9,
      }),
      throwsA(anything),
    );
  });

  test('screen uses canonical ledger navigation and receipt action', () {
    final source = File(
      'lib/features/money/overdue_screen.dart',
    ).readAsStringSync();

    expect(source, contains('UnitCommandCenterTab.ledger'));
    expect(source, contains('showRecordTenantReceiptSheet('));
    expect(source, contains('initialAmount: account.pastDueAmount'));
    expect(
      source,
      contains("'Load more (\${state.items.length} of \${state.totalCount})'"),
    );
    expect(source, isNot(contains('oldestPaymentId')));
    expect(source, isNot(contains('Lease #')));
    expect(source, isNot(contains('Tenant account #')));
    expect(source, isNot(contains('DateTime.now()')));
    expect(source, contains('businessDate.difference(lease.oldestDueOn)'));
  });
}
