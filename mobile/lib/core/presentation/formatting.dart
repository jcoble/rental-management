// Shared money and date formatting for the whole app: the thousands-separated
// `$1,234.56` money style and the `Mar 3, 2026` date style.

/// Month abbreviations indexed by month number (1–12); index 0 is unused.
const monthAbbrs = [
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

/// `$1,234.56`, or `$1,235` when [whole] is set (rounded, no cents).
String moneyFmt(num amount, {bool whole = false}) {
  final num value = whole ? amount.round() : amount;
  final isNegative = value < 0;
  final abs = value.abs();
  final String intPart;
  final String? decPart;
  if (whole) {
    intPart = abs.toString();
    decPart = null;
  } else {
    final parts = abs.toStringAsFixed(2).split('.');
    intPart = parts[0];
    decPart = parts[1];
  }
  final buf = StringBuffer();
  final len = intPart.length;
  for (var i = 0; i < len; i++) {
    if (i > 0 && (len - i) % 3 == 0) buf.write(',');
    buf.write(intPart[i]);
  }
  final cents = decPart == null ? '' : '.$decPart';
  return '${isNegative ? '-' : ''}\$$buf$cents';
}

/// `Mar 3, 2026`
String dateFmt(DateTime d) {
  if (d.year <= 1) return '';
  return '${monthAbbrs[d.month]} ${d.day}, ${d.year}';
}

/// `Mar 3` (no year) — for compact rows.
String shortDateFmt(DateTime d) {
  if (d.year <= 1) return '';
  return '${monthAbbrs[d.month]} ${d.day}';
}

/// Returns a compact chart label without guessing at malformed source data.
String shortMonthLabel(String? value) {
  final label = value?.trim() ?? '';
  if (label.isEmpty) return '—';

  final canonicalMonth = RegExp(r'^\d{4}-(0[1-9]|1[0-2])$').firstMatch(label);
  if (canonicalMonth == null) return label;

  return monthAbbrs[int.parse(canonicalMonth.group(1)!)];
}
