import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

void main() {
  test('property managers load only scoped reconciliation banking data', () {
    final source = File(
      'lib/features/banking/banking_screen.dart',
    ).readAsStringSync();

    expect(source, contains("'money.reconciliation.operate'"));
    expect(source, contains("'bank-connections.manage'"));
    expect(source, contains("'money.reconciliation.destructive'"));
    expect(
      source,
      contains('canManageConnections\n        ? ref.watch(bankingSummaryProvider)'),
    );
    expect(
      source,
      contains('canOperate\n        ? ref.watch(bankingReviewQueueProvider)'),
    );
    expect(source, contains('if (canDismiss) ...['));
  });

  test('review models contain no bank administration or match target ids', () {
    final source = File(
      'lib/features/banking/banking_models.dart',
    ).readAsStringSync();
    final reviewSuggestion = source.substring(
      source.indexOf('class BankReviewSuggestion'),
      source.indexOf('/// Lightweight bank line'),
    );
    final reviewTransaction = source.substring(
      source.indexOf('class BankReviewTransaction'),
      source.indexOf('/// The full duplicate-review queue'),
    );

    expect(reviewSuggestion, isNot(contains('entityId')));
    expect(reviewSuggestion, isNot(contains('tenantAccountId')));
    expect(reviewTransaction, isNot(contains('institutionName')));
    expect(reviewTransaction, isNot(contains('accountName')));
    expect(reviewTransaction, isNot(contains('providerTransactionId')));
  });
}
