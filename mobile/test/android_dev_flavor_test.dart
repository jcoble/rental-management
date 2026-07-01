import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

void main() {
  test('Android has side-by-side prod and dev application flavors', () {
    final gradle = File('android/app/build.gradle.kts').readAsStringSync();

    expect(gradle, contains('flavorDimensions += "environment"'));
    expect(gradle, contains('create("prod")'));
    expect(gradle, contains('create("dev")'));
    expect(gradle, contains('applicationIdSuffix = ".dev"'));
    expect(
      gradle,
      contains('manifestPlaceholders["appLabel"] = "Rental Command"'),
    );
    expect(
      gradle,
      contains('manifestPlaceholders["appLabel"] = "Rental Command Dev"'),
    );
  });

  test('Android manifest reads the launch label from flavor placeholders', () {
    final manifest = File(
      'android/app/src/main/AndroidManifest.xml',
    ).readAsStringSync();

    expect(manifest, contains('android:label="\${appLabel}"'));
    expect(manifest, isNot(contains('android:label="rental_command"')));
  });

  test('Android dev flavor does not require prod Firebase config', () {
    final gradle = File('android/app/build.gradle.kts').readAsStringSync();

    expect(gradle, contains('processDev'));
    expect(gradle, contains('GoogleServices'));
    expect(gradle, contains('enabled = false'));
  });

  test('voice deep-link helper can target the dev package', () {
    final script = File('scripts/voice-test.sh').readAsStringSync();

    expect(
      script,
      contains(r'PKG="${ANDROID_PACKAGE:-com.rentalcommand.rental_command}"'),
    );
    expect(
      script,
      contains('ANDROID_PACKAGE=com.rentalcommand.rental_command.dev'),
    );
  });

  test('Play Store release checklist uses the prod Android flavor bundle', () {
    final checklist = File(
      '../Docs/legal/play-store-internal-testing-steps.md',
    ).readAsStringSync();

    expect(
      checklist,
      contains(
        'flutter build appbundle --release --flavor prod '
        '--dart-define=FLAVOR=prod',
      ),
    );
    expect(
      checklist,
      contains('build/app/outputs/bundle/prodRelease/app-prod-release.aab'),
    );
    expect(
      checklist,
      isNot(contains('build/app/outputs/bundle/release/app-release.aab')),
    );
  });
}
