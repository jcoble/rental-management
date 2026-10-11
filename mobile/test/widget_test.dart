import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/places/address_autocomplete_field.dart';
import 'package:rental_command/features/places/places_repository.dart';

// Placeholder test file. Feature tests will be added in Phase 5.
void main() {
  test('placeholder', () {
    expect(true, isTrue);
  });

  testWidgets('address suggestions hide when the field loses focus', (
    tester,
  ) async {
    final controller = TextEditingController();
    addTearDown(controller.dispose);

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          placesRepositoryProvider.overrideWithValue(_FakePlacesRepository()),
        ],
        child: MaterialApp(
          home: Scaffold(
            body: Column(
              children: [
                AddressAutocompleteField(
                  controller: controller,
                  testKey: 'address-field',
                ),
                const SizedBox(height: 300),
                const TextField(key: Key('other-field')),
              ],
            ),
          ),
        ),
      ),
    );

    await tester.tap(find.byKey(const Key('address-field')));
    await tester.enterText(find.byKey(const Key('address-field')), '123 Main');
    await tester.pump(const Duration(milliseconds: 250));
    await tester.pump();

    expect(find.text('123 Main Street'), findsOneWidget);
    await tester.tap(find.byKey(const Key('other-field')));
    await tester.pump();
    expect(find.text('123 Main Street'), findsNothing);
  });
}

class _FakePlacesRepository extends PlacesRepository {
  _FakePlacesRepository() : super(dio: Dio());

  @override
  Future<List<PlaceSuggestion>> autocomplete(
    String query,
    String session,
  ) async {
    return const [
      PlaceSuggestion(
        placeId: 'place-1',
        primary: '123 Main Street',
        secondary: 'Akron',
      ),
    ];
  }
}
