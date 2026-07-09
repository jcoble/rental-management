import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/models/models.dart';
import 'package:rental_command/features/leases/eviction_cases_repository.dart';
import 'package:rental_command/features/leases/lease_form_defaults.dart';
import 'package:rental_command/features/leases/leases_list_screen.dart';
import 'package:rental_command/features/leases/leases_repository.dart';
import 'package:rental_command/features/payments/payments_repository.dart';
import 'package:rental_command/features/properties/properties_repository.dart';
import 'package:rental_command/features/tenants/tenants_repository.dart';
import 'package:rental_command/features/units/unit_command_center_screen.dart';
import 'package:rental_command/features/units/units_repository.dart';

void main() {
  test(
    'unit lease defaults lock the form to the current property and unit',
    () {
      final defaults = buildUnitLeaseFormDefaults(
        unit: _unit(),
        propertyName: 'Maple Ridge',
      );

      expect(defaults.propertyId, 7);
      expect(defaults.unitId, 42);
      expect(defaults.propertyLabel, 'Maple Ridge');
      expect(defaults.unitLabel, 'Unit 4B');
      expect(defaults.monthlyRent, '1400');
      expect(defaults.status, 'Active');
      expect(defaults.lateFeeAmount, '75');
      expect(defaults.rentDueDay, '1');
    },
  );

  testWidgets('unit lease tab blocks Add lease while an active lease exists', (
    tester,
  ) async {
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          leasesRepositoryProvider.overrideWithValue(_FakeLeasesRepository()),
          propertiesRepositoryProvider.overrideWithValue(
            _FakePropertiesRepository(),
          ),
          paymentsRepositoryProvider.overrideWithValue(
            _FakePaymentsRepository(),
          ),
          evictionCasesRepositoryProvider.overrideWithValue(
            _FakeEvictionCasesRepository(),
          ),
          tenantsRepositoryProvider.overrideWithValue(_FakeTenantsRepository()),
        ],
        child: MaterialApp(
          home: UnitCommandCenterScreen(
            dashboard: _unitDashboard(currentLease: _leaseSummary()),
            initialTab: UnitCommandCenterTab.lease,
            initialLease: _lease(),
          ),
        ),
      ),
    );
    await tester.pump();

    expect(find.text('Lease #L-2026-10'), findsAtLeastNWidgets(1));
    expect(find.text('Add lease'), findsOneWidget);
    expect(
      find.text('End or terminate the active lease before adding another.'),
      findsOneWidget,
    );
    final addButton = tester.widget<FilledButton>(
      find.widgetWithText(FilledButton, 'Add lease'),
    );
    expect(addButton.onPressed, isNull);
  });

  testWidgets('vacant unit lease tab opens the active lease form', (
    tester,
  ) async {
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          leasesRepositoryProvider.overrideWithValue(_FakeLeasesRepository()),
          propertiesRepositoryProvider.overrideWithValue(
            _FakePropertiesRepository(),
          ),
          paymentsRepositoryProvider.overrideWithValue(
            _FakePaymentsRepository(),
          ),
          tenantsRepositoryProvider.overrideWithValue(_FakeTenantsRepository()),
        ],
        child: MaterialApp(
          home: UnitCommandCenterScreen(
            dashboard: _unitDashboard(unitStatus: 'Vacant'),
            initialTab: UnitCommandCenterTab.lease,
          ),
        ),
      ),
    );
    await tester.pump();

    await tester.tap(find.text('Add lease'));
    await tester.pumpAndSettle();

    expect(find.text('New Lease'), findsOneWidget);
    expect(find.text('Maple Ridge'), findsAtLeastNWidgets(1));
    expect(find.text('Unit 4B'), findsAtLeastNWidgets(1));
    expect(find.text('Location'), findsOneWidget);
    expect(find.text('Tenants'), findsAtLeastNWidgets(1));
    expect(find.text('Avery Available'), findsNothing);

    await tester.tap(find.text('Next'));
    await tester.pumpAndSettle();

    expect(find.text('Avery Available'), findsOneWidget);
    expect(find.text('Existing'), findsOneWidget);
    expect(find.text('New tenant'), findsOneWidget);

    await tester.tap(find.text('New tenant'));
    await tester.pumpAndSettle();

    expect(find.text('First name'), findsAtLeastNWidgets(1));
    expect(find.text('Last name'), findsAtLeastNWidgets(1));
    expect(find.text('Email'), findsAtLeastNWidgets(1));
    expect(find.text('Phone'), findsAtLeastNWidgets(1));
    expect(
      find.text(
        'The tenant and lease are saved together. If the lease fails, neither record is created.',
      ),
      findsOneWidget,
    );
  });

  testWidgets('unit Add Lease sheet ignores outside taps', (tester) async {
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          leasesRepositoryProvider.overrideWithValue(_FakeLeasesRepository()),
          propertiesRepositoryProvider.overrideWithValue(
            _FakePropertiesRepository(),
          ),
          paymentsRepositoryProvider.overrideWithValue(
            _FakePaymentsRepository(),
          ),
          tenantsRepositoryProvider.overrideWithValue(_FakeTenantsRepository()),
        ],
        child: MaterialApp(
          home: UnitCommandCenterScreen(
            dashboard: _unitDashboard(unitStatus: 'Vacant'),
            initialTab: UnitCommandCenterTab.lease,
          ),
        ),
      ),
    );
    await tester.pump();

    await tester.tap(find.text('Add lease'));
    await tester.pumpAndSettle();
    await tester.tapAt(const Offset(8, 8));
    await tester.pumpAndSettle();

    expect(find.text('New Lease'), findsOneWidget);
  });

  testWidgets('new tenant lease submits one atomic create payload', (
    tester,
  ) async {
    final leasesRepository = _FakeLeasesRepository();
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          leasesRepositoryProvider.overrideWithValue(leasesRepository),
          propertiesRepositoryProvider.overrideWithValue(
            _FakePropertiesRepository(),
          ),
          tenantsRepositoryProvider.overrideWithValue(_FakeTenantsRepository()),
        ],
        child: MaterialApp(
          home: Scaffold(
            body: LeaseFormSheet(
              unitDefaults: buildUnitLeaseFormDefaults(
                unit: _unit(status: 'Vacant'),
                propertyName: 'Maple Ridge',
              ),
              onSaved: () {},
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    await _tapNext(tester); // Location
    await tester.tap(find.text('New tenant'));
    await tester.pumpAndSettle();

    final tenantFields = find.byType(TextFormField);
    expect(tenantFields, findsNWidgets(4));
    await tester.enterText(tenantFields.at(0), 'Avery');
    await tester.enterText(tenantFields.at(1), 'Stone');
    await tester.enterText(tenantFields.at(2), 'avery.stone@example.test');
    await tester.enterText(tenantFields.at(3), '614-555-0184');
    await _tapNext(tester); // Tenant

    expect(find.text('Lease number'), findsOneWidget);
    await _tapNext(tester); // Lease number

    await tester.tap(
      find
          .ancestor(of: find.text('Start date'), matching: find.byType(InkWell))
          .first,
    );
    await tester.pumpAndSettle();
    await tester.tap(find.text('OK'));
    await tester.pumpAndSettle();
    expect(find.text('Select date'), findsNothing);
    await _tapNext(tester); // Dates; end defaults one calendar year later

    await tester.enterText(
      find.widgetWithText(TextFormField, 'Security deposit (\$)'),
      '1200',
    );
    await _tapNext(tester); // Rent
    await _tapNext(tester); // Fees
    await _tapNext(tester); // Status

    expect(find.text('Rent tracking start'), findsOneWidget);
    await tester.tap(find.text('Start from today'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Backfill from lease start').last);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Create Lease'));
    await tester.pumpAndSettle();

    final payload = leasesRepository.createdPayload;
    expect(payload, isNotNull);
    expect(payload!.containsKey('tenantId'), isFalse);
    expect(payload.containsKey('tenantIds'), isFalse);
    expect(payload['newTenant'], {
      'firstName': 'Avery',
      'lastName': 'Stone',
      'email': 'avery.stone@example.test',
      'phone': '614-555-0184',
    });
    expect(payload['status'], 'Active');
    expect(payload['rentTrackingStartMode'], 'BackfillFromLeaseStart');
    expect(payload['lateFeeAmount'], 75);
  });

  testWidgets(
    'unit overview opens edit sheet and locks status for current leases',
    (tester) async {
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            propertiesRepositoryProvider.overrideWithValue(
              _FakePropertiesRepository(),
            ),
          ],
          child: MaterialApp(
            home: UnitCommandCenterScreen(
              dashboard: _unitDashboard(currentLease: _leaseSummary()),
              initialTab: UnitCommandCenterTab.overview,
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Edit unit'), findsOneWidget);
      await tester.tap(find.text('Edit unit'));
      await tester.pumpAndSettle();

      expect(find.text('Edit Unit 4B'), findsOneWidget);
      expect(find.text('Floor plan'), findsOneWidget);
      expect(find.text('Square feet'), findsOneWidget);
      await tester.tap(find.text('Next'));
      await tester.pumpAndSettle();

      expect(find.text('Status'), findsAtLeastNWidgets(1));
      expect(
        find.text(
          'End, move out, or cancel notice on the current lease before changing unit status.',
        ),
        findsOneWidget,
      );
    },
  );
}

Future<void> _tapNext(WidgetTester tester) async {
  await tester.tap(find.text('Next'));
  await tester.pumpAndSettle();
}

UnitDashboard _unitDashboard({
  UnitLeaseSummary? currentLease,
  String unitStatus = 'Occupied',
}) {
  return UnitDashboard(
    unit: _unit(status: unitStatus),
    propertyName: 'Maple Ridge',
    lifecycleStage: 'Active',
    nextBestAction: const UnitNextBestAction(label: '', href: ''),
    header: const UnitDashboardHeader(
      rentState: 'Current',
      outstandingRentBalance: 0,
      openWorkOrderCount: 0,
      docsNeedingReviewCount: 0,
    ),
    currentLease: currentLease,
    overview: const UnitDashboardOverview(
      recentPayments: [],
      openWorkOrders: [],
      pendingDocs: [],
      upcomingAppointments: [],
    ),
  );
}

Unit _unit({String status = 'Occupied'}) {
  return Unit(
    id: 42,
    propertyId: 7,
    unitNumber: '4B',
    bedrooms: 2,
    bathrooms: 1,
    marketRent: 1400,
    status: status,
    createdAt: DateTime(2026),
    updatedAt: DateTime(2026),
  );
}

UnitLeaseSummary _leaseSummary() {
  return UnitLeaseSummary(
    id: 10,
    leaseNumber: 'L-2026-10',
    status: 'Active',
    startDate: DateTime(2026),
    endDate: DateTime(2027),
    monthlyRent: 1400,
    securityDeposit: 1400,
  );
}

Lease _lease() {
  return Lease(
    id: 10,
    portfolioId: 1,
    propertyId: 7,
    unitId: 42,
    tenantId: 3,
    tenantIds: const [3],
    leaseNumber: 'L-2026-10',
    status: 'Active',
    startDate: DateTime(2026),
    endDate: DateTime(2027),
    monthlyRent: 1400,
    securityDeposit: 1400,
    lateFeeAmount: 75,
    rentDueDay: 1,
    tenantName: 'Jesse Coble',
    propertyName: 'Maple Ridge',
    unitNumber: '4B',
    createdAt: DateTime(2026),
    updatedAt: DateTime(2026),
  );
}

class _FakeLeasesRepository extends LeasesRepository {
  _FakeLeasesRepository() : super(Dio());

  Map<String, dynamic>? createdPayload;

  @override
  Future<Lease> createLease(Map<String, dynamic> data) async {
    createdPayload = Map<String, dynamic>.of(data);
    return _lease();
  }

  @override
  Future<LeaseSignatureStatus> signatureStatus(int id) async {
    return LeaseSignatureStatus(
      leaseId: id,
      esignStatus: 'None',
      leaseStatus: 'Active',
      hasSignedDocument: false,
    );
  }
}

class _FakePaymentsRepository extends PaymentsRepository {
  _FakePaymentsRepository() : super(Dio());

  @override
  Future<List<Payment>> listPayments({
    int? leaseId,
    int? applicationId,
    String sort = '-createdAt',
    String? dueFrom,
    String? dueTo,
    String? paidFrom,
    String? paidTo,
  }) async {
    return const [];
  }

  @override
  Future<Uint8List> scanBytes(int id) async => Uint8List(0);
}

class _FakeEvictionCasesRepository extends EvictionCasesRepository {
  _FakeEvictionCasesRepository() : super(Dio());

  @override
  Future<EvictionCaseListPage> listCasesPage({
    required int leaseId,
    int skip = 0,
    int take = 20,
    String sort = '-filedOnDate',
  }) async {
    return EvictionCaseListPage(
      items: const [],
      totalCount: 0,
      skip: skip,
      take: take,
    );
  }
}

class _FakePropertiesRepository extends PropertiesRepository {
  _FakePropertiesRepository() : super(Dio());

  @override
  Future<List<Property>> listProperties({
    bool availableForLease = false,
  }) async {
    return [
      Property(
        id: 7,
        portfolioId: 1,
        name: 'Maple Ridge',
        type: 'SingleFamily',
        status: 'Active',
        addressLine1: '100 Main St',
        city: 'Columbus',
        state: 'OH',
        postalCode: '43215',
        createdAt: DateTime(2026),
        updatedAt: DateTime(2026),
      ),
    ];
  }

  @override
  Future<List<Unit>> listUnits(
    int propertyId, {
    bool availableForLease = false,
  }) async {
    return [_unit()];
  }
}

class _FakeTenantsRepository extends TenantsRepository {
  _FakeTenantsRepository() : super(Dio());

  @override
  Future<List<Tenant>> listTenants() async {
    throw StateError('Add Lease should load lease-available tenants.');
  }

  @override
  Future<List<Tenant>> listAvailableForLeaseTenants({int take = 200}) async {
    throw StateError('Add Lease should load paged lease-available tenants.');
  }

  @override
  Future<TenantPage> listPage([
    TenantListQuery query = const TenantListQuery(),
  ]) async {
    expect(query.availableForLease, true);
    return TenantPage(
      items: [
        Tenant(
          id: 3,
          portfolioId: 1,
          firstName: 'Avery',
          lastName: 'Available',
          email: 'avery@example.test',
          createdAt: DateTime(2026),
          updatedAt: DateTime(2026),
        ),
      ],
      totalCount: 1,
      skip: query.skip,
      take: query.take,
    );
  }
}
