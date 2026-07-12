import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/models/models.dart';
import 'package:rental_command/features/payments/payment_lease_labels.dart';

void main() {
  test('receipt detail prefers property and unit over relationship number', () {
    final receipt = PaymentReceipt.fromJson({
      'id': 8,
      'publicId': '7b5b31ec-a3ea-4d87-b515-01b76f42a34c',
      'portfolioId': 1,
      'tenantAccountId': 42,
      'leaseManagementId': 52,
      'propertyId': 7,
      'unitId': 9,
      'accountNumber': 'TA-42',
      'relationshipNumber': 'LM-52',
      'amount': 1200,
      'currency': 'USD',
      'receivedOn': '2026-07-01',
      'postedAtUtc': '2026-07-01T12:00:00Z',
      'description': 'Rent received',
      'tenantName': 'Jesse Coble',
      'propertyName': 'Maple Ridge',
      'unitNumber': '2B',
    });

    expect(formatPaymentReceiptRentalDisplay(receipt), 'Maple Ridge · Unit 2B');
  });
}
