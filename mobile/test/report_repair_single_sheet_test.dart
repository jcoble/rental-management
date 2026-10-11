import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/auth/auth_controller.dart';
import 'package:rental_command/core/auth/auth_models.dart';
import 'package:rental_command/core/models/models.dart';
import 'package:rental_command/features/maintenance/create_work_order_sheet.dart';
import 'package:rental_command/features/maintenance/work_orders_repository.dart';
import 'package:rental_command/features/maintenance/work_orders_screen.dart';
import 'package:rental_command/features/properties/properties_repository.dart';
import 'package:rental_command/features/tenants/tenants_repository.dart';
import 'package:rental_command/features/vendors/vendors_models.dart' as vendors;
import 'package:rental_command/features/vendors/vendors_repository.dart';

void main() {
  testWidgets('report repair sheet is one screen with vendor visible', (
    tester,
  ) async {
    await _pumpRepairSheet(tester, _FakeWorkOrdersRepository());

    // No stepper, no paging through cards.
    expect(find.byKey(const Key('tabbed-form-step-0')), findsNothing);
    expect(find.text('Next'), findsNothing);
    expect(find.text('Save repair'), findsOneWidget);

    // The five essentials are all on the one screen.
    expect(find.text('Property'), findsOneWidget);
    expect(find.text('Unit'), findsOneWidget);
    expect(find.text('What happened?'), findsOneWidget);
    expect(find.text('Details'), findsOneWidget);
    expect(find.text('Add a photo'), findsOneWidget);
    expect(find.text("Who's fixing it?"), findsOneWidget);

    // Everything else waits behind the disclosure.
    expect(find.text('More details'), findsOneWidget);
    expect(find.text('Priority'), findsNothing);
    expect(find.text('Category'), findsNothing);
    expect(find.text('Estimated cost (optional)'), findsNothing);

    await tester.ensureVisible(find.text('More details'));
    await tester.tap(find.text('More details'));
    await tester.pumpAndSettle();

    expect(find.text('Priority'), findsOneWidget);
    expect(find.text('Category'), findsOneWidget);
  });

  testWidgets('report repair posts property title description and vendor', (
    tester,
  ) async {
    final repo = _FakeWorkOrdersRepository();
    await _pumpRepairSheet(tester, repo);

    await tester.enterText(
      find.byKey(const Key('work-order-title-field')),
      'Sink leak',
    );
    await tester.enterText(
      find.byKey(const Key('work-order-description-field')),
      'Water is dripping under the kitchen sink.',
    );
    await tester.pumpAndSettle();

    await tester.ensureVisible(find.text('Not decided yet'));
    await tester.tap(find.text('Not decided yet'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Ace Plumbing').last);
    await tester.pumpAndSettle();

    await tester.ensureVisible(find.text('Save repair'));
    await tester.tap(find.text('Save repair'));
    await tester.pumpAndSettle();

    expect(repo.createdData?['propertyId'], 7);
    expect(repo.createdData?['unitId'], 42);
    expect(repo.createdData?['title'], 'Sink leak');
    expect(
      repo.createdData?['description'],
      'Water is dripping under the kitchen sink.',
    );
    expect(repo.createdData?['vendorId'], 5);
    expect(repo.createdData?['priority'], 'Normal');
    expect(repo.createdData?['category'], 'General');
  });

  testWidgets('repairs empty state has one sentence', (tester) async {
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          authControllerProvider.overrideWith(
            () => _StaticAuthController(_managementAuthority({'work.manage'})),
          ),
          workOrdersRepositoryProvider.overrideWithValue(
            _FakeWorkOrdersRepository(),
          ),
        ],
        child: const MaterialApp(home: WorkOrdersScreen()),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('No open repairs'), findsOneWidget);
    expect(find.text('Tap + to report one.'), findsOneWidget);
    expect(find.textContaining('All caught up'), findsNothing);
    expect(find.textContaining('work order'), findsNothing);
    expect(find.textContaining('Work order'), findsNothing);
  });
}

Future<void> _pumpRepairSheet(
  WidgetTester tester,
  _FakeWorkOrdersRepository workOrdersRepo,
) async {
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        authControllerProvider.overrideWith(
          () => _StaticAuthController(_managementAuthority({'work.manage'})),
        ),
        workOrdersRepositoryProvider.overrideWithValue(workOrdersRepo),
        propertiesRepositoryProvider.overrideWithValue(
          _FakePropertiesRepository(),
        ),
        tenantsRepositoryProvider.overrideWithValue(_FakeTenantsRepository()),
        vendorsRepositoryProvider.overrideWithValue(_FakeVendorsRepository()),
      ],
      child: MaterialApp(
        home: Consumer(
          builder: (context, ref, _) => Scaffold(
            body: Center(
              child: ElevatedButton(
                onPressed: () => showCreateWorkOrderSheet(
                  context: context,
                  ref: ref,
                  onSaved: () {},
                  initialPropertyId: 7,
                  initialUnitId: 42,
                  initialPropertyLabel: 'Maple Ridge',
                  initialUnitLabel: 'Unit 4B',
                ),
                child: const Text('open'),
              ),
            ),
          ),
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
  await tester.tap(find.text('open'));
  await tester.pumpAndSettle();
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
  Future<WorkOrderListPage> listWorkOrdersPage([
    WorkOrderListQuery query = const WorkOrderListQuery(),
  ]) async => WorkOrderListPage(
    items: const [],
    totalCount: 0,
    skip: query.skip,
    take: query.take,
  );

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

  @override
  Future<TenantPage> listPage([
    TenantListQuery query = const TenantListQuery(),
  ]) async => TenantPage(
    items: const [],
    totalCount: 0,
    skip: query.skip,
    take: query.take,
  );
}

class _FakeVendorsRepository extends VendorsRepository {
  _FakeVendorsRepository() : super(Dio());

  @override
  Future<List<vendors.Vendor>> list({
    int skip = 0,
    int take = 50,
    String sort = 'name',
  }) async => const [
    vendors.Vendor(id: 5, name: 'Ace Plumbing', serviceType: 'Plumbing'),
  ];
}
