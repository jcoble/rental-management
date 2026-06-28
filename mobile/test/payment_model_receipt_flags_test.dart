import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/models/payment.dart';

void main() {
  test('Payment parses receipt scan availability flags', () {
    final payment = Payment.fromJson({
      'id': 11,
      'portfolioId': 7,
      'leaseId': 99,
      'paymentType': 'Rent',
      'status': 'Paid',
      'amount': 1200,
      'dueDate': '2026-06-01T00:00:00.000Z',
      'createdAt': '2026-06-01T00:00:00.000Z',
      'updatedAt': '2026-06-01T00:00:00.000Z',
      'hasScan': true,
      'scanIsImage': true,
    });

    expect(payment.hasScan, isTrue);
    expect(payment.scanIsImage, isTrue);
  });
}
