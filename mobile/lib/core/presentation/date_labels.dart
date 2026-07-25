const _shortMonths = <String>[
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

/// Returns a compact chart label without guessing at malformed source data.
String shortMonthLabel(String? value) {
  final label = value?.trim() ?? '';
  if (label.isEmpty) return '—';

  final canonicalMonth = RegExp(r'^\d{4}-(0[1-9]|1[0-2])$').firstMatch(label);
  if (canonicalMonth == null) return label;

  final month = int.parse(canonicalMonth.group(1)!);
  return _shortMonths[month - 1];
}
