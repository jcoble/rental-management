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
      status: 'Active',
      leaseNumber: 'Maple Ridge - Unit 4B - 2026-07-01',
      notes: '  Annual renewal  ',
    );

    expect(payload['propertyId'], 7);
    expect(payload['unitId'], 42);
    expect(payload['tenantId'], 3);
    expect(payload['tenantIds'], [3, 9]);
    expect(payload['leaseNumber'], 'Maple Ridge - Unit 4B - 2026-07-01');
    expect(payload['rentTrackingStartMode'], 'ForwardOnly');
    expect(payload['notes'], 'Annual renewal');
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
      rentTrackingStartMode: null,
      notes: '',
    );

    expect(payload.containsKey('propertyId'), isFalse);
    expect(payload['leaseNumber'], 'Ignored on edit');
    expect(payload.containsKey('rentTrackingStartMode'), isFalse);
    expect(payload['tenantId'], 3);
    expect(payload['notes'], '');
  });

  test('create lease payload can create one tenant in the same request', () {
    final payload = buildLeaseSubmitPayload(
      isEdit: false,
      propertyId: 7,
      unitId: 42,
      tenantIds: const [],
      newTenant: buildInlineTenantPayload(
        firstName: '  Avery ',
        lastName: ' Stone  ',
        email: ' avery.stone@example.test ',
        phone: ' 614-555-0184 ',
      ),
      startDate: DateTime(2026, 7, 15),
      endDate: DateTime(2027, 7, 15),
      monthlyRent: '1200',
      securityDeposit: '1200',
      lateFeeAmount: '75',
      rentDueDay: '31',
      status: 'Active',
      leaseNumber: 'L-2026-014',
      rentTrackingStartMode: 'BackfillFromLeaseStart',
    );

    expect(payload.containsKey('tenantId'), isFalse);
    expect(payload.containsKey('tenantIds'), isFalse);
    expect(payload['newTenant'], {
      'firstName': 'Avery',
      'lastName': 'Stone',
      'email': 'avery.stone@example.test',
      'phone': '614-555-0184',
    });
    expect(payload['rentDueDay'], 31);
    expect(payload['rentTrackingStartMode'], 'BackfillFromLeaseStart');
  });

  test('opening balance fields are sent only for opening balance mode', () {
    final payload = buildLeaseSubmitPayload(
      isEdit: false,
      propertyId: 7,
      unitId: 42,
      tenantIds: const [3],
      startDate: DateTime(2026, 1, 1),
      endDate: DateTime(2027, 1, 1),
      monthlyRent: '1200',
      securityDeposit: '0',
      lateFeeAmount: '75',
      rentDueDay: '1',
      status: 'Active',
      leaseNumber: 'L-2026-015',
      rentTrackingStartMode: 'OpeningBalanceOnly',
      openingBalanceAmount: '450.25',
      openingBalanceAsOfDate: DateTime(2026, 6, 30),
      openingBalanceNote: '  Imported balance  ',
    );

    expect(payload['openingBalanceAmount'], 450.25);
    expect(payload['openingBalanceAsOfDate'], '2026-06-30');
    expect(payload['openingBalanceNote'], 'Imported balance');
  });

  test('existing and new tenant inputs cannot be mixed', () {
    expect(
      () => buildLeaseSubmitPayload(
        isEdit: false,
        propertyId: 7,
        unitId: 42,
        tenantIds: const [3],
        newTenant: const {'firstName': 'Avery', 'lastName': 'Stone'},
        startDate: DateTime(2026, 7, 15),
        endDate: DateTime(2027, 7, 15),
        monthlyRent: '1200',
        securityDeposit: '1200',
        lateFeeAmount: '75',
        rentDueDay: '1',
        status: 'Active',
        leaseNumber: 'L-2026-016',
      ),
      throwsArgumentError,
    );
  });
}
