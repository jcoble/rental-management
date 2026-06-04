import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import 'applications_models.dart';

/// Repository for rental applications (landlord-facing review).
///
/// Endpoints used (all JWT-scoped, portfolio from claim):
///   GET    /applications?status=     — list, optionally filtered by status
///   GET    /applications/{id}        — single application
///   POST   /applications/{id}/approve  — approve (also creates a Tenant)
///   POST   /applications/{id}/decline  — decline with optional { reason }
///   POST   /applications/{id}/withdraw — withdraw
///   POST   /applications/link        — mint a shareable apply link
class ApplicationsRepository {
  ApplicationsRepository(this._dio);

  final Dio _dio;

  Future<List<RentalApplication>> list({String? status}) async {
    try {
      final response = await _dio.get<List<dynamic>>(
        '/applications',
        queryParameters:
            (status == null || status.isEmpty) ? null : {'status': status},
      );
      return (response.data ?? [])
          .whereType<Map<String, dynamic>>()
          .map(RentalApplication.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<RentalApplication> get(int id) async {
    try {
      final response =
          await _dio.get<Map<String, dynamic>>('/applications/$id');
      return RentalApplication.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Approve — returns { applicationId, status, tenantId }. Also creates a Tenant.
  Future<ApplicationApproval> approve(int id) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/applications/$id/approve',
        data: {},
      );
      return ApplicationApproval.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Decline with an optional reason — returns the updated application.
  Future<RentalApplication> decline(int id, {String? reason}) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/applications/$id/decline',
        data: {
          if (reason != null && reason.isNotEmpty) 'reason': reason,
        },
      );
      return RentalApplication.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Withdraw — returns the updated application.
  Future<RentalApplication> withdraw(int id) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/applications/$id/withdraw',
        data: {},
      );
      return RentalApplication.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Mint a shareable apply link.
  Future<ApplicationLink> createLink() async {
    try {
      final response =
          await _dio.post<Map<String, dynamic>>('/applications/link', data: {});
      return ApplicationLink.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

// ── Providers ─────────────────────────────────────────────────────────────────

final applicationsRepositoryProvider = Provider<ApplicationsRepository>((ref) {
  return ApplicationsRepository(ref.watch(dioProvider));
});

/// List of applications filtered by [status] (null/empty = all statuses).
final applicationsProvider = FutureProvider.autoDispose
    .family<List<RentalApplication>, String?>((ref, status) {
  return ref.watch(applicationsRepositoryProvider).list(status: status);
});

/// A single application by id.
final applicationDetailProvider = FutureProvider.autoDispose
    .family<RentalApplication, int>((ref, id) {
  return ref.watch(applicationsRepositoryProvider).get(id);
});
