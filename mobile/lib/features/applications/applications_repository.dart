import 'dart:typed_data';

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

  /// Runs a tenant-screening request — returns the new result.
  ///
  /// Throws [ApiException] with `statusCode == 400` when FCRA consent is
  /// missing, and `statusCode == 503` when screening is not configured (the
  /// provider is dormant).
  Future<ScreeningResult> screen(int id) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/applications/$id/screen',
        data: {},
      );
      return ScreeningResult.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Lists all screening results for an application (newest typically first).
  Future<List<ScreeningResult>> screening(int id) async {
    try {
      final response =
          await _dio.get<List<dynamic>>('/applications/$id/screening');
      return (response.data ?? [])
          .whereType<Map<String, dynamic>>()
          .map(ScreeningResult.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Generates an FCRA adverse-action notice for a declined application.
  Future<AdverseActionNotice> adverseAction(
    int id, {
    String? reason,
    required bool sendToApplicant,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/applications/$id/adverse-action',
        data: {
          if (reason != null && reason.isNotEmpty) 'reason': reason,
          'sendToApplicant': sendToApplicant,
        },
      );
      return AdverseActionNotice.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Raw bytes for a stored file (authed via the shared interceptor) — used to
  /// open the generated adverse-action PDF.
  Future<Uint8List> documentBytes(int storedFileId) async {
    try {
      final response = await _dio.get<List<int>>(
        '/documents/$storedFileId/file',
        options: Options(responseType: ResponseType.bytes),
      );
      return Uint8List.fromList(response.data ?? const []);
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

/// Screening results for an application (auto-disposes so it re-fetches on open).
final applicationScreeningProvider = FutureProvider.autoDispose
    .family<List<ScreeningResult>, int>((ref, id) {
  return ref.watch(applicationsRepositoryProvider).screening(id);
});
