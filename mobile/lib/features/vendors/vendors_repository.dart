import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import 'vendors_models.dart';

/// Repository for vendors, their scorecards, ratings, and SMS dispatch.
///
/// Endpoints (all JWT-scoped, portfolio from claim):
///   GET    /vendors                         — list (incl. rating summary)
///   GET    /vendors/{id}/scorecard          — performance scorecard
///   POST   /vendors/{id}/ratings            — record a 1–5 star rating
///   POST   /work-orders/{id}/dispatch       — text a vendor the job
class VendorsRepository {
  VendorsRepository(this._dio);

  final Dio _dio;

  Future<List<Vendor>> list() async {
    try {
      final response = await _dio.get<List<dynamic>>('/vendors');
      return (response.data ?? [])
          .whereType<Map<String, dynamic>>()
          .map(Vendor.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<VendorScorecard> scorecard(int vendorId) async {
    try {
      final response =
          await _dio.get<Map<String, dynamic>>('/vendors/$vendorId/scorecard');
      return VendorScorecard.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Record a 1–5 star rating for a vendor, optionally tied to a work order.
  Future<void> rate(
    int vendorId, {
    required int stars,
    String? comment,
    int? workOrderId,
  }) async {
    try {
      final trimmed = comment?.trim();
      await _dio.post<Map<String, dynamic>>(
        '/vendors/$vendorId/ratings',
        data: {
          'stars': stars,
          if (trimmed != null && trimmed.isNotEmpty) 'comment': trimmed,
          'workOrderId': ?workOrderId,
        },
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Text a vendor a work order. Throws [ApiException] (400) when the vendor
  /// has no phone number on file.
  Future<VendorDispatchResult> dispatch(
    int workOrderId, {
    required int vendorId,
    String? note,
  }) async {
    try {
      final trimmed = note?.trim();
      final response = await _dio.post<Map<String, dynamic>>(
        '/work-orders/$workOrderId/dispatch',
        data: {
          'vendorId': vendorId,
          if (trimmed != null && trimmed.isNotEmpty) 'note': trimmed,
        },
      );
      return VendorDispatchResult.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

// ── Providers ─────────────────────────────────────────────────────────────────

final vendorsRepositoryProvider = Provider<VendorsRepository>((ref) {
  return VendorsRepository(ref.watch(dioProvider));
});

/// All vendors with their rating summary.
final vendorsProvider = FutureProvider.autoDispose<List<Vendor>>((ref) {
  return ref.watch(vendorsRepositoryProvider).list();
});

/// Performance scorecard for a single vendor.
final vendorScorecardProvider = FutureProvider.autoDispose
    .family<VendorScorecard, int>((ref, vendorId) {
  return ref.watch(vendorsRepositoryProvider).scorecard(vendorId);
});
