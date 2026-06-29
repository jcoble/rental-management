import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';

/// One landlord-authored notice template (one per notice type).
///
/// Mirrors the API's `NoticeTemplateResponse`. `noticeType` is one of:
/// RentReminder, RenewalOffer, MonthToMonthConversion, MoveOutReminder,
/// LateRentNotice (serialized as string names). `hasTemplate` is false when the
/// landlord hasn't saved one yet (subject/body are blank in that case).
class NoticeTemplate {
  const NoticeTemplate({
    required this.noticeType,
    required this.subject,
    required this.body,
    required this.hasTemplate,
    required this.availableFields,
  });

  final String noticeType;
  final String subject;
  final String body;
  final bool hasTemplate;
  final List<String> availableFields;

  factory NoticeTemplate.fromJson(Map<String, dynamic> j) => NoticeTemplate(
        noticeType: j['noticeType'] as String? ?? '',
        subject: j['subject'] as String? ?? '',
        body: j['body'] as String? ?? '',
        hasTemplate: j['hasTemplate'] as bool? ?? false,
        availableFields: ((j['availableFields'] as List<dynamic>?) ?? const [])
            .whereType<String>()
            .toList(),
      );
}

/// Notice template API calls.
///
/// Endpoints (portfolio-scoped, camelCase JSON, enums as string names):
///   GET /notices/templates          → all supported types (empty placeholders
///                                      for types without a saved template)
///   PUT /notices/templates/{type}   { subject, body } → upserted template
class NoticeTemplatesRepository {
  NoticeTemplatesRepository(this._dio);

  final Dio _dio;

  Future<List<NoticeTemplate>> list() async {
    try {
      final res = await _dio.get<List<dynamic>>('/notices/templates');
      return (res.data ?? [])
          .whereType<Map<String, dynamic>>()
          .map(NoticeTemplate.fromJson)
          .toList();
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<NoticeTemplate> upsert(
    String type, {
    required String subject,
    required String body,
  }) async {
    try {
      final res = await _dio.put<Map<String, dynamic>>(
        '/notices/templates/$type',
        data: {'subject': subject, 'body': body},
      );
      final data = res.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return NoticeTemplate.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

final noticeTemplatesRepositoryProvider =
    Provider<NoticeTemplatesRepository>((ref) {
  return NoticeTemplatesRepository(ref.watch(dioProvider));
});
