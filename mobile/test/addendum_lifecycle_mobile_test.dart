import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/leases/leases_repository.dart';

void main() {
  test(
    'addendum history keeps authoritative paging and artifact identities',
    () {
      final page = LeaseAddendumHistoryPage.fromJson({
        'totalCount': 21,
        'skip': 10,
        'take': 10,
        'items': [
          {
            'leaseAddendumId': 44,
            'seriesPublicId': 'series-1',
            'baseAgreementId': 8,
            'versionNumber': 2,
            'addendumNumber': 'PET-1',
            'purpose': 'Pet',
            'effectiveFromOn': '2026-07-13',
            'addendumStatus': 'Active',
            'financialEffectCount': 1,
            'recurringRentDelta': 25,
            'signerCount': 2,
            'canCorrect': true,
            'issuedArtifact': {
              'legalDocumentArtifactId': 90,
              'fileName': 'issued.pdf',
              'contentType': 'application/pdf',
              'byteLength': 100,
            },
            'executedArtifact': {
              'legalDocumentArtifactId': 91,
              'fileName': 'executed.pdf',
              'contentType': 'application/pdf',
              'byteLength': 110,
            },
          },
        ],
      });

      expect(page.hasPrevious, isTrue);
      expect(page.hasNext, isTrue);
      expect(page.items.single.canCorrect, isTrue);
      expect(page.items.single.issuedArtifact!.id, 90);
      expect(page.items.single.executedArtifact!.id, 91);
    },
  );

  test(
    'mobile addendum lifecycle uses only canonical routes and stable keys',
    () {
      final repository = File(
        'lib/features/leases/leases_repository.dart',
      ).readAsStringSync();
      final sheet = File(
        'lib/features/leases/addendum_action_sheets.dart',
      ).readAsStringSync();
      final detail = File(
        'lib/features/leases/lease_detail_screen.dart',
      ).readAsStringSync();

      expect(
        repository,
        contains("'/lease-managements/\$leaseManagementId/addenda'"),
      );
      expect(repository, contains("'eligible-base-agreements/page'"));
      expect(repository, contains("'\$sourceAddendumId/correct'"));
      expect(repository, contains("'\$leaseAddendumId/issuance-preparations'"));
      expect(repository, contains("'\$leaseAddendumId/issue'"));
      expect(sheet, contains('final String _operationKey'));
      expect(sheet, contains('final String _prepareKey'));
      expect(sheet, contains('final String _issueKey'));
      expect(sheet, contains('isRequired: true'));
      expect(sheet, isNot(contains("Text('Optional')")));
      expect(sheet, contains("label: 'Effects'"));
      expect(sheet, contains('Add financial effect'));
      expect(sheet, contains('_FinancialEffectSheet'));
      expect(
        sheet,
        contains('financialEffects: List<LeaseAddendumFinancialEffect>.from('),
      );
      expect(detail, contains("label: const Text('Issued PDF')"));
      expect(detail, contains("label: const Text('Executed PDF')"));
      expect(detail, contains('leaseAddendumHistoryProvider(_query)'));
    },
  );

  test('lease lifecycle date defaults never derive from the device clock', () {
    final successor = File(
      'lib/features/leases/successor_agreement_sheet.dart',
    ).readAsStringSync();
    final addendum = File(
      'lib/features/leases/addendum_action_sheets.dart',
    ).readAsStringSync();
    final moveIn = File(
      'lib/features/leases/prepare_move_in_sheet.dart',
    ).readAsStringSync();
    final repository = File(
      'lib/features/leases/leases_repository.dart',
    ).readAsStringSync();

    expect(successor, isNot(contains('DateTime.now()')));
    expect(addendum, isNot(contains('DateTime.now()')));
    expect(moveIn, isNot(contains('DateTime.now()')));
    expect(successor, contains('required DateTime businessDate'));
    expect(moveIn, contains('required this.businessDate'));
    expect(
      repository,
      contains("'/lease-managements/prepare-move-in-context'"),
    );
    expect(repository, contains("'isRequiredSigner': isAgreementSigner"));
  });
}
