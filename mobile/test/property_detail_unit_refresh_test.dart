import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/api/api_exception.dart';
import 'package:rental_command/core/auth/auth_controller.dart';
import 'package:rental_command/core/auth/auth_models.dart';
import 'package:rental_command/core/models/models.dart';
import 'package:rental_command/features/properties/properties_repository.dart';
import 'package:rental_command/features/properties/property_detail_screen.dart';
import 'package:rental_command/features/properties/property_workspace_sections.dart';

void main() {
  testWidgets(
    'successful add refreshes the mounted units, header detail, and property list',
    (tester) async {
      final repository = _UnitRefreshRepository();
      final harness = await _pumpPropertyDetail(tester, repository);

      final initialUnitReads = repository.unitPageReads;
      final initialDetailReads = repository.detailReads;
      final initialPropertyPageReads = repository.propertyPageReads;

      await tester.tap(find.text('Add unit'));
      await tester.pumpAndSettle();
      await tester.enterText(
        find.widgetWithText(TextFormField, 'Unit number'),
        '92',
      );
      await _completeUnitForm(tester);

      expect(repository.createCalls, 1);
      expect(repository.unitPageReads, initialUnitReads + 1);
      expect(
        repository.unitPageQueries.last,
        const PropertyWorkspacePageQuery(propertyId: 7, skip: 0, take: 20),
      );
      expect(repository.legacyUnitReads, 0);
      expect(repository.detailReads, initialDetailReads + 1);
      expect(repository.propertyPageReads, initialPropertyPageReads + 1);
      expect(find.text('Unit 92'), findsOneWidget);
      expect(find.text('3', skipOffstage: false), findsWidgets);
      expect(
        harness.container
            .read(propertiesPageProvider(const PropertyListQuery()))
            .requireValue
            .items
            .single
            .rentalStructure,
        RentalStructure.multiRental,
      );
    },
  );

  testWidgets('blank, cancel, and failed add do not refresh unit consumers', (
    tester,
  ) async {
    final repository = _UnitRefreshRepository();
    final harness = await _pumpPropertyDetail(tester, repository);

    final initialUnitReads = repository.unitPageReads;
    final initialDetailReads = repository.detailReads;
    final initialPropertyPageReads = repository.propertyPageReads;

    await tester.tap(find.text('Add unit'));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Add Unit'));
    await tester.pumpAndSettle();
    expect(find.text('Unit number is required'), findsOneWidget);
    expect(repository.createCalls, 0);
    _expectReadCounts(
      repository,
      units: initialUnitReads,
      detail: initialDetailReads,
      properties: initialPropertyPageReads,
    );

    await tester.tap(find.byIcon(Icons.close));
    await tester.pumpAndSettle();
    _expectReadCounts(
      repository,
      units: initialUnitReads,
      detail: initialDetailReads,
      properties: initialPropertyPageReads,
    );

    repository.failCreate = true;
    await tester.tap(find.text('Add unit'));
    await tester.pumpAndSettle();
    await tester.enterText(
      find.widgetWithText(TextFormField, 'Unit number'),
      '93',
    );
    await _completeUnitForm(tester);

    expect(repository.createCalls, 1);
    expect(find.text('Unit creation failed.'), findsOneWidget);
    _expectReadCounts(
      repository,
      units: initialUnitReads,
      detail: initialDetailReads,
      properties: initialPropertyPageReads,
    );
    expect(
      harness.container
          .read(propertiesPageProvider(const PropertyListQuery()))
          .requireValue
          .items
          .single
          .unitCount,
      2,
    );
  });
}

Future<_PropertyDetailHarness> _pumpPropertyDetail(
  WidgetTester tester,
  _UnitRefreshRepository repository,
) async {
  tester.view.physicalSize = const Size(1080, 2400);
  tester.view.devicePixelRatio = 3;
  addTearDown(tester.view.reset);

  final container = ProviderContainer(
    overrides: [
      authControllerProvider.overrideWith(
        () => _StaticAuthController(_authenticatedState),
      ),
      propertiesRepositoryProvider.overrideWithValue(repository),
    ],
  );
  addTearDown(container.dispose);

  final detailSubscription = container.listen(
    propertyWorkspaceDetailProvider(7),
    (_, _) {},
  );
  final listSubscription = container.listen(
    propertiesPageProvider(const PropertyListQuery()),
    (_, _) {},
  );
  addTearDown(detailSubscription.close);
  addTearDown(listSubscription.close);
  await Future.wait([
    container.read(propertyWorkspaceDetailProvider(7).future),
    container.read(propertiesPageProvider(const PropertyListQuery()).future),
  ]);

  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
        home: PropertyDetailScreen(property: repository.currentProperty),
      ),
    ),
  );
  await tester.pumpAndSettle();
  await tester.tap(find.byKey(const Key('property-area-rentals')));
  await tester.pumpAndSettle();

  return _PropertyDetailHarness(container);
}

Future<void> _completeUnitForm(WidgetTester tester) async {
  await tester.tap(find.widgetWithText(FilledButton, 'Add Unit'));
  await tester.pumpAndSettle();
}

void _expectReadCounts(
  _UnitRefreshRepository repository, {
  required int units,
  required int detail,
  required int properties,
}) {
  expect(repository.unitPageReads, units);
  expect(repository.detailReads, detail);
  expect(repository.propertyPageReads, properties);
}

class _PropertyDetailHarness {
  const _PropertyDetailHarness(this.container);

  final ProviderContainer container;
}

class _UnitRefreshRepository extends PropertiesRepository {
  _UnitRefreshRepository() : super(Dio());

  bool failCreate = false;
  int createCalls = 0;
  int unitPageReads = 0;
  int legacyUnitReads = 0;
  int detailReads = 0;
  int propertyPageReads = 0;
  final unitPageQueries = <PropertyWorkspacePageQuery>[];
  var currentProperty = _property(unitCount: 2);

  @override
  Future<PropertyWorkspaceDetail> getPropertyWorkspaceDetail(int id) async {
    detailReads += 1;
    return PropertyWorkspaceDetail(
      property: currentProperty,
      workspaceEntry: PropertyWorkspaceEntry(
        destination: PropertyWorkspaceDestination.property,
        propertyId: 7,
        areas: [],
      ),
    );
  }

  @override
  Future<PropertyListPage> listPropertiesPage([
    PropertyListQuery query = const PropertyListQuery(),
  ]) async {
    propertyPageReads += 1;
    return PropertyListPage(
      items: [currentProperty],
      totalCount: 1,
      skip: query.skip,
      take: query.take,
      workspaceEntries: const {},
    );
  }

  @override
  Future<PropertyWorkspaceUnitPage> listWorkspaceUnitsPage(
    PropertyWorkspacePageQuery query,
  ) async {
    unitPageReads += 1;
    unitPageQueries.add(query);
    final count = currentProperty.unitCount!;
    return PropertyWorkspaceUnitPage(
      items: [
        for (var index = 1; index <= count; index++)
          PropertyWorkspaceUnit(
            id: index,
            unitNumber: index == 3 ? '92' : '$index',
            status: 'Vacant',
            marketRent: 1200,
            openWorkOrderCount: 0,
          ),
      ],
      totalCount: count,
      skip: query.skip,
      take: query.take,
    );
  }

  @override
  Future<List<Unit>> listUnits(
    int propertyId, {
    bool availableForLease = false,
  }) async {
    legacyUnitReads += 1;
    return const [];
  }

  @override
  Future<PropertyWorkspaceLeasePage> listWorkspaceLeasesPage(
    PropertyWorkspacePageQuery query,
  ) async {
    return PropertyWorkspaceLeasePage(
      items: const [],
      totalCount: 0,
      skip: query.skip,
      take: query.take,
    );
  }

  @override
  Future<Unit> createUnit(int propertyId, Map<String, dynamic> data) async {
    createCalls += 1;
    if (failCreate) {
      throw const ApiException(
        statusCode: 422,
        message: 'Unit creation failed.',
      );
    }
    currentProperty = _property(
      unitCount: 3,
      rentalStructure: RentalStructure.multiRental,
    );
    return Unit(
      id: 3,
      propertyId: propertyId,
      unitNumber: data['unitNumber'] as String,
      bedrooms: data['bedrooms'] as int,
      bathrooms: data['bathrooms'] as double,
      marketRent: data['marketRent'] as double,
      status: 'Vacant',
      createdAt: DateTime(2027, 2, 12),
      updatedAt: DateTime(2027, 2, 12),
    );
  }
}

Property _property({
  required int unitCount,
  RentalStructure rentalStructure = RentalStructure.singleRental,
}) {
  return Property(
    id: 7,
    portfolioId: 1,
    name: 'Cedar Bend',
    type: 'MultiFamily',
    rentalStructure: rentalStructure,
    status: 'Active',
    addressLine1: '92 Cedar Bend',
    city: 'Columbus',
    state: 'OH',
    postalCode: '43215',
    unitCount: unitCount,
    occupiedUnits: 2,
    createdAt: DateTime(2026),
    updatedAt: DateTime(2027, 2, 12),
  );
}

final _authenticatedState = AuthStateAuthenticated(
  const AuthUser(
    id: 1,
    email: 'admin@example.test',
    displayName: 'Admin',
    emailVerified: true,
  ),
  const AccessEnvelope(
    identity: AccessIdentity(userId: 1, displayName: 'Admin'),
    selectedContext: SelectedAccessContext(
      accessContextId: 1,
      portfolioId: 1,
      workspaceName: 'Test workspace',
      accessRevision: 1,
      activeExperience: WorkspaceExperience.management,
    ),
    defaultExperience: WorkspaceExperience.management,
    availableExperiences: [WorkspaceExperience.management],
    assignments: [
      AccessAssignment(
        assignmentId: 1,
        roleProfileKey: 'administrator',
        roleProfileName: 'Administrator',
        status: 'Active',
        scope: AccessAssignmentScope(
          kind: 'AllProperties',
          selectedPropertyCount: 0,
          selectedProperties: [],
        ),
      ),
    ],
    navigation: [
      NavigationCapabilities(
        experience: WorkspaceExperience.management,
        capabilityKeys: ['rentals.manage'],
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
