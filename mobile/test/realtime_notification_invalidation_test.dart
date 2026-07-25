import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

void main() {
  test('notification events invalidate the tenant dashboard snapshot', () {
    final source = File(
      'lib/core/realtime/realtime_providers.dart',
    ).readAsStringSync();
    final notificationCase = RegExp(
      r"case 'Notification':(?<body>[\s\S]*?)\n\s*default:",
    ).firstMatch(source);

    expect(notificationCase, isNotNull);
    expect(
      notificationCase!.namedGroup('body'),
      contains('ref.invalidate(tenantPortalSnapshotProvider);'),
      reason:
          'The tenant dashboard notification card comes from the portal snapshot '
          'and must refetch when SignalR announces a Notification.',
    );
  });
}
