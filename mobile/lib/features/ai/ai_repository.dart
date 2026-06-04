import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import 'ai_models.dart';

/// Repository for the AI endpoints.
///
/// Endpoints:
///   GET  /api/v1/ai/briefing
///   POST /api/v1/ai/ask
class AiRepository {
  const AiRepository(this._dio);

  final Dio _dio;

  /// Fetches the daily briefing for the authenticated user's portfolio.
  Future<BriefingResponse> briefing() async {
    try {
      final response =
          await _dio.get<Map<String, dynamic>>('/ai/briefing');
      return BriefingResponse.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Sends [question] with optional [history] and returns the assistant answer.
  ///
  /// Pass [delivery] to also have the server text/email the answer to the landlord.
  Future<AskResponse> ask(
    String question,
    List<QaTurn> history, {
    AskDelivery? delivery,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/ai/ask',
        data: {
          'question': question,
          if (history.isNotEmpty)
            'history': history.map((t) => t.toJson()).toList(),
          if (delivery != null) ...delivery.toJson(),
        },
      );
      return AskResponse.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

final aiRepositoryProvider = Provider<AiRepository>((ref) {
  return AiRepository(ref.watch(dioProvider));
});
