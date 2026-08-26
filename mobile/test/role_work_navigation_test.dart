import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/auth/auth_models.dart';
import 'package:rental_command/core/auth/mobile_access_policy.dart';
import 'package:rental_command/features/home/home_shell.dart';
import 'package:rental_command/features/home/mobile_destination.dart';
import 'package:rental_command/features/home/mobile_domain_navigation.dart';
import 'package:rental_command/features/home/mobile_role_shell.dart';

void main() {
  group('Tenant navigation', () {
    test('shows exactly five destinations in blueprint order', () {
      expect(
        tenantShellDestinations.map((destination) => destination.id),
        TenantShellDestinationId.values,
      );
      expect(
        tenantShellDestinations.map((destination) => destination.label),
        const ['Home', 'Account & lease', 'Maintenance', 'Messages', 'Profile'],
      );
      expect(tenantShellDestinations, hasLength(5));
    });

    test('uses compact bottom labels without changing canonical labels', () {
      expect(
        tenantShellDestinations.map(
          (destination) => destination.bottomNavigationLabel,
        ),
        const ['Home', 'Lease', 'Repairs', 'Messages', 'Profile'],
      );
    });

    test('formats overdue item counts with correct grammar', () {
      expect(tenantOverdueItemsLabel(0), '0 overdue items');
      expect(tenantOverdueItemsLabel(1), '1 overdue item');
      expect(tenantOverdueItemsLabel(3), '3 overdue items');
    });

    testWidgets(
      'fits five accessible destinations and preserves routing at 320px',
      (tester) async {
        await tester.binding.setSurfaceSize(const Size(320, 640));
        final semanticsHandle = tester.ensureSemantics();
        addTearDown(() => tester.binding.setSurfaceSize(null));

        try {
          await tester.pumpWidget(
            MaterialApp(
              home: MobileRoleShell(
                destinations: [
                  for (final destination in tenantShellDestinations)
                    MobileRoleDestination(
                      label: destination.label,
                      bottomNavigationLabel: destination.bottomNavigationLabel,
                      icon: destination.icon,
                      ownsScaffold: true,
                      builder: (_) => Scaffold(
                        body: Center(
                          key: ValueKey('tenant-${destination.id.name}'),
                          child: Text(destination.label),
                        ),
                      ),
                    ),
                ],
              ),
            ),
          );
          await tester.pumpAndSettle();

          final navigationDestinations = tester
              .widgetList<NavigationDestination>(
                find.byType(NavigationDestination),
              )
              .toList(growable: false);
          expect(
            navigationDestinations.map((destination) => destination.label),
            const ['Home', 'Lease', 'Repairs', 'Messages', 'Profile'],
          );
          expect(
            navigationDestinations.map((destination) => destination.tooltip),
            const [null, 'Account & lease', 'Maintenance', null, null],
          );
          expect(
            find.bySemanticsLabel(RegExp(r'^Account & lease$')),
            findsOneWidget,
          );
          expect(
            find.bySemanticsLabel(RegExp(r'^Maintenance$')),
            findsOneWidget,
          );

          for (final destination in navigationDestinations) {
            final rect = tester.getRect(find.byWidget(destination));
            expect(rect.width, greaterThanOrEqualTo(48));
            expect(rect.height, greaterThanOrEqualTo(48));
          }
          expect(tester.takeException(), isNull);

          for (final destination in tenantShellDestinations) {
            final destinationFinder = find.byWidgetPredicate(
              (widget) =>
                  widget is NavigationDestination &&
                  widget.label == destination.bottomNavigationLabel,
            );
            await tester.tap(destinationFinder);
            await tester.pumpAndSettle();

            expect(
              find.byKey(ValueKey('tenant-${destination.id.name}')),
              findsOneWidget,
            );
            expect(tester.takeException(), isNull);
          }
        } finally {
          semanticsHandle.dispose();
        }
      },
    );

    testWidgets('system Back returns from Account history to Account & lease', (
      tester,
    ) async {
      await tester.pumpWidget(
        MaterialApp(
          home: MobileRoleShell(
            destinations: [
              for (final destination in tenantShellDestinations)
                MobileRoleDestination(
                  label: destination.label,
                  bottomNavigationLabel: destination.bottomNavigationLabel,
                  icon: destination.icon,
                  ownsScaffold: true,
                  builder: (context) {
                    if (destination.id !=
                        TenantShellDestinationId.accountAndLease) {
                      return Scaffold(
                        body: Center(child: Text(destination.label)),
                      );
                    }

                    return Scaffold(
                      appBar: AppBar(title: const Text('Account & lease')),
                      body: Center(
                        child: TextButton(
                          onPressed: () => Navigator.of(context).push<void>(
                            MaterialPageRoute<void>(
                              builder: (_) => Scaffold(
                                appBar: AppBar(
                                  title: const Text('Account history'),
                                ),
                                body: const Center(
                                  child: Text('Canonical account history'),
                                ),
                              ),
                            ),
                          ),
                          child: const Text('Account history'),
                        ),
                      ),
                    );
                  },
                ),
            ],
          ),
        ),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.text('Lease'));
      await tester.pumpAndSettle();
      expect(find.text('Account & lease'), findsOneWidget);

      await tester.tap(find.text('Account history'));
      await tester.pumpAndSettle();
      expect(find.text('Canonical account history'), findsOneWidget);

      await tester.binding.handlePopRoute();
      await tester.pumpAndSettle();

      expect(tester.takeException(), isNull);
      expect(find.text('Account & lease'), findsOneWidget);
      expect(find.text('Canonical account history'), findsNothing);
    });

    test('keeps management projections restricted', () {
      const capabilities = {'rentals.read', 'money.balances.read', 'work.read'};

      expect(
        canOpenMobilePath(
          experience: WorkspaceExperience.tenant,
          capabilities: capabilities,
          path: '/units/42',
        ),
        isFalse,
      );
      expect(
        canOpenMobilePath(
          experience: WorkspaceExperience.tenant,
          capabilities: capabilities,
          path: '/tenant-accounts/7/entries/9',
        ),
        isFalse,
      );
      expect(
        canOpenMobilePath(
          experience: WorkspaceExperience.tenant,
          capabilities: capabilities,
          path: '/portal/tenant-accounts/7/entries/9',
        ),
        isTrue,
      );
      expect(
        canOpenMobilePath(
          experience: WorkspaceExperience.tenant,
          capabilities: capabilities,
          path: '/portal/tenant-accounts/7',
        ),
        isTrue,
      );
      expect(
        canOpenMobilePath(
          experience: WorkspaceExperience.tenant,
          capabilities: capabilities,
          path: '/portal/tenant-accounts/7/entries/0',
        ),
        isFalse,
      );
      expect(
        canOpenMobilePath(
          experience: WorkspaceExperience.tenant,
          capabilities: capabilities,
          path: '/portal/tenant-accounts/page',
        ),
        isFalse,
      );
      expect(
        canOpenMobilePath(
          experience: WorkspaceExperience.tenant,
          capabilities: capabilities,
          path: '/messages/12',
        ),
        isTrue,
      );
      expect(
        canOpenMobilePath(
          experience: WorkspaceExperience.tenant,
          capabilities: capabilities,
          path: '/settings',
        ),
        isTrue,
      );
    });

    test('renders user-safe Tenant load errors with recovery', () {
      final source = File(
        'lib/features/home/home_shell.dart',
      ).readAsStringSync();
      final tenantStart = source.indexOf('class _TenantHomeTab');
      final tenantEnd = source.indexOf('class _TenantCard', tenantStart);
      final tenantShell = source.substring(tenantStart, tenantEnd);
      final repairsSource = File(
        'lib/features/portal/tenant_maintenance_screen.dart',
      ).readAsStringSync();

      expect(tenantShell, isNot(contains(r"Text('$err')")));
      expect(tenantShell, isNot(contains(r'$err')));
      expect(tenantShell, isNot(contains('error.toString()')));
      expect(tenantShell, isNot(contains('Text(widget.error')));
      expect(tenantShell, isNot(contains('ApiException(401)')));
      expect(
        tenantShell,
        isNot(contains('Session expired. Please sign in again.')),
      );

      expect(tenantShell, contains('class _TenantLoadError'));
      expect(tenantShell, contains('error.statusCode == 401'));
      expect(source, contains('restoreSession()'));
      expect(source, contains('_tenantSessionRecoveryInFlight ??='));
      expect(tenantShell, contains("We couldn't load your home."));
      expect(
        tenantShell,
        contains('Please check your connection and try again.'),
      );
      expect(tenantShell, contains("We're reconnecting your account."));
      expect(
        tenantShell,
        contains("We couldn't reconnect your account. Please try again."),
      );
      expect(
        tenantShell,
        contains("We couldn't complete that right now. Please try again."),
      );
      expect(tenantShell, contains("const Text('Try again')"));
      expect(repairsSource, contains("We couldn't load maintenance requests."));
      expect(repairsSource, contains('onRetry: _refresh'));
      expect(
        repairsSource,
        contains('ref.invalidate(tenantPortalSnapshotProvider);'),
      );
      expect(
        repairsSource,
        contains(
          'return ref.refresh('
          'tenantPortalWorkOrdersPageProvider(_request).future);',
        ),
      );
      expect(
        repairsSource,
        contains(
          'FilledButton.tonal('
          "onPressed: onRetry, child: const Text('Try again'))",
        ),
      );
    });

    test('repairs root stays a dedicated screen without nested tabs', () {
      final shell = File(
        'lib/features/home/home_shell.dart',
      ).readAsStringSync();
      final repairs = File(
        'lib/features/portal/tenant_maintenance_screen.dart',
      ).readAsStringSync();

      expect(shell, contains('const TenantMaintenanceScreen()'));
      expect(shell, isNot(contains('_TenantMaintenanceTab')));
      expect(repairs, isNot(contains('TabBar(')));
      expect(repairs, isNot(contains('DefaultTabController')));
      expect(repairs, contains("title: const Text('Maintenance')"));
      expect(repairs, contains("label: const Text('Add repair')"));
    });
  });

  group('Management navigation', () {
    test('keeps Rentals and Unit detail deep links canonical', () {
      const capabilities = {'rentals.read'};

      expect(
        canOpenMobilePath(
          experience: WorkspaceExperience.management,
          capabilities: capabilities,
          path: '/rentals',
        ),
        isTrue,
      );
      expect(
        canOpenMobilePath(
          experience: WorkspaceExperience.management,
          capabilities: capabilities,
          path: '/units/42',
        ),
        isTrue,
      );
      expect(
        canOpenMobilePath(
          experience: WorkspaceExperience.management,
          capabilities: capabilities,
          path: '/portal/tenant-accounts/7/entries/9',
        ),
        isFalse,
      );
    });
  });

  test('maintenance access exposes only assigned work', () {
    final destinations = workDestinationsFor(const {
      'maintenance.assigned-work.read',
      'maintenance.assigned-work.update',
      'maintenance.assigned-work.converse',
    }, assignedWorkExperience: true);

    expect(destinations, hasLength(1));
    expect(canOpenWorkOrders(const {'maintenance.assigned-work.read'}), isTrue);
    expect(destinations.single.id, MobileDestinationId.workOrders);
    expect(destinations.single.label, 'My work');
    expect(
      destinations.single.subtitle,
      'Repairs and maintenance assigned to you',
    );
  });

  test('leasing access exposes showing calendar without management work', () {
    const capabilities = {
      'rentals.read',
      'leasing.applications.manage',
      'leasing.showings.manage',
      'leasing.onboarding.manage',
    };
    final destinations = workDestinationsFor(
      capabilities,
      assignedWorkExperience: false,
    );

    expect(canOpenWorkHub(capabilities), isTrue);
    expect(canOpenWorkOrders(capabilities), isFalse);
    expect(destinations.map((item) => item.id), [MobileDestinationId.calendar]);
  });

  test('tenant notices require their independent capability', () {
    final destinations = workDestinationsFor(const {
      'leasing.showings.manage',
      'notifications.tenant-notices.manage',
    }, assignedWorkExperience: false);

    expect(destinations.map((item) => item.id), [
      MobileDestinationId.calendar,
      MobileDestinationId.notices,
    ]);
  });

  test('property manager sees the complete operational work set', () {
    final destinations = workDestinationsFor(const {
      'rentals.manage',
      'work.read',
      'work.manage',
      'responsibility.assign-existing-member',
      'notifications.tenant-notices.manage',
    }, assignedWorkExperience: false);

    expect(destinations.map((item) => item.id), [
      MobileDestinationId.workOrders,
      MobileDestinationId.calendar,
      MobileDestinationId.inspections,
      MobileDestinationId.vendors,
      MobileDestinationId.automations,
      MobileDestinationId.notices,
    ]);
    expect(destinations.first.label, 'Repairs');
  });
}
