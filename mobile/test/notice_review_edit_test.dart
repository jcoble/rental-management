import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/notices/create_tenant_notice.dart';

void main() {
  testWidgets('safe review copy keeps Send notice enabled', (tester) async {
    await tester.pumpWidget(
      const MaterialApp(
        home: Scaffold(
          body: ReviewNoticeSheetTestHarness(
            subject: 'Original subject',
            body: 'Original body',
          ),
        ),
      ),
    );

    // Subject + body are editable text fields pre-filled with the draft copy.
    expect(find.widgetWithText(TextField, 'Original subject'), findsOneWidget);
    expect(find.widgetWithText(TextField, 'Original body'), findsOneWidget);
    expect(
      tester
          .widget<FilledButton>(
            find.widgetWithText(FilledButton, 'Send notice'),
          )
          .onPressed,
      isNotNull,
    );
  });

  testWidgets('unsafe review copy blocks send until corrected', (tester) async {
    await tester.pumpWidget(
      const MaterialApp(
        home: Scaffold(
          body: ReviewNoticeSheetTestHarness(
            subject: 'Notice for {Sofia Rodriguez}',
            body: 'Workspace administrator: review this first.',
          ),
        ),
      ),
    );

    FilledButton sendButton() => tester.widget<FilledButton>(
      find.widgetWithText(FilledButton, 'Send notice'),
    );

    expect(sendButton().onPressed, isNull);
    expect(
      find.text(
        'Review the notice and remove any unfinished merge fields or internal instructions before sending.',
      ),
      findsOneWidget,
    );

    await tester.enterText(
      find.widgetWithText(TextField, 'Notice for {Sofia Rodriguez}'),
      'Notice for Sofia Rodriguez',
    );
    await tester.enterText(
      find.widgetWithText(
        TextField,
        'Workspace administrator: review this first.',
      ),
      'Your rent is due Friday.',
    );
    await tester.pump();

    expect(sendButton().onPressed, isNotNull);
    expect(
      find.text(
        'Review the notice and remove any unfinished merge fields or internal instructions before sending.',
      ),
      findsNothing,
    );
  });
}
