import 'dart:async';
import 'dart:io';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:material_symbols_icons/symbols.dart';
import 'package:rental_command/core/api/api_exception.dart';
import 'package:rental_command/core/auth/auth_controller.dart';
import 'package:rental_command/core/auth/auth_models.dart';
import 'package:rental_command/core/models/lease.dart';
import 'package:rental_command/core/models/unit.dart';
import 'package:rental_command/core/navigation/mobile_restoration_state.dart';
import 'package:rental_command/core/theme/app_theme.dart';
import 'package:rental_command/features/activity/activity_repository.dart';
import 'package:rental_command/features/applications/application_detail_screen.dart';
import 'package:rental_command/features/applications/applications_models.dart';
import 'package:rental_command/features/applications/applications_repository.dart';
import 'package:rental_command/features/home/mobile_destination.dart';
import 'package:rental_command/features/home/mobile_domain_hub.dart';
import 'package:rental_command/features/home/mobile_domain_navigation.dart';
import 'package:rental_command/features/leases/lease_detail_screen.dart';
import 'package:rental_command/features/leases/leases_repository.dart';
import 'package:rental_command/features/money/expense_models.dart';
import 'package:rental_command/features/money/money_repository.dart';
import 'package:rental_command/features/units/unit_command_center_screen.dart';
import 'package:rental_command/features/units/unit_navigation.dart';
import 'package:rental_command/features/units/units_list_screen.dart';
import 'package:rental_command/features/units/units_repository.dart';

void main() {
  tearDown(() {
    final current = MobileShellNavigationRegistry.current;
    if (current != null) {
      MobileShellNavigationRegistry.detach(current);
    }
  });

  testWidgets(
    'Units list restores scroll after async loading attaches paged ListView',
    (tester) async {
      tester.view.physicalSize = const Size(430, 900);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      final page = Completer<UnitHealthPage>();
      final restorationKey = GlobalKey<_UnitsListRestorationHarnessState>();

      Widget app() => ProviderScope(
        overrides: [
          unitHealthPageProvider.overrideWith((ref, query) => page.future),
        ],
        child: MaterialApp(
          restorationScopeId: 'app',
          home: _UnitsListRestorationHarness(
            key: restorationKey,
            child: const UnitsListScreen(),
          ),
        ),
      );

      await tester.pumpWidget(app());
      await tester.pump();
      restorationKey.currentState!.controller.value =
          const MobileRestorationState().updateCollection(
            sort: 'propertyName',
            skip: 0,
            scrollOffset: 360,
          );
      await tester.pump();
      restorationKey.currentState!.attachChild();
      await tester.pump();

      expect(find.byType(CircularProgressIndicator), findsOneWidget);
      expect(find.byType(ListView), findsNothing);

      await tester.restartAndRestore();
      await tester.pump();

      expect(restorationKey.currentState!.controller.value.scrollOffset, 360);
      restorationKey.currentState!.attachChild();
      await tester.pump();

      expect(find.byType(CircularProgressIndicator), findsOneWidget);
      expect(find.byType(ListView), findsNothing);

      page.complete(
        UnitHealthPage(
          items: List.generate(
            30,
            (index) => UnitHealth(
              id: index + 1,
              propertyId: 8,
              propertyName: 'Hilliard Duplex',
              unitNumber: '${index + 1}',
              status: 'Occupied',
              marketRent: 825,
              openWorkOrderCount: 0,
              docsNeedingReviewCount: 0,
              simpleStage: 'Active',
            ),
          ),
          totalCount: 30,
          skip: 0,
          take: 20,
        ),
      );
      await tester.pump();
      await tester.pump();

      final list = tester.widget<ListView>(find.byType(ListView));
      expect(list.controller!.offset, 360);

      list.controller!.jumpTo(80);
      await tester.pump();
      expect(restorationKey.currentState!.controller.value.scrollOffset, 80);

      tester.view.physicalSize = const Size(431, 900);
      await tester.pump();

      expect(list.controller!.offset, 80);
    },
  );

  testWidgets(
    'Unit Applications queries server and restores list state after detail',
    (tester) async {
      tester.view.physicalSize = const Size(430, 900);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      final repository = _UnitApplicationsWorkflowRepository();
      await tester.pumpWidget(
        ProviderScope(
          retry: (_, _) => null,
          overrides: [
            applicationsRepositoryProvider.overrideWithValue(repository),
          ],
          child: MaterialApp(
            home: UnitCommandCenterScreen(
              dashboard: _unitDashboard(
                nextBestAction: const UnitNextBestAction(
                  label: 'Review applications',
                  href: '/units/19?tab=leasing&view=applications',
                ),
                unitId: 19,
                unitNumber: 'Left',
                propertyName: 'Hilliard Duplex',
              ),
              initialTab: UnitCommandCenterTab.leasing,
              initialView: UnitCommandCenterView.applications,
            ),
          ),
        ),
      );
      await tester.pump();

      expect(find.byType(CircularProgressIndicator), findsOneWidget);
      expect(repository.queries.single.unitId, 19);
      expect(repository.queries.single.skip, 0);
      expect(repository.queries.single.search, isNull);
      expect(repository.queries.single.sort, '-submittedAt');

      repository.initialPage.complete(
        _applicationPage(
          items: [_application(id: 701, firstName: 'Initial')],
          totalCount: 21,
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Previous'), findsOneWidget);
      expect(find.text('Page 1 · 21 total').hitTestable(), findsOneWidget);
      expect(find.text('Next').hitTestable(), findsOneWidget);
      await tester.tap(find.text('Next'));
      await tester.pumpAndSettle();
      expect(repository.queries.last.unitId, 19);
      expect(repository.queries.last.skip, 20);
      expect(find.text('Page Two Applicant'), findsOneWidget);

      expect(find.text('Previous').hitTestable(), findsOneWidget);
      expect(find.text('Page 2 · 21 total').hitTestable(), findsOneWidget);
      expect(find.text('Next'), findsOneWidget);
      await tester.tap(find.text('Previous'));
      await tester.pumpAndSettle();

      await tester.enterText(
        find.byKey(const ValueKey('unit-applications-search')),
        'Ada',
      );
      await tester.testTextInput.receiveAction(TextInputAction.search);
      await tester.pumpAndSettle();

      expect(repository.queries.last.unitId, 19);
      expect(repository.queries.last.search, 'Ada');
      expect(repository.queries.last.skip, 0);
      expect(find.text('Applications unavailable'), findsOneWidget);

      await tester.tap(find.text('Retry'));
      await tester.pumpAndSettle();
      expect(
        repository.queries.where((query) => query.search == 'Ada'),
        hasLength(2),
      );
      expect(find.text('No applications'), findsOneWidget);
      expect(find.text('No applications match "Ada".'), findsOneWidget);

      await tester.tap(find.byKey(const ValueKey('unit-applications-sort')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Oldest').last);
      await tester.pumpAndSettle();

      expect(repository.queries.last.unitId, 19);
      expect(repository.queries.last.search, 'Ada');
      expect(repository.queries.last.sort, 'submittedAt');
      expect(repository.queries.last.skip, 0);
      expect(find.text('Ada Applicant'), findsOneWidget);

      await tester.tap(find.byKey(const ValueKey('unit-application-703')));
      await tester.pumpAndSettle();

      expect(find.byType(ApplicationDetailScreen), findsOneWidget);
      expect(repository.detailIds, [703]);
      expect(find.text('Back to applications'), findsOneWidget);

      await tester.tap(find.text('Back to applications'));
      await tester.pumpAndSettle();

      expect(find.byType(ApplicationDetailScreen), findsNothing);
      expect(find.text('Ada Applicant'), findsOneWidget);
      expect(
        tester
            .widget<TextField>(
              find.byKey(const ValueKey('unit-applications-search')),
            )
            .controller!
            .text,
        'Ada',
      );
      expect(
        tester
            .widget<DropdownButton<String>>(
              find.byKey(const ValueKey('unit-applications-sort')),
            )
            .value,
        'submittedAt',
      );
      expect(repository.queries.last.skip, 0);
    },
  );

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
                      initialTab: UnitCommandCenterTab.tenantLease,
                      initialView: UnitCommandCenterView.agreements,
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
    expect(loader.initialTab, UnitCommandCenterTab.tenantLease);
    expect(loader.initialView, UnitCommandCenterView.agreements);
  });

  test(
    'unit href parser accepts only canonical command center destinations',
    () {
      final lease = parseUnitCommandCenterRoute(
        '/units/42?tab=tenant-lease&view=agreements',
      );
      expect(lease?.unitId, 42);
      expect(lease?.initialTab, UnitCommandCenterTab.tenantLease);
      expect(lease?.initialView, UnitCommandCenterView.agreements);

      final maintenance = parseUnitCommandCenterRoute(
        '/units/42?tab=maintenance',
      );
      expect(maintenance?.unitId, 42);
      expect(maintenance?.initialTab, UnitCommandCenterTab.maintenance);
      expect(maintenance?.initialView, UnitCommandCenterView.workOrders);

      final turnover = parseUnitCommandCenterRoute(
        '/units/42?tab=maintenance&view=turnover',
      );
      expect(turnover?.initialTab, UnitCommandCenterTab.maintenance);
      expect(turnover?.initialView, UnitCommandCenterView.turnover);

      for (final view in ['inspections', 'recurring']) {
        final target = parseUnitCommandCenterRoute(
          '/units/42?tab=maintenance&view=$view',
        );
        expect(target?.initialTab, UnitCommandCenterTab.maintenance);
        expect(
          target?.initialView,
          view == 'inspections'
              ? UnitCommandCenterView.inspections
              : UnitCommandCenterView.recurring,
        );
      }

      final apps = parseUnitCommandCenterRoute(
        '/units/42?tab=leasing&view=applications',
      );
      expect(apps?.initialTab, UnitCommandCenterTab.leasing);
      expect(apps?.initialView, UnitCommandCenterView.applications);

      final listing = parseUnitCommandCenterRoute(
        '/units/42?tab=leasing&view=listing',
      );
      expect(listing?.initialTab, UnitCommandCenterTab.leasing);
      expect(listing?.initialView, UnitCommandCenterView.listing);

      final ledger = parseUnitCommandCenterRoute('/units/42?tab=money');
      expect(ledger?.initialTab, UnitCommandCenterTab.money);

      for (final removedAlias in ['lease', 'apps', 'rent', 'make-ready']) {
        final fallback = parseUnitCommandCenterRoute(
          '/units/42?tab=$removedAlias',
        );
        expect(fallback?.initialTab, UnitCommandCenterTab.summary);
        expect(fallback?.initialView, isNull);
      }

      expect(parseUnitCommandCenterRoute('/units/0?tab=tenant-lease'), isNull);
      expect(parseUnitCommandCenterRoute('/maintenance/42'), isNull);
    },
  );

  test(
    'unit dashboard parses turnover summary and defaults missing summary',
    () {
      final dashboard = UnitDashboard.fromJson({
        ..._unitDashboardJson(),
        'turnover': {
          'status': 'InProgress',
          'totalTaskCount': 3,
          'openTaskCount': 1,
          'completedTaskCount': 2,
          'receiptCount': 2,
          'estimatedCost': 300,
          'actualCost': 220,
          'startedAt': '2026-06-01T00:00:00Z',
          'targetReadyDate': '2026-06-10T00:00:00Z',
          'lastActivityAt': '2026-06-05T00:00:00Z',
          'daysInTurnover': 4,
        },
      });

      expect(dashboard.turnover.status, 'InProgress');
      expect(dashboard.turnover.openTaskCount, 1);
      expect(dashboard.turnover.actualCost, 220);
      expect(dashboard.turnover.targetReadyDate, isNotNull);

      final legacyDashboard = UnitDashboard.fromJson(_unitDashboardJson());
      expect(legacyDashboard.turnover.status, 'NotStarted');
      expect(legacyDashboard.turnover.totalTaskCount, 0);
    },
  );

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
                      href: '/units/42?tab=tenant-lease&view=agreements',
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
    expect(find.text('No tenant relationship'), findsOneWidget);
    expect(
      find.text(
        'Approve an application and prepare move-in, or scan an existing signed agreement.',
      ),
      findsOneWidget,
    );
  });

  test('Unit destinations keep one top-level tab surface', () {
    final source = File(
      'lib/features/units/unit_command_center_screen.dart',
    ).readAsStringSync();

    expect(
      RegExp(r'const unitTabs = TabBar\(').allMatches(source),
      hasLength(1),
    );
    expect(
      RegExp(r'final tabView = TabBarView\(').allMatches(source),
      hasLength(1),
    );
    expect(source, isNot(contains('class _UnitAreaTabs')));
    expect(RegExp(r'_UnitAreaSurface\(').allMatches(source), hasLength(4));
  });

  testWidgets(
    'Tenant & lease uses one top-level tab bar and exposes Agreement and Residents together',
    (tester) async {
      await tester.pumpWidget(
        ProviderScope(
          child: MaterialApp(
            home: UnitCommandCenterScreen(
              dashboard: _unitDashboard(
                nextBestAction: const UnitNextBestAction(
                  label: 'Review lease',
                  href: '/units/42?tab=tenant-lease&view=agreements',
                ),
              ),
              initialTab: UnitCommandCenterTab.tenantLease,
              initialView: UnitCommandCenterView.residents,
            ),
          ),
        ),
      );

      expect(find.byType(TabBar), findsOneWidget);
      expect(find.text('Agreement'), findsOneWidget);
      expect(find.text('Residents'), findsOneWidget);
      expect(find.text('This unit is currently vacant.'), findsOneWidget);
      expect(find.text('No tenant relationship'), findsOneWidget);
    },
  );

  testWidgets(
    'Documents & history lets the destination scroll from Documents to History',
    (tester) async {
      tester.view.physicalSize = const Size(320, 640);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            activityRepositoryProvider.overrideWithValue(
              _UnitNavigationActivityRepository(),
            ),
          ],
          child: MaterialApp(
            home: UnitCommandCenterScreen(
              dashboard: _unitDashboard(
                nextBestAction: const UnitNextBestAction(
                  label: 'Review documents',
                  href: '/units/42?tab=documents-history&view=documents',
                ),
              ),
              initialTab: UnitCommandCenterTab.documentsHistory,
              initialView: UnitCommandCenterView.documents,
            ),
          ),
        ),
      );
      await tester.pump();
      await tester.pump();

      expect(find.byType(TabBar), findsOneWidget);
      expect(find.byType(TabBarView), findsOneWidget);
      expect(find.text('Documents'), findsWidgets);
      expect(find.text('Scan document').hitTestable(), findsOneWidget);
      expect(find.text('History'), findsOneWidget);

      await tester.drag(find.text('Scan document'), const Offset(0, -180));
      await tester.pumpAndSettle();

      expect(find.text('Unit history').hitTestable(), findsOneWidget);
      expect(find.text('Scan document'), findsOneWidget);
    },
  );

  testWidgets(
    'Tenant & lease replaces relationship render state when the management id changes',
    (tester) async {
      final overrides = [
        leaseManagementDetailProvider.overrideWith(
          (ref, id) => Completer<LeaseManagementDetail>().future,
        ),
        leaseAgreementHistoryProvider.overrideWith(
          (ref, id) => Completer<LeaseAgreementHistoryPage>().future,
        ),
      ];

      Widget app(int leaseManagementId) => ProviderScope(
        overrides: overrides,
        child: MaterialApp(
          home: UnitCommandCenterScreen(
            dashboard: _unitDashboard(
              nextBestAction: const UnitNextBestAction(
                label: 'Review lease',
                href: '/units/42?tab=tenant-lease&view=agreements',
              ),
            ),
            initialTab: UnitCommandCenterTab.tenantLease,
            initialView: UnitCommandCenterView.agreements,
            initialLeaseManagementId: leaseManagementId,
          ),
        ),
      );

      await tester.pumpWidget(app(17));
      final firstRelationship = tester.element(
        find.byType(LeaseManagementDetailScreen),
      );

      await tester.pumpWidget(app(1));
      final secondRelationship = tester.element(
        find.byType(LeaseManagementDetailScreen),
      );

      expect(identical(firstRelationship, secondRelationship), isFalse);
    },
  );

  testWidgets(
    'management 17 paints Tenant & lease before and after a Money tab cycle',
    (tester) async {
      tester.view.physicalSize = const Size(430, 900);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      final pageStorage = PageStorageBucket();
      await tester.pumpWidget(
        MaterialApp(
          home: PageStorage(
            bucket: pageStorage,
            child: Scaffold(
              body: ListView(
                key: const PageStorageKey<int>(17),
                children: List.generate(
                  80,
                  (index) => SizedBox(
                    height: 100,
                    child: Text('Stale relationship row $index'),
                  ),
                ),
              ),
            ),
          ),
        ),
      );
      await tester.drag(find.byType(ListView), const Offset(0, -7000));
      await tester.pumpAndSettle();

      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            leaseManagementDetailProvider.overrideWith(
              (ref, id) async => _management17Detail(),
            ),
            leaseAgreementHistoryProvider.overrideWith(
              (ref, id) async => const LeaseAgreementHistoryPage(items: []),
            ),
            leaseHouseholdContextProvider.overrideWith(
              (ref, id) async => const ReturnPossessionContext(
                parties: [],
                activeTenantUserAccesses: [],
              ),
            ),
            leaseAddendumHistoryProvider.overrideWith(
              (ref, query) async => const LeaseAddendumHistoryPage(
                items: [],
                totalCount: 0,
                skip: 0,
                take: 10,
              ),
            ),
            unitExpensesProvider.overrideWith(
              (ref, unitId) async => const <Expense>[],
            ),
          ],
          child: MaterialApp(
            home: PageStorage(
              bucket: pageStorage,
              child: UnitCommandCenterScreen(
                dashboard: _unitDashboard(
                  nextBestAction: const UnitNextBestAction(
                    label: 'Review residents',
                    href: '/units/19?tab=tenant-lease&view=residents',
                  ),
                  unitId: 19,
                  unitNumber: 'Left',
                  propertyName: 'Hilliard Duplex',
                  leaseManagementId: 17,
                  tenantAccountId: 17,
                  currentTenants: const [
                    UnitTenantSummary(id: 17, name: 'Resident 17'),
                  ],
                ),
                initialTab: UnitCommandCenterTab.tenantLease,
                initialView: UnitCommandCenterView.residents,
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Residents').hitTestable(), findsOneWidget);
      expect(find.text('No governing agreement').hitTestable(), findsOneWidget);

      await tester.tap(find.text('Money'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Tenant & lease'));
      await tester.pumpAndSettle();

      expect(find.text('Residents').hitTestable(), findsOneWidget);
      expect(find.text('No governing agreement').hitTestable(), findsOneWidget);
    },
  );

  for (final fixture in [
    (
      label: 'ACTIVE-017',
      unitId: 19,
      managementId: 17,
      accountId: 17,
      unitNumber: 'Left',
      propertyName: 'Hilliard Duplex',
      agreement: false,
    ),
    (
      label: 'ACTIVE-001',
      unitId: 1,
      managementId: 1,
      accountId: 1,
      unitNumber: '1',
      propertyName: 'Demo Apartments',
      agreement: true,
    ),
  ]) {
    testWidgets(
      '${fixture.label} paints deferred Tenant & lease content through the real domain detail shell',
      (tester) async {
        tester.view.physicalSize = const Size(320, 640);
        tester.view.devicePixelRatio = 1;
        addTearDown(tester.view.resetPhysicalSize);
        addTearDown(tester.view.resetDevicePixelRatio);

        final dashboardCompleter = Completer<UnitDashboard>();
        final managementCompleter = Completer<LeaseManagementDetail>();
        final agreementCompleter = Completer<LeaseAgreementHistoryPage>();
        MobileDomainNavigator? domainNavigator;

        await tester.pumpWidget(
          ProviderScope(
            overrides: [
              authControllerProvider.overrideWith(
                () => _StaticAuthController(_managementWorkAuthority()),
              ),
              unitDashboardProvider.overrideWith(
                (ref, unitId) => dashboardCompleter.future,
              ),
              leaseManagementDetailProvider.overrideWith(
                (ref, id) => managementCompleter.future,
              ),
              leaseAgreementHistoryProvider.overrideWith(
                (ref, id) => agreementCompleter.future,
              ),
              leaseHouseholdContextProvider.overrideWith(
                (ref, id) async => const ReturnPossessionContext(
                  parties: [],
                  activeTenantUserAccesses: [],
                ),
              ),
              leaseAddendumHistoryProvider.overrideWith(
                (ref, query) async => const LeaseAddendumHistoryPage(
                  items: [],
                  totalCount: 0,
                  skip: 0,
                  take: 10,
                ),
              ),
              unitExpensesProvider.overrideWith(
                (ref, unitId) async => const <Expense>[],
              ),
            ],
            child: MaterialApp(
              theme: AppTheme.dark,
              home: MobileDomainHubScreen(
                title: 'Rentals',
                subtitle: 'Test',
                onControllerReady: (value) => domainNavigator = value,
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

        domainNavigator!.openDestination(
          MobileDestinationId.units,
          detailBuilder: (_) =>
              UnitCommandCenterLoaderScreen(unitId: fixture.unitId),
        );
        await tester.pump();
        await tester.pump();
        expect(find.byType(CircularProgressIndicator), findsOneWidget);

        dashboardCompleter.complete(
          _unitDashboard(
            nextBestAction: const UnitNextBestAction(
              label: 'Review residents',
              href: '/units/19?tab=tenant-lease&view=residents',
            ),
            unitId: fixture.unitId,
            unitNumber: fixture.unitNumber,
            propertyName: fixture.propertyName,
            leaseManagementId: fixture.managementId,
            tenantAccountId: fixture.accountId,
            currentTenants: [
              UnitTenantSummary(
                id: fixture.accountId,
                name: 'Resident ${fixture.accountId}',
              ),
            ],
          ),
        );
        await tester.pump();
        await tester.pump();
        expect(find.text('Summary'), findsOneWidget);

        final tenantLeaseTab = find.text('Tenant & lease');
        await tester.ensureVisible(tenantLeaseTab);
        await tester.pumpAndSettle();
        expect(tenantLeaseTab.hitTestable(), findsOneWidget);
        await tester.tap(tenantLeaseTab);
        await tester.pump();
        await tester.pump(const Duration(milliseconds: 450));
        final settledTabController = DefaultTabController.of(
          tester.element(find.byType(TabBar).hitTestable()),
        );
        expect(settledTabController.indexIsChanging, isFalse);
        expect(
          settledTabController.animation!.value,
          closeTo(UnitCommandCenterTab.tenantLease.index.toDouble(), 0.001),
        );
        managementCompleter.complete(
          _managementDetail(
            managementId: fixture.managementId,
            accountId: fixture.accountId,
            unitId: fixture.unitId,
            unitNumber: fixture.unitNumber,
            propertyName: fixture.propertyName,
            hasAgreement: fixture.agreement,
          ),
        );
        agreementCompleter.complete(
          LeaseAgreementHistoryPage(
            items: fixture.agreement ? [_executedAgreement()] : const [],
          ),
        );
        await tester.pumpAndSettle();

        final agreementState = fixture.agreement
            ? find.text('Executed PDF')
            : find.text('No governing agreement');
        Future<void> expectPaintedTenantLeaseSurface() async {
          final visibleTabBar = find.byType(TabBar).hitTestable();
          expect(visibleTabBar, findsOneWidget);
          final tabController = DefaultTabController.of(
            tester.element(visibleTabBar),
          );
          expect(tabController.index, UnitCommandCenterTab.tenantLease.index);
          expect(tabController.indexIsChanging, isFalse);
          expect(
            tabController.animation!.value,
            closeTo(UnitCommandCenterTab.tenantLease.index.toDouble(), 0.001),
          );

          final visibleTabView = find.byType(TabBarView).hitTestable();
          expect(visibleTabView, findsOneWidget);
          final viewport = tester.getSize(visibleTabView);
          expect(viewport.width, 320);
          expect(viewport.height, greaterThan(0));

          final leaseScrollable = find.descendant(
            of: find.byType(LeaseManagementDetailScreen),
            matching: find.byType(Scrollable),
          );
          expect(leaseScrollable, findsOneWidget);

          final agreementHeading = find.text('Agreement');
          await tester.scrollUntilVisible(
            agreementHeading,
            -200,
            scrollable: leaseScrollable,
          );
          expect(agreementHeading.hitTestable(), findsOneWidget);

          await tester.scrollUntilVisible(
            agreementState,
            200,
            scrollable: leaseScrollable,
          );
          final agreementTarget = fixture.agreement
              ? find.ancestor(
                  of: agreementState,
                  matching: find.byType(OutlinedButton),
                )
              : agreementState;
          await tester.ensureVisible(agreementTarget);
          await tester.pumpAndSettle();
          expect(agreementTarget.hitTestable(), findsOneWidget);

          final residentsHeading = find.text('Residents');
          await tester.scrollUntilVisible(
            residentsHeading,
            200,
            scrollable: leaseScrollable,
          );
          expect(residentsHeading.hitTestable(), findsOneWidget);
        }

        await expectPaintedTenantLeaseSurface();

        final moneyTab = find.text('Money');
        await tester.ensureVisible(moneyTab);
        await tester.pumpAndSettle();
        expect(moneyTab.hitTestable(), findsOneWidget);
        await tester.tap(moneyTab);
        await tester.pumpAndSettle();
        await tester.ensureVisible(tenantLeaseTab);
        await tester.pumpAndSettle();
        expect(tenantLeaseTab.hitTestable(), findsOneWidget);
        await tester.tap(tenantLeaseTab);
        await tester.pumpAndSettle();

        await expectPaintedTenantLeaseSurface();

        domainNavigator!.popToCurrentRoot();
        await tester.pumpAndSettle();
        expect(find.text('Units root'), findsOneWidget);
      },
    );
  }

  testWidgets('unit turnover tab shows turnover summary and task controls', (
    tester,
  ) async {
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          authControllerProvider.overrideWith(
            () => _StaticAuthController(_managementWorkAuthority()),
          ),
        ],
        child: MaterialApp(
          home: UnitCommandCenterScreen(
            dashboard: _unitDashboard(
              nextBestAction: const UnitNextBestAction(
                label: 'Review turnover',
                href: '/units/42?tab=maintenance&view=turnover',
              ),
              turnover: UnitTurnoverSummary(
                status: 'InProgress',
                totalTaskCount: 3,
                openTaskCount: 1,
                completedTaskCount: 2,
                receiptCount: 2,
                estimatedCost: 300,
                actualCost: 220,
                startedAt: DateTime(2026, 6, 1),
                targetReadyDate: DateTime(2026, 6, 10),
                lastActivityAt: DateTime(2026, 6, 5),
                daysInTurnover: 4,
              ),
            ),
            initialTab: UnitCommandCenterTab.maintenance,
            initialView: UnitCommandCenterView.turnover,
          ),
        ),
      ),
    );

    expect(find.text('Turnover'), findsWidgets);
    expect(find.text('In progress'), findsOneWidget);
    expect(find.text('1 open / 2 done'), findsOneWidget);
    expect(find.text(r'$300 / $220'), findsOneWidget);

    await tester.drag(find.byType(ListView), const Offset(0, -500));
    await tester.pumpAndSettle();

    expect(find.byTooltip('Scan / Add'), findsOneWidget);
    await tester.tap(find.byTooltip('Scan / Add'));
    await tester.pumpAndSettle();

    expect(find.text('New turnover task'), findsOneWidget);
  });
}

class _UnitsListRestorationHarness extends StatefulWidget {
  const _UnitsListRestorationHarness({super.key, required this.child});

  final Widget child;

  @override
  State<_UnitsListRestorationHarness> createState() =>
      _UnitsListRestorationHarnessState();
}

class _UnitsListRestorationHarnessState
    extends State<_UnitsListRestorationHarness>
    with RestorationMixin {
  final controller = RestorableMobileRestorationState();
  bool _childAttached = false;

  @override
  String? get restorationId => 'units-list-restoration-harness';

  @override
  void restoreState(RestorationBucket? oldBucket, bool initialRestore) {
    registerForRestoration(controller, 'unit-navigation');
  }

  void attachChild() => setState(() => _childAttached = true);

  @override
  void dispose() {
    controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return MobileRestorationScope(
      controller: controller,
      child: _childAttached ? widget.child : const SizedBox.shrink(),
    );
  }
}

AuthStateAuthenticated _managementWorkAuthority() {
  const experience = WorkspaceExperience.management;
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
        activeExperience: experience,
      ),
      defaultExperience: experience,
      availableExperiences: [experience],
      assignments: [],
      navigation: [
        NavigationCapabilities(
          experience: experience,
          capabilityKeys: ['work.manage', 'rentals.manage'],
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

class _UnitApplicationsWorkflowRepository extends ApplicationsRepository {
  _UnitApplicationsWorkflowRepository() : super(Dio());

  final initialPage = Completer<ApplicationListPage>();
  final queries = <ApplicationListQuery>[];
  final detailIds = <int>[];
  var _adaNewestAttempts = 0;

  @override
  Future<ApplicationListPage> listPage([
    ApplicationListQuery query = const ApplicationListQuery(),
  ]) {
    queries.add(query);

    if (queries.length == 1) {
      return initialPage.future;
    }
    if (query.skip == 20) {
      return Future.value(
        _applicationPage(
          items: [_application(id: 702, firstName: 'Page Two')],
          totalCount: 21,
          skip: 20,
        ),
      );
    }
    if (query.search == 'Ada' && query.sort == '-submittedAt') {
      _adaNewestAttempts += 1;
      if (_adaNewestAttempts == 1) {
        return Future.error(
          const ApiException(
            statusCode: 503,
            message: 'Applications unavailable',
          ),
        );
      }
      return Future.value(_applicationPage());
    }
    if (query.search == 'Ada' && query.sort == 'submittedAt') {
      return Future.value(
        _applicationPage(
          items: [_application(id: 703, firstName: 'Ada')],
          totalCount: 1,
        ),
      );
    }

    return Future.value(
      _applicationPage(
        items: [_application(id: 701, firstName: 'Initial')],
        totalCount: 21,
      ),
    );
  }

  @override
  Future<RentalApplication> get(int id) async {
    detailIds.add(id);
    return _application(id: id, firstName: 'Ada');
  }

  @override
  Future<ScreeningWorkspace> screening(int id) async {
    return ScreeningWorkspace.fromJson(const {});
  }
}

ApplicationListPage _applicationPage({
  List<RentalApplication> items = const [],
  int totalCount = 0,
  int skip = 0,
}) {
  return ApplicationListPage(
    items: items,
    totalCount: totalCount,
    skip: skip,
    take: 20,
  );
}

RentalApplication _application({required int id, required String firstName}) {
  return RentalApplication(
    id: id,
    propertyId: 8,
    unitId: 19,
    firstName: firstName,
    lastName: 'Applicant',
    consentGiven: true,
    status: 'Submitted',
    submittedAtUtc: DateTime.utc(2026, 7, 16),
  );
}

class _UnitNavigationActivityRepository extends ActivityRepository {
  _UnitNavigationActivityRepository() : super(Dio());

  @override
  Future<List<ActivityEntry>> list({
    int skip = 0,
    int take = 20,
    String sort = '-timestamp',
    String? search,
    String? operation,
    String? entityType,
    int? entityId,
  }) async {
    return const [];
  }
}

UnitDashboard _unitDashboard({
  required UnitNextBestAction nextBestAction,
  UnitTurnoverSummary turnover = const UnitTurnoverSummary(),
  int unitId = 42,
  String unitNumber = '4B',
  String propertyName = 'Maple Ridge',
  int? leaseManagementId,
  int? tenantAccountId,
  List<UnitTenantSummary> currentTenants = const [],
}) {
  return UnitDashboard(
    unit: Unit(
      id: unitId,
      propertyId: 7,
      unitNumber: unitNumber,
      bedrooms: 2,
      bathrooms: 1,
      marketRent: 1400,
      status: 'Occupied',
      createdAt: DateTime(2026),
      updatedAt: DateTime(2026),
    ),
    propertyName: propertyName,
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
    leaseManagementId: leaseManagementId,
    tenantAccountId: tenantAccountId,
    currentTenants: currentTenants,
    turnover: turnover,
  );
}

LeaseManagementDetail _management17Detail() => LeaseManagementDetail(
  summary: LeaseManagementSummary(
    id: 17,
    publicId: 'DEMO-LM-ACTIVE-017',
    relationshipNumber: 'DEMO-LM-ACTIVE-017',
    propertyId: 8,
    propertyName: 'Hilliard Duplex',
    unitId: 19,
    unitNumber: 'Left',
    lifecycle: 'Active',
    businessDate: DateTime(2026, 7, 16),
    tenantAccountId: 17,
    primaryTenantId: 17,
    primaryTenantName: 'Resident 17',
    currentPartyCount: 1,
    currentResidentCount: 1,
    hasReconciliationException: false,
    updatedAt: DateTime(2026, 7, 16),
  ),
  parties: const [],
  agreementCount: 0,
  addendumCount: 0,
  legalArtifactCount: 0,
);

LeaseManagementDetail _managementDetail({
  required int managementId,
  required int accountId,
  required int unitId,
  required String unitNumber,
  required String propertyName,
  required bool hasAgreement,
}) => LeaseManagementDetail(
  summary: LeaseManagementSummary(
    id: managementId,
    publicId: 'DEMO-LM-ACTIVE-${managementId.toString().padLeft(3, '0')}',
    relationshipNumber:
        'DEMO-LM-ACTIVE-${managementId.toString().padLeft(3, '0')}',
    propertyId: 8,
    propertyName: propertyName,
    unitId: unitId,
    unitNumber: unitNumber,
    lifecycle: 'Active',
    businessDate: DateTime(2026, 7, 16),
    agreementId: hasAgreement ? 81 : null,
    agreementNumber: hasAgreement ? 'AGR-81' : null,
    agreementStatus: hasAgreement ? 'Active' : null,
    termStartOn: hasAgreement ? DateTime(2026, 1, 1) : null,
    termEndOn: hasAgreement ? DateTime(2026, 12, 31) : null,
    tenantAccountId: accountId,
    primaryTenantId: accountId,
    primaryTenantName: 'Resident $accountId',
    currentPartyCount: 1,
    currentResidentCount: 1,
    hasReconciliationException: false,
    updatedAt: DateTime(2026, 7, 16),
  ),
  parties: const [],
  agreementCount: hasAgreement ? 1 : 0,
  addendumCount: 0,
  legalArtifactCount: hasAgreement ? 2 : 0,
);

LeaseAgreementHistory _executedAgreement() => LeaseAgreementHistory.fromJson({
  'leaseAgreementId': 81,
  'versionNumber': 1,
  'agreementNumber': 'AGR-81',
  'changeType': 'Initial',
  'termType': 'Fixed',
  'termStartOn': '2026-01-01',
  'termEndOn': '2026-12-31',
  'governingFromOn': '2026-01-01',
  'baseRentAmount': 1500,
  'agreementStatus': 'Active',
  'isGoverning': true,
  'hasLiveReissue': false,
  'fullyExecutedAtUtc': '2025-12-20T12:00:00Z',
  'executedArtifact': {
    'legalDocumentArtifactId': 2,
    'fileName': 'executed-agreement.pdf',
    'contentType': 'application/pdf',
    'byteLength': 2048,
  },
});

Map<String, dynamic> _unitDashboardJson() {
  return {
    'unit': {
      'id': 42,
      'propertyId': 7,
      'unitNumber': '4B',
      'bedrooms': 2,
      'bathrooms': 1,
      'marketRent': 1400,
      'status': 'Occupied',
      'createdAt': '2026-01-01T00:00:00Z',
      'updatedAt': '2026-01-01T00:00:00Z',
    },
    'propertyName': 'Maple Ridge',
    'lifecycleStage': 'Turnover',
    'nextBestAction': {
      'label': 'Review turnover',
      'href': '/units/42?tab=maintenance&view=turnover',
    },
    'header': {
      'rentState': 'Current',
      'outstandingRentBalance': 0,
      'openWorkOrderCount': 0,
      'docsNeedingReviewCount': 0,
    },
    'overview': {
      'recentPayments': [],
      'openWorkOrders': [],
      'pendingDocs': [],
      'upcomingAppointments': [],
    },
  };
}
