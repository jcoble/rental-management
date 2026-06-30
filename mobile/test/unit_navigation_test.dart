import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/models/unit.dart';
import 'package:rental_command/features/home/mobile_domain_navigation.dart';
import 'package:rental_command/features/units/unit_command_center_screen.dart';
import 'package:rental_command/features/units/unit_navigation.dart';
import 'package:rental_command/features/units/units_repository.dart';

void main() {
  tearDown(() {
    final current = MobileShellNavigationRegistry.current;
    if (current != null) {
      MobileShellNavigationRegistry.detach(current);
    }
  });

  testWidgets('unit navigation targets the rentals units tab', (tester) async {
    final calls =
        <
          ({
            MobileShellTabId tab,
            MobileDestinationId? destination,
            MobileDetailBuilder? detailBuilder,
          })
        >[];
    late BuildContext buttonContext;

    await tester.pumpWidget(
      MaterialApp(
        home: MobileShellNavigation(
          controller: MobileShellNavigator(
            openTab: (tab, {destination, detailBuilder}) {
              calls.add((
                tab: tab,
                destination: destination,
                detailBuilder: detailBuilder,
              ));
            },
            openRoute: (_) => false,
          ),
          child: Builder(
            builder: (context) {
              buttonContext = context;
              return Scaffold(
                body: Center(
                  child: FilledButton(
                    onPressed: () => openUnitCommandCenter(
                      context,
                      unitId: 42,
                      initialTab: UnitCommandCenterTab.lease,
                    ),
                    child: const Text('Open unit lease'),
                  ),
                ),
              );
            },
          ),
        ),
      ),
    );

    await tester.tap(find.text('Open unit lease'));
    await tester.pumpAndSettle();

    expect(calls, hasLength(1));
    expect(calls.single.tab, MobileShellTabId.rentals);
    expect(calls.single.destination, MobileDestinationId.units);
    expect(calls.single.detailBuilder, isNotNull);

    final detail = calls.single.detailBuilder!(buttonContext);
    expect(detail, isA<UnitCommandCenterLoaderScreen>());
    final loader = detail as UnitCommandCenterLoaderScreen;
    expect(loader.unitId, 42);
    expect(loader.initialTab, UnitCommandCenterTab.lease);
  });

  test('unit href parser maps tab aliases to command center tabs', () {
    final lease = parseUnitCommandCenterRoute('/units/42?tab=lease');
    expect(lease?.unitId, 42);
    expect(lease?.initialTab, UnitCommandCenterTab.lease);

    final maintenance = parseUnitCommandCenterRoute(
      '/units/42?tab=maintenance',
    );
    expect(maintenance?.unitId, 42);
    expect(maintenance?.initialTab, UnitCommandCenterTab.work);

    final apps = parseUnitCommandCenterRoute('/units/42?tab=apps');
    expect(apps?.initialTab, UnitCommandCenterTab.applications);

    final listing = parseUnitCommandCenterRoute('/units/42?tab=listing');
    expect(listing?.initialTab, UnitCommandCenterTab.listing);

    expect(parseUnitCommandCenterRoute('/units/0?tab=lease'), isNull);
    expect(parseUnitCommandCenterRoute('/work-orders/42'), isNull);
  });

  testWidgets('unit next action href opens the target unit sub-tab', (
    tester,
  ) async {
    final calls =
        <
          ({
            MobileShellTabId tab,
            MobileDestinationId? destination,
            MobileDetailBuilder? detailBuilder,
          })
        >[];
    await tester.pumpWidget(
      ProviderScope(
        child: MaterialApp(
          home: MobileShellNavigation(
            controller: MobileShellNavigator(
              openTab: (tab, {destination, detailBuilder}) {
                calls.add((
                  tab: tab,
                  destination: destination,
                  detailBuilder: detailBuilder,
                ));
              },
              openRoute: (_) => false,
            ),
            child: Builder(
              builder: (context) {
                return UnitCommandCenterScreen(
                  dashboard: _unitDashboard(
                    nextBestAction: const UnitNextBestAction(
                      label: 'Review lease',
                      href: '/units/42?tab=lease',
                    ),
                  ),
                );
              },
            ),
          ),
        ),
      ),
    );

    await tester.tap(find.text('Review lease'));
    await tester.pumpAndSettle();

    expect(calls, isEmpty);
    expect(find.text('No lease'), findsOneWidget);
    expect(find.text('This unit has no current lease.'), findsOneWidget);
  });
}

UnitDashboard _unitDashboard({required UnitNextBestAction nextBestAction}) {
  return UnitDashboard(
    unit: Unit(
      id: 42,
      propertyId: 7,
      unitNumber: '4B',
      bedrooms: 2,
      bathrooms: 1,
      marketRent: 1400,
      status: 'Occupied',
      createdAt: DateTime(2026),
      updatedAt: DateTime(2026),
    ),
    propertyName: 'Maple Ridge',
    lifecycleStage: 'Active',
    nextBestAction: nextBestAction,
    header: const UnitDashboardHeader(
      rentState: 'Current',
      outstandingRentBalance: 0,
      openWorkOrderCount: 0,
      docsNeedingReviewCount: 0,
    ),
    overview: const UnitDashboardOverview(
      recentPayments: [],
      openWorkOrders: [],
      pendingDocs: [],
      upcomingAppointments: [],
    ),
  );
}
