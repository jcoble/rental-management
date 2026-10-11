import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

void main() {
  final repository = File(
    'lib/features/settings/ai_provider_repository.dart',
  ).readAsStringSync();
  final screen = File(
    'lib/features/settings/ai_provider_settings_screen.dart',
  ).readAsStringSync();
  final settings = File(
    'lib/features/settings/settings_screen.dart',
  ).readAsStringSync();

  group('AI provider settings', () {
    test('is write-only and test-before-activate', () {
      expect(repository, contains('/integrations/ai/test'));
      expect(repository, contains('/integrations/ai/rotate'));
      expect(repository, contains("'/integrations/ai'"));
      expect(screen, contains('Test credential'));
      expect(screen, contains('Activate provider'));
      expect(screen, contains('Rotate credential'));
      expect(screen, contains('Remove provider'));
      expect(screen, contains('write-only after save'));
      expect(screen, contains('_testedSignature != _signature'));
    });

    test('states the explicit missing-key policy', () {
      expect(screen, contains('clear missing-key message'));
      expect(screen, contains('never uses a shared Rental Command key'));
    });

    test('is discoverable only to integration managers', () {
      expect(
        settings,
        contains("capabilities.contains('integrations.manage')"),
      );
      expect(settings, contains('if (canManageAiProvider)'));
      expect(settings, contains("import 'ai_provider_settings_screen.dart';"));
      expect(settings, contains("title: 'AI provider'"));
      expect(settings, contains('onTap: () =>'));
      expect(
        settings,
        contains('_open(context, const AiProviderSettingsScreen())'),
      );
    });
  });
}
