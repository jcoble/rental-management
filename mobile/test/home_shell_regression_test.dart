import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/auth/auth_controller.dart';
import 'package:rental_command/core/auth/auth_models.dart';
import 'package:rental_command/core/time/app_clock.dart';
import 'package:rental_command/core/realtime/realtime_providers.dart';
import 'package:rental_command/features/accounting/accounting_repository.dart';
import 'package:rental_command/features/ai/ai_models.dart';
import 'package:rental_command/features/home/home_access_providers.dart';
import 'package:rental_command/features/home/home_shell.dart';
import 'package:rental_command/features/onboarding/getting_started_provider.dart';
import 'package:rental_command/features/onboarding/onboarding_repository.dart';

void main() {
  testWidgets('evening greeting and LeaseAgreement bullet are rendered', (
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
            (ref) async => const BriefingResponse(
              date: '2026-08-25',
              generatedAt: '2026-08-25T00:00:00Z',
              llmEnhanced: false,
              bullets: [
                BriefingBullet(
                  title: 'Lease expiring',
                  detail: 'A lease ends soon.',
                  category: 'LeaseExpiring',
                  severity: BulletSeverity.warning,
                  entityType: 'LeaseAgreement',
                  entityId: 42,
                ),
              ],
            ),
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
    expect(
      find.ancestor(
        of: find.text('Lease expiring'),
        matching: find.byType(InkWell),
      ),
      findsOneWidget,
    );
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
