import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

void main() {
  final help = File(
    'lib/features/settings/notification_help_action.dart',
  ).readAsStringSync();
  final screen = File(
    'lib/features/settings/tenant_notices_screen.dart',
  ).readAsStringSync();
  final repository = File(
    'lib/features/settings/notification_foundation_repository.dart',
  ).readAsStringSync();

  group('notification settings contract', () {
    test('opens accessible help', () {
      expect(help, contains('NotificationHelpAction'));
      expect(help, contains('tooltip:'));
      expect(help, contains('settings-and-notifications'));
    });

    test('renders realistic preview', () {
      expect(screen, contains('Preview unsaved message'));
      expect(screen, contains('Render current edits'));
      expect(repository, contains('previewTenantNotice'));
      expect(repository, contains('/preview'));
    });

    test('gates test send', () {
      expect(screen, contains('Controlled non-tenant test email'));
      expect(repository, contains('sendTenantNoticeTest'));
      expect(repository, contains('Accepted'));
      expect(repository, contains('Suppressed'));
      expect(repository, contains('ProviderError'));
      expect(repository, contains('Idempotency-Key'));
    });
  });
}
