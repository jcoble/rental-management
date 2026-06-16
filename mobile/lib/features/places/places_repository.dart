import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import '../../core/api/dio_client.dart';

/// One address suggestion (mirrors RentalCommand.Api PlaceSuggestion).
class PlaceSuggestion {
  const PlaceSuggestion({required this.placeId, required this.primary, required this.secondary});
  final String placeId;
  final String primary;
  final String secondary;

  factory PlaceSuggestion.fromJson(Map<String, dynamic> j) => PlaceSuggestion(
        placeId: (j['placeId'] ?? '').toString(),
        primary: (j['primary'] ?? '').toString(),
        secondary: (j['secondary'] ?? '').toString(),
      );
}

/// A resolved address split into the fields the forms own (mirrors ResolvedAddress).
class ResolvedAddress {
  const ResolvedAddress({required this.line1, required this.city, required this.state, required this.zip});
  final String line1;
  final String city;
  final String state;
  final String zip;

  factory ResolvedAddress.fromJson(Map<String, dynamic> j) => ResolvedAddress(
        line1: (j['line1'] ?? '').toString(),
        city: (j['city'] ?? '').toString(),
        state: (j['state'] ?? '').toString(),
        zip: (j['zip'] ?? '').toString(),
      );
}

/// Thin client over the EXISTING server-side Google Places proxy. The API key stays server-side;
/// this only calls /places/autocomplete and /places/details. Any failure (incl. enabled:false)
/// returns empty/null so the field degrades to plain manual entry.
class PlacesRepository {
  PlacesRepository({required Dio dio}) : _dio = dio;
  final Dio _dio;

  Future<List<PlaceSuggestion>> autocomplete(String query, String session) async {
    if (query.trim().length < 3) return const [];
    try {
      final res = await _dio.get<Map<String, dynamic>>(
        '/places/autocomplete',
        queryParameters: {'q': query, 'session': session},
      );
      final data = res.data;
      if (data == null || data['enabled'] != true) return const [];
      final list = (data['suggestions'] as List<dynamic>? ?? const []);
      return list
          .whereType<Map<String, dynamic>>()
          .map(PlaceSuggestion.fromJson)
          .toList(growable: false);
    } on DioException {
      return const [];
    }
  }

  Future<ResolvedAddress?> details(String placeId, String session) async {
    if (placeId.isEmpty) return null;
    try {
      final res = await _dio.get<Map<String, dynamic>>(
        '/places/details',
        queryParameters: {'placeId': placeId, 'session': session},
      );
      final data = res.data;
      if (data == null) return null;
      return ResolvedAddress.fromJson(data);
    } on DioException {
      return null;
    }
  }
}

final placesRepositoryProvider = Provider<PlacesRepository>((ref) {
  return PlacesRepository(dio: ref.watch(dioProvider));
});
