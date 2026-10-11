import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/widgets/mobile_grid_controls.dart';

void main() {
  testWidgets('searches after typing settles and coalesces quick changes', (
    tester,
  ) async {
    final controller = TextEditingController();
    addTearDown(controller.dispose);
    final searches = <String>[];

    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: MobileGridSearchField(
            controller: controller,
            labelText: 'Search properties',
            onSubmitted: searches.add,
            onClear: controller.clear,
          ),
        ),
      ),
    );

    await tester.enterText(find.byType(SearchBar), 'Map');
    await tester.pump(const Duration(milliseconds: 300));

    expect(searches, ['Map']);

    await tester.enterText(find.byType(SearchBar), 'Mapl');
    await tester.pump(const Duration(milliseconds: 100));
    await tester.enterText(find.byType(SearchBar), 'Maple');
    await tester.pump(const Duration(milliseconds: 300));

    expect(searches, ['Map', 'Maple']);
  });
}
