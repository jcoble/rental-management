import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:material_symbols_icons/symbols.dart';
import 'package:rental_command/core/auth/auth_controller.dart';
import 'package:rental_command/core/auth/auth_models.dart';
import 'package:rental_command/core/realtime/realtime_providers.dart';
import 'package:rental_command/features/home/home_access_providers.dart';
import 'package:rental_command/features/home/home_shell.dart';

void main() {
  testWidgets('back returns to Today before leaving the app', (tester) async {
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          authControllerProvider.overrideWith(
            () => _StaticAuthController(_authenticated()),
          ),
          homeBriefingProvider.overrideWith(
            (ref) => Future.error(StateError('briefing unavailable')),
          ),
          homeLatestMessagesProvider.overrideWith((ref) async => const []),
          homeFieldQueueProvider.overrideWith((ref) async => const []),
          realtimeWatcherProvider.overrideWith((ref) {}),
        ],
        child: const MaterialApp(home: HomeShell()),
      ),
    );
    await tester.pump();
    await tester.pump();

    await tester.tap(find.byIcon(Symbols.apartment_rounded));
    await tester.pumpAndSettle();

    expect(await tester.binding.handlePopRoute(), isTrue);
    await tester.pumpAndSettle();
    expect(find.text('Rental Command'), findsOneWidget);

    expect(await tester.binding.handlePopRoute(), isFalse);
  });
}

AuthStateAuthenticated _authenticated() {
  return AuthStateAuthenticated(
    const AuthUser(
      id: 1,
      email: 'manager@example.test',
      displayName: 'Test manager',
      emailVerified: true,
    ),
    const AccessEnvelope(
      identity: AccessIdentity(userId: 1, displayName: 'Test manager'),
      selectedContext: SelectedAccessContext(
        accessContextId: 1,
        portfolioId: 1,
        workspaceName: 'Test workspace',
        accessRevision: 1,
        activeExperience: WorkspaceExperience.management,
      ),
      defaultExperience: WorkspaceExperience.management,
      availableExperiences: [WorkspaceExperience.management],
      assignments: [],
      navigation: [
        NavigationCapabilities(
          experience: WorkspaceExperience.management,
          capabilityKeys: ['rentals.read'],
        ),
      ],
    ),
    activeExperience: WorkspaceExperience.management,
  );
}

class _StaticAuthController extends AuthController {
  _StaticAuthController(this.initialState);

  final AuthState initialState;

  @override
  AuthState build() => initialState;
}
