import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

void main() {
  test('portfolio rent KPI qualifies the governing lease value', () {
    final source = File(
      'lib/features/analytics/insights_screen.dart',
    ).readAsStringSync();

    expect(source, contains("label: 'Signed lease rent'"));
    expect(source, contains("subtitle: 'currently governing'"));
    expect(
      source,
      contains('value: moneyFmt(overview.monthlyRecurringRent)'),
    );
    expect(source, isNot(contains("label: 'Monthly Rent'")));
    expect(source, isNot(contains("subtitle: 'recurring'")));
  });
}
