import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/leases/lease_form_payload.dart';

void main() {
  test('create lease payload includes fields required by the API', () {
    final payload = buildLeaseSubmitPayload(
      isEdit: false,
      propertyId: 7,
      unitId: 42,
      tenantIds: const [3, 9],
      startDate: DateTime(2026, 7, 1),
      endDate: DateTime(2027, 6, 30),
      monthlyRent: '1400',
      securityDeposit: '1400',
      lateFeeAmount: '75',
      rentDueDay: '1',
      status: 'Draft',
      leaseNumber: 'Maple Ridge - Unit 4B - 2026-07-01',
    );

    expect(payload['propertyId'], 7);
    expect(payload['unitId'], 42);
    expect(payload['tenantId'], 3);
    expect(payload['tenantIds'], [3, 9]);
    expect(payload['leaseNumber'], 'Maple Ridge - Unit 4B - 2026-07-01');
    expect(payload['rentTrackingStartMode'], 'ForwardOnly');
    expect(payload['startDate'], '2026-07-01');
    expect(payload['endDate'], '2027-06-30');
  });

  test('edit lease payload keeps the partial update shape', () {
    final payload = buildLeaseSubmitPayload(
      isEdit: true,
      propertyId: 7,
      unitId: 42,
      tenantIds: const [3],
      startDate: DateTime(2026, 7, 1),
      endDate: DateTime(2027, 6, 30),
      monthlyRent: '1400',
      securityDeposit: '1400',
      lateFeeAmount: '75',
      rentDueDay: '1',
      status: 'Active',
      leaseNumber: 'Ignored on edit',
    );

    expect(payload.containsKey('propertyId'), isFalse);
    expect(payload.containsKey('leaseNumber'), isFalse);
    expect(payload.containsKey('rentTrackingStartMode'), isFalse);
    expect(payload['tenantId'], 3);
  });
}
