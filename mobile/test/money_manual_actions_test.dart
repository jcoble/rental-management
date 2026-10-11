import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/time/app_clock.dart';
import 'package:rental_command/features/home/mobile_quick_action_fab.dart';
import 'package:rental_command/features/payments/payments_screen.dart';

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

  testWidgets(
    'record receipt sheet defaults received date from the app clock',
    (tester) async {
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            appNowProvider.overrideWith(
              (ref) async => DateTime.utc(2027, 1, 5, 5),
            ),
          ],
          child: MaterialApp(
            home: Consumer(
              builder: (context, ref, _) => Scaffold(
                body: Center(
                  child: FilledButton(
                    onPressed: () => showRecordTenantReceiptSheet(
                      context,
                      ref,
                      tenantAccountId: 42,
                      leaseManagementId: 7,
                    ),
                    child: const Text('Open receipt sheet'),
                  ),
                ),
              ),
            ),
          ),
        ),
      );

      await tester.tap(find.text('Open receipt sheet'));
      await tester.pumpAndSettle();

      expect(find.text('Received on'), findsOneWidget);
      expect(find.text('Jan 5, 2027'), findsOneWidget);
    },
  );
}
