import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/widgets/mobile_grid_controls.dart';

void main() {
  testWidgets(
    'MobileGridSearchField releases focus when Android Back dismisses keyboard',
    (tester) async {
      addTearDown(tester.view.reset);

      final controller = TextEditingController();
      addTearDown(controller.dispose);
      var rowTapped = false;

      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: Padding(
              padding: const EdgeInsets.all(16),
              child: Column(
                children: [
                  MobileGridSearchField(
                    controller: controller,
                    labelText: 'Search leases',
                    onSubmitted: (_) {},
                    onClear: controller.clear,
                  ),
                  ListTile(
                    key: const Key('lease-row'),
                    title: const Text('Lease #R234323'),
                    onTap: () => rowTapped = true,
                  ),
                ],
              ),
            ),
          ),
        ),
      );

      await tester.tap(find.byType(SearchBar));
      await tester.showKeyboard(find.byType(EditableText));
      tester.view.viewInsets = const FakeViewPadding(bottom: 500);
      await tester.pump();

      expect(find.byTooltip('Close search'), findsOneWidget);

      tester.view.viewInsets = FakeViewPadding.zero;
      await tester.pumpAndSettle();

      expect(find.byTooltip('Search'), findsOneWidget);

      await tester.tap(find.byKey(const Key('lease-row')));
      await tester.pump();

      expect(rowTapped, isTrue);
      expect(find.byTooltip('Search'), findsOneWidget);
    },
  );
}
