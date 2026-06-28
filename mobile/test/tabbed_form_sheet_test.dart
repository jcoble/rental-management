import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/widgets/tabbed_form_sheet.dart';

void main() {
  testWidgets('tabbed form sheet marks a visited tab complete', (tester) async {
    await _pumpTabbedForm(tester);

    expect(find.text('Basics body'), findsOneWidget);
    expect(find.byKey(const Key('tabbed-form-complete-0')), findsNothing);

    await tester.tap(find.text('Money'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 260));

    expect(find.text('Money body'), findsOneWidget);
    expect(find.byKey(const Key('tabbed-form-complete-0')), findsOneWidget);
    expect(find.byKey(const Key('tabbed-form-current-1')), findsOneWidget);
  });

  testWidgets('tabbed form sheet does not draw a line under the tabs', (
    tester,
  ) async {
    await _pumpTabbedForm(tester);

    expect(find.byType(LinearProgressIndicator), findsNothing);
    expect(
      tester.widget<TabBar>(find.byType(TabBar)).dividerColor,
      Colors.transparent,
    );
  });
}

Future<void> _pumpTabbedForm(WidgetTester tester) {
  return tester.pumpWidget(
    MaterialApp(
      home: Scaffold(
        body: TabbedFormSheet(
          title: 'Dense form',
          saveLabel: 'Save',
          saving: false,
          onSave: () {},
          tabs: const [
            TabbedFormStepSpec(label: 'Basics', child: Text('Basics body')),
            TabbedFormStepSpec(label: 'Money', child: Text('Money body')),
            TabbedFormStepSpec(label: 'Notes', child: Text('Notes body')),
          ],
        ),
      ),
    ),
  );
}
