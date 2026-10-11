import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/auth/auth_controller.dart';
import 'package:rental_command/core/auth/biometric_auth_service.dart';
import 'package:rental_command/features/auth/login_screen.dart';

void main() {
  testWidgets(
    'locked session prompts once per mount and offers a compact fingerprint action',
    (tester) async {
      final controller = _FakeLockedAuthController([
        BiometricAuthenticationResult.canceled,
        BiometricAuthenticationResult.canceled,
      ]);

      await tester.pumpWidget(_app(controller));
      await tester.pump();

      expect(controller.unlockCalls, 1);
      expect(find.text('Fingerprint'), findsOneWidget);
      expect(find.byIcon(Icons.fingerprint), findsOneWidget);
      expect(find.text('Unlock with biometrics'), findsNothing);
      expect(find.text('Try biometric sign-in again'), findsNothing);
      expect(find.text('Sign In'), findsOneWidget);
      expect(find.text('Sign in with Google'), findsOneWidget);

      await tester.pump();
      expect(controller.unlockCalls, 1);

      await tester.tap(find.text('Fingerprint'));
      await tester.pump();
      expect(controller.unlockCalls, 2);
    },
  );

  testWidgets('ordinary signed-out login hides the fingerprint action', (
    tester,
  ) async {
    final controller = _FakeLockedAuthController(
      const [],
      initialState: const AuthStateUnauthenticated(),
    );

    await tester.pumpWidget(_app(controller));
    await tester.pump();

    expect(controller.unlockCalls, 0);
    expect(find.text('Fingerprint'), findsNothing);
    expect(find.byIcon(Icons.fingerprint), findsNothing);
  });

  for (final testCase in <(BiometricAuthenticationResult, String)>[
    (
      BiometricAuthenticationResult.canceled,
      'Biometric sign-in was canceled. Use your password or Google, or try again.',
    ),
    (
      BiometricAuthenticationResult.lockedOut,
      'Biometric sign-in is temporarily locked. Unlock your phone or use your password or Google.',
    ),
    (
      BiometricAuthenticationResult.notEnrolled,
      'No fingerprint or other biometric is set up on this device. Use your password or Google.',
    ),
    (
      BiometricAuthenticationResult.unavailable,
      'Biometric sign-in isn’t available on this device. Use your password or Google.',
    ),
  ]) {
    testWidgets('${testCase.$1} shows non-technical fallback copy', (
      tester,
    ) async {
      final controller = _FakeLockedAuthController([testCase.$1]);

      await tester.pumpWidget(_app(controller));
      await tester.pump();

      expect(find.text(testCase.$2), findsOneWidget);
      expect(find.text('Sign In'), findsOneWidget);
      expect(find.text('Sign in with Google'), findsOneWidget);
    });
  }
}

Widget _app(_FakeLockedAuthController controller) {
  return ProviderScope(
    overrides: [authControllerProvider.overrideWith(() => controller)],
    child: const MaterialApp(home: LoginScreen()),
  );
}

class _FakeLockedAuthController extends AuthController {
  _FakeLockedAuthController(
    this.results, {
    this.initialState = const AuthStateBiometricLocked(),
  });

  final List<BiometricAuthenticationResult> results;
  final AuthState initialState;
  int unlockCalls = 0;

  @override
  AuthState build() => initialState;

  @override
  Future<void> unlockBiometricSession() async {
    final result = results[unlockCalls.clamp(0, results.length - 1)];
    unlockCalls++;
    state = AuthStateBiometricLocked(lastResult: result);
  }
}
