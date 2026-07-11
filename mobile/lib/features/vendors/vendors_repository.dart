import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:uuid/uuid.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import 'vendors_models.dart';

/// Repository for vendors, their scorecards, ratings, and SMS dispatch.
///
/// Endpoints (all JWT-scoped, portfolio from claim):
///   GET    /vendors                         — list (incl. rating summary)
///   POST   /vendors                         — create vendor
///   PATCH  /vendors/{id}                    — update vendor
///   DELETE /vendors/{id}                    — delete vendor
///   GET    /vendors/{id}/scorecard          — performance scorecard
///   POST   /vendors/{id}/ratings            — record a 1–5 star rating
///   POST   /vendors/{id}/request-w9         — text the vendor a W-9 request
///   POST   /work-orders/{id}/dispatch       — text a vendor the job
class VendorsRepository {
  VendorsRepository(this._dio);

  final Dio _dio;

  Future<List<Vendor>> list({
    int skip = 0,
    int take = 50,
    String sort = 'name',
  }) async {
    try {
      final response = await _dio.get<List<dynamic>>(
        '/vendors',
        queryParameters: {'skip': skip, 'take': take, 'sort': sort},
      );
      return (response.data ?? [])
          .whereType<Map<String, dynamic>>()
          .map(Vendor.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<VendorPage> listPage([
    VendorListQuery query = const VendorListQuery(),
  ]) async {
    final parameters = <String, dynamic>{
      'skip': query.skip,
      'take': query.take,
      'search': query.search,
      'sort': query.sort,
    }..removeWhere((_, value) => value == null || value == '');

    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/vendors/page',
        queryParameters: parameters,
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return VendorPage.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<Vendor> createVendor(Map<String, dynamic> data) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/vendors',
        data: data,
      );
      final responseData = response.data;
      if (responseData == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return Vendor.fromJson(responseData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<Vendor> updateVendor(int id, Map<String, dynamic> data) async {
    try {
      final response = await _dio.patch<Map<String, dynamic>>(
        '/vendors/$id',
        data: data,
      );
      final responseData = response.data;
      if (responseData == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return Vendor.fromJson(responseData);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> deleteVendor(int id) async {
    try {
      await _dio.delete<dynamic>('/vendors/$id');
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<VendorScorecard> scorecard(int vendorId) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/vendors/$vendorId/scorecard',
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return VendorScorecard.fromJson(data);
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

  /// Texts the vendor a request to send back their W-9. Throws [ApiException]
  /// (400) when the vendor has no phone number on file.
  Future<W9RequestResult> requestW9(
    int vendorId, {
    required String clientOperationId,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/vendors/$vendorId/request-w9',
        data: {'clientOperationId': clientOperationId},
      );
      return W9RequestResult.fromJson(response.data ?? const {});
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Marks whether a signed W-9 is on file for the vendor (PATCH /vendors/{id}).
  Future<void> setW9OnFile(int vendorId, bool w9OnFile) async {
    try {
      await _dio.patch<Map<String, dynamic>>(
        '/vendors/$vendorId',
        data: {'w9OnFile': w9OnFile},
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
          'idempotencyKey': const Uuid().v4(),
          'vendorId': vendorId,
          if (trimmed != null && trimmed.isNotEmpty) 'note': trimmed,
        },
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return VendorDispatchResult.fromJson(data);
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

final vendorsBySortProvider = FutureProvider.autoDispose
    .family<List<Vendor>, String>((ref, sort) {
      return ref.watch(vendorsRepositoryProvider).list(sort: sort);
    });

final vendorsPageProvider = FutureProvider.autoDispose
    .family<VendorPage, VendorListQuery>((ref, query) {
      return ref.watch(vendorsRepositoryProvider).listPage(query);
    });

/// Performance scorecard for a single vendor.
final vendorScorecardProvider = FutureProvider.autoDispose
    .family<VendorScorecard, int>((ref, vendorId) {
      return ref.watch(vendorsRepositoryProvider).scorecard(vendorId);
    });
