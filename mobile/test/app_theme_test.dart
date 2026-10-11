import 'dart:io';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:google_fonts/src/google_fonts_base.dart' as google_fonts_base;
import 'package:rental_command/core/theme/app_theme.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  GoogleFonts.config.allowRuntimeFetching = false;

  setUpAll(() async {
    google_fonts_base.assetManifest = _TestFontAssetManifest();
    final fontBytes = await File(_flutterTestFontPath()).readAsBytes();
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMessageHandler('flutter/assets', (_) async {
          return ByteData.sublistView(fontBytes);
        });
  });

  tearDownAll(() {
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMessageHandler('flutter/assets', null);
    google_fonts_base.assetManifest = null;
    google_fonts_base.clearCache();
    GoogleFonts.config.allowRuntimeFetching = true;
  });

  for (final brightness in Brightness.values) {
    final label = brightness.name;

    test('$label button themes use a finite 64x48 minimum', () async {
      final appTheme = brightness == Brightness.light
          ? AppTheme.light
          : AppTheme.dark;
      await GoogleFonts.pendingFonts();
      final styles = [
        appTheme.filledButtonTheme.style,
        appTheme.elevatedButtonTheme.style,
        appTheme.outlinedButtonTheme.style,
      ];

      for (final style in styles) {
        final minimumSize = style?.minimumSize?.resolve(<WidgetState>{});
        expect(minimumSize, const Size(64, 48));
        expect(minimumSize!.width.isFinite, isTrue);
        expect(minimumSize.height.isFinite, isTrue);
      }
    });

    testWidgets('$label themed buttons lay out directly inside Rows', (
      tester,
    ) async {
      final appTheme = brightness == Brightness.light
          ? AppTheme.light
          : AppTheme.dark;
      await GoogleFonts.pendingFonts();
      await tester.pumpWidget(
        MaterialApp(
          theme: appTheme,
          home: Scaffold(
            body: Column(
              children: [
                Row(
                  children: [
                    FilledButton.tonalIcon(
                      onPressed: () {},
                      icon: const Icon(Icons.add),
                      label: const Text('Add'),
                    ),
                  ],
                ),
                Row(
                  children: [
                    ElevatedButton(
                      onPressed: () {},
                      child: const Text('Elevated'),
                    ),
                  ],
                ),
                Row(
                  children: [
                    OutlinedButton(
                      onPressed: () {},
                      child: const Text('Outlined'),
                    ),
                  ],
                ),
              ],
            ),
          ),
        ),
      );

      expect(tester.takeException(), isNull);
      expect(find.byType(FilledButton), findsOneWidget);
      expect(find.byType(ElevatedButton), findsOneWidget);
      expect(find.byType(OutlinedButton), findsOneWidget);
    });
  }
}

class _TestFontAssetManifest implements AssetManifest {
  @override
  List<String> listAssets() => const [
    'test_fonts/FunnelDisplay-Regular.ttf',
    'test_fonts/FunnelDisplay-SemiBold.ttf',
    'test_fonts/FunnelSans-Regular.ttf',
    'test_fonts/FunnelSans-Medium.ttf',
    'test_fonts/FunnelSans-SemiBold.ttf',
  ];

  @override
  List<AssetMetadata>? getAssetVariants(String key) => null;
}

String _flutterTestFontPath() {
  var flutterRoot = File(Platform.resolvedExecutable).parent;
  for (var index = 0; index < 5; index += 1) {
    flutterRoot = flutterRoot.parent;
  }
  return '${flutterRoot.path}/bin/cache/artifacts/material_fonts/Roboto-Regular.ttf';
}
