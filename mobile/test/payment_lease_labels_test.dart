import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/models/models.dart';
import 'package:rental_command/features/payments/payment_lease_labels.dart';

void main() {
  test(
    'payment detail prefers property and unit over internal lease number',
    () {
      final payment = Payment.fromJson({
        'id': 8,
        'portfolioId': 1,
        'leaseId': 42,
        'paymentType': 'Rent',
        'status': 'Scheduled',
        'amount': 1200,
        'dueDate': '2026-07-01',
        'tenantName': 'Jesse Coble',
        'leaseNumber': 'L-2026-0004',
        'propertyName': 'Maple Ridge',
        'unitNumber': '2B',
        'createdAt': '2026-06-01T00:00:00Z',
        'updatedAt': '2026-06-01T00:00:00Z',
      });

      expect(payment.propertyName, 'Maple Ridge');
      expect(payment.unitNumber, '2B');
      expect(formatPaymentLeaseDisplay(payment), 'Maple Ridge · Unit 2B');
    },
  );

  test('lease picker label speaks in property and unit terms', () {
    final lease = Lease(
      id: 42,
      portfolioId: 1,
      propertyId: 7,
      unitId: 9,
      tenantId: 3,
      leaseNumber: 'L-2026-0004',
      status: 'Active',
      startDate: DateTime(2026),
      endDate: DateTime(2027),
      monthlyRent: 1200,
      securityDeposit: 1200,
      lateFeeAmount: 50,
      rentDueDay: 1,
      tenantName: 'Jesse Coble',
      propertyName: 'Maple Ridge',
      unitNumber: '2B',
      createdAt: DateTime(2026),
      updatedAt: DateTime(2026),
    );

    expect(
      formatLeasePickerLabel(lease),
      'Maple Ridge · Unit 2B — Jesse Coble',
    );
  });

  test('lease picker does not append internal number when home is known', () {
    final lease = Lease(
      id: 42,
      portfolioId: 1,
      propertyId: 7,
      unitId: 9,
      tenantId: 3,
      leaseNumber: 'L-2026-0004',
      status: 'Active',
      startDate: DateTime(2026),
      endDate: DateTime(2027),
      monthlyRent: 1200,
      securityDeposit: 1200,
      lateFeeAmount: 50,
      rentDueDay: 1,
      propertyName: 'Maple Ridge',
      unitNumber: '2B',
      createdAt: DateTime(2026),
      updatedAt: DateTime(2026),
    );

    expect(formatLeasePickerLabel(lease), 'Maple Ridge · Unit 2B');
  });
}
