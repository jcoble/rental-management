/// Helpers for serializing user-entered wall-clock [DateTime]s onto the wire.
///
/// A landlord picks a wall-clock time ("2:00 PM") with a date + time picker. The
/// combined value is a *local* [DateTime] (`isUtc == false`). Dart's
/// [DateTime.toIso8601String] emits a local value with **no** trailing `Z` and
/// **no** offset (e.g. `2026-06-15T14:00:00.000`), which the API binds as
/// `DateTimeKind.Unspecified` and then relabels as UTC without converting — so
/// "2 PM" entered in US-Eastern is stored (and texted to the tenant) as "2 PM
/// UTC". See REVIEW-coherence.md C1.
///
/// [localToWireIso] fixes this by emitting the instant with the device's local
/// UTC offset (e.g. `2026-06-15T14:00:00-04:00`). The API binds that to a
/// zoned/local [DateTime] and converts it to the correct UTC instant before it
/// reaches Postgres, so the stored value is the true moment the landlord meant.
library;

/// Serializes a wall-clock [local] [DateTime] as ISO-8601 with the device's
/// local UTC offset (e.g. `2026-06-15T14:00:00-04:00`), so the API stores the
/// true instant rather than relabeling the wall-clock as UTC.
///
/// If [local] is already a UTC [DateTime] it is emitted with a `Z` suffix
/// unchanged (it is already an unambiguous instant).
String localToWireIso(DateTime local) {
  if (local.isUtc) return local.toIso8601String();

  final offset = local.timeZoneOffset;
  final sign = offset.isNegative ? '-' : '+';
  final abs = offset.abs();
  final hh = abs.inHours.toString().padLeft(2, '0');
  final mm = (abs.inMinutes % 60).toString().padLeft(2, '0');

  // Strip Dart's millisecond/microsecond fractional suffix and the implicit
  // local marker, then append the explicit numeric offset.
  final base = _isoSecondsNoZone(local);
  return '$base$sign$hh:$mm';
}

/// Formats a local [DateTime] to `yyyy-MM-ddTHH:mm:ss` (seconds precision, no
/// fractional part, no zone marker) — the body the offset is appended to.
String _isoSecondsNoZone(DateTime dt) {
  String two(int n) => n.toString().padLeft(2, '0');
  final y = dt.year.toString().padLeft(4, '0');
  return '$y-${two(dt.month)}-${two(dt.day)}'
      'T${two(dt.hour)}:${two(dt.minute)}:${two(dt.second)}';
}
