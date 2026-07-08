import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/models/models.dart';
import 'package:rental_command/features/owners/owners_models.dart';
import 'package:rental_command/features/owners/owners_repository.dart';
import 'package:rental_command/features/properties/properties_list_screen.dart';
import 'package:rental_command/features/properties/properties_repository.dart';

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
      await tester.tap(find.byKey(const Key('property-owner-field')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('North Coast Holdings').last);
      await tester.pumpAndSettle();
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
      expect(repo.createdPayload, containsPair('status', 'UnderMaintenance'));
      expect(repo.createdPayload, containsPair('ownerEntityId', 42));
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
      expect(repo.createdUnits, hasLength(1));
      expect(
        repo.createdUnits.single,
        containsPair('unitNumber', 'Vineyard Flats'),
      );
      expect(repo.createdUnits.single, containsPair('status', 'Vacant'));
    },
  );

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

    await tester.tap(find.byKey(const Key('property-owner-add-button')));
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

    await tester.tap(find.byKey(const Key('property-owner-edit-button')));
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
}

class _FakePropertiesRepository extends PropertiesRepository {
  _FakePropertiesRepository() : super(Dio());

  Map<String, dynamic>? createdPayload;
  final createdUnits = <Map<String, dynamic>>[];
  final ownerOptions = <PropertyOwnerOption>[
    const PropertyOwnerOption(id: 42, name: 'North Coast Holdings'),
  ];

  @override
  Future<List<PropertyOwnerOption>> listOwnerOptions() async => ownerOptions;

  @override
  Future<Property> createProperty(Map<String, dynamic> data) async {
    createdPayload = Map<String, dynamic>.from(data);
    return _property(type: data['type'] as String? ?? 'MultiFamily');
  }

  @override
  Future<List<Unit>> listUnits(int propertyId) async => const [];

  @override
  Future<Unit> createUnit(int propertyId, Map<String, dynamic> data) async {
    createdUnits.add(Map<String, dynamic>.from(data));
    return Unit(
      id: createdUnits.length,
      propertyId: propertyId,
      unitNumber: data['unitNumber'] as String? ?? '',
      bedrooms: (data['bedrooms'] as num?)?.toInt() ?? 0,
      bathrooms: (data['bathrooms'] as num?)?.toDouble() ?? 0,
      marketRent: (data['marketRent'] as num?)?.toDouble() ?? 0,
      status: data['status'] as String? ?? 'Vacant',
      createdAt: DateTime(2026),
      updatedAt: DateTime(2026),
    );
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
      ownerEntityType:
          OwnerEntityType.fromJson(data['ownerEntityType']) ??
          owner.ownerEntityType,
      name: data['name'] as String? ?? owner.name,
    );
    return owner;
  }
}

Property _property({required String type}) {
  return Property(
    id: 1,
    portfolioId: 1,
    ownerEntityId: 42,
    name: 'Vineyard Flats',
    type: type,
    status: 'UnderMaintenance',
    addressLine1: '401 Market St',
    addressLine2: 'Suite 12',
    city: 'Akron',
    state: 'OH',
    postalCode: '44308',
    yearBuilt: 1998,
    managementFeePercent: 8.5,
    notes: 'North building has separate utility meters.',
    ownerName: 'North Coast Holdings',
    unitCount: 0,
    createdAt: DateTime(2026),
    updatedAt: DateTime(2026),
  );
}
