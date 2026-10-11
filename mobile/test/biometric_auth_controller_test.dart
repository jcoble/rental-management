import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/api/api_exception.dart';
import 'package:rental_command/core/auth/auth_controller.dart';
import 'package:rental_command/core/auth/auth_models.dart';
import 'package:rental_command/core/auth/auth_repository.dart';
import 'package:rental_command/core/auth/biometric_auth_service.dart';
import 'package:rental_command/core/auth/token_store.dart';
import 'package:rental_command/core/router/mobile_restoration_state.dart';
import 'package:rental_command/core/push/push_service.dart';
import 'package:rental_command/features/onboarding/onboarding_models.dart';
import 'package:rental_command/features/onboarding/onboarding_repository.dart';

void main() {
  test(
    'cold start keeps an opted-in stored session biometric locked',
    () async {
      final harness = _Harness();
      addTearDown(harness.dispose);

      await harness.controller.restoreSession();

      final state = harness.container.read(authControllerProvider);
      expect(state, isA<AuthStateUnauthenticated>());
      expect(state, isA<AuthStateBiometricLocked>());
      expect(harness.repository.currentUserCalls, 0);
      expect(harness.repository.currentAccessCalls, 0);
    },
  );

  test('biometric success revalidates the stored server session', () async {
    final harness = _Harness();
    addTearDown(harness.dispose);
    await harness.controller.restoreSession();

    await harness.controller.unlockBiometricSession();

    final state = harness.container.read(authControllerProvider);
    expect(state, isA<AuthStateAuthenticated>());
    expect((state as AuthStateAuthenticated).user, _user);
    expect(harness.repository.currentUserCalls, 1);
    expect(harness.repository.currentAccessCalls, 1);
    expect(harness.restorationStore.clearCalls, 1);
  });

  test('normal stored-session restore keeps the previous navigation', () async {
    final harness = _Harness(biometricEnabled: false);
    addTearDown(harness.dispose);

    await harness.controller.restoreSession();

    expect(
      harness.container.read(authControllerProvider),
      isA<AuthStateAuthenticated>(),
    );
    expect(harness.restorationStore.clearCalls, 0);
  });

  for (final result in [
    BiometricAuthenticationResult.canceled,
    BiometricAuthenticationResult.lockedOut,
    BiometricAuthenticationResult.notEnrolled,
    BiometricAuthenticationResult.unavailable,
  ]) {
    test('$result keeps password and Google fallback available', () async {
      final harness = _Harness(biometricResult: result);
      addTearDown(harness.dispose);
      await harness.controller.restoreSession();

      await harness.controller.unlockBiometricSession();

      final state = harness.container.read(authControllerProvider);
      expect(state, isA<AuthStateBiometricLocked>());
      expect((state as AuthStateBiometricLocked).lastResult, result);
      expect(harness.repository.currentUserCalls, 0);
      expect(harness.repository.currentAccessCalls, 0);
      expect(harness.biometric.clearEnabledCalls, 0);
    });
  }

  test(
    'invalid stored session clears tokens and biometric eligibility',
    () async {
      final harness = _Harness(
        currentUserError: const ApiException(
          statusCode: 401,
          message: 'Session expired.',
        ),
      );
      addTearDown(harness.dispose);
      await harness.controller.restoreSession();

      await harness.controller.unlockBiometricSession();

      expect(
        harness.container.read(authControllerProvider),
        isA<AuthStateUnauthenticated>(),
      );
      expect(harness.tokenStore.clearTokensCalls, 1);
      expect(harness.biometric.clearEnabledCalls, 1);
    },
  );

  for (final error in [
    const ApiException(
      statusCode: 0,
      message: 'Unable to connect. Check your network connection.',
    ),
    const ApiException(
      statusCode: 408,
      message: 'The request is taking longer than expected. Try again.',
    ),
    const ApiException(
      statusCode: 503,
      message: 'Something went wrong. Please try again.',
    ),
  ]) {
    test(
      'transient ${error.statusCode} revalidation failure preserves the locked session',
      () async {
        final harness = _Harness(currentUserError: error);
        addTearDown(harness.dispose);
        await harness.controller.restoreSession();

        await harness.controller.unlockBiometricSession();

        expect(
          harness.container.read(authControllerProvider),
          isA<AuthStateBiometricLocked>(),
        );
        expect(harness.tokenStore.clearTokensCalls, 0);
        expect(harness.biometric.clearEnabledCalls, 0);
      },
    );
  }

  test(
    'transient access-envelope failure also preserves the locked session',
    () async {
      final harness = _Harness(
        currentAccessError: const ApiException(
          statusCode: 503,
          message: 'Something went wrong. Please try again.',
        ),
      );
      addTearDown(harness.dispose);
      await harness.controller.restoreSession();

      await harness.controller.unlockBiometricSession();

      expect(
        harness.container.read(authControllerProvider),
        isA<AuthStateBiometricLocked>(),
      );
      expect(harness.repository.currentUserCalls, 1);
      expect(harness.repository.currentAccessCalls, 1);
      expect(harness.tokenStore.clearTokensCalls, 0);
      expect(harness.biometric.clearEnabledCalls, 0);
    },
  );

  test(
    'transient restore failure without biometric opt-in does not offer fingerprint',
    () async {
      final harness = _Harness(
        biometricEnabled: false,
        currentUserError: const ApiException(
          statusCode: 503,
          message: 'Something went wrong. Please try again.',
        ),
      );
      addTearDown(harness.dispose);

      await harness.controller.restoreSession();

      expect(
        harness.container.read(authControllerProvider),
        isNot(isA<AuthStateBiometricLocked>()),
      );
      expect(
        harness.container.read(authControllerProvider),
        isA<AuthStateUnauthenticated>(),
      );
      expect(harness.tokenStore.clearTokensCalls, 0);
      expect(harness.biometric.clearEnabledCalls, 0);
    },
  );

  test('logout clears biometric eligibility with the local session', () async {
    final harness = _Harness();
    addTearDown(harness.dispose);

    await harness.controller.logout();

    expect(harness.repository.logoutCalls, 1);
    expect(harness.biometric.clearEnabledCalls, 1);
    expect(
      harness.container.read(authControllerProvider),
      isA<AuthStateUnauthenticated>(),
    );
  });
}

class _Harness {
  _Harness({
    BiometricAuthenticationResult biometricResult =
        BiometricAuthenticationResult.authenticated,
    bool biometricEnabled = true,
    ApiException? currentUserError,
    ApiException? currentAccessError,
  }) : tokenStore = _FakeTokenStore(),
       biometric = _FakeBiometricAuthService(
         biometricResult,
         enabled: biometricEnabled,
       ),
       restorationStore = _FakeMobileRestorationStateStore(),
       repository = _FakeAuthRepository(
         tokenStore: _FakeTokenStore(),
         currentUserError: currentUserError,
         currentAccessError: currentAccessError,
       ) {
    repository.tokenStore = tokenStore;
    container = ProviderContainer(
      overrides: [
        tokenStoreProvider.overrideWithValue(tokenStore),
        biometricAuthServiceProvider.overrideWithValue(biometric),
        mobileRestorationStateStoreProvider.overrideWithValue(restorationStore),
        authRepositoryProvider.overrideWithValue(repository),
        onboardingRepositoryProvider.overrideWithValue(
          _FakeOnboardingRepository(),
        ),
        pushServiceProvider.overrideWithValue(_FakePushService()),
      ],
    );
  }

  final _FakeTokenStore tokenStore;
  final _FakeBiometricAuthService biometric;
  final _FakeMobileRestorationStateStore restorationStore;
  final _FakeAuthRepository repository;
  late final ProviderContainer container;

  AuthController get controller =>
      container.read(authControllerProvider.notifier);

  void dispose() => container.dispose();
}

class _FakeMobileRestorationStateStore implements MobileRestorationStateStore {
  int clearCalls = 0;

  @override
  Future<void> clear() async {
    clearCalls++;
  }

  @override
  Future<MobileRestorationStateLoadResult> load() async =>
      const MobileRestorationStateLoadMissing();

  @override
  Future<void> save(MobileRestorationState state) async {}
}

class _FakeTokenStore implements TokenStore {
  int clearTokensCalls = 0;

  @override
  Future<void> clearTokens() async {
    clearTokensCalls++;
  }

  @override
  Future<String?> getAccessToken() async => 'stored-access-token';

  @override
  Future<String?> getRefreshToken() async => 'stored-refresh-token';

  @override
  Future<Map<String, dynamic>?> getAccessEnvelope() async => null;

  @override
  Future<void> saveAccessEnvelope(Map<String, dynamic> access) async {}

  @override
  Future<void> saveTokens({
    required String accessToken,
    required String refreshToken,
  }) async {}
}

class _FakeBiometricAuthService extends BiometricAuthService {
  _FakeBiometricAuthService(this.result, {required this.enabled})
    : super(isAndroid: false);

  final BiometricAuthenticationResult result;
  final bool enabled;
  int clearEnabledCalls = 0;

  @override
  Future<BiometricAuthenticationResult> authenticate() async => result;

  @override
  Future<bool> isEnabled() async => enabled;

  @override
  Future<void> clearEnabled() async {
    clearEnabledCalls++;
  }
}

class _FakeAuthRepository extends AuthRepository {
  _FakeAuthRepository({
    required super.tokenStore,
    this.currentUserError,
    this.currentAccessError,
  }) : super(dio: Dio());

  TokenStore? tokenStore;
  final ApiException? currentUserError;
  final ApiException? currentAccessError;
  int currentUserCalls = 0;
  int currentAccessCalls = 0;
  int logoutCalls = 0;

  @override
  Future<AuthUser> currentUser() async {
    currentUserCalls++;
    final error = currentUserError;
    if (error != null) throw error;
    return _user;
  }

  @override
  Future<AccessEnvelope> currentAccess() async {
    currentAccessCalls++;
    final error = currentAccessError;
    if (error != null) throw error;
    return _access;
  }

  @override
  Future<void> logout() async {
    logoutCalls++;
    await tokenStore?.clearTokens();
  }
}

class _FakeOnboardingRepository extends OnboardingRepository {
  _FakeOnboardingRepository() : super(Dio());

  @override
  Future<SandboxState> sandboxState() async => const SandboxState(
    portfolioId: 1,
    isSandbox: false,
    onboardingChoicePending: false,
  );
}

class _FakePushService implements PushService {
  @override
  Future<void> unregisterOnLogout() async {}

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

const _user = AuthUser(
  id: 1,
  email: 'landlord@example.test',
  displayName: 'Landlord',
  emailVerified: true,
);

const _access = AccessEnvelope(
  identity: AccessIdentity(userId: 1, displayName: 'Landlord'),
  selectedContext: SelectedAccessContext(
    accessContextId: 1,
    portfolioId: 1,
    workspaceName: 'Rental Command',
    accessRevision: 1,
    activeExperience: WorkspaceExperience.management,
  ),
  defaultExperience: WorkspaceExperience.management,
  availableExperiences: [WorkspaceExperience.management],
  assignments: [],
  navigation: [],
);
