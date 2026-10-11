import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/leases/leases_repository.dart';

void main() {
  test(
    'prepare move-in posts the canonical typed payload with idempotency',
    () async {
      final adapter = _PrepareMoveInAdapter();
      final repository = LeaseManagementsRepository(
        Dio(BaseOptions(baseUrl: 'https://example.test'))
          ..httpClientAdapter = adapter,
      );

      final result = await repository.prepareMoveIn(
        PrepareMoveInInput(
          applicationId: 12,
          unitId: 34,
          plannedPossessionAtUtc: DateTime.utc(2027, 6, 1, 15),
          partyEffectiveFrom: DateTime(2027, 6, 1),
          parties: const [
            PrepareMoveInPartyInput(
              tenantId: 56,
              role: 'PrimaryTenant',
              isAgreementSigner: true,
            ),
          ],
          documentTemplateId: 78,
          termType: 'FixedTerm',
          termStartOn: DateTime(2027, 6, 1),
          termEndOn: DateTime(2028, 5, 31),
          baseRentAmount: 1500,
          rentDueDay: 1,
          securityDepositObligation: 1500,
          lateFeeAmount: 50,
          gracePeriodDays: 5,
          createSecurityDepositAccount: true,
          openingBalanceAmount: 125,
          openingBalanceEffectiveOn: DateTime(2027, 6, 1),
          openingBalanceNote: 'Existing balance',
        ),
        operationKey: 'mobile-prepare-12',
      );

      expect(adapter.path, '/lease-managements/prepare-move-in');
      expect(adapter.headers['Idempotency-Key'], 'mobile-prepare-12');
      expect(adapter.data['applicationId'], 12);
      expect(adapter.data['unitId'], 34);
      expect(adapter.data['parties'], [
        {
          'tenantId': 56,
          'role': 'PrimaryTenant',
          'guarantorLegalNoticeEligible': false,
          'changeReason': 'Approved application move-in',
          'isAgreementSigner': true,
          'signingOrder': 1,
          'isRequiredSigner': true,
        },
      ]);
      expect(adapter.data['termsSchemaVersion'], 1);
      expect(adapter.data['termsPayload'], <String, dynamic>{});
      expect(adapter.data['termStartOn'], '2027-06-01');
      expect(adapter.data['termEndOn'], '2028-05-31');
      expect(result.leaseManagementId, 90);
      expect(result.tenantAccountId, 91);
      expect(result.leaseAgreementId, 92);
    },
  );

  test('mobile launch points use the approved-application prepare sheet', () {
    final sheet = File(
      'lib/features/leases/prepare_move_in_sheet.dart',
    ).readAsStringSync();
    final leases = File(
      'lib/features/leases/leases_list_screen.dart',
    ).readAsStringSync();
    final application = File(
      'lib/features/applications/application_detail_screen.dart',
    ).readAsStringSync();

    expect(sheet, contains("status: 'Approved'"));
    expect(sheet, contains('ApplicationListQuery('));
    expect(sheet, contains('skip: _applicationSkip'));
    expect(sheet, contains('leaseTemplatesPage('));
    expect(sheet, contains('propertyId: _application?.propertyId'));
    expect(sheet, contains('TabbedFormSheet('));
    expect(sheet, contains('approvedTenantId'));
    expect(sheet, contains('unitId: unitId'));
    expect(sheet, isNot(contains('LeaseId')));
    expect(leases, contains('showPrepareMoveInSheet(context, ref: ref)'));
    expect(leases, isNot(contains('ApplicationsListScreen')));
    expect(application, contains('application: application'));
    expect(application, contains("label: const Text('Prepare move-in')"));
  });
}

class _PrepareMoveInAdapter implements HttpClientAdapter {
  String path = '';
  Map<String, dynamic> headers = const {};
  Map<String, dynamic> data = const {};

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    path = options.path;
    headers = Map<String, dynamic>.from(options.headers);
    data = Map<String, dynamic>.from(options.data as Map);
    return ResponseBody.fromString(
      jsonEncode({
        'leaseManagementId': 90,
        'tenantAccountId': 91,
        'leaseAgreementId': 92,
      }),
      201,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}
