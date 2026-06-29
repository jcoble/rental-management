import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/notices/create_tenant_notice.dart';

void main() {
  testWidgets('review sheet shows editable subject and body fields', (tester) async {
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
    expect(find.text('Send notice'), findsOneWidget);
  });
}
