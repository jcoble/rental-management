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
  testWidgets('Today shows the warning first and a row for the rest', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(1200, 5000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    final bullets = <BriefingBullet>[
      for (var i = 1; i <= 6; i++)
        BriefingBullet(
          title: 'Rent overdue $i',
          detail: 'Unit $i is behind on rent.',
          category: 'RentLate',
          severity: BulletSeverity.info,
        ),
      const BriefingBullet(
        title: 'Lease ending soon',
        detail: 'The lease at 12 Oak St ends in 30 days.',
        category: 'LeaseExpiring',
        severity: BulletSeverity.warning,
      ),
    ];

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          authControllerProvider.overrideWith(
            () => _StaticAuthController(
              _authenticated(WorkspaceExperience.management),
            ),
          ),
          appNowProvider.overrideWith((ref) async => DateTime(2026, 8, 25, 21)),
          homeBriefingProvider.overrideWith(
            (ref) async => BriefingResponse(
              date: '2026-08-25',
              generatedAt: '2026-08-25T21:00:00Z',
              llmEnhanced: false,
              bullets: bullets,
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

    // The warning is promoted into the five shown, and the tail is announced.
    expect(find.text('Lease ending soon'), findsOneWidget);
    expect(find.text('Rent overdue 1'), findsOneWidget);
    expect(find.text('Rent overdue 4'), findsOneWidget);
    expect(find.text('Rent overdue 5'), findsNothing);
    expect(find.text('Rent overdue 6'), findsNothing);
    expect(find.text('2 more today'), findsOneWidget);

    await tester.tap(find.text('2 more today'));
    await tester.pump();

    expect(find.text('Rent overdue 5'), findsOneWidget);
    expect(find.text('Rent overdue 6'), findsOneWidget);
    expect(find.text('2 more today'), findsNothing);
    expect(find.text('Show fewer'), findsOneWidget);
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
