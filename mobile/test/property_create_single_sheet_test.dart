import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/models/models.dart';
import 'package:rental_command/features/properties/properties_list_screen.dart';
import 'package:rental_command/features/properties/properties_repository.dart';
import 'package:rental_command/features/units/unit_form_sheet.dart';

void main() {
  testWidgets('create property sheet shows five essentials and no tabs', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(420, 1200));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    await _pumpCreateSheet(tester, _FakePropertiesRepository());

    expect(find.byType(TabBar), findsNothing);
    expect(find.byKey(const Key('tabbed-form-step-0')), findsNothing);

    expect(find.text('Property name'), findsOneWidget);
    expect(find.text('Address'), findsOneWidget);
    expect(find.text('Monthly rent'), findsOneWidget);
    expect(find.text('Bedrooms'), findsOneWidget);
    expect(find.text('Bathrooms'), findsOneWidget);
    expect(find.text('Add rental'), findsOneWidget);

    expect(find.text('More details'), findsOneWidget);
    expect(find.text('Purchase price'), findsNothing);
    expect(find.text('Land value'), findsNothing);
    expect(find.text('Management fee %'), findsNothing);

    await tester.ensureVisible(find.text('More details'));
    await tester.tap(find.text('More details'));
    await tester.pumpAndSettle();

    expect(find.text('Purchase price'), findsOneWidget);
    expect(find.text('Land value'), findsOneWidget);
    expect(find.text('Management fee %'), findsOneWidget);
  });

  testWidgets(
    'single rental create posts one silent unit with rent beds baths',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(420, 1200));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      final repo = _FakePropertiesRepository();
      await _pumpCreateSheet(tester, repo);

      await tester.enterText(
        find.byKey(const Key('property-name-field')),
        'Cedar Cottage',
      );
      await tester.enterText(
        find.byKey(const Key('property-address-field')),
        '18 Cedar Lane',
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
      await tester.enterText(
        find.byKey(const Key('property-rental-market-rent-field')),
        '1450',
      );
      await tester.enterText(
        find.byKey(const Key('property-rental-bedrooms-field')),
        '3',
      );
      await tester.enterText(
        find.byKey(const Key('property-rental-bathrooms-field')),
        '1.5',
      );
      await tester.pump();

      await tester.tap(find.text('Add rental'));
      await tester.pumpAndSettle();

      expect(repo.createdPayload, containsPair('name', 'Cedar Cottage'));
      expect(
        repo.createdPayload,
        containsPair('addressLine1', '18 Cedar Lane'),
      );
      expect(repo.createdPayload, containsPair('city', 'Akron'));
      expect(repo.createdPayload, containsPair('state', 'OH'));
      expect(repo.createdPayload, containsPair('postalCode', '44308'));
      expect(repo.createdPayload, containsPair('type', 'SingleFamily'));
      expect(repo.createdPayload, containsPair('status', 'Active'));
      expect(
        repo.createdPayload,
        containsPair('rentalStructure', 'SingleRental'),
      );

      expect(repo.setupUnits, hasLength(1));
      expect(repo.setupUnits.single, containsPair('unitNumber', '1'));
      expect(repo.setupUnits.single, containsPair('bedrooms', 3));
      expect(repo.setupUnits.single, containsPair('bathrooms', 1.5));
      expect(repo.setupUnits.single, containsPair('marketRent', 1450.0));
    },
  );

  testWidgets('building toggle swaps to unit names', (tester) async {
    await tester.binding.setSurfaceSize(const Size(420, 1200));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    await _pumpCreateSheet(tester, _FakePropertiesRepository());

    expect(
      find.text('Does this address have more than one rental?'),
      findsOneWidget,
    );
    expect(
      find.byKey(const Key('property-rental-market-rent-field')),
      findsOneWidget,
    );
    expect(find.byKey(const Key('property-unit-numbers-field')), findsNothing);

    final toggle = find.byKey(const Key('property-multi-rental-field'));
    await tester.ensureVisible(toggle);
    await tester.tap(toggle);
    await tester.pumpAndSettle();

    expect(
      find.byKey(const Key('property-unit-numbers-field')),
      findsOneWidget,
    );
    expect(
      find.byKey(const Key('property-rental-market-rent-field')),
      findsNothing,
    );
    expect(
      find.byKey(const Key('property-rental-bedrooms-field')),
      findsNothing,
    );
  });

  testWidgets('unit sheet is one screen', (tester) async {
    await tester.binding.setSurfaceSize(const Size(420, 1200));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          propertiesRepositoryProvider.overrideWithValue(
            _FakePropertiesRepository(),
          ),
        ],
        child: const MaterialApp(
          home: Scaffold(body: UnitFormSheet(propertyId: 7)),
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.byType(TabBar), findsNothing);
    expect(find.byKey(const Key('tabbed-form-step-0')), findsNothing);

    expect(find.text('Unit number'), findsOneWidget);
    expect(find.text('Beds'), findsOneWidget);
    expect(find.text('Baths'), findsOneWidget);
    expect(find.text('Market rent (\$)'), findsOneWidget);

    expect(find.text('More details'), findsOneWidget);
    expect(find.text('Square feet'), findsNothing);
    expect(find.text('Floor plan'), findsNothing);

    await tester.ensureVisible(find.text('More details'));
    await tester.tap(find.text('More details'));
    await tester.pumpAndSettle();

    expect(find.text('Square feet'), findsOneWidget);
    expect(find.text('Floor plan'), findsOneWidget);
    expect(find.text('Notes'), findsOneWidget);
  });
}

Future<void> _pumpCreateSheet(
  WidgetTester tester,
  _FakePropertiesRepository repo,
) async {
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
}

class _FakePropertiesRepository extends PropertiesRepository {
  _FakePropertiesRepository() : super(Dio());

  Map<String, dynamic>? createdPayload;
  final setupUnits = <Map<String, dynamic>>[];

  @override
  Future<List<PropertyOwnerOption>> listOwnerOptions() async => const [
    PropertyOwnerOption(id: 42, name: 'North Coast Holdings'),
  ];

  @override
  Future<PropertySetupResult> setupProperty({
    int? propertyId,
    required Map<String, dynamic> property,
    required List<Map<String, dynamic>> units,
  }) async {
    createdPayload = Map<String, dynamic>.from(property);
    setupUnits
      ..clear()
      ..addAll(units.map(Map<String, dynamic>.from));

    final saved = Property(
      id: 1,
      portfolioId: 1,
      ownerships: const [],
      name: property['name'] as String? ?? '',
      type: property['type'] as String? ?? 'SingleFamily',
      rentalStructure: RentalStructure.fromJson(property['rentalStructure']),
      status: property['status'] as String? ?? 'Active',
      addressLine1: property['addressLine1'] as String? ?? '',
      city: property['city'] as String? ?? '',
      state: property['state'] as String? ?? '',
      postalCode: property['postalCode'] as String? ?? '',
      unitCount: units.length,
      createdAt: DateTime(2026),
      updatedAt: DateTime(2026),
    );

    return PropertySetupResult(
      property: saved,
      units: const [],
      updated: false,
    );
  }
}
