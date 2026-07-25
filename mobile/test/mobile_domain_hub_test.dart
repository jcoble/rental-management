import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:material_symbols_icons/symbols.dart';
import 'package:rental_command/core/auth/auth_controller.dart';
import 'package:rental_command/core/auth/auth_models.dart';
import 'package:rental_command/features/home/mobile_destination.dart';
import 'package:rental_command/features/home/mobile_domain_chrome.dart';
import 'package:rental_command/features/home/mobile_domain_hub.dart';
import 'package:rental_command/features/home/mobile_domain_navigation.dart';
import 'package:rental_command/features/home/mobile_quick_action_fab.dart';

void main() {
  tearDown(() {
    final current = MobileShellNavigationRegistry.current;
    if (current != null) {
      MobileShellNavigationRegistry.detach(current);
    }
  });

  test('rentals destinations place units next to properties', () {
    expect(rentalDestinations.map((destination) => destination.id), [
      MobileDestinationId.properties,
      MobileDestinationId.owners,
      MobileDestinationId.units,
      MobileDestinationId.tenants,
      MobileDestinationId.leases,
      MobileDestinationId.applications,
    ]);
  });

  testWidgets('cross-tab detail opens target tab and backs to that tab root', (
    tester,
  ) async {
    await tester.pumpWidget(
      ProviderScope(
        child: MaterialApp(
          home: MobileDomainHubScreen(
            title: 'Rentals',
            subtitle: 'Test',
            destinations: [
              MobileDestination(
                id: MobileDestinationId.tenants,
                icon: Symbols.group_rounded,
                label: 'Tenants',
                subtitle: 'People',
                builder: (context) => Scaffold(
                  body: Center(
                    child: FilledButton(
                      onPressed: () {
                        MobileDomainNavigation.maybeOf(
                          context,
                        )!.openDestination(
                          MobileDestinationId.leases,
                          detailBuilder: (_) => Scaffold(
                            appBar: AppBar(title: const Text('Lease')),
                            body: const Center(child: Text('Lease detail')),
                          ),
                        );
                      },
                      child: const Text('Open lease'),
                    ),
                  ),
                ),
              ),
              MobileDestination(
                id: MobileDestinationId.leases,
                icon: Symbols.description_rounded,
                label: 'Leases',
                subtitle: 'Agreements',
                builder: (_) =>
                    const Scaffold(body: Center(child: Text('Leases root'))),
              ),
            ],
          ),
        ),
      ),
    );

    expect(find.text('Open lease'), findsOneWidget);

    await tester.tap(find.text('Open lease'));
    await tester.pumpAndSettle();

    expect(find.text('Lease detail'), findsOneWidget);

    await tester.binding.handlePopRoute();
    await tester.pumpAndSettle();

    expect(find.text('Leases root'), findsOneWidget);
    expect(find.text('Open lease'), findsNothing);
  });

  testWidgets('domain navigator pops selected detail to current root', (
    tester,
  ) async {
    MobileDomainNavigator? controller;

    await tester.pumpWidget(
      ProviderScope(
        child: MaterialApp(
          home: MobileDomainHubScreen(
            title: 'Money',
            subtitle: 'Test',
            onControllerReady: (value) => controller = value,
            destinations: [
              MobileDestination(
                id: MobileDestinationId.moneyLedger,
                icon: Symbols.receipt_long_rounded,
                label: 'Ledger',
                subtitle: 'Transactions feed',
                builder: (context) => Scaffold(
                  body: Center(
                    child: FilledButton(
                      onPressed: () {
                        MobileDomainNavigation.maybeOf(
                          context,
                        )!.openDestination(
                          MobileDestinationId.moneyLedger,
                          detailBuilder: (_) => const MobileDomainDetailHeader(
                            title: 'Payment #42',
                            subtitle: 'Paid rent',
                            child: Scaffold(
                              body: Center(child: Text('Payment detail')),
                            ),
                          ),
                        );
                      },
                      child: const Text('Open payment detail'),
                    ),
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );

    expect(controller, isNotNull);
    expect(find.text('Transactions feed'), findsOneWidget);

    await tester.tap(find.text('Open payment detail'));
    await tester.pumpAndSettle();

    expect(find.text('Payment detail'), findsOneWidget);
    expect(find.text('Payment #42'), findsOneWidget);
    expect(find.byTooltip('Back'), findsOneWidget);

    controller!.popToCurrentRoot();
    await tester.pumpAndSettle();

    expect(find.text('Payment detail'), findsNothing);
    expect(find.text('Payment #42'), findsNothing);
    expect(find.byTooltip('Back'), findsNothing);
    expect(find.text('Transactions feed'), findsOneWidget);
    expect(find.text('Open payment detail'), findsOneWidget);
  });

  testWidgets('detail header replaces the destination header above top tabs', (
    tester,
  ) async {
    await tester.pumpWidget(
      ProviderScope(
        child: MaterialApp(
          home: MobileDomainHubScreen(
            title: 'Rentals',
            subtitle: 'Test',
            destinations: [
              MobileDestination(
                id: MobileDestinationId.units,
                icon: Symbols.home_work_rounded,
                label: 'Units',
                subtitle: 'Command centers',
                builder: (context) => Scaffold(
                  body: Center(
                    child: FilledButton(
                      onPressed: () {
                        MobileDomainNavigation.maybeOf(
                          context,
                        )!.openDestination(
                          MobileDestinationId.units,
                          detailBuilder: (_) => const MobileDomainDetailHeader(
                            title: 'Unit 2',
                            subtitle: '123 Main St',
                            child: Scaffold(body: Text('Unit detail')),
                          ),
                        );
                      },
                      child: const Text('Open unit'),
                    ),
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );

    expect(find.text('Command centers'), findsOneWidget);
    expect(find.text('Units'), findsNWidgets(2));

    await tester.tap(find.text('Open unit'));
    await tester.pumpAndSettle();

    expect(find.text('Command centers'), findsNothing);
    expect(find.text('Unit 2'), findsOneWidget);
    expect(find.text('123 Main St'), findsOneWidget);
    expect(find.byTooltip('Back'), findsOneWidget);
    expect(find.text('Units'), findsNothing);

    await tester.tap(find.byTooltip('Back'));
    await tester.pumpAndSettle();

    expect(find.text('Command centers'), findsOneWidget);
    expect(find.text('Open unit'), findsOneWidget);
    expect(find.text('Units'), findsNWidgets(2));
  });

  testWidgets('embedded root app bars are hidden and hub header collapses', (
    tester,
  ) async {
    await tester.pumpWidget(
      ProviderScope(
        child: MaterialApp(
          home: MobileDomainHubScreen(
            title: 'Money',
            subtitle: 'Test',
            destinations: [
              MobileDestination(
                id: MobileDestinationId.moneyLedger,
                icon: Symbols.receipt_long_rounded,
                label: 'Ledger',
                subtitle: 'Transactions feed',
                builder: (context) => Scaffold(
                  appBar: mobileDomainRootAppBar(
                    context,
                    title: const Text('Nested Ledger'),
                  ),
                  body: ListView.builder(
                    itemCount: 40,
                    itemBuilder: (_, index) =>
                        ListTile(title: Text('Transaction $index')),
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );

    expect(find.text('Nested Ledger'), findsNothing);
    expect(find.text('Transactions feed'), findsOneWidget);
    expect(find.text('Ledger'), findsWidgets);

    await tester.drag(find.byType(ListView), const Offset(0, -420));
    await tester.pumpAndSettle();

    expect(find.text('Transactions feed'), findsNothing);
    expect(find.text('Ledger'), findsOneWidget);
  });

  testWidgets('domain hub provides quick actions when destination has no FAB', (
    tester,
  ) async {
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          authControllerProvider.overrideWith(
            () => _StaticAuthController(_authenticatedState),
          ),
        ],
        child: MaterialApp(
          home: MobileDomainHubScreen(
            title: 'Rentals',
            subtitle: 'Test',
            destinations: [
              MobileDestination(
                id: MobileDestinationId.units,
                icon: Symbols.home_work_rounded,
                label: 'Units',
                subtitle: 'Command centers',
                builder: (_) =>
                    const Scaffold(body: Center(child: Text('Units root'))),
              ),
            ],
          ),
        ),
      ),
    );

    await tester.pump();

    expect(find.text('Units root'), findsOneWidget);
    expect(find.byTooltip('Scan / Add'), findsOneWidget);

    await tester.tap(find.byTooltip('Scan / Add'));
    await tester.pumpAndSettle();

    expect(find.text('Assistant'), findsOneWidget);
    expect(find.text('Record'), findsOneWidget);
    expect(find.text('Scan / Add'), findsOneWidget);
  });

  testWidgets('domain hub does not duplicate destination quick action FABs', (
    tester,
  ) async {
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          authControllerProvider.overrideWith(
            () => _StaticAuthController(_authenticatedState),
          ),
        ],
        child: MaterialApp(
          home: MobileDomainHubScreen(
            title: 'Work',
            subtitle: 'Test',
            destinations: [
              MobileDestination(
                id: MobileDestinationId.workOrders,
                icon: Symbols.build_rounded,
                label: 'Work orders',
                subtitle: 'Open tickets',
                builder: (_) => Scaffold(
                  body: const Center(child: Text('Work orders root')),
                  floatingActionButton: MobileQuickActionFab(
                    primaryAction: MobileQuickAction(
                      label: 'New work order',
                      icon: Icons.add,
                      onPressed: () {},
                    ),
                    onChat: () {},
                    onRecord: () {},
                    onScan: () {},
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );

    await tester.pump();

    expect(find.text('Work orders root'), findsOneWidget);
    expect(find.byTooltip('Scan / Add'), findsOneWidget);

    await tester.tap(find.byTooltip('Scan / Add'));
    await tester.pumpAndSettle();

    expect(find.text('New work order'), findsOneWidget);
    expect(find.text('Assistant'), findsOneWidget);
    expect(find.text('Record'), findsOneWidget);
    expect(find.text('Scan / Add'), findsOneWidget);
  });

  testWidgets('browse destinations prefer registered shell tabs', (
    tester,
  ) async {
    final calls = <({MobileShellTabId tab, MobileDestinationId destination})>[];
    final shellNavigator = MobileShellNavigator(
      openTab: (tab, {destination, detailBuilder}) {
        calls.add((tab: tab, destination: destination!));
      },
      openRoute: (_) => false,
    );
    MobileShellNavigationRegistry.attach(shellNavigator);

    await tester.pumpWidget(
      MaterialApp(
        home: Builder(
          builder: (context) => Scaffold(
            body: Center(
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  FilledButton(
                    onPressed: () {
                      const MobileDestination(
                        id: MobileDestinationId.payments,
                        icon: Symbols.receipt_long_rounded,
                        label: 'Payments',
                        subtitle: 'Standalone fallback',
                        builder: _standaloneBuilder,
                      ).open(context);
                    },
                    child: const Text('Open payments'),
                  ),
                  FilledButton(
                    onPressed: () {
                      const MobileDestination(
                        id: MobileDestinationId.units,
                        icon: Symbols.home_work_rounded,
                        label: 'Units',
                        subtitle: 'Standalone fallback',
                        builder: _standaloneBuilder,
                      ).open(context);
                    },
                    child: const Text('Open units'),
                  ),
                  FilledButton(
                    onPressed: () {
                      const MobileDestination(
                        id: MobileDestinationId.owners,
                        icon: Symbols.account_balance_rounded,
                        label: 'Owners',
                        subtitle: 'Standalone fallback',
                        builder: _standaloneBuilder,
                      ).open(context);
                    },
                    child: const Text('Open owners'),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );

    await tester.tap(find.text('Open payments'));
    await tester.pumpAndSettle();

    expect(calls, [
      (
        tab: MobileShellTabId.money,
        destination: MobileDestinationId.moneyLedger,
      ),
    ]);
    expect(find.text('Standalone payments'), findsNothing);

    await tester.tap(find.text('Open units'));
    await tester.pumpAndSettle();

    expect(calls, [
      (
        tab: MobileShellTabId.money,
        destination: MobileDestinationId.moneyLedger,
      ),
      (tab: MobileShellTabId.rentals, destination: MobileDestinationId.units),
    ]);

    await tester.tap(find.text('Open owners'));
    await tester.pumpAndSettle();

    expect(calls, [
      (
        tab: MobileShellTabId.money,
        destination: MobileDestinationId.moneyLedger,
      ),
      (tab: MobileShellTabId.rentals, destination: MobileDestinationId.units),
      (tab: MobileShellTabId.rentals, destination: MobileDestinationId.owners),
    ]);
  });
}

Widget _standaloneBuilder(BuildContext context) {
  return const Scaffold(body: Text('Standalone payments'));
}

final _authenticatedState = AuthStateAuthenticated(
  const AuthUser(
    id: 1,
    email: 'test@example.test',
    displayName: 'Test user',
    emailVerified: true,
  ),
  const AccessEnvelope(
    identity: AccessIdentity(userId: 1, displayName: 'Test user'),
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
        capabilityKeys: [
          'money.expenses.manage',
          'rentals.manage',
          'reports.read',
        ],
      ),
    ],
  ),
  activeExperience: WorkspaceExperience.management,
);

class _StaticAuthController extends AuthController {
  _StaticAuthController(this.initialState);

  final AuthState initialState;

  @override
  AuthState build() => initialState;
}
