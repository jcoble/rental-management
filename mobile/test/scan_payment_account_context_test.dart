import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/scan/scan_models.dart';

void main() {
  test(
    'payment scan preserves tenant-account launch context for preselection',
    () {
      final draft = ScanDraft.fromJson({
        'id': 8,
        'portfolioId': 1,
        'targetEntityType': 'Payment',
        'status': 'Reviewing',
        'fileUrl': '/api/v1/scans/8/file',
        'fields': <Map<String, dynamic>>[],
        'createdAt': '2026-07-13T12:00:00Z',
        'captureContext': {'tenantAccountId': 42},
      });

      expect(draft.captureContext?.tenantAccountId, 42);
    },
  );

  test('contextual account lookup uses one exact canonical read', () {
    final repository = File(
      'lib/features/scan/scan_repository.dart',
    ).readAsStringSync();
    final review = File(
      'lib/features/scan/scan_review_screen.dart',
    ).readAsStringSync();

    expect(repository, contains("'/tenant-accounts/\$tenantAccountId'"));
    expect(review, contains('_tenantAccountOptionProvider(contextualId)'));
    expect(review, contains('accounts.insert(0, contextualAccount)'));
    expect(review, contains('if (error.statusCode == 404) return null'));
  });
}
