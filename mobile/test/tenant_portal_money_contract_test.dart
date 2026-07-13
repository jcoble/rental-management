import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/portal/tenant_portal_repository.dart';

void main() {
  test('canonical tenant account and charge DTOs keep exact identities', () {
    final account = PortalTenantAccount.fromJson({
      'tenantAccountId': 41,
      'leaseManagementId': 17,
      'propertyName': 'Main Street',
      'unitNumber': '2B',
      'accountNumber': 'TA-0041',
      'relationshipNumber': 'LM-0017',
      'lifecycle': 'Occupied',
      'currency': 'USD',
      'receivableBalance': 1250,
      'unappliedCredit': 0,
      'pastDueAmount': 200,
      'pastDueCount': 1,
      'nextDueOn': '2026-08-01',
      'nextDueAmount': 1250,
      'condition': 'PastDue',
    });
    final charge = PortalTenantCharge.fromJson({
      'tenantAccountId': 41,
      'leaseManagementId': 17,
      'tenantLedgerEntryId': 9000000001,
      'entryType': 'RentCharge',
      'description': 'August rent',
      'currency': 'USD',
      'originalAmount': 1250,
      'openAmount': 1050,
      'isPastDue': false,
      'dueOn': '2026-08-01',
    });
    final entry = PortalTenantLedgerEntry.fromJson({
      'tenantAccountId': 41,
      'leaseManagementId': 17,
      'tenantLedgerEntryId': 9000000002,
      'entryType': 'Receipt',
      'direction': 'Credit',
      'amount': 200,
      'currency': 'USD',
      'effectiveOn': '2026-07-15',
      'dueOn': null,
      'description': 'Online payment',
    });

    expect(account.tenantAccountId, 41);
    expect(account.leaseManagementId, 17);
    expect(account.pastDueCount, 1);
    expect(charge.tenantAccountId, 41);
    expect(charge.leaseManagementId, 17);
    expect(charge.tenantLedgerEntryId, 9000000001);
    expect(charge.openAmount, 1050);
    expect(entry.tenantAccountId, 41);
    expect(entry.leaseManagementId, 17);
    expect(entry.tenantLedgerEntryId, 9000000002);
  });

  test('portal money sources use canonical routes and explicit selection', () {
    final repository = File(
      'lib/features/portal/tenant_portal_repository.dart',
    ).readAsStringSync();
    final home = File('lib/features/home/home_shell.dart').readAsStringSync();
    final history = File(
      'lib/features/portal/tenant_account_history_screen.dart',
    ).readAsStringSync();

    expect(repository, contains('/portal/tenant-accounts/page'));
    expect(repository, contains('/charges/page'));
    expect(repository, contains('/entries/page'));
    expect(repository, contains('/deposit'));
    expect(repository, isNot(contains("'/portal/balance'")));
    expect(repository, isNot(contains("'/portal/payments'")));
    expect(home, contains('Choose an account'));
    expect(history, contains('Choose an account'));
    expect(home, isNot(contains('.sort(')));
    expect(history, isNot(contains('.sort(')));
  });
}
