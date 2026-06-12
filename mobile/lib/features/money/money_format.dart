/// Shared money/date formatting for the Money feature, matching the
/// thousands-separated `$1,234.56` style used across the app.
String moneyFmt(num amount) {
  final isNegative = amount < 0;
  final abs = amount.abs();
  final parts = abs.toStringAsFixed(2).split('.');
  final intPart = parts[0];
  final decPart = parts[1];
  final buf = StringBuffer();
  final len = intPart.length;
  for (var i = 0; i < len; i++) {
    if (i > 0 && (len - i) % 3 == 0) buf.write(',');
    buf.write(intPart[i]);
  }
  return '\$${isNegative ? '-' : ''}$buf.$decPart';
}

const _months = [
  '',
  'Jan',
  'Feb',
  'Mar',
  'Apr',
  'May',
  'Jun',
  'Jul',
  'Aug',
  'Sep',
  'Oct',
  'Nov',
  'Dec',
];

/// `Mar 3, 2026`
String dateFmt(DateTime d) {
  if (d.year <= 1) return '';
  return '${_months[d.month]} ${d.day}, ${d.year}';
}

/// `Mar 3` (no year) — for compact rows.
String shortDateFmt(DateTime d) {
  if (d.year <= 1) return '';
  return '${_months[d.month]} ${d.day}';
}
