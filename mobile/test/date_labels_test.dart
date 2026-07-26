import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/presentation/date_labels.dart';

void main() {
  test('canonical year-month values use understandable short month labels', () {
    expect(shortMonthLabel('2026-01'), 'Jan');
    expect(shortMonthLabel('2026-08'), 'Aug');
    expect(shortMonthLabel('2026-12'), 'Dec');
  });

  test('readable, blank, and invalid labels are handled without guessing', () {
    expect(shortMonthLabel('Jan'), 'Jan');
    expect(shortMonthLabel(''), '—');
    expect(shortMonthLabel('   '), '—');
    expect(shortMonthLabel(null), '—');
    expect(shortMonthLabel('2026-13'), '2026-13');
    expect(shortMonthLabel('not-a-month'), 'not-a-month');
  });
}
