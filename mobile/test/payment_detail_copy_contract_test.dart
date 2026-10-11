import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

void main() {
  test('payment detail and correction sheet use plain-English copy', () {
    final source = File(
      'lib/features/payments/payment_detail_screen.dart',
    ).readAsStringSync();

    for (final expected in const [
      "label: 'Account number'",
      "label: 'Lease number'",
      'This receipt is a permanent account record.',
      'correction, Rental Command creates a linked refund and keeps both ',
      'records in the account history.',
      'The original receipt stays in the account history.',
      "labelText: 'Payment reference'",
      'Nothing was changed.',
      'Refund recorded.',
      'related balance was updated.',
      'related balances were updated.',
    ]) {
      expect(source, contains(expected), reason: 'Missing copy: $expected');
    }

    for (final forbidden in const [
      'immutable posted record',
      'compensating allocation',
      'Payout provenance',
      'entry #',
      'refund entry #',
      "label: 'Relationship'",
    ]) {
      expect(
        source,
        isNot(contains(forbidden)),
        reason: 'User-facing internal language remains: $forbidden',
      );
    }
  });
}
