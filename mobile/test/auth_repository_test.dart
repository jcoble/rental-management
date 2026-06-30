import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/auth/auth_repository.dart';
import 'package:rental_command/core/auth/token_store.dart';

void main() {
  test(
    'confirmEmail posts userId and token to confirm-email endpoint',
    () async {
      final adapter = _RecordingAdapter();
      final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter;
      final repo = AuthRepository(dio: dio, tokenStore: _NoopTokenStore());

      await repo.confirmEmail(userId: 'user-123', token: 'token+with/slash');

      expect(adapter.method, 'POST');
      expect(adapter.path, '/auth/confirm-email');
      expect(adapter.data, isA<Map<String, dynamic>>());
      final body = adapter.data! as Map<String, dynamic>;
      expect(body['userId'], 'user-123');
      expect(body['token'], 'token+with/slash');
    },
  );
}

class _RecordingAdapter implements HttpClientAdapter {
  String? method;
  String? path;
  Object? data;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    method = options.method;
    path = options.path;
    data = options.data;

    return ResponseBody.fromString(
      jsonEncode({'message': 'Email confirmed successfully.'}),
      200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

class _NoopTokenStore implements TokenStore {
  @override
  Future<void> clearTokens() async {}

  @override
  Future<String?> getAccessToken() async => null;

  @override
  Future<String?> getRefreshToken() async => null;

  @override
  Future<void> saveTokens({
    required String accessToken,
    required String refreshToken,
  }) async {}
}
