import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/auth/auth_controller.dart';
import 'package:rental_command/core/auth/auth_models.dart';
import 'package:rental_command/core/time/app_clock.dart';
import 'package:rental_command/core/realtime/realtime_providers.dart';
import 'package:rental_command/features/accounting/accounting_repository.dart';
import 'package:rental_command/features/home/home_access_providers.dart';
import 'package:rental_command/features/home/home_shell.dart';
import 'package:rental_command/features/onboarding/getting_started_provider.dart';
import 'package:rental_command/features/onboarding/onboarding_repository.dart';

void main() {
  testWidgets('local evening hour renders the evening greeting', (
    tester,
  ) async {
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          authControllerProvider.overrideWith(
            () => _StaticAuthController(
              _authenticated(WorkspaceExperience.management),
            ),
          ),
          appNowProvider.overrideWith(
            (ref) async => DateTime(2026, 8, 25, 21),
          ),
          homeBriefingProvider.overrideWith(
            (ref) => Future.error(StateError('briefing unavailable')),
          ),
          homeLatestMessagesProvider.overrideWith((ref) async => const []),
          homeFieldQueueProvider.overrideWith((ref) async => const []),
          moneySnapshotProvider.overrideWith(
            (ref) => Future.error(StateError('money unavailable')),
          ),
          sandboxStateProvider.overrideWith(
            (ref) => Future.error(StateError('sandbox unavailable')),
          ),
          gettingStartedSignalsProvider.overrideWith(
            (ref) => Future.error(StateError('signals unavailable')),
          ),
          realtimeWatcherProvider.overrideWith((ref) {}),
        ],
        child: const MaterialApp(home: HomeShell()),
      ),
    );
    await tester.pump();
    await tester.pump();

    expect(find.textContaining('Good evening'), findsOneWidget);
  });
}

AuthStateAuthenticated _authenticated(WorkspaceExperience experience) {
  return AuthStateAuthenticated(
    const AuthUser(
      id: 1,
      email: 'manager@example.test',
      displayName: 'Test manager',
      emailVerified: true,
    ),
    AccessEnvelope(
      identity: const AccessIdentity(userId: 1, displayName: 'Test manager'),
      selectedContext: SelectedAccessContext(
        accessContextId: 1,
        portfolioId: 1,
        workspaceName: 'Test workspace',
        accessRevision: 1,
        activeExperience: experience,
      ),
      defaultExperience: experience,
      availableExperiences: [experience],
      assignments: const [],
      navigation: [
        NavigationCapabilities(
          experience: experience,
          capabilityKeys: const [],
        ),
      ],
    ),
    activeExperience: experience,
  );
}

class _StaticAuthController extends AuthController {
  _StaticAuthController(this.initialState);

  final AuthState initialState;

  @override
  AuthState build() => initialState;
}
