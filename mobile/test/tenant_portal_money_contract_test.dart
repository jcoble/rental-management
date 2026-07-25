import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/models/models.dart';
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

  test('portal lease agreement parses document availability metadata', () {
    final agreement = PortalLeaseAgreement.fromJson({
      'leaseAgreementId': 81,
      'agreementNumber': 'AGR-81',
      'agreementStatus': 'Executed',
      'termStartOn': '2026-01-01',
      'termEndOn': '2026-12-31',
      'baseRentAmount': 1400,
      'securityDepositObligation': 1400,
      'lateFeeAmount': 50,
      'rentDueDay': 1,
      'executedDocumentAvailable': true,
      'executedDocumentFileName': 'signed-lease.pdf',
      'executedDocumentContentType': 'application/pdf',
    });

    expect(agreement.executedDocumentAvailable, isTrue);
    expect(agreement.executedDocumentFileName, 'signed-lease.pdf');
    expect(agreement.executedDocumentContentType, 'application/pdf');
  });

  test(
    'portal lease document download uses exact tenant endpoint and metadata',
    () async {
      final adapter = _PortalLeaseDocumentAdapter();
      final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter;
      final repository = TenantPortalRepository(dio);

      final document = await repository.executedAgreementDocument(
        leaseManagementId: 44,
        leaseAgreementId: 81,
        fileName: 'signed-lease.pdf',
        contentType: 'application/pdf',
      );

      expect(adapter.method, 'GET');
      expect(adapter.path, '/portal/leases/44/agreements/81/executed-document');
      expect(document.bytes, Uint8List.fromList([1, 2, 3]));
      expect(document.fileName, 'signed-lease.pdf');
      expect(document.contentType, 'application/pdf');
    },
  );

  test(
    'tenant lease screen opens exact document bytes and shows safe states',
    () {
      final screen = File(
        'lib/features/tenants/tenant_lease_screen.dart',
      ).readAsStringSync();

      expect(screen, contains('tenantLeaseDocumentOpenerProvider'));
      expect(screen, contains('executedAgreementDocument('));
      expect(screen, contains('bytes: document.bytes'));
      expect(screen, contains('fileName: document.fileName'));
      expect(screen, contains('mimeType: document.contentType'));
      expect(
        screen,
        contains('Signed lease PDF is not available yet. Contact management.'),
      );
      expect(
        screen,
        contains(
          'Could not open the signed lease PDF. Please try again or contact management.',
        ),
      );
      expect(screen, isNot(contains('/lease-managements/')));
    },
  );
}

class _PortalLeaseDocumentAdapter implements HttpClientAdapter {
  String? method;
  String? path;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    method = options.method;
    path = options.path;

    if (options.path.endsWith('/executed-document')) {
      return ResponseBody.fromBytes(
        [1, 2, 3],
        200,
        headers: {
          Headers.contentTypeHeader: ['application/pdf'],
        },
      );
    }

    return ResponseBody.fromString(
      jsonEncode(<String, dynamic>{}),
      404,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}
