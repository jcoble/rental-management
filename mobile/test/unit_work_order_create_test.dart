import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/auth/auth_controller.dart';
import 'package:rental_command/core/auth/auth_models.dart';
import 'package:rental_command/core/models/models.dart';
import 'package:rental_command/features/maintenance/work_orders_repository.dart';
import 'package:rental_command/features/properties/properties_repository.dart';
import 'package:rental_command/features/tenants/tenants_repository.dart';
import 'package:rental_command/features/units/unit_command_center_screen.dart';
import 'package:rental_command/features/units/units_repository.dart';
import 'package:rental_command/features/vendors/vendors_models.dart' as vendors;
import 'package:rental_command/features/vendors/vendors_repository.dart';

void main() {
  testWidgets(
    'unit work tab creates a work order with the current property and unit',
    (tester) async {
      final workOrdersRepo = _FakeWorkOrdersRepository();
      final tenantsRepo = _FakeTenantsRepository();

      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            authControllerProvider.overrideWith(
              () =>
                  _StaticAuthController(_managementAuthority({'work.manage'})),
            ),
            workOrdersRepositoryProvider.overrideWithValue(workOrdersRepo),
            propertiesRepositoryProvider.overrideWithValue(
              _FakePropertiesRepository(),
            ),
            tenantsRepositoryProvider.overrideWithValue(tenantsRepo),
            vendorsRepositoryProvider.overrideWithValue(
              _FakeVendorsRepository(),
            ),
          ],
          child: MaterialApp(
            home: UnitCommandCenterScreen(
              dashboard: _unitDashboard(),
              initialTab: UnitCommandCenterTab.maintenance,
              initialView: UnitCommandCenterView.workOrders,
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.byTooltip('Scan / Add'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('New repair'));
      await tester.pumpAndSettle();

      expect(find.text('New repair'), findsOneWidget);
      expect(find.text('Location'), findsOneWidget);
      expect(find.text('Issue'), findsOneWidget);
      expect(find.text('Schedule'), findsOneWidget);
      expect(find.text('Assign'), findsOneWidget);
      expect(find.text('Attach'), findsOneWidget);
      expect(find.text('Maple Ridge'), findsAtLeastNWidgets(1));
      expect(find.text('Unit 4B'), findsAtLeastNWidgets(1));

      await tester.tap(find.text('Next'));
      await tester.pumpAndSettle();

      await tester.enterText(
        find.byKey(const Key('work-order-title-field')),
        'Sink leak',
      );
      await tester.enterText(
        find.byKey(const Key('work-order-description-field')),
        'Water is dripping under the kitchen sink.',
      );
      await tester.tap(find.text('Next'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Next'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Next'));
      await tester.pumpAndSettle();

      await tester.ensureVisible(find.text('Save repair'));
      await tester.tap(find.text('Save repair'));
      await tester.pumpAndSettle();

      expect(workOrdersRepo.createdData?['propertyId'], 7);
      expect(workOrdersRepo.createdData?['unitId'], 42);
      expect(tenantsRepo.lastQuery?.propertyId, 7);
      expect(tenantsRepo.lastQuery?.unitId, 42);
    },
  );
}

AuthStateAuthenticated _managementAuthority(Set<String> capabilities) {
  const experience = WorkspaceExperience.management;
  return AuthStateAuthenticated(
    const AuthUser(
      id: 1,
      email: 'manager@example.test',
      displayName: 'Test manager',
      emailVerified: true,
    ),
    AccessEnvelope(
      identity: const AccessIdentity(userId: 1, displayName: 'Test manager'),
      selectedContext: const SelectedAccessContext(
        accessContextId: 1,
        portfolioId: 1,
        workspaceName: 'Test workspace',
        accessRevision: 1,
        activeExperience: experience,
      ),
      defaultExperience: experience,
      availableExperiences: const [experience],
      assignments: const [],
      navigation: [
        NavigationCapabilities(
          experience: experience,
          capabilityKeys: capabilities.toList(growable: false),
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

UnitDashboard _unitDashboard() {
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
    nextBestAction: const UnitNextBestAction(label: '', href: ''),
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

Property _property() {
  return Property(
    id: 7,
    portfolioId: 1,
    name: 'Maple Ridge',
    type: 'MultiFamily',
    rentalStructure: RentalStructure.multiRental,
    status: 'Active',
    addressLine1: '123 Main St',
    city: 'Akron',
    state: 'OH',
    postalCode: '44319',
    createdAt: DateTime(2026),
    updatedAt: DateTime(2026),
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

class _FakeWorkOrdersRepository extends WorkOrdersRepository {
  _FakeWorkOrdersRepository() : super(Dio());

  Map<String, dynamic>? createdData;

  @override
  Future<List<Property>> listProperties() async => [_property()];

  @override
  Future<WorkOrder> createWorkOrder(Map<String, dynamic> data) async {
    createdData = Map<String, dynamic>.from(data);
    return WorkOrder(
      id: 99,
      portfolioId: 1,
      propertyId: (data['propertyId'] as num).toInt(),
      unitId: (data['unitId'] as num?)?.toInt(),
      title: data['title'] as String? ?? '',
      description: data['description'] as String? ?? '',
      category: data['category'] as String? ?? '',
      priority: data['priority'] as String? ?? '',
      status: 'New',
      requestedAt: DateTime(2026),
      updatedAt: DateTime(2026),
    );
  }

  @override
  Future<void> uploadWorkOrderPhoto({
    required int workOrderId,
    required Uint8List bytes,
    required String fileName,
    required String contentType,
    required String clientOperationId,
  }) async {}
}

class _FakePropertiesRepository extends PropertiesRepository {
  _FakePropertiesRepository() : super(Dio());

  @override
  Future<List<Unit>> listUnits(
    int propertyId, {
    bool availableForLease = false,
  }) async => [_unit()];
}

class _FakeTenantsRepository extends TenantsRepository {
  _FakeTenantsRepository() : super(Dio());

  TenantListQuery? lastQuery;

  @override
  Future<TenantPage> listPage([
    TenantListQuery query = const TenantListQuery(),
  ]) async {
    lastQuery = query;
    return TenantPage(
      items: const [],
      totalCount: 0,
      skip: query.skip,
      take: query.take,
    );
  }
}

class _FakeVendorsRepository extends VendorsRepository {
  _FakeVendorsRepository() : super(Dio());

  @override
  Future<List<vendors.Vendor>> list({
    int skip = 0,
    int take = 50,
    String sort = 'name',
  }) async => [];
}
