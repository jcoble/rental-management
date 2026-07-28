import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/auth/biometric_auth_service.dart';

void main() {
  group('BiometricAuthService availability', () {
    test('reports available when Android supports an enrolled biometric', () async {
      final platform = _FakeBiometricPlatform(
        isDeviceSupported: true,
        canCheckBiometrics: true,
        enrolledBiometrics: const [DeviceBiometric.fingerprint],
      );
      final service = BiometricAuthService(
        platform: platform,
        isAndroid: true,
      );

      expect(
        await service.checkAvailability(),
        BiometricAvailability.available,
      );
    });

    test('reports not enrolled when the device has no enrolled biometric', () async {
      final platform = _FakeBiometricPlatform(
        isDeviceSupported: true,
        canCheckBiometrics: true,
      );
      final service = BiometricAuthService(
        platform: platform,
        isAndroid: true,
      );

      expect(
        await service.checkAvailability(),
        BiometricAvailability.notEnrolled,
      );
    });

    test('reports unavailable when biometric hardware is unsupported', () async {
      final platform = _FakeBiometricPlatform();
      final service = BiometricAuthService(
        platform: platform,
        isAndroid: true,
      );

      expect(
        await service.checkAvailability(),
        BiometricAvailability.unavailable,
      );
    });
  });

  group('BiometricAuthService authentication', () {
    test('maps a successful system prompt to authenticated', () async {
      final service = BiometricAuthService(
        platform: _FakeBiometricPlatform(authenticated: true),
        isAndroid: true,
      );

      expect(
        await service.authenticate(),
        BiometricAuthenticationResult.authenticated,
      );
    });

    test('maps system-prompt cancellation to canceled', () async {
      final service = BiometricAuthService(
        platform: _FakeBiometricPlatform(
          authenticationError: const BiometricPlatformException(
            BiometricPlatformError.canceled,
          ),
        ),
        isAndroid: true,
      );

      expect(
        await service.authenticate(),
        BiometricAuthenticationResult.canceled,
      );
    });

    test('maps biometric lockout to lockedOut', () async {
      final service = BiometricAuthService(
        platform: _FakeBiometricPlatform(
          authenticationError: const BiometricPlatformException(
            BiometricPlatformError.lockedOut,
          ),
        ),
        isAndroid: true,
      );

      expect(
        await service.authenticate(),
        BiometricAuthenticationResult.lockedOut,
      );
    });
  });
}

class _FakeBiometricPlatform implements BiometricPlatform {
  _FakeBiometricPlatform({
    this.isDeviceSupported = false,
    this.canCheckBiometrics = false,
    this.enrolledBiometrics = const [],
    this.authenticated = false,
    this.authenticationError,
  });

  final bool isDeviceSupported;
  final bool canCheckBiometrics;
  final List<DeviceBiometric> enrolledBiometrics;
  final bool authenticated;
  final BiometricPlatformException? authenticationError;

  @override
  Future<bool> deviceSupportsBiometrics() async => isDeviceSupported;

  @override
  Future<bool> canCheckEnrolledBiometrics() async => canCheckBiometrics;

  @override
  Future<List<DeviceBiometric>> getEnrolledBiometrics() async =>
      enrolledBiometrics;

  @override
  Future<bool> showBiometricPrompt() async {
    final error = authenticationError;
    if (error != null) throw error;
    return authenticated;
  }
}
