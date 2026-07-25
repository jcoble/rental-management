import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/scan/scan_models.dart';

void main() {
  test('scan draft decodes every explicit canonical capture id', () {
    final context = ScanCaptureContext.fromJson({
      'experience': 'Operator',
      'accessContextId': 10,
      'accessRevision': 11,
      'propertyId': 1,
      'unitId': 2,
      'leaseManagementId': 3,
      'leaseAgreementId': 4,
      'tenantAccountId': 5,
      'tenantLedgerEntryId': 6,
      'workOrderId': 7,
      'applicationId': 8,
      'rentalListingId': 9,
      'sourceLabel': 'Unit ledger',
    });

    expect(context.propertyId, 1);
    expect(context.unitId, 2);
    expect(context.leaseManagementId, 3);
    expect(context.leaseAgreementId, 4);
    expect(context.tenantAccountId, 5);
    expect(context.tenantLedgerEntryId, 6);
    expect(context.workOrderId, 7);
    expect(context.applicationId, 8);
    expect(context.rentalListingId, 9);
    expect(context.hasBusinessContext, isTrue);
  });
}
