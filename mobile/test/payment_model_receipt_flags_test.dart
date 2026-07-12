import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/models/payment.dart';

void main() {
  test('PaymentReceipt parses the canonical tenant-account projection', () {
    final receipt = PaymentReceipt.fromJson({
      'id': 11,
      'publicId': '7b5b31ec-a3ea-4d87-b515-01b76f42a34c',
      'portfolioId': 7,
      'tenantAccountId': 99,
      'leaseManagementId': 55,
      'propertyId': 3,
      'unitId': 4,
      'accountNumber': 'TA-000099',
      'relationshipNumber': 'LM-000055',
      'amount': 1200,
      'currency': 'USD',
      'receivedOn': '2026-06-01',
      'postedAtUtc': '2026-06-01T12:00:00Z',
      'description': 'June rent receipt',
      'paymentMethodSummary': 'Check',
      'sourceStoredFileId': 81,
    });

    expect(receipt.tenantAccountId, 99);
    expect(receipt.leaseManagementId, 55);
    expect(receipt.receivedOn, DateTime(2026, 6, 1));
    expect(receipt.sourceStoredFileId, 81);
  });
}
