import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import 'notices_models.dart';

class NoticesRepository {
  NoticesRepository(this._dio);

  final Dio _dio;

  Future<List<NoticeDraft>> list({String status = 'Draft'}) async {
    try {
      final response = await _dio.get<List<dynamic>>(
        '/notices',
        queryParameters: status.isEmpty ? null : {'status': status},
      );
      return (response.data ?? [])
          .whereType<Map<String, dynamic>>()
          .map(NoticeDraft.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> generate() async {
    try {
      await _dio.post<Map<String, dynamic>>('/notices/generate', data: {});
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Generates notice draft(s), optionally limited to a tenant, lease, payment,
  /// and/or [noticeType]. Returns the drafts that were created or already exist
  /// for the requested scope so the caller can review / send them. An empty list
  /// means nothing applied (e.g. a late notice with no overdue payment).
  ///
  /// POST /notices/generate  body: { tenantId?, leaseId?, paymentId?, noticeType? }
  Future<List<NoticeDraft>> generateForTenant(
    int? tenantId, {
    int? leaseId,
    int? paymentId,
    String? noticeType,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/notices/generate',
        data: {
          'tenantId': ?tenantId,
          'leaseId': ?leaseId,
          'paymentId': ?paymentId,
          'noticeType': ?noticeType,
        },
      );
      final drafts = (response.data?['drafts'] as List<dynamic>? ?? [])
          .whereType<Map<String, dynamic>>()
          .map(NoticeDraft.fromJson)
          .toList();
      return drafts;
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Edits a draft's subject/body before sending. PATCH /notices/{id}.
  Future<NoticeDraft> update(int id, {String? subject, String? body}) async {
    try {
      final response = await _dio.patch<Map<String, dynamic>>(
        '/notices/$id',
        data: {'subject': ?subject, 'body': ?body},
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return NoticeDraft.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> approve(int id, List<String> channels) async {
    try {
      await _dio.post<Map<String, dynamic>>(
        '/notices/$id/approve',
        data: {'channels': channels},
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> dismiss(int id) async {
    try {
      await _dio.post<Map<String, dynamic>>('/notices/$id/dismiss', data: {});
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

final noticesRepositoryProvider = Provider<NoticesRepository>((ref) {
  return NoticesRepository(ref.watch(dioProvider));
});

final noticeDraftsProvider = FutureProvider.autoDispose<List<NoticeDraft>>((
  ref,
) {
  return ref.watch(noticesRepositoryProvider).list();
});
