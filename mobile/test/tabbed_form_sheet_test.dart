import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/widgets/tabbed_form_sheet.dart';

void main() {
  testWidgets('tabbed form sheet advances with next instead of saving early', (
    tester,
  ) async {
    var saves = 0;
    await _pumpTabbedForm(
      tester,
      onSave: () async {
        saves++;
      },
    );

    expect(find.text('Basics body'), findsOneWidget);
    expect(find.text('Next'), findsOneWidget);
    expect(find.text('Save'), findsNothing);
    expect(find.byKey(const Key('tabbed-form-complete-0')), findsNothing);

    await tester.enterText(find.byKey(const Key('basics-field')), 'Roof leak');
    await tester.tap(find.text('Next'));
    await tester.pump();

    expect(find.byIcon(Icons.check), findsAtLeastNWidgets(1));
    expect(saves, 0);

    await tester.pump(const Duration(milliseconds: 420));

    expect(find.text('Money body'), findsOneWidget);
    expect(find.byKey(const Key('tabbed-form-complete-0')), findsOneWidget);
    expect(find.byKey(const Key('tabbed-form-current-1')), findsOneWidget);
    expect(find.text('Save'), findsNothing);
  });

  testWidgets('tabbed form sheet does not draw a line under the tabs', (
    tester,
  ) async {
    await _pumpTabbedForm(tester);

    expect(find.byType(LinearProgressIndicator), findsNothing);
    expect(find.byType(TabBar), findsNothing);
    expect(find.byKey(const Key('tabbed-form-step-0')), findsOneWidget);
  });

  testWidgets('next validates the current card before advancing', (
    tester,
  ) async {
    await _pumpTabbedForm(tester);

    await tester.tap(find.text('Next'));
    await tester.pump();

    expect(find.text('Name is required'), findsOneWidget);
    expect(find.text('Basics body'), findsOneWidget);
    expect(find.byKey(const Key('tabbed-form-current-0')), findsOneWidget);
    expect(find.byKey(const Key('tabbed-form-error-0')), findsOneWidget);
  });

  testWidgets('next also runs custom step validation', (tester) async {
    var customValidationPassed = false;
    await _pumpTabbedForm(
      tester,
      firstStepValidate: () => customValidationPassed,
    );

    await tester.enterText(find.byKey(const Key('basics-field')), 'Roof leak');
    await tester.tap(find.text('Next'));
    await tester.pumpAndSettle();

    expect(find.text('Basics body'), findsOneWidget);
    expect(find.byKey(const Key('tabbed-form-current-0')), findsOneWidget);

    customValidationPassed = true;
    await tester.tap(find.text('Next'));
    await tester.pumpAndSettle();

    expect(find.text('Money body'), findsOneWidget);
    expect(find.byKey(const Key('tabbed-form-current-1')), findsOneWidget);
  });

  testWidgets('forward tab taps revalidate the current card', (tester) async {
    await _pumpTabbedForm(tester);

    await tester.enterText(find.byKey(const Key('basics-field')), 'Roof leak');
    await tester.tap(find.text('Next'));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Basics'));
    await tester.pumpAndSettle();

    await tester.enterText(find.byKey(const Key('basics-field')), '');
    await tester.tap(find.text('Money'));
    await tester.pump();

    expect(find.text('Name is required'), findsOneWidget);
    expect(find.text('Basics body'), findsOneWidget);
    expect(find.byKey(const Key('tabbed-form-current-0')), findsOneWidget);
  });

  testWidgets('final save validates, completes, and shows a green snackbar', (
    tester,
  ) async {
    var saves = 0;
    await _pumpTabbedForm(tester, onSave: () async => saves++);

    await tester.enterText(find.byKey(const Key('basics-field')), 'Roof leak');
    await tester.tap(find.text('Next'));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Next'));
    await tester.pumpAndSettle();

    expect(find.text('Save'), findsOneWidget);

    await tester.tap(find.text('Save'));
    await tester.pump();

    expect(find.text('Notes are required'), findsOneWidget);
    expect(saves, 0);

    await tester.enterText(find.byKey(const Key('notes-field')), 'Call vendor');
    await tester.tap(find.text('Save'));
    await tester.pump();

    expect(find.byIcon(Icons.check), findsAtLeastNWidgets(1));

    await tester.pumpAndSettle();

    expect(saves, 1);
    expect(find.text('Save complete'), findsOneWidget);
    expect(
      tester.widget<SnackBar>(find.byType(SnackBar)).backgroundColor,
      const Color(0xFF2E7D32),
    );
  });
}

Future<void> _pumpTabbedForm(
  WidgetTester tester, {
  Future<void> Function()? onSave,
  bool Function()? firstStepValidate,
}) {
  return tester.pumpWidget(
    MaterialApp(
      home: Scaffold(
        body: TabbedFormSheet(
          title: 'Dense form',
          saveLabel: 'Save',
          saving: false,
          onSave: onSave ?? () async {},
          tabs: [
            TabbedFormStepSpec(
              label: 'Basics',
              validate: firstStepValidate,
              child: Column(
                children: [
                  const Text('Basics body'),
                  TextFormField(
                    key: const Key('basics-field'),
                    decoration: const InputDecoration(labelText: 'Name'),
                    validator: (value) =>
                        (value == null || value.trim().isEmpty)
                        ? 'Name is required'
                        : null,
                  ),
                ],
              ),
            ),
            const TabbedFormStepSpec(label: 'Money', child: Text('Money body')),
            TabbedFormStepSpec(
              label: 'Notes',
              child: Column(
                children: [
                  const Text('Notes body'),
                  TextFormField(
                    key: const Key('notes-field'),
                    decoration: const InputDecoration(labelText: 'Notes'),
                    validator: (value) =>
                        (value == null || value.trim().isEmpty)
                        ? 'Notes are required'
                        : null,
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    ),
  );
}
