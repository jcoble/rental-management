import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import 'voice_intake_models.dart';

/// API calls for the conversational voice intake.
///
/// Endpoints (both return the draft + slot state, parsed into [VoiceTurn]):
///   POST /api/v1/voice/drafts                — start (audio or transcript)
///   POST /api/v1/voice/drafts/{id}/answer    — answer the current question
///
/// Confirmation reuses the shared scan confirm path (see [VoiceConversationController]).
class VoiceIntakeRepository {
  VoiceIntakeRepository(this._dio);

  final Dio _dio;

  /// Starts a conversation from a spoken note. Pass [audio] (preferred) or a
  /// [transcript] (handy for tests / non-audio entry).
  Future<VoiceTurn> start({
    required String operationKey,
    Uint8List? audio,
    String filename = 'voice.m4a',
    String contentType = 'audio/mp4',
    String? transcript,
  }) {
    return _post(
      '/voice/drafts',
      operationKey,
      audio,
      filename,
      contentType,
      transcript,
    );
  }

  /// Answers the current question for [draftId].
  Future<VoiceTurn> answer(
    int draftId, {
    required String operationKey,
    Uint8List? audio,
    String filename = 'voice.m4a',
    String contentType = 'audio/mp4',
    String? transcript,
  }) {
    return _post(
      '/voice/drafts/$draftId/answer',
      operationKey,
      audio,
      filename,
      contentType,
      transcript,
    );
  }

  Future<VoiceTurn> _post(
    String path,
    String operationKey,
    Uint8List? audio,
    String filename,
    String contentType,
    String? transcript,
  ) async {
    try {
      final formData = FormData.fromMap({
        if (audio != null)
          'audio': MultipartFile.fromBytes(
            audio,
            filename: filename,
            contentType: DioMediaType.parse(contentType),
          ),
        if (transcript != null && transcript.trim().isNotEmpty)
          'transcript': transcript,
      });

      final response = await _dio.post<Map<String, dynamic>>(
        path,
        data: formData,
        options: Options(headers: {'Idempotency-Key': operationKey}),
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return VoiceTurn.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }
}

final voiceIntakeRepositoryProvider = Provider<VoiceIntakeRepository>((ref) {
  return VoiceIntakeRepository(ref.watch(dioProvider));
});
