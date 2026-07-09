import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/models/models.dart';
import 'package:rental_command/features/leases/lease_form_defaults.dart';
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
      expect(defaults.status, 'Draft');
      expect(defaults.lateFeeAmount, '75');
      expect(defaults.rentDueDay, '1');
    },
  );

  testWidgets('unit lease tab offers Add lease from an existing lease detail', (
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

UnitDashboard _unitDashboard({UnitLeaseSummary? currentLease}) {
  return UnitDashboard(
    unit: _unit(),
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

Unit _unit() {
  return Unit(
    id: 42,
    propertyId: 7,
    unitNumber: '4B',
    bedrooms: 2,
    bathrooms: 1,
    marketRent: 1400,
    status: 'Occupied',
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
