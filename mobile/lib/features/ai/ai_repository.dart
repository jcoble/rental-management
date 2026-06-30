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
///   POST /api/v1/ai/actions/draft
///   POST /api/v1/ai/actions/execute
class AiRepository {
  const AiRepository(this._dio);

  final Dio _dio;

  /// Fetches the daily briefing for the authenticated user's portfolio.
  Future<BriefingResponse> briefing() async {
    try {
      final response = await _dio.get<Map<String, dynamic>>('/ai/briefing');
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return BriefingResponse.fromJson(data);
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
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return AskResponse.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<AssistantActionDraftResponse> draftAction(
    String command,
    bool writeModeEnabled,
  ) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/ai/actions/draft',
        data: {'command': command, 'writeModeEnabled': writeModeEnabled},
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return AssistantActionDraftResponse.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<AssistantActionExecuteResponse> executeAction(
    AssistantActionDraft draft,
    bool writeModeEnabled,
  ) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/ai/actions/execute',
        data: {
          'draft': draft.toJson(),
          'writeModeEnabled': writeModeEnabled,
          'confirmed': true,
        },
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return AssistantActionExecuteResponse.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

final aiRepositoryProvider = Provider<AiRepository>((ref) {
  return AiRepository(ref.watch(dioProvider));
});
