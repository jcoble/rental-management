import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:material_symbols_icons/symbols.dart';
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

    await tester.tap(find.text('Open unit'));
    await tester.pumpAndSettle();

    expect(find.text('Command centers'), findsNothing);
    expect(find.text('Unit 2'), findsOneWidget);
    expect(find.text('123 Main St'), findsOneWidget);
    expect(find.byTooltip('Back'), findsOneWidget);

    await tester.tap(find.byTooltip('Back'));
    await tester.pumpAndSettle();

    expect(find.text('Command centers'), findsOneWidget);
    expect(find.text('Open unit'), findsOneWidget);
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
    expect(find.byTooltip('Open quick actions'), findsOneWidget);

    await tester.tap(find.byTooltip('Open quick actions'));
    await tester.pumpAndSettle();

    expect(find.text('Chat'), findsOneWidget);
    expect(find.text('Record'), findsOneWidget);
    expect(find.text('Scan'), findsOneWidget);
  });

  testWidgets('domain hub does not duplicate destination quick action FABs', (
    tester,
  ) async {
    await tester.pumpWidget(
      ProviderScope(
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
    expect(find.byTooltip('Open quick actions'), findsOneWidget);

    await tester.tap(find.byTooltip('Open quick actions'));
    await tester.pumpAndSettle();

    expect(find.text('New work order'), findsOneWidget);
    expect(find.text('Chat'), findsOneWidget);
    expect(find.text('Record'), findsOneWidget);
    expect(find.text('Scan'), findsOneWidget);
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
  });
}

Widget _standaloneBuilder(BuildContext context) {
  return const Scaffold(body: Text('Standalone payments'));
}
