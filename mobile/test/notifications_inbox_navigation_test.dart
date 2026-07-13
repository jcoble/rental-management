import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:rental_command/features/home/mobile_domain_navigation.dart';
import 'package:rental_command/features/notifications/notification_models.dart';
import 'package:rental_command/features/notifications/notifications_inbox_screen.dart';
import 'package:rental_command/features/notifications/notifications_repository.dart';

void main() {
  testWidgets('inbox detail taps push so back returns to the inbox', (
    tester,
  ) async {
    final repo = _FakeNotificationsRepository([
      AppNotification(
        id: 1,
        type: 'Payment',
        title: 'Rent posted',
        message: 'Payment received',
        severity: 'Info',
        actionUrl: '/tenant-accounts/42/entries/8',
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
        overrides: [notificationsRepositoryProvider.overrideWithValue(repo)],
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
