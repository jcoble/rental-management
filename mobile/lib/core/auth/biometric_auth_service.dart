import 'dart:io';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:local_auth/local_auth.dart';

const _biometricEnabledKey = 'rc_biometric_sign_in_enabled';

enum BiometricAvailability { available, notEnrolled, unavailable }

enum BiometricAuthenticationResult {
  authenticated,
  canceled,
  lockedOut,
  notEnrolled,
  unavailable,
}

enum DeviceBiometric { face, fingerprint, iris, strong, weak }

enum BiometricPlatformError { canceled, lockedOut, notEnrolled, unavailable }

class BiometricPlatformException implements Exception {
  const BiometricPlatformException(this.error);

  final BiometricPlatformError error;
}

abstract interface class BiometricPlatform {
  Future<bool> deviceSupportsBiometrics();

  Future<bool> canCheckEnrolledBiometrics();

  Future<List<DeviceBiometric>> getEnrolledBiometrics();

  Future<bool> showBiometricPrompt();
}

class BiometricAuthService {
  BiometricAuthService({
    BiometricPlatform? platform,
    FlutterSecureStorage? storage,
    bool? isAndroid,
  }) : _platform = platform ?? LocalAuthBiometricPlatform(),
       _storage = storage ?? const FlutterSecureStorage(),
       _isAndroid = isAndroid ?? Platform.isAndroid;

  final BiometricPlatform _platform;
  final FlutterSecureStorage _storage;
  final bool _isAndroid;

  bool get isSupportedPlatform => _isAndroid;

  Future<BiometricAvailability> checkAvailability() async {
    if (!_isAndroid ||
        !await _platform.deviceSupportsBiometrics() ||
        !await _platform.canCheckEnrolledBiometrics()) {
      return BiometricAvailability.unavailable;
    }

    final enrolled = await _platform.getEnrolledBiometrics();
    return enrolled.isEmpty
        ? BiometricAvailability.notEnrolled
        : BiometricAvailability.available;
  }

  Future<BiometricAuthenticationResult> authenticate() async {
    if (!_isAndroid) return BiometricAuthenticationResult.unavailable;

    try {
      final authenticated = await _platform.showBiometricPrompt();
      return authenticated
          ? BiometricAuthenticationResult.authenticated
          : BiometricAuthenticationResult.canceled;
    } on BiometricPlatformException catch (error) {
      return switch (error.error) {
        BiometricPlatformError.canceled =>
          BiometricAuthenticationResult.canceled,
        BiometricPlatformError.lockedOut =>
          BiometricAuthenticationResult.lockedOut,
        BiometricPlatformError.notEnrolled =>
          BiometricAuthenticationResult.notEnrolled,
        BiometricPlatformError.unavailable =>
          BiometricAuthenticationResult.unavailable,
      };
    }
  }

  Future<bool> isEnabled() async =>
      await _storage.read(key: _biometricEnabledKey) == 'true';

  Future<void> setEnabled(bool enabled) => enabled
      ? _storage.write(key: _biometricEnabledKey, value: 'true')
      : _storage.delete(key: _biometricEnabledKey);

  Future<void> clearEnabled() => _storage.delete(key: _biometricEnabledKey);
}

class LocalAuthBiometricPlatform implements BiometricPlatform {
  LocalAuthBiometricPlatform({LocalAuthentication? localAuthentication})
    : _localAuthentication = localAuthentication ?? LocalAuthentication();

  final LocalAuthentication _localAuthentication;

  @override
  Future<bool> deviceSupportsBiometrics() =>
      _localAuthentication.isDeviceSupported();

  @override
  Future<bool> canCheckEnrolledBiometrics() =>
      _localAuthentication.canCheckBiometrics;

  @override
  Future<List<DeviceBiometric>> getEnrolledBiometrics() async {
    final biometrics = await _localAuthentication.getAvailableBiometrics();
    return biometrics.map(_mapBiometricType).toList(growable: false);
  }

  @override
  Future<bool> showBiometricPrompt() async {
    try {
      return await _localAuthentication.authenticate(
        localizedReason: 'Unlock Rental Command',
        biometricOnly: true,
        persistAcrossBackgrounding: true,
      );
    } on LocalAuthException catch (error) {
      throw BiometricPlatformException(_mapExceptionCode(error.code));
    }
  }

  DeviceBiometric _mapBiometricType(BiometricType biometric) =>
      switch (biometric) {
        BiometricType.face => DeviceBiometric.face,
        BiometricType.fingerprint => DeviceBiometric.fingerprint,
        BiometricType.iris => DeviceBiometric.iris,
        BiometricType.strong => DeviceBiometric.strong,
        BiometricType.weak => DeviceBiometric.weak,
      };

  BiometricPlatformError _mapExceptionCode(LocalAuthExceptionCode code) {
    return switch (code) {
      LocalAuthExceptionCode.userCanceled ||
      LocalAuthExceptionCode.systemCanceled ||
      LocalAuthExceptionCode.timeout ||
      LocalAuthExceptionCode.userRequestedFallback =>
        BiometricPlatformError.canceled,
      LocalAuthExceptionCode.temporaryLockout ||
      LocalAuthExceptionCode.biometricLockout =>
        BiometricPlatformError.lockedOut,
      LocalAuthExceptionCode.noCredentialsSet ||
      LocalAuthExceptionCode.noBiometricsEnrolled =>
        BiometricPlatformError.notEnrolled,
      _ => BiometricPlatformError.unavailable,
    };
  }
}

final biometricAuthServiceProvider = Provider<BiometricAuthService>(
  (ref) => BiometricAuthService(),
);
