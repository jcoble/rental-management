import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/auth/biometric_auth_service.dart';
import 'package:rental_command/features/settings/settings_screen.dart';

void main() {
  testWidgets(
    'enable checks availability and requires successful biometric confirmation',
    (tester) async {
      final biometric = _FakeBiometricAuthService();

      await tester.pumpWidget(_app(biometric));
      await tester.pump();

      await tester.tap(find.byKey(const Key('biometric-sign-in-switch')));
      await tester.pump();

      expect(biometric.availabilityCalls, 1);
      expect(biometric.authenticateCalls, 1);
      expect(biometric.enabledWrites, [true]);
      expect(
        tester
            .widget<SwitchListTile>(
              find.byKey(const Key('biometric-sign-in-switch')),
            )
            .value,
        isTrue,
      );
    },
  );

  testWidgets('disable is explicit and immediate', (tester) async {
    final biometric = _FakeBiometricAuthService(initiallyEnabled: true);

    await tester.pumpWidget(_app(biometric));
    await tester.pump();

    await tester.tap(find.byKey(const Key('biometric-sign-in-switch')));
    await tester.pump();

    expect(biometric.availabilityCalls, 0);
    expect(biometric.authenticateCalls, 0);
    expect(biometric.enabledWrites, [false]);
    expect(
      tester
          .widget<SwitchListTile>(
            find.byKey(const Key('biometric-sign-in-switch')),
          )
          .value,
      isFalse,
    );
  });

  for (final testCase in <(BiometricAvailability, String)>[
    (
      BiometricAvailability.notEnrolled,
      'Set up a fingerprint or other biometric in Android settings first.',
    ),
    (
      BiometricAvailability.unavailable,
      'Biometric sign-in isn’t available on this device.',
    ),
  ]) {
    testWidgets('${testCase.$1} does not enable and explains the fallback', (
      tester,
    ) async {
      final biometric = _FakeBiometricAuthService(availability: testCase.$1);

      await tester.pumpWidget(_app(biometric));
      await tester.pump();
      await tester.tap(find.byKey(const Key('biometric-sign-in-switch')));
      await tester.pump();

      expect(biometric.authenticateCalls, 0);
      expect(biometric.enabledWrites, isEmpty);
      expect(find.text(testCase.$2), findsOneWidget);
      expect(
        tester
            .widget<SwitchListTile>(
              find.byKey(const Key('biometric-sign-in-switch')),
            )
            .value,
        isFalse,
      );
    });
  }

  testWidgets('canceled biometric confirmation does not enable', (
    tester,
  ) async {
    final biometric = _FakeBiometricAuthService(
      authenticationResult: BiometricAuthenticationResult.canceled,
    );

    await tester.pumpWidget(_app(biometric));
    await tester.pump();
    await tester.tap(find.byKey(const Key('biometric-sign-in-switch')));
    await tester.pump();

    expect(biometric.enabledWrites, isEmpty);
    expect(
      find.text(
        'Biometric confirmation was canceled. Biometric sign-in is still off.',
      ),
      findsOneWidget,
    );
  });

  testWidgets('non-Android settings hide biometric sign-in', (tester) async {
    final biometric = _FakeBiometricAuthService(isAndroid: false);

    await tester.pumpWidget(_app(biometric));
    await tester.pump();

    expect(find.byKey(const Key('biometric-sign-in-switch')), findsNothing);
    expect(find.text('Biometric sign-in'), findsNothing);
  });
}

Widget _app(_FakeBiometricAuthService biometric) {
  return ProviderScope(
    overrides: [biometricAuthServiceProvider.overrideWithValue(biometric)],
    child: const MaterialApp(home: SettingsScreen()),
  );
}

class _FakeBiometricAuthService extends BiometricAuthService {
  _FakeBiometricAuthService({
    this.initiallyEnabled = false,
    this.availability = BiometricAvailability.available,
    this.authenticationResult = BiometricAuthenticationResult.authenticated,
    this.isAndroid = true,
  }) : super(isAndroid: isAndroid);

  final bool initiallyEnabled;
  final BiometricAvailability availability;
  final BiometricAuthenticationResult authenticationResult;
  final bool isAndroid;
  int availabilityCalls = 0;
  int authenticateCalls = 0;
  final List<bool> enabledWrites = [];

  @override
  Future<bool> isEnabled() async => initiallyEnabled;

  @override
  Future<BiometricAvailability> checkAvailability() async {
    availabilityCalls++;
    return availability;
  }

  @override
  Future<BiometricAuthenticationResult> authenticate() async {
    authenticateCalls++;
    return authenticationResult;
  }

  @override
  Future<void> setEnabled(bool enabled) async {
    enabledWrites.add(enabled);
  }
}
