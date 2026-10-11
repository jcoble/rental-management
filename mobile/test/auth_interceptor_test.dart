import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/auth/auth_interceptor.dart';
import 'package:rental_command/core/auth/token_store.dart';

/// Regression coverage for audit finding H-5: on a 401 the interceptor retries
/// the original request, but a multipart `FormData` body is single-use. Once it
/// has been dispatched (finalized) it cannot be re-sent — so the retry path must
/// rebuild a fresh, replayable body. These tests pin the exact behaviour the fix
/// relies on without standing up a full HTTP + secure-storage harness.
void main() {
  group('AuthInterceptor.replayableBody', () {
    test('a FormData consumed by the first send is rebuilt into a fresh, '
        're-finalizable instance for the retry (no StateError)', () {
      final original = FormData.fromMap({
        'file': MultipartFile.fromBytes(
          Uint8List.fromList([1, 2, 3, 4]),
          filename: 'receipt.jpg',
          contentType: DioMediaType.parse('image/jpeg'),
        ),
        'targetEntityType': 'Expense',
      });

      // Simulate the first dispatch finalizing the body in place (this is what
      // `dio.fetch` does internally before the 401 comes back).
      original.finalize().drain<void>();
      expect(original.isFinalized, isTrue);
      // Sanity: re-sending the SAME instance is exactly the bug — it throws.
      expect(original.finalize, throwsStateError);

      final replay = AuthInterceptor.replayableBody(original);

      // The retry body must be a *different*, not-yet-finalized FormData...
      expect(replay, isA<FormData>());
      expect(identical(replay, original), isFalse);
      final replayForm = replay as FormData;
      expect(replayForm.isFinalized, isFalse);

      // ...whose fields and files survived the clone...
      expect(
        replayForm.fields.map((e) => '${e.key}=${e.value}'),
        contains('targetEntityType=Expense'),
      );
      expect(replayForm.files.single.key, 'file');
      expect(replayForm.files.single.value.filename, 'receipt.jpg');

      // ...and which can be finalized (i.e. re-sent) without throwing. This is
      // the assertion that fails on the unpatched interceptor.
      expect(() => replayForm.finalize().drain<void>(), returnsNormally);
    });

    test(
      'non-multipart bodies are returned unchanged so JSON retries still work',
      () {
        const jsonBody = {'amount': 40, 'note': 'plumbing'};
        expect(
          identical(AuthInterceptor.replayableBody(jsonBody), jsonBody),
          isTrue,
        );

        const stringBody = 'raw=payload';
        expect(
          identical(AuthInterceptor.replayableBody(stringBody), stringBody),
          isTrue,
        );

        expect(AuthInterceptor.replayableBody(null), isNull);
      },
    );
  });

  group('stale access revision replay policy', () {
    test('safe reads may be replayed after the envelope refreshes', () {
      expect(AuthInterceptor.canReplayAfterAccessRefresh('GET'), isTrue);
      expect(AuthInterceptor.canReplayAfterAccessRefresh('HEAD'), isTrue);
      expect(AuthInterceptor.canReplayAfterAccessRefresh('OPTIONS'), isTrue);
    });

    test('mutations must return to the caller without automatic replay', () {
      for (final method in ['POST', 'PUT', 'PATCH', 'DELETE']) {
        expect(
          AuthInterceptor.canReplayAfterAccessRefresh(method),
          isFalse,
          reason: '$method could duplicate or apply under changed authority',
        );
      }
    });

    test(
      'ordinary token expiry may replay a mutation in the same authority',
      () {
        expect(
          AuthInterceptor.canReplayRequestAfterRefresh(
            method: 'POST',
            requestStartAccess: _accessEnvelope(),
            refreshedAccess: _accessEnvelope(),
            staleRevisionRecovery: false,
          ),
          isTrue,
        );
      },
    );

    test(
      'mutation cannot cross workspace revision or experience boundaries',
      () {
        for (final refreshed in [
          _accessEnvelope(accessContextId: 22),
          _accessEnvelope(accessRevision: 8),
          _accessEnvelope(activeExperience: 'Leasing'),
        ]) {
          expect(
            AuthInterceptor.canReplayRequestAfterRefresh(
              method: 'PATCH',
              requestStartAccess: _accessEnvelope(),
              refreshedAccess: refreshed,
              staleRevisionRecovery: false,
            ),
            isFalse,
          );
        }
      },
    );

    test('stale-revision recovery never replays mutations automatically', () {
      expect(
        AuthInterceptor.canReplayRequestAfterRefresh(
          method: 'DELETE',
          requestStartAccess: _accessEnvelope(),
          refreshedAccess: _accessEnvelope(accessRevision: 8),
          staleRevisionRecovery: true,
        ),
        isFalse,
      );
    });

    test('reads may recover after an authority change', () {
      expect(
        AuthInterceptor.canReplayRequestAfterRefresh(
          method: 'GET',
          requestStartAccess: _accessEnvelope(),
          refreshedAccess: _accessEnvelope(accessContextId: 22),
          staleRevisionRecovery: false,
        ),
        isTrue,
      );
    });

    test('missing request-start authority fails closed for mutations', () {
      expect(
        AuthInterceptor.canReplayRequestAfterRefresh(
          method: 'POST',
          requestStartAccess: null,
          refreshedAccess: _accessEnvelope(),
          staleRevisionRecovery: false,
        ),
        isFalse,
      );
    });
  });

  group('refresh failure classification', () {
    test(
      'clearing refresh cookie preserves the stored refresh token',
      () async {
        final tokenStore = _MemoryTokenStore();
        final refreshDio = Dio()
          ..httpClientAdapter = _RefreshAdapter.clearingCookieSuccess();
        final requestDio = Dio()
          ..httpClientAdapter = _UnauthorizedThenSuccessAdapter();
        requestDio.interceptors.add(
          AuthInterceptor(
            tokenStore: tokenStore,
            dio: refreshDio,
            onLogout: () {},
            onAccessChanged: (_) {},
          ),
        );

        await requestDio.get<dynamic>('/protected');

        expect(tokenStore.savedRefreshToken, 'stored-refresh-token');
      },
    );

    test(
      'an unavailable refresh endpoint preserves tokens and does not signal logout',
      () async {
        final tokenStore = _MemoryTokenStore();
        var logoutCalls = 0;
        final refreshDio = Dio()
          ..httpClientAdapter = _RefreshAdapter.transientFailure();
        final requestDio = Dio()
          ..httpClientAdapter = _AlwaysUnauthorizedAdapter();
        requestDio.interceptors.add(
          AuthInterceptor(
            tokenStore: tokenStore,
            dio: refreshDio,
            onLogout: () => logoutCalls++,
            onAccessChanged: (_) {},
          ),
        );

        await expectLater(
          requestDio.get<dynamic>('/protected'),
          throwsA(
            isA<DioException>().having(
              (error) => error.type,
              'type',
              DioExceptionType.connectionError,
            ),
          ),
        );
        expect(tokenStore.clearTokensCalls, 0);
        expect(logoutCalls, 0);
      },
    );

    test(
      'a confirmed 401 refresh rejection clears tokens and logs out',
      () async {
        final tokenStore = _MemoryTokenStore();
        var logoutCalls = 0;
        final refreshDio = Dio()
          ..httpClientAdapter = _RefreshAdapter.unauthorized();
        final requestDio = Dio()
          ..httpClientAdapter = _AlwaysUnauthorizedAdapter();
        requestDio.interceptors.add(
          AuthInterceptor(
            tokenStore: tokenStore,
            dio: refreshDio,
            onLogout: () => logoutCalls++,
            onAccessChanged: (_) {},
          ),
        );

        await expectLater(
          requestDio.get<dynamic>('/protected'),
          throwsA(isA<DioException>()),
        );
        expect(tokenStore.clearTokensCalls, 1);
        expect(logoutCalls, 1);
      },
    );
  });
}

Map<String, dynamic> _accessEnvelope({
  int accessContextId = 11,
  int accessRevision = 7,
  String activeExperience = 'Management',
}) => {
  'identity': {'userId': 5, 'displayName': 'Test user'},
  'selectedContext': {
    'accessContextId': accessContextId,
    'portfolioId': 3,
    'workspaceName': 'Test workspace',
    'accessRevision': accessRevision,
    'activeExperience': activeExperience,
  },
};

class _MemoryTokenStore implements TokenStore {
  int clearTokensCalls = 0;
  String? savedRefreshToken;

  @override
  Future<void> clearTokens() async {
    clearTokensCalls++;
  }

  @override
  Future<String?> getAccessToken() async => 'expired-access-token';

  @override
  Future<Map<String, dynamic>?> getAccessEnvelope() async => _accessEnvelope();

  @override
  Future<String?> getRefreshToken() async => 'stored-refresh-token';

  @override
  Future<void> saveAccessEnvelope(Map<String, dynamic> access) async {}

  @override
  Future<void> saveTokens({
    required String accessToken,
    required String refreshToken,
  }) async {
    savedRefreshToken = refreshToken;
  }
}

class _UnauthorizedThenSuccessAdapter implements HttpClientAdapter {
  int calls = 0;

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    calls++;
    return ResponseBody.fromString(
      '{}',
      calls == 1 ? 401 : 200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }
}

class _AlwaysUnauthorizedAdapter implements HttpClientAdapter {
  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async => ResponseBody.fromString(
    '{"error":"Session expired"}',
    401,
    headers: {
      Headers.contentTypeHeader: [Headers.jsonContentType],
    },
  );
}

class _RefreshAdapter implements HttpClientAdapter {
  _RefreshAdapter._(this._mode);

  factory _RefreshAdapter.transientFailure() => _RefreshAdapter._('transient');
  factory _RefreshAdapter.unauthorized() => _RefreshAdapter._('unauthorized');
  factory _RefreshAdapter.clearingCookieSuccess() =>
      _RefreshAdapter._('success');

  final String _mode;

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    if (_mode == 'unauthorized') {
      return ResponseBody.fromString(
        '{"error":"Invalid or expired refresh token"}',
        401,
        headers: {
          Headers.contentTypeHeader: [Headers.jsonContentType],
        },
      );
    }
    if (_mode == 'success') {
      return ResponseBody.fromString(
        '{"accessToken":"new-access-token","access":${_jsonAccessEnvelope()}}',
        200,
        headers: {
          Headers.contentTypeHeader: [Headers.jsonContentType],
          'set-cookie': ['rc_refresh_token=; Path=/; HttpOnly'],
        },
      );
    }
    throw DioException.connectionError(
      requestOptions: options,
      reason: 'preview API unavailable',
    );
  }
}

String _jsonAccessEnvelope() => '''{
  "identity":{"userId":5,"displayName":"Test user"},
  "selectedContext":{"accessContextId":11,"portfolioId":3,"workspaceName":"Test workspace","accessRevision":7,"activeExperience":"Management"}
}''';
