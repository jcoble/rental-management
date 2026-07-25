import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

void main() {
  test('owner relationship lists use the bounded page API', () {
    final repository = File(
      'lib/features/owner_portal/owner_portal_repository.dart',
    ).readAsStringSync();

    expect(repository, contains('statementsPage({'));
    expect(repository, isNot(contains('Future<List<OwnerSummary>> statements(')));
    for (final path in [
      "'/owner/properties/page'",
      "'/owner/statements'",
      "'/owner/distributions/page'",
      "'/owner/approvals/page'",
      "'/owner/messages/page'",
    ]) {
      expect(repository, contains(path));
    }
    expect(repository, contains("'skip': skip"));
    expect(repository, contains("'take': take"));
  });

  test('owner shell exposes paging controls for every list family', () {
    final screen = File(
      'lib/features/home/owner_landing_screen.dart',
    ).readAsStringSync();

    expect(RegExp(r'_OwnerPager\(').allMatches(screen).length, greaterThanOrEqualTo(5));
    expect(screen, contains('_pageStatements('));
    expect(screen, contains('_pageDistributions('));
    expect(screen, contains('repository.approvalsPage(skip: _skip'));
    expect(screen, contains('repository.messagesPage(skip: _skip'));
  });
}
