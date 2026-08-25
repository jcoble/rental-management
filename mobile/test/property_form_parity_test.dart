import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/models/models.dart';
import 'package:rental_command/features/owners/owners_models.dart';
import 'package:rental_command/features/owners/owners_repository.dart';
import 'package:rental_command/features/properties/properties_list_screen.dart';
import 'package:rental_command/features/properties/properties_repository.dart';
import 'package:rental_command/features/properties/property_form_sheet.dart';

void main() {
  testWidgets(
    'property stepper includes web property fields in create payload',
    (tester) async {
      final repo = _FakePropertiesRepository();
      final ownersRepo = _FakeOwnersRepository();

      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            propertiesRepositoryProvider.overrideWithValue(repo),
            ownersRepositoryProvider.overrideWithValue(ownersRepo),
          ],
          child: MaterialApp(
            home: Scaffold(
              body: Builder(
                builder: (context) => FilledButton(
                  onPressed: () => showAddPropertySheet(context),
                  child: const Text('Add property'),
                ),
              ),
            ),
          ),
        ),
      );

      await tester.tap(find.text('Add property'));
      await tester.pumpAndSettle();

      expect(find.text('Status'), findsOneWidget);
      expect(find.text('Owner'), findsOneWidget);
      expect(
        find.byKey(const Key('property-owner-add-button')),
        findsOneWidget,
      );
      expect(
        find.byKey(const Key('property-owner-edit-button')),
        findsOneWidget,
      );

      await tester.enterText(
        find.byKey(const Key('property-name-field')),
        'Vineyard Flats',
      );
      await tester.tap(find.byKey(const Key('property-status-field')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Under maintenance').last);
      await tester.pumpAndSettle();
      final ownerField = find.byKey(const Key('property-owner-field'));
      await tester.ensureVisible(ownerField);
      await tester.pumpAndSettle();
      await tester.tap(ownerField);
      await tester.pumpAndSettle();
      await tester.tap(find.text('North Coast Holdings').last);
      await tester.pumpAndSettle();
      await tester.tap(find.text('Next'));
      await tester.pumpAndSettle();

      expect(find.text('Rental details'), findsOneWidget);
      await tester.enterText(
        find.byKey(const Key('property-rental-bedrooms-field')),
        '3',
      );
      await tester.enterText(
        find.byKey(const Key('property-rental-bathrooms-field')),
        '2.5',
      );
      await tester.enterText(
        find.byKey(const Key('property-rental-market-rent-field')),
        '1875',
      );
      await tester.tap(find.text('Next'));
      await tester.pumpAndSettle();

      expect(find.text('Apt / Suite / Unit #'), findsOneWidget);
      await tester.enterText(
        find.byKey(const Key('property-address-field')),
        '401 Market St',
      );
      await tester.enterText(
        find.byKey(const Key('property-address2-field')),
        'Suite 12',
      );
      await tester.enterText(
        find.byKey(const Key('property-city-field')),
        'Akron',
      );
      await tester.enterText(
        find.byKey(const Key('property-state-field')),
        'OH',
      );
      await tester.enterText(
        find.byKey(const Key('property-zip-field')),
        '44308',
      );
      await tester.tap(find.text('Next'));
      await tester.pumpAndSettle();

      expect(find.text('Year built'), findsOneWidget);
      expect(find.text('Management fee %'), findsOneWidget);
      expect(find.text('Notes'), findsOneWidget);
      await tester.enterText(
        find.byKey(const Key('property-year-built-field')),
        '1998',
      );
      await tester.enterText(
        find.byKey(const Key('property-management-fee-field')),
        '8.5',
      );
      await tester.enterText(
        find.byKey(const Key('property-notes-field')),
        'North building has separate utility meters.',
      );
      await tester.tap(find.text('Next'));
      await tester.pumpAndSettle();

      expect(find.text('Purchase price'), findsOneWidget);
      expect(find.text('Land value'), findsOneWidget);
      expect(find.text('In-service date'), findsOneWidget);
      expect(find.text('Manual annual depreciation'), findsOneWidget);
      await tester.enterText(
        find.byKey(const Key('property-purchase-price-field')),
        '350000',
      );
      await tester.enterText(
        find.byKey(const Key('property-land-value-field')),
        '50000',
      );
      await tester.enterText(
        find.byKey(const Key('property-in-service-date-field')),
        '2024-01-15',
      );
      await tester.enterText(
        find.byKey(const Key('property-manual-depreciation-field')),
        '10909',
      );

      await tester.tap(find.text('Save Property'));
      await tester.pumpAndSettle();

      expect(repo.createdPayload, containsPair('name', 'Vineyard Flats'));
      expect(repo.createdPayload, containsPair('type', 'SingleFamily'));
      expect(
        repo.createdPayload,
        containsPair('rentalStructure', 'SingleRental'),
      );
      expect(repo.createdPayload, containsPair('status', 'UnderMaintenance'));
      final ownerships = repo.createdPayload!['ownerships'] as List<dynamic>;
      expect(ownerships, hasLength(1));
      final ownership = ownerships.single as Map<String, dynamic>;
      expect(ownership, containsPair('ownerEntityId', 42));
      expect(ownership, containsPair('ownershipSharePercent', 100));
      expect(repo.createdPayload, containsPair('clearOwnership', false));
      expect(repo.createdPayload, containsPair('addressLine2', 'Suite 12'));
      expect(repo.createdPayload, containsPair('yearBuilt', 1998));
      expect(repo.createdPayload, containsPair('managementFeePercent', 8.5));
      expect(
        repo.createdPayload,
        containsPair('notes', 'North building has separate utility meters.'),
      );
      expect(repo.createdPayload, containsPair('purchasePrice', 350000.0));
      expect(repo.createdPayload, containsPair('landValue', 50000.0));
      expect(repo.createdPayload, containsPair('inServiceDate', '2024-01-15'));
      expect(
        repo.createdPayload,
        containsPair('manualAnnualDepreciation', 10909.0),
      );
      expect(repo.setupUnits, hasLength(1));
      expect(
        repo.setupUnits.single,
        containsPair('unitNumber', 'Vineyard Flats'),
      );
      expect(repo.setupUnits.single, containsPair('bedrooms', 3));
      expect(repo.setupUnits.single, containsPair('bathrooms', 2.5));
      expect(repo.setupUnits.single, containsPair('marketRent', 1875.0));
    },
  );

  testWidgets('multi-rental setup accepts one initial unit atomically', (
    tester,
  ) async {
    final repo = _FakePropertiesRepository();

    await tester.pumpWidget(
      ProviderScope(
        overrides: [propertiesRepositoryProvider.overrideWithValue(repo)],
        child: MaterialApp(
          home: Scaffold(
            body: Builder(
              builder: (context) => FilledButton(
                onPressed: () => showAddPropertySheet(context),
                child: const Text('Add property'),
              ),
            ),
          ),
        ),
      ),
    );

    await tester.tap(find.text('Add property'));
    await tester.pumpAndSettle();
    await tester.enterText(
      find.byKey(const Key('property-name-field')),
      'Maple Court',
    );
    await tester.tap(find.byKey(const Key('property-type-field')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Multi-family').last);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Building with units'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Next'));
    await tester.pumpAndSettle();

    expect(find.text('Initial units'), findsOneWidget);
    await tester.enterText(
      find.byKey(const Key('property-unit-numbers-field')),
      '1A',
    );
    await tester.tap(find.text('Next'));
    await tester.pumpAndSettle();
    await tester.enterText(
      find.byKey(const Key('property-address-field')),
      '1200 Maple Avenue',
    );
    await tester.enterText(
      find.byKey(const Key('property-city-field')),
      'Columbus',
    );
    await tester.enterText(find.byKey(const Key('property-state-field')), 'OH');
    await tester.enterText(
      find.byKey(const Key('property-zip-field')),
      '43215',
    );
    await tester.tap(find.text('Next'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Next'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Save Property'));
    await tester.pumpAndSettle();

    expect(repo.createdPayload, containsPair('rentalStructure', 'MultiRental'));
    expect(repo.createdPayload, containsPair('type', 'MultiFamily'));
    expect(repo.setupUnits.map((unit) => unit['unitNumber']), ['1A']);
  });

  testWidgets('property owner picker can add and edit owners in place', (
    tester,
  ) async {
    final repo = _FakePropertiesRepository();
    final ownersRepo = _FakeOwnersRepository();

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          propertiesRepositoryProvider.overrideWithValue(repo),
          ownersRepositoryProvider.overrideWithValue(ownersRepo),
        ],
        child: MaterialApp(
          home: Scaffold(
            body: Builder(
              builder: (context) => FilledButton(
                onPressed: () => showAddPropertySheet(context),
                child: const Text('Add property'),
              ),
            ),
          ),
        ),
      ),
    );

    await tester.tap(find.text('Add property'));
    await tester.pumpAndSettle();

    final addOwnerButton = find.byKey(const Key('property-owner-add-button'));
    await tester.ensureVisible(addOwnerButton);
    await tester.pumpAndSettle();
    await tester.tap(addOwnerButton);
    await tester.pumpAndSettle();

    expect(find.text('New Owner'), findsOneWidget);
    await tester.enterText(
      find.byKey(const Key('owner-name-field')),
      'Blue Door LLC',
    );
    await tester.tap(find.text('Next').last);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Next').last);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Add Owner'));
    await tester.pumpAndSettle();

    expect(ownersRepo.createdPayload, containsPair('name', 'Blue Door LLC'));
    expect(find.text('Blue Door LLC'), findsOneWidget);

    final editOwnerButton = find.byKey(const Key('property-owner-edit-button'));
    await tester.ensureVisible(editOwnerButton);
    await tester.pumpAndSettle();
    await tester.tap(editOwnerButton);
    await tester.pumpAndSettle();

    expect(find.text('Edit Owner'), findsOneWidget);
    expect(find.text('Blue Door LLC'), findsWidgets);
    await tester.enterText(
      find.byKey(const Key('owner-name-field')),
      'Blue Door Holdings',
    );
    await tester.tap(find.text('Next').last);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Next').last);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Save Owner'));
    await tester.pumpAndSettle();

    expect(ownersRepo.updatedOwnerId, 77);
    expect(
      ownersRepo.updatedPayload,
      containsPair('name', 'Blue Door Holdings'),
    );
    expect(find.text('Blue Door Holdings'), findsOneWidget);
  });

  testWidgets('property owner row fits a 360px edit sheet', (tester) async {
    const ownerName = 'Northstar Property Partners LLC';
    final repo = _FakePropertiesRepository(
      ownerOptions: const [PropertyOwnerOption(id: 42, name: ownerName)],
    );

    await tester.binding.setSurfaceSize(const Size(360, 800));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    await tester.pumpWidget(
      ProviderScope(
        overrides: [propertiesRepositoryProvider.overrideWithValue(repo)],
        child: MaterialApp(
          home: Scaffold(
            body: Builder(
              builder: (context) => FilledButton(
                onPressed: () => showPropertyFormSheet(
                  context,
                  property: _property(
                    type: 'SingleFamily',
                    ownerName: ownerName,
                  ),
                ),
                child: const Text('Edit property'),
              ),
            ),
          ),
        ),
      ),
    );

    await tester.tap(find.text('Edit property'));
    await tester.pumpAndSettle();

    final ownerField = find.byKey(const Key('property-owner-field'));
    final addOwnerButton = find.byKey(const Key('property-owner-add-button'));
    final editOwnerButton = find.byKey(const Key('property-owner-edit-button'));

    expect(tester.takeException(), isNull);
    expect(ownerField, findsOneWidget);
    expect(addOwnerButton, findsOneWidget);
    expect(editOwnerButton, findsOneWidget);
    expect(
      tester.getRect(ownerField).right,
      lessThan(tester.getRect(addOwnerButton).left),
    );
    expect(
      tester.getRect(editOwnerButton).right,
      lessThanOrEqualTo(tester.getSize(find.byType(MaterialApp)).width),
    );

    final selectedOwner = tester.widget<Text>(find.text(ownerName));
    expect(selectedOwner.maxLines, 1);
    expect(selectedOwner.overflow, TextOverflow.ellipsis);
  });

  testWidgets('property edit omits creation-only rental structure', (
    tester,
  ) async {
    final repo = _FakePropertiesRepository();

    await tester.pumpWidget(
      ProviderScope(
        overrides: [propertiesRepositoryProvider.overrideWithValue(repo)],
        child: MaterialApp(
          home: Scaffold(
            body: Builder(
              builder: (context) => FilledButton(
                onPressed: () => showPropertyFormSheet(
                  context,
                  property: _property(type: 'SingleFamily'),
                ),
                child: const Text('Edit property'),
              ),
            ),
          ),
        ),
      ),
    );

    await tester.tap(find.text('Edit property'));
    await tester.pumpAndSettle();

    while (find.text('Next').evaluate().isNotEmpty) {
      await tester.tap(find.text('Next').first);
      await tester.pumpAndSettle();
    }
    await tester.tap(find.text('Save Property'));
    await tester.pumpAndSettle();

    expect(repo.updatedPayload, isNotNull);
    expect(repo.updatedPayload, isNot(contains('rentalStructure')));
  });
}

class _FakePropertiesRepository extends PropertiesRepository {
  _FakePropertiesRepository({
    this.ownerOptions = const [
      PropertyOwnerOption(id: 42, name: 'North Coast Holdings'),
    ],
  }) : super(Dio());

  Map<String, dynamic>? createdPayload;
  Map<String, dynamic>? updatedPayload;
  final setupUnits = <Map<String, dynamic>>[];
  final List<PropertyOwnerOption> ownerOptions;

  @override
  Future<List<PropertyOwnerOption>> listOwnerOptions() async => ownerOptions;

  @override
  Future<PropertySetupResult> setupProperty({
    int? propertyId,
    required Map<String, dynamic> property,
    required List<Map<String, dynamic>> units,
  }) async {
    expect(propertyId, isNull);
    final data = property;
    createdPayload = Map<String, dynamic>.from(data);
    setupUnits
      ..clear()
      ..addAll(units.map(Map<String, dynamic>.from));
    final structure = RentalStructure.fromJson(data['rentalStructure']);
    final saved = _property(
      type: data['type'] as String? ?? 'MultiFamily',
      rentalStructure: structure,
    );
    return PropertySetupResult(
      property: saved,
      units: [
        for (var index = 0; index < setupUnits.length; index++)
          Unit(
            id: index + 1,
            propertyId: saved.id,
            unitNumber: setupUnits[index]['unitNumber'] as String? ?? '',
            bedrooms: (setupUnits[index]['bedrooms'] as num?)?.toInt() ?? 0,
            bathrooms:
                (setupUnits[index]['bathrooms'] as num?)?.toDouble() ?? 0,
            marketRent:
                (setupUnits[index]['marketRent'] as num?)?.toDouble() ?? 0,
            status: setupUnits[index]['status'] as String? ?? 'Vacant',
            createdAt: DateTime(2026),
            updatedAt: DateTime(2026),
          ),
      ],
      updated: false,
    );
  }

  @override
  Future<Property> updateProperty(int id, Map<String, dynamic> data) async {
    updatedPayload = Map<String, dynamic>.from(data);
    return _property(type: data['type'] as String? ?? 'SingleFamily');
  }
}

class _FakeOwnersRepository extends OwnersRepository {
  _FakeOwnersRepository() : super(Dio());

  Map<String, dynamic>? createdPayload;
  int? updatedOwnerId;
  Map<String, dynamic>? updatedPayload;
  OwnerEntity owner = const OwnerEntity(
    id: 77,
    portfolioId: 1,
    ownerEntityType: OwnerEntityType.llc,
    name: 'Blue Door LLC',
  );

  @override
  Future<OwnerEntity> createOwner(Map<String, dynamic> data) async {
    createdPayload = Map<String, dynamic>.from(data);
    owner = OwnerEntity(
      id: 77,
      portfolioId: 1,
      ownerEntityType: OwnerEntityType.fromJson(data['ownerEntityType']),
      name: data['name'] as String? ?? 'Blue Door LLC',
    );
    return owner;
  }

  @override
  Future<OwnerEntity> getOwner(int id) async => owner;

  @override
  Future<OwnerEntity> updateOwner(int id, Map<String, dynamic> data) async {
    updatedOwnerId = id;
    updatedPayload = Map<String, dynamic>.from(data);
    owner = OwnerEntity(
      id: id,
      portfolioId: owner.portfolioId,
      ownerEntityType: OwnerEntityType.fromJson(data['ownerEntityType']),
      name: data['name'] as String? ?? owner.name,
    );
    return owner;
  }
}

Property _property({
  required String type,
  RentalStructure rentalStructure = RentalStructure.singleRental,
  String ownerName = 'North Coast Holdings',
}) {
  return Property(
    id: 1,
    portfolioId: 1,
    ownerships: [
      (
        id: 17,
        ownerEntityId: 42,
        ownerName: ownerName,
        ownershipSharePercent: 100,
        effectiveFromUtc: DateTime(2026),
        effectiveToUtc: null,
        statementRecipientName: ownerName,
        statementRecipientEmail: null,
        payeeName: ownerName,
      ),
    ],
    name: 'Vineyard Flats',
    type: type,
    rentalStructure: rentalStructure,
    status: 'UnderMaintenance',
    addressLine1: '401 Market St',
    addressLine2: 'Suite 12',
    city: 'Akron',
    state: 'OH',
    postalCode: '44308',
    yearBuilt: 1998,
    managementFeePercent: 8.5,
    notes: 'North building has separate utility meters.',
    unitCount: 0,
    createdAt: DateTime(2026),
    updatedAt: DateTime(2026),
  );
}
