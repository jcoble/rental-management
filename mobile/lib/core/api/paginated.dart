/// Parses a JSON value that is a list of objects into a typed Dart list.
///
/// [data] can be a [List<dynamic>] (e.g. the raw decoded JSON array).
/// Each element is cast to [Map<String,dynamic>] and forwarded to [fromJson].
/// Returns an empty list rather than throwing if [data] is null or not a list.
List<T> parseList<T>(
  dynamic data,
  T Function(Map<String, dynamic>) fromJson,
) {
  if (data is! List) return const [];
  return data
      .whereType<Map<String, dynamic>>()
      .map(fromJson)
      .toList();
}
