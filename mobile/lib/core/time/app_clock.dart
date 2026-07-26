import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../api/dio_client.dart';

/// Resolves the clock that date-sensitive mobile presentation should use.
///
/// Preview/test environments expose the simulation-only `/dev/clock` route.
/// Production returns 404, where the device clock remains authoritative.
/// Other failures are surfaced instead of silently displaying a potentially
/// incorrect business date.
final appNowProvider = FutureProvider.autoDispose<DateTime>((ref) async {
  final dio = ref.read(dioProvider);
  try {
    final response = await dio.get<Map<String, dynamic>>('/dev/clock');
    final raw = response.data?['simNowUtc'];
    final parsed = raw is String ? DateTime.tryParse(raw) : null;
    if (parsed == null) {
      throw const FormatException(
        'The server returned an invalid simulation clock.',
      );
    }
    // The simulation controller's date command and the server's business-date
    // calculations use the UTC calendar date. Converting this instant to the
    // device zone can move a midnight simulation into the previous day.
    return parsed.toUtc();
  } on DioException catch (error) {
    if (error.response?.statusCode == 404) {
      return DateTime.now();
    }
    rethrow;
  }
});
