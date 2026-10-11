import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/auth/auth_controller.dart';
import 'package:rental_command/core/auth/auth_models.dart';

void main() {
  test('experience-only changes cross the access authority boundary', () {
    final management = _authenticated(WorkspaceExperience.management);
    final leasing = _authenticated(WorkspaceExperience.leasing);

    expect(accessAuthorityChanged(management, management), isFalse);
    expect(accessAuthorityChanged(management, leasing), isTrue);
  });

  test('auth controller serializes selectors and awaits current authority', () {
    final controllerSource = File(
      'lib/core/auth/auth_controller.dart',
    ).readAsStringSync();
    final selectorSource = File(
      'lib/features/home/mobile_shell_actions.dart',
    ).readAsStringSync();

    expect(controllerSource, contains('_accessSelectionInFlight'));
    expect(controllerSource, contains('_runAccessSelection'));
    expect(
      controllerSource,
      contains(
        'current.activeExperience == access.selectedContext.activeExperience',
      ),
    );
    expect(selectorSource, contains('bool _selectionInFlight = false'));
    expect(selectorSource, contains('await _runSelection('));
    expect(selectorSource, contains('onChanged: _selectionInFlight'));
  });

  test('authority changes clear root routes and all scoped cache families', () {
    final mainSource = File('lib/main.dart').readAsStringSync();
    final resetSource = File(
      'lib/core/realtime/realtime_providers.dart',
    ).readAsStringSync();

    expect(mainSource, contains('accessAuthorityChanged(previous, next)'));
    expect(mainSource, contains('rootNavigatorKey.currentState?.popUntil'));
    expect(mainSource, contains("router.go('/')"));
    expect(mainSource, contains('resetAccessScopedClient(ref)'));

    for (final provider in [
      'homeBriefingProvider',
      'homeLatestMessagesProvider',
      'homeFieldQueueProvider',
      'unitHealthPageProvider',
      'unitDashboardProvider',
      'unitListingWorkspaceProvider',
      'bankingSummaryProvider',
      'bankingTransactionsProvider',
      'bankingReviewQueueProvider',
      'noticeDraftsProvider',
      'applicationScreeningProvider',
      'leaseAddendumHistoryProvider',
      'paymentDetailProvider',
      'expenseDetailProvider',
      'expenseReceiptProvider',
      'tenantPortalSnapshotProvider',
      'tenantPortalAccountProvider',
      'tenantPortalChargesPageProvider',
      'tenantPortalEntriesPageProvider',
      'tenantWorkOrderDetailProvider',
      'tenantAutopayStatusProvider',
      'voiceConversationProvider',
    ]) {
      expect(
        resetSource,
        contains('ref.invalidate($provider)'),
        reason: '$provider must be invalidated across access boundaries.',
      );
    }
  });

  test('Home recent messages use server-side paging', () {
    final homeProviderSource = File(
      'lib/features/home/home_access_providers.dart',
    ).readAsStringSync();
    final messagesSource = File(
      'lib/features/messages/messages_repository.dart',
    ).readAsStringSync();

    expect(homeProviderSource, contains('listRecentConversations(take: 5)'));
    expect(homeProviderSource, isNot(contains('.take(5)')));
    expect(messagesSource, contains("'/conversations/page'"));
    expect(messagesSource, contains("'take': take"));
  });
}

AuthStateAuthenticated _authenticated(WorkspaceExperience experience) {
  final access = AccessEnvelope(
    identity: const AccessIdentity(userId: 1, displayName: 'Test user'),
    selectedContext: SelectedAccessContext(
      accessContextId: 7,
      portfolioId: 9,
      workspaceName: 'Test workspace',
      accessRevision: 11,
      activeExperience: experience,
    ),
    defaultExperience: WorkspaceExperience.management,
    availableExperiences: const [
      WorkspaceExperience.management,
      WorkspaceExperience.leasing,
    ],
    assignments: const [],
    navigation: const [],
  );
  return AuthStateAuthenticated(
    const AuthUser(
      id: 1,
      email: 'test@example.test',
      displayName: 'Test user',
      emailVerified: true,
    ),
    access,
    activeExperience: experience,
  );
}
