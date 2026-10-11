import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';

enum AiProvider { openai, anthropic }

class AiProviderStatus {
  const AiProviderStatus({
    required this.configured,
    required this.provider,
    required this.modelId,
    required this.lastTestedAtUtc,
    required this.updatedAtUtc,
  });

  final bool configured;
  final AiProvider? provider;
  final String? modelId;
  final DateTime? lastTestedAtUtc;
  final DateTime? updatedAtUtc;

  factory AiProviderStatus.fromJson(Map<String, dynamic> json) =>
      AiProviderStatus(
        configured: json['configured'] as bool? ?? false,
        provider: switch (json['provider'] as String?) {
          'openai' => AiProvider.openai,
          'anthropic' => AiProvider.anthropic,
          _ => null,
        },
        modelId: json['modelId'] as String?,
        lastTestedAtUtc: DateTime.tryParse(
          json['lastTestedAtUtc'] as String? ?? '',
        ),
        updatedAtUtc: DateTime.tryParse(json['updatedAtUtc'] as String? ?? ''),
      );
}

class AiCredentialTestResult {
  const AiCredentialTestResult({
    required this.succeeded,
    required this.provider,
    required this.modelId,
    required this.error,
  });

  final bool succeeded;
  final AiProvider provider;
  final String modelId;
  final String? error;

  factory AiCredentialTestResult.fromJson(Map<String, dynamic> json) =>
      AiCredentialTestResult(
        succeeded: json['succeeded'] as bool? ?? false,
        provider: json['provider'] == 'anthropic'
            ? AiProvider.anthropic
            : AiProvider.openai,
        modelId: json['modelId'] as String? ?? '',
        error: json['error'] as String?,
      );
}

class AiProviderRepository {
  AiProviderRepository({required Dio dio}) : _dio = dio;

  final Dio _dio;

  Future<AiProviderStatus> getStatus() async {
    try {
      final response = await _dio.get<Map<String, dynamic>>('/integrations/ai');
      return AiProviderStatus.fromJson(response.data ?? const {});
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<AiCredentialTestResult> test({
    required AiProvider provider,
    required String modelId,
    required String apiKey,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/integrations/ai/test',
        data: _credentialBody(provider, modelId, apiKey),
      );
      return AiCredentialTestResult.fromJson(response.data ?? const {});
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<AiProviderStatus> activate({
    required AiProvider provider,
    required String modelId,
    required String apiKey,
  }) async {
    try {
      final response = await _dio.put<Map<String, dynamic>>(
        '/integrations/ai',
        data: _credentialBody(provider, modelId, apiKey),
      );
      return AiProviderStatus.fromJson(response.data ?? const {});
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<AiProviderStatus> rotate({
    required AiProvider provider,
    required String modelId,
    required String apiKey,
  }) async {
    try {
      final response = await _dio.put<Map<String, dynamic>>(
        '/integrations/ai/rotate',
        data: _credentialBody(provider, modelId, apiKey),
      );
      return AiProviderStatus.fromJson(response.data ?? const {});
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<void> remove() async {
    try {
      await _dio.delete<void>('/integrations/ai');
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  static Map<String, dynamic> _credentialBody(
    AiProvider provider,
    String modelId,
    String apiKey,
  ) => {
    'provider': provider.name,
    'modelId': modelId.trim(),
    'apiKey': apiKey.trim(),
  };
}

final aiProviderRepositoryProvider = Provider<AiProviderRepository>(
  (ref) => AiProviderRepository(dio: ref.watch(dioProvider)),
);
