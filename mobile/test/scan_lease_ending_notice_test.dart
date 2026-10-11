import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/scan/scan_models.dart';
import 'package:rental_command/features/scan/scan_review_screen.dart';

void main() {
  test(
    'lease-ending notice overrides use relationship fields without expense fallback',
    () {
      final overrides = buildOverridesMap(
        editedFields: const {
          'lease_management_id': '56',
          'unit_id': '34',
          'notice_given_date': '2027-01-14',
          'planned_move_out_date': '2027-02-28',
          'notice_type': 'tenant non-renewal notice',
          'reason': 'Tenant will not renew.',
        },
        isPayment: false,
        isWorkOrder: false,
        isLease: false,
        isApplication: false,
        isLoan: false,
        isLeaseEndingNotice: true,
        isPaid: true,
        selectedTenantAccountId: null,
        applicationPropertyId: null,
        applicationUnitId: null,
        createNewProperty: false,
        selectedPropertyId: null,
        selectedUnitId: null,
        selectedTenantId: null,
        loanPropertyId: null,
      );

      expect(overrides['leaseManagementId'], '56');
      expect(overrides['unitId'], '34');
      expect(overrides['noticeGivenDate'], '2027-01-14');
      expect(overrides['plannedMoveOutDate'], '2027-02-28');
      expect(overrides['notice_type'], 'tenant non-renewal notice');
      expect(overrides['reason'], 'Tenant will not renew.');
      expect(overrides.containsKey('is_paid'), isFalse);
    },
  );

  test('confirmed and rejected scan reviews have no editable submit surface', () {
    final source = File(
      'lib/features/scan/scan_review_screen.dart',
    ).readAsStringSync();

    expect(source, contains('bottomSheet: isTerminal'));
    expect(source, contains('? null'));
    expect(source, contains('if (isTerminal) ...['));
    expect(source, contains('_ReadOnlyFieldsSection(draft: draft)'));
    expect(
      source.indexOf('if (isTerminal) ...['),
      lessThan(source.indexOf('] else if (draft.isLoan) ...[')),
    );
    expect(
      source,
      contains(
        "draft.isLeaseEndingNotice\n                                    ? 'Record Move-out Notice'",
      ),
    );
  });

  test(
    'failed scans remain recoverable while confirmed scans are immutable',
    () {
      final failed = ScanDraft(
        id: 95,
        portfolioId: 1,
        targetEntityType: 'LeaseEndingNotice',
        status: 'Failed',
        fileUrl: '/api/v1/scans/95/file',
        fields: const [],
        createdAt: DateTime(2027, 1, 14),
      );
      final confirmed = ScanDraft(
        id: 95,
        portfolioId: 1,
        targetEntityType: 'LeaseEndingNotice',
        status: 'Confirmed',
        fileUrl: '/api/v1/scans/95/file',
        fields: const [],
        createdAt: DateTime(2027, 1, 14),
      );

      expect(failed.isTerminal, isFalse);
      expect(confirmed.isTerminal, isTrue);
      expect(confirmed.isLeaseEndingNotice, isTrue);
    },
  );
}
