import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/home/mobile_quick_action_fab.dart';

void main() {
  testWidgets('money quick actions expose manual payment and expense entry', (
    tester,
  ) async {
    var paymentTapped = false;
    var expenseTapped = false;

    await tester.pumpWidget(
      ProviderScope(
        child: MaterialApp(
          home: Scaffold(
            body: const SizedBox.shrink(),
            floatingActionButton: MobileQuickActionFab(
              heroTag: 'test-money-actions',
              primaryActions: [
                MobileQuickAction(
                  label: 'Add payment',
                  icon: Icons.add_card_outlined,
                  onPressed: () => paymentTapped = true,
                ),
                MobileQuickAction(
                  label: 'Add expense',
                  icon: Icons.receipt_long_outlined,
                  onPressed: () => expenseTapped = true,
                ),
              ],
              onChat: () {},
              onRecord: () {},
              onScan: () {},
            ),
          ),
        ),
      ),
    );

    await tester.tap(find.byTooltip('Open quick actions'));
    await tester.pumpAndSettle();

    expect(find.text('Add payment'), findsOneWidget);
    expect(find.text('Add expense'), findsOneWidget);

    await tester.tap(find.text('Add payment'));
    await tester.pumpAndSettle();
    await tester.tap(find.byTooltip('Open quick actions'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Add expense'));

    expect(paymentTapped, isTrue);
    expect(expenseTapped, isTrue);
  });
}
