import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/home/mobile_quick_action_fab.dart';

void main() {
  testWidgets('quick action FAB expands into chat record and scan actions', (
    tester,
  ) async {
    var chatCount = 0;
    var recordCount = 0;
    var scanCount = 0;

    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          floatingActionButton: MobileQuickActionFab(
            onChat: () => chatCount++,
            onRecord: () => recordCount++,
            onScan: () => scanCount++,
          ),
        ),
      ),
    );

    expect(find.text('Chat'), findsNothing);
    expect(find.text('Record'), findsNothing);
    expect(find.text('Scan'), findsNothing);

    await tester.tap(find.byTooltip('Open quick actions'));
    await tester.pumpAndSettle();

    expect(find.text('Chat'), findsOneWidget);
    expect(find.text('Record'), findsOneWidget);
    expect(find.text('Scan'), findsOneWidget);

    await tester.tap(find.text('Record'));
    await tester.pumpAndSettle();

    expect(recordCount, 1);
    expect(chatCount, 0);
    expect(scanCount, 0);
    expect(find.text('Record'), findsNothing);
  });

  testWidgets('quick action FAB keeps an existing page action in the menu', (
    tester,
  ) async {
    var primaryCount = 0;

    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          floatingActionButton: MobileQuickActionFab(
            primaryAction: MobileQuickAction(
              label: 'New work order',
              icon: Icons.add,
              onPressed: () => primaryCount++,
            ),
            onChat: () {},
            onRecord: () {},
            onScan: () {},
          ),
        ),
      ),
    );

    await tester.tap(find.byTooltip('Open quick actions'));
    await tester.pumpAndSettle();

    expect(find.text('New work order'), findsOneWidget);
    expect(find.text('Chat'), findsOneWidget);
    expect(find.text('Record'), findsOneWidget);
    expect(find.text('Scan'), findsOneWidget);

    await tester.tap(find.text('New work order'));
    await tester.pumpAndSettle();

    expect(primaryCount, 1);
  });
}
