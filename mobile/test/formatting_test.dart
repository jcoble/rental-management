import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/presentation/formatting.dart';

void main() {
  test('money shows cents by default and whole dollars on request', () {
    expect(moneyFmt(1234.5), r'$1,234.50');
    expect(moneyFmt(-1234.5), r'$-1,234.50');
    expect(moneyFmt(1200, whole: true), r'$1,200');
    expect(moneyFmt(1234.56, whole: true), r'$1,235');
  });

  test('dates read as Aug 25, 2026', () {
    expect(dateFmt(DateTime(2026, 8, 25)), 'Aug 25, 2026');
    expect(shortDateFmt(DateTime(2026, 8, 25)), 'Aug 25');
    expect(dateFmt(DateTime(1)), '');
  });

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
