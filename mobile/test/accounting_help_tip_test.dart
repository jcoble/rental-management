import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/accounting/accounting_help.dart';
import 'package:rental_command/features/accounting/accounting_help_tip.dart';

void main() {
  test('every accounting help topic uses one of the five public KB slugs', () {
    expect(accountingHelp.keys.toSet(), AccountingHelpTopic.values.toSet());

    for (final entry in accountingHelp.values) {
      expect(
        knownAccountingHelpSlugs,
        contains(entry.slug),
        reason: '${entry.title} references an unknown KB slug',
      );
      expect(
        entry.uri.toString(),
        'https://rentalcommand.net/docs/${entry.slug}',
      );
    }
  });

  testWidgets('help button shows its plain-English tip', (tester) async {
    await tester.pumpWidget(
      const MaterialApp(
        home: Scaffold(
          body: AccountingHelpTipButton(
            topic: AccountingHelpTopic.cashPosition,
          ),
        ),
      ),
    );

    await tester.tap(
      find.byKey(const ValueKey('accounting-help-cashPosition')),
    );
    await tester.pumpAndSettle();

    expect(find.text('Cash and tenant deposits'), findsOneWidget);
    expect(
      find.textContaining('Cash on hand includes deposit money.'),
      findsOneWidget,
    );
    expect(find.text('Learn more'), findsOneWidget);
  });

  testWidgets('Learn more opens the public KB link', (tester) async {
    Uri? openedUri;

    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: AccountingHelpTipButton(
            topic: AccountingHelpTopic.tenantLedger,
            openLink: (uri) async {
              openedUri = uri;
              return true;
            },
          ),
        ),
      ),
    );

    await tester.tap(
      find.byKey(const ValueKey('accounting-help-tenantLedger')),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('accounting-help-learn-more')));
    await tester.pump();

    expect(
      openedUri,
      Uri.parse('https://rentalcommand.net/docs/lease-and-tenant-ledgers'),
    );
  });
}
