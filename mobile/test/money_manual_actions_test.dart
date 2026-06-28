import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/money/money_screen.dart';

void main() {
  testWidgets('money quick actions expose manual payment and expense entry', (
    tester,
  ) async {
    var paymentTapped = false;
    var expenseTapped = false;

    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: MoneyQuickActions(
            onAddPayment: () => paymentTapped = true,
            onAddExpense: () => expenseTapped = true,
          ),
        ),
      ),
    );

    expect(find.text('Add payment'), findsOneWidget);
    expect(find.text('Add expense'), findsOneWidget);

    await tester.tap(find.text('Add payment'));
    await tester.tap(find.text('Add expense'));

    expect(paymentTapped, isTrue);
    expect(expenseTapped, isTrue);
  });
}
