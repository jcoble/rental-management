import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:rental_command/core/auth/auth_controller.dart';
import 'package:rental_command/core/auth/auth_models.dart';
import 'package:rental_command/core/push/mobile_navigation_intent.dart';
import 'package:rental_command/core/push/notification_routing.dart';
import 'package:rental_command/features/home/mobile_domain_navigation.dart';
import 'package:rental_command/features/notifications/notification_models.dart';
import 'package:rental_command/features/notifications/notifications_inbox_screen.dart';
import 'package:rental_command/features/notifications/notifications_repository.dart';

void main() {
  group('typed notification navigation', () {
    test('parses a valid typed payload and never adapts a raw URL', () {
      final parsed = MobileNavigationIntent.tryParse(
        _payload(
          destination: 'Message',
          resource: const {'kind': 'Conversation', 'id': 19},
        ),
      );

      expect(parsed, isNotNull);
      expect(parsed!.destination, MobileNavigationDestination.message);
      expect(
        parsed.resolveFor(_authority(), nowUtc: DateTime.utc(2026, 7, 23, 12)),
        '/messages/19',
      );
      expect(
        MobileNavigationIntent.tryParse({
          ..._payload(),
          'actionUrl': '/messages/19',
        }),
        isNull,
      );
    });

    test('maps every member of the closed destination set exhaustively', () {
      final routes = <MobileNavigationDestination, String>{};
      for (final destination in MobileNavigationDestination.values) {
        final intent = _intentFor(destination);
        final route = resolveNavigationIntentRoute(intent);
        expect(route, isNotNull, reason: destination.name);
        routes[destination] = route!;
      }

      expect(routes.length, MobileNavigationDestination.values.length);
      expect(
        routes[MobileNavigationDestination.unitMoney],
        '/units/7?tab=money',
      );
      expect(
        routes[MobileNavigationDestination.tenantLedgerEntry],
        '/tenant-accounts/3/entries/8',
      );
      expect(
        routes[MobileNavigationDestination.tenantAccount],
        '/portal/tenant-accounts/3',
      );
      expect(
        routes[MobileNavigationDestination.technicianWork],
        '/maintenance/work/7',
      );
    });

    test('malformed and unknown payloads fail closed', () {
      expect(MobileNavigationIntent.tryParse(null), isNull);
      expect(
        MobileNavigationIntent.tryParse({'destination': 'Message'}),
        isNull,
      );
      expect(
        MobileNavigationIntent.tryParse({
          ..._payload(),
          'experience': null,
        }),
        isNull,
      );
      expect(
        MobileNavigationIntent.tryParse({
          ..._payload(),
          'experience': 'FutureUnknownExperience',
        }),
        isNull,
      );
      expect(
        MobileNavigationIntent.tryParse(
          _payload(destination: 'FutureUnknownDestination'),
        ),
        isNull,
      );
      expect(
        MobileNavigationIntent.tryParse(
          _payload(
            destination: 'Message',
            resource: const {'kind': 'Conversation', 'id': 0},
          ),
        ),
        isNull,
      );
      expect(
        MobileNavigationIntent.tryParse(
          _payload(
            destination: 'Message',
            resource: const {'kind': 'Conversation', 'id': 1.5},
          ),
        ),
        isNull,
      );
      expect(
        MobileNavigationIntent.tryParse(_payload(fallbackDestination: 'Money')),
        isNull,
      );
    });

    test(
      'expired, revoked, cross-context and cross-experience intents fall back',
      () {
        final valid = MobileNavigationIntent.tryParse(
          _payload(
            destination: 'Message',
            resource: const {'kind': 'Conversation', 'id': 19},
          ),
        )!;

        expect(
          valid.resolveFor(_authority(), nowUtc: DateTime.utc(2099, 1, 1)),
          '/',
          reason: 'expiry is exclusive',
        );
        expect(
          valid.resolveFor(
            _authority(accessRevision: 5),
            nowUtc: DateTime.utc(2026, 7, 23, 12),
          ),
          '/',
          reason: 'a revised/revoked authority invalidates the intent',
        );
        expect(
          valid.resolveFor(
            _authority(accessContextId: 99),
            nowUtc: DateTime.utc(2026, 7, 23, 12),
          ),
          '/',
        );
        expect(
          valid.resolveFor(
            _authority(experience: WorkspaceExperience.tenant),
            nowUtc: DateTime.utc(2026, 7, 23, 12),
          ),
          '/',
        );
      },
    );

    test(
      'tenant ledger entry intents use the tenant portal route for tenants',
      () {
        final intent = MobileNavigationIntent.tryParse(
          _payload(
            experience: 'Tenant',
            destination: 'TenantLedgerEntry',
            resource: const {'kind': 'TenantLedgerEntry', 'id': 8},
            parentResource: const {'kind': 'TenantAccount', 'id': 3},
          ),
        )!;

        expect(
          intent.resolveFor(
            _authority(experience: WorkspaceExperience.tenant),
            nowUtc: DateTime.utc(2026, 7, 23, 12),
          ),
          '/portal/tenant-accounts/3/entries/8',
        );
      },
    );

    test('resource kind mismatch cannot synthesize a route', () {
      final intent = MobileNavigationIntent.tryParse(
        _payload(
          destination: 'Message',
          resource: const {'kind': 'WorkOrder', 'id': 19},
        ),
      )!;

      expect(resolveNavigationIntentRoute(intent), isNull);
      expect(
        intent.resolveFor(_authority(), nowUtc: DateTime.utc(2026, 7, 23, 12)),
        '/',
      );
    });
  });

  testWidgets('typed inbox detail taps push so back returns to the inbox', (
    tester,
  ) async {
    final repo = _FakeNotificationsRepository([
      AppNotification(
        id: 1,
        type: 'Payment',
        title: 'Rent posted',
        message: 'Payment received',
        severity: 'Info',
        navigationIntent: MobileNavigationIntent.tryParse(
          _payload(
            destination: 'TenantLedgerEntry',
            resource: const {'kind': 'TenantLedgerEntry', 'id': 8},
            parentResource: const {'kind': 'TenantAccount', 'id': 42},
          ),
        ),
        isRead: false,
        createdAt: DateTime(2026),
      ),
    ]);
    final shellRoutes = <String>[];

    final router = GoRouter(
      initialLocation: '/notifications',
      routes: [
        GoRoute(
          path: '/notifications',
          builder: (context, state) => MobileShellNavigation(
            controller: MobileShellNavigator(
              openTab: (_, {destination, detailBuilder}) {},
              openRoute: (route) {
                shellRoutes.add(route);
                return true;
              },
            ),
            child: const NotificationsInboxScreen(),
          ),
        ),
        GoRoute(
          path:
              '/tenant-accounts/:tenantAccountId/entries/:tenantLedgerEntryId',
          builder: (context, state) => Scaffold(
            body: Text(
              'Payment ${state.pathParameters['tenantAccountId']}/'
              '${state.pathParameters['tenantLedgerEntryId']}',
            ),
          ),
        ),
      ],
    );

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          notificationsRepositoryProvider.overrideWithValue(repo),
          authControllerProvider.overrideWith(
            () => _StaticAuthController(_authority()),
          ),
        ],
        child: MaterialApp.router(routerConfig: router),
      ),
    );
    await tester.pump();
    await tester.pump();

    await tester.tap(find.text('Rent posted'));
    await tester.pumpAndSettle();

    expect(shellRoutes, isEmpty);
    expect(repo.markedReadIds, [1]);
    expect(find.text('Payment 42/8'), findsOneWidget);

    await tester.binding.handlePopRoute();
    await tester.pumpAndSettle();

    expect(find.byType(NotificationsInboxScreen), findsOneWidget);
    expect(find.text('Rent posted'), findsOneWidget);
  });

  testWidgets('tenant inbox ledger taps push to tenant portal account history', (
    tester,
  ) async {
    final repo = _FakeNotificationsRepository([
      AppNotification(
        id: 1,
        type: 'Payment',
        title: 'Rent posted',
        message: 'Payment received',
        severity: 'Info',
        navigationIntent: MobileNavigationIntent.tryParse(
          _payload(
            experience: 'Tenant',
            destination: 'TenantLedgerEntry',
            resource: const {'kind': 'TenantLedgerEntry', 'id': 8},
            parentResource: const {'kind': 'TenantAccount', 'id': 42},
          ),
        ),
        isRead: false,
        createdAt: DateTime(2026),
      ),
    ]);
    final shellRoutes = <String>[];

    final router = GoRouter(
      initialLocation: '/notifications',
      routes: [
        GoRoute(
          path: '/notifications',
          builder: (context, state) => MobileShellNavigation(
            controller: MobileShellNavigator(
              openTab: (_, {destination, detailBuilder}) {},
              openRoute: (route) {
                shellRoutes.add(route);
                return true;
              },
            ),
            child: const NotificationsInboxScreen(),
          ),
        ),
        GoRoute(
          path:
              '/portal/tenant-accounts/:tenantAccountId/entries/:tenantLedgerEntryId',
          builder: (context, state) => Scaffold(
            body: Text(
              'Portal account ${state.pathParameters['tenantAccountId']}',
            ),
          ),
        ),
      ],
    );

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          notificationsRepositoryProvider.overrideWithValue(repo),
          authControllerProvider.overrideWith(
            () => _StaticAuthController(
              _authority(experience: WorkspaceExperience.tenant),
            ),
          ),
        ],
        child: MaterialApp.router(routerConfig: router),
      ),
    );
    await tester.pump();
    await tester.pump();

    await tester.tap(find.text('Rent posted'));
    await tester.pumpAndSettle();

    expect(shellRoutes, isEmpty);
    expect(repo.markedReadIds, [1]);
    expect(find.text('Portal account 42'), findsOneWidget);
  });
}

Map<String, dynamic> _payload({
  String experience = 'Management',
  String destination = 'Home',
  String fallbackDestination = 'Home',
  Object? resource,
  Object? parentResource,
  Object? childResource,
  String expiresAtUtc = '2099-01-01T00:00:00Z',
}) => {
  'experience': experience,
  'destination': destination,
  'accessContextId': 12,
  'accessRevision': 4,
  'resource': resource,
  'parentResource': parentResource,
  'childResource': childResource,
  'action': 'Open',
  'expiresAtUtc': expiresAtUtc,
  'fallbackDestination': fallbackDestination,
};

MobileNavigationIntent _intentFor(MobileNavigationDestination destination) {
  const generic = MobileNavigationResource(kind: 'Unit', id: 7);
  final (resource, parent) = switch (destination) {
    MobileNavigationDestination.unitSummary ||
    MobileNavigationDestination.unitTenantLease ||
    MobileNavigationDestination.unitMoney ||
    MobileNavigationDestination.unitMaintenance ||
    MobileNavigationDestination.unitRecords ||
    MobileNavigationDestination.leasingRental => (generic, null),
    MobileNavigationDestination.tenantLedgerEntry => (
      const MobileNavigationResource(kind: 'TenantLedgerEntry', id: 8),
      const MobileNavigationResource(kind: 'TenantAccount', id: 3),
    ),
    MobileNavigationDestination.expense => (
      const MobileNavigationResource(kind: 'Expense', id: 7),
      null,
    ),
    MobileNavigationDestination.scanDraft => (
      const MobileNavigationResource(kind: 'ScanDraft', id: 7),
      null,
    ),
    MobileNavigationDestination.message ||
    MobileNavigationDestination.leasingConversation => (
      const MobileNavigationResource(kind: 'Conversation', id: 7),
      null,
    ),
    MobileNavigationDestination.workOrder ||
    MobileNavigationDestination.technicianWork => (
      const MobileNavigationResource(kind: 'WorkOrder', id: 7),
      null,
    ),
    MobileNavigationDestination.leasingApplication => (
      const MobileNavigationResource(kind: 'RentalApplication', id: 7),
      null,
    ),
    MobileNavigationDestination.leasingAppointment => (
      const MobileNavigationResource(kind: 'Appointment', id: 7),
      null,
    ),
    MobileNavigationDestination.leasingMoveIn => (
      const MobileNavigationResource(kind: 'LeaseManagement', id: 7),
      null,
    ),
    MobileNavigationDestination.tenantAccount => (
      const MobileNavigationResource(kind: 'TenantAccount', id: 3),
      null,
    ),
    MobileNavigationDestination.home ||
    MobileNavigationDestination.notifications ||
    MobileNavigationDestination.rentals ||
    MobileNavigationDestination.owners ||
    MobileNavigationDestination.money ||
    MobileNavigationDestination.work ||
    MobileNavigationDestination.inbox => (null, null),
  };
  return MobileNavigationIntent(
    experience: destination == MobileNavigationDestination.tenantAccount
        ? WorkspaceExperience.tenant
        : WorkspaceExperience.management,
    destination: destination,
    accessContextId: 12,
    accessRevision: 4,
    resource: resource,
    parentResource: parent,
    action: MobileNavigationAction.open,
    expiresAtUtc: DateTime.utc(2099),
    fallbackDestination: MobileNavigationDestination.home,
  );
}

AuthStateAuthenticated _authority({
  int accessContextId = 12,
  int accessRevision = 4,
  WorkspaceExperience experience = WorkspaceExperience.management,
}) => AuthStateAuthenticated(
  const AuthUser(
    id: 1,
    email: 'manager@example.test',
    displayName: 'Manager',
    emailVerified: true,
  ),
  AccessEnvelope(
    identity: const AccessIdentity(userId: 1, displayName: 'Manager'),
    selectedContext: SelectedAccessContext(
      accessContextId: accessContextId,
      portfolioId: 1,
      workspaceName: 'Workspace',
      accessRevision: accessRevision,
      activeExperience: experience,
    ),
    defaultExperience: experience,
    availableExperiences: [experience],
    assignments: const [],
    navigation: [
      NavigationCapabilities(experience: experience, capabilityKeys: const []),
    ],
  ),
  activeExperience: experience,
);

class _StaticAuthController extends AuthController {
  _StaticAuthController(this.initialState);

  final AuthState initialState;

  @override
  AuthState build() => initialState;
}

class _FakeNotificationsRepository extends NotificationsRepository {
  _FakeNotificationsRepository(this.items) : super(Dio());

  final List<AppNotification> items;
  final markedReadIds = <int>[];

  @override
  Future<List<AppNotification>> list({
    bool unreadOnly = false,
    int skip = 0,
    int take = 20,
  }) async {
    return items.skip(skip).take(take).toList();
  }

  @override
  Future<int> unreadCount() async {
    return items.where((item) => !item.isRead).length;
  }

  @override
  Future<void> markRead(int id) async {
    markedReadIds.add(id);
  }
}
