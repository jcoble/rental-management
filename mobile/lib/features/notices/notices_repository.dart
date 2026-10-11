import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/api/idempotent_mutation.dart';
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
      await IdempotentMutation.run(
        'notice-drafts:generate:all',
        (key) => _dio.post<Map<String, dynamic>>(
          '/notices/generate',
          data: {},
          options: Options(headers: {'Idempotency-Key': key}),
        ),
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Generates notice draft(s), optionally limited to a recipient, lease relationship,
  /// tenant account, or exact ledger charge and/or [noticeType]. Returns the drafts
  /// that were created or already exist
  /// for the requested scope so the caller can review / send them. An empty list
  /// means nothing applied (e.g. a late notice with no overdue charge).
  ///
  /// POST /notices/generate body: canonical notice scope plus optional noticeType.
  Future<List<NoticeDraft>> generateScoped(
    int? recipientTenantId, {
    int? leaseManagementId,
    int? tenantAccountId,
    int? tenantLedgerEntryId,
    String? noticeType,
  }) async {
    try {
      final payload = {
        'recipientTenantId': ?recipientTenantId,
        'leaseManagementId': ?leaseManagementId,
        'tenantAccountId': ?tenantAccountId,
        'tenantLedgerEntryId': ?tenantLedgerEntryId,
        'noticeType': ?noticeType,
      };
      final response = await IdempotentMutation.run(
        'notice-drafts:generate:$recipientTenantId:$leaseManagementId:'
        '$tenantAccountId:$tenantLedgerEntryId:$noticeType',
        (key) => _dio.post<Map<String, dynamic>>(
          '/notices/generate',
          data: payload,
          options: Options(headers: {'Idempotency-Key': key}),
        ),
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
      final payload = {'subject': ?subject, 'body': ?body};
      final response = await IdempotentMutation.run(
        'notice-drafts:update:$id:$payload',
        (key) => _dio.patch<Map<String, dynamic>>(
          '/notices/$id',
          data: payload,
          options: Options(headers: {'Idempotency-Key': key}),
        ),
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
      final payload = {'channels': channels};
      await IdempotentMutation.run(
        'notice-drafts:approve:$id:$payload',
        (key) => _dio.post<Map<String, dynamic>>(
          '/notices/$id/approve',
          data: payload,
          options: Options(headers: {'Idempotency-Key': key}),
        ),
      );
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<void> dismiss(int id) async {
    try {
      await IdempotentMutation.run(
        'notice-drafts:dismiss:$id',
        (key) => _dio.post<Map<String, dynamic>>(
          '/notices/$id/dismiss',
          data: {},
          options: Options(headers: {'Idempotency-Key': key}),
        ),
      );
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
