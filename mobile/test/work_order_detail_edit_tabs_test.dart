import 'dart:async';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/auth/auth_controller.dart';
import 'package:rental_command/core/auth/auth_models.dart';
import 'package:rental_command/core/models/models.dart';
import 'package:rental_command/features/maintenance/work_order_detail_screen.dart';
import 'package:rental_command/features/maintenance/work_orders_repository.dart';
import 'package:rental_command/features/properties/properties_repository.dart';
import 'package:rental_command/features/tenants/tenants_repository.dart';
import 'package:rental_command/features/vendors/vendors_models.dart' as vendors;
import 'package:rental_command/features/vendors/vendors_repository.dart';

void main() {
  testWidgets(
    'pending work order keeps its app bar and shows contextual detail structure',
    (tester) async {
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            authControllerProvider.overrideWith(
              () => _StaticAuthController(_managementAuthority(const {})),
            ),
            workOrdersRepositoryProvider.overrideWithValue(
              _PendingWorkOrdersRepository(),
            ),
          ],
          child: const MaterialApp(
            home: WorkOrderDetailScreen(workOrderId: 17),
          ),
        ),
      );
      await tester.pump();

      expect(find.text('Repair'), findsOneWidget);
      expect(
        find.byKey(const Key('work-order-detail-loading')),
        findsOneWidget,
      );
      expect(find.text('Loading repair'), findsOneWidget);
      expect(find.text('Details, schedule, and activity'), findsOneWidget);
      expect(find.text('Repair details'), findsOneWidget);
      expect(find.byType(CircularProgressIndicator), findsOneWidget);
    },
  );

  testWidgets(
    'work order edit form is tabbed and saves schedule and cost fields',
    (tester) async {
      final repo = _FakeWorkOrdersRepository();

      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            authControllerProvider.overrideWith(
              () => _StaticAuthController(_managementAuthority(const {})),
            ),
            workOrdersRepositoryProvider.overrideWithValue(repo),
            propertiesRepositoryProvider.overrideWithValue(
              _FakePropertiesRepository(),
            ),
            tenantsRepositoryProvider.overrideWithValue(
              _FakeTenantsRepository(),
            ),
            vendorsRepositoryProvider.overrideWithValue(
              _FakeVendorsRepository(),
            ),
          ],
          child: const MaterialApp(
            home: WorkOrderDetailScreen(workOrderId: 17),
          ),
        ),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.byTooltip('Edit'));
      await tester.pumpAndSettle();

      expect(find.text('Edit repair'), findsOneWidget);
      expect(
        find.descendant(
          of: find.byKey(const Key('tabbed-form-step-0')),
          matching: find.text('Details'),
        ),
        findsOneWidget,
      );
      expect(
        find.descendant(
          of: find.byKey(const Key('tabbed-form-step-1')),
          matching: find.text('Schedule'),
        ),
        findsOneWidget,
      );
      expect(
        find.descendant(
          of: find.byKey(const Key('tabbed-form-step-2')),
          matching: find.text('Costs'),
        ),
        findsOneWidget,
      );
      expect(
        find.descendant(
          of: find.byKey(const Key('tabbed-form-step-3')),
          matching: find.text('Assign'),
        ),
        findsOneWidget,
      );

      await tester.tap(find.text('Schedule'));
      await tester.pumpAndSettle();

      expect(find.text('Scheduled date (optional)'), findsOneWidget);
      expect(find.text('Start time'), findsOneWidget);

      await tester.tap(find.text('Costs'));
      await tester.pumpAndSettle();

      expect(find.text('Arrival window end'), findsOneWidget);

      await tester.enterText(
        find.byKey(const Key('work-order-estimated-cost-field')),
        '185.75',
      );
      await tester.tap(find.text('Next'));
      await tester.pumpAndSettle();

      expect(find.text('Vendor'), findsOneWidget);

      await tester.tap(find.text('Save Changes'));
      await tester.pumpAndSettle();

      expect(repo.updatedData?['scheduledFor'], isA<String>());
      expect(repo.updatedData?['scheduledWindowEnd'], isA<String>());
      expect(repo.updatedData?['estimatedCost'], 185.75);
    },
  );

  testWidgets('missing current assignee candidate shows an error', (
    tester,
  ) async {
    final repo = _FakeWorkOrdersRepository(canAssignTechnician: true)
      ..responsibilities = [
        {
          'id': 'responsibility-1',
          'kind': 'Primary',
          'effectiveToUtc': null,
          'memberDisplayName': 'Former technician',
          'accessContextId': 99,
        },
      ]
      ..candidates = [
        {
          'membershipRoleAssignmentId': 7,
          'workspaceMembershipId': 8,
          'memberDisplayName': 'Available technician',
          'accessContextId': 1,
          'accessRevision': 2,
        },
      ];

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          authControllerProvider.overrideWith(
            () => _StaticAuthController(_managementAuthority(const {})),
          ),
          workOrdersRepositoryProvider.overrideWithValue(repo),
        ],
        child: const MaterialApp(home: WorkOrderDetailScreen(workOrderId: 17)),
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.byTooltip('Assign technician'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Unassign'));
    await tester.pump();

    expect(
      find.text('That technician assignment is no longer available.'),
      findsOneWidget,
    );
    expect(tester.takeException(), isNull);
  });

  testWidgets('work order edit sends a flag when an existing cost is cleared', (
    tester,
  ) async {
    final repo = _FakeWorkOrdersRepository();

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          authControllerProvider.overrideWith(
            () => _StaticAuthController(_managementAuthority(const {})),
          ),
          workOrdersRepositoryProvider.overrideWithValue(repo),
          propertiesRepositoryProvider.overrideWithValue(
            _FakePropertiesRepository(),
          ),
          tenantsRepositoryProvider.overrideWithValue(
            _FakeTenantsRepository(),
          ),
          vendorsRepositoryProvider.overrideWithValue(
            _FakeVendorsRepository(),
          ),
        ],
        child: const MaterialApp(
          home: WorkOrderDetailScreen(workOrderId: 17),
        ),
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.byTooltip('Edit'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Schedule'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Costs'));
    await tester.pumpAndSettle();
    await tester.enterText(
      find.byKey(const Key('work-order-estimated-cost-field')),
      '',
    );
    await tester.tap(find.text('Next'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Save Changes'));
    await tester.pumpAndSettle();

    expect(repo.updatedData?['estimatedCost'], isNull);
    expect(repo.updatedData?['clearEstimatedCost'], isTrue);
    expect(repo.updatedData?['clearActualCost'], isFalse);
  });
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

WorkOrder _workOrder() {
  return WorkOrder(
    id: 17,
    portfolioId: 1,
    propertyId: 7,
    unitId: 42,
    tenantId: 5,
    vendorId: 8,
    title: 'Kitchen sink leak',
    description: 'Water is dripping under the sink.',
    category: 'Plumbing',
    priority: 'High',
    status: 'Scheduled',
    requestedAt: DateTime.utc(2026, 6, 20, 13),
    scheduledFor: DateTime.utc(2026, 6, 21, 18),
    scheduledWindowEnd: DateTime.utc(2026, 6, 21, 20),
    estimatedCost: 125.50,
    createdBy: 'Staff',
    updatedAt: DateTime.utc(2026, 6, 20, 13),
    propertyName: 'Maple Ridge',
    unitNumber: '4B',
    tenantName: 'Jesse Coble',
    vendorName: 'Akron Plumbing',
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

Tenant _tenant() {
  return Tenant(
    id: 5,
    portfolioId: 1,
    firstName: 'Jesse',
    lastName: 'Coble',
    fullName: 'Jesse Coble',
    createdAt: DateTime(2026),
    updatedAt: DateTime(2026),
  );
}

vendors.Vendor _vendor() {
  return const vendors.Vendor(
    id: 8,
    name: 'Akron Plumbing',
    serviceType: 'Plumbing',
  );
}

class _FakeWorkOrdersRepository extends WorkOrdersRepository {
  _FakeWorkOrdersRepository({this.canAssignTechnician = false}) : super(Dio());

  final bool canAssignTechnician;
  List<Map<String, dynamic>> responsibilities = [];
  List<Map<String, dynamic>> candidates = [];

  Map<String, dynamic>? updatedData;

  @override
  Future<WorkOrderDetail> getWorkOrderDetail(int id) async {
    return RoleAwareWorkOrderDetail(
      workOrder: _workOrder(),
      timeline: const [],
      detailRole: 'Management',
      capabilities: WorkOrderDetailCapabilities(
        canViewTenantContact: false,
        canViewResidents: false,
        canViewAccessInstructions: false,
        canViewPrivateManagementNotes: true,
        canViewCosts: true,
        canCommentPublicly: false,
        canCommentPrivately: false,
        canUploadPhoto: false,
        canDeletePhoto: false,
        canCancel: false,
        canEditRequestFields: false,
        canEditManagementFields: true,
        canAssignTechnician: canAssignTechnician,
        canDispatchVendor: false,
        allowedStatusTransitions: [],
      ),
      residentNames: const [],
      activity: const [],
      hasActiveDispatch: false,
    );
  }

  @override
  Future<WorkOrder> updateWorkOrder(int id, Map<String, dynamic> data) async {
    updatedData = Map<String, dynamic>.from(data);
    return _workOrder();
  }

  @override
  Future<List<Document>> listWorkOrderDocuments(int workOrderId) async => [];

  @override
  Future<Uint8List> downloadDocument(int documentId) async => Uint8List(0);

  @override
  Future<List<Property>> listProperties() async => [_property()];

  @override
  Future<List<Map<String, dynamic>>> listResponsibilities(
    int workOrderId,
  ) async => responsibilities;

  @override
  Future<List<Map<String, dynamic>>> listResponsibilityCandidates(
    int workOrderId,
  ) async => candidates;
}

class _PendingWorkOrdersRepository extends WorkOrdersRepository {
  _PendingWorkOrdersRepository() : super(Dio());

  final _pending = Completer<WorkOrderDetail>();

  @override
  Future<WorkOrderDetail> getWorkOrderDetail(int id) => _pending.future;
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

  @override
  Future<TenantPage> listPage([
    TenantListQuery query = const TenantListQuery(),
  ]) async {
    return TenantPage(
      items: [_tenant()],
      totalCount: 1,
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
  }) async => [_vendor()];
}
