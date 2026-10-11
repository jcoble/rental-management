import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../api/api_exception.dart';
import '../api/dio_client.dart';
import '../api/idempotent_mutation.dart';
import 'auth_interceptor.dart';
import 'auth_models.dart';
import 'token_store.dart';

/// Repository for all authentication API calls.
///
/// Endpoints (all under /api/v1/auth):
///   POST /login   — body: { email, password }; response: LoginResponse
///   GET  /me      — bearer-authenticated; response: UserDto
///   POST /logout  — clears server-side refresh token
///   POST /refresh — body: { refreshToken } (mobile) or cookie (web); the
///                   refresh itself lives in [AuthInterceptor]
///
/// The refresh endpoint accepts the refresh token in a JSON body, so the mobile
/// client no longer crafts a Cookie header for it (see [AuthInterceptor]). The
/// logout call below still sends the cookie header — `/auth/logout` reads only
/// the cookie; a body field there is a separate, lower-priority follow-up.
class AuthRepository {
  AuthRepository({required Dio dio, required TokenStore tokenStore})
    : _dio = dio,
      _tokenStore = tokenStore;

  final Dio _dio;
  final TokenStore _tokenStore;

  /// Authenticates with email + password.
  ///
  /// On success, persists the access token and the refresh token extracted from
  /// the `Set-Cookie` response header.
  Future<LoginResponse> login(
    String email,
    String password, {
    int? accessContextId,
  }) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/auth/login',
        data: {
          'email': email,
          'password': password,
          'accessContextId': ?accessContextId,
        },
        options: Options(headers: {clientTypeHeader: mobileClientType}),
      );

      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      final loginResponse = LoginResponse.fromJson(data);

      // Refresh token: body-first (mobile), Set-Cookie fallback.
      final refreshToken = resolveRefreshToken(response);

      if (refreshToken == null || refreshToken.isEmpty) {
        throw const ApiException(
          statusCode: 0,
          message: 'Refresh token missing from login response.',
        );
      }

      await _tokenStore.saveTokens(
        accessToken: loginResponse.accessToken,
        refreshToken: refreshToken,
      );
      await _tokenStore.saveAccessEnvelope(loginResponse.access.toJson());

      return loginResponse;
    } on DioException catch (e) {
      final body = e.response?.data;
      if (e.response?.statusCode == 409 &&
          body is Map &&
          body['code'] == 'ACCESS_CONTEXT_REQUIRED') {
        final choices = (body['contexts'] as List<dynamic>? ?? const [])
            .map(
              (item) => EffectiveAccessContextOption.fromJson(
                Map<String, dynamic>.from(item as Map),
              ),
            )
            .toList(growable: false);
        throw AccessContextSelectionRequiredException(choices);
      }
      throw ApiException.fromDioException(e);
    }
  }

  /// Registers a new account.
  ///
  /// The API does NOT auto-login: on success it returns 200 with a generic
  /// `{ message }` and emails a confirmation link. No tokens are issued here,
  /// so we just relay the server message; the user confirms via email then
  /// signs in. Throws [ApiException] (with field details folded into the
  /// message) on 400/401.
  Future<RegisterResult> register({
    required String email,
    required String password,
    required String displayName,
  }) async {
    final request = {
      'email': email,
      'password': password,
      'displayName': displayName,
    };
    return IdempotentMutation.run(
      'auth:register:${Object.hash(email, password, displayName)}',
      (operationKey) async {
        try {
          final response = await _dio.post<Map<String, dynamic>>(
            '/auth/register',
            data: request,
            options: Options(headers: {'Idempotency-Key': operationKey}),
          );
          return RegisterResult.fromJson(response.data ?? const {});
        } on DioException catch (e) {
          throw ApiException.fromDioException(e);
        }
      },
    );
  }

  /// Signs in with a Google **id_token** obtained on-device via `google_sign_in`.
  ///
  /// POSTs `{ idToken }` to `/auth/google`; on success the server returns a
  /// [LoginResponse] and sets the `rc_refresh_token` cookie exactly like
  /// `/auth/login`, so this mirrors [login]'s token-extraction + save.
  Future<LoginResponse> signInWithGoogle(String idToken) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/auth/google',
        data: {'idToken': idToken},
        options: Options(headers: {clientTypeHeader: mobileClientType}),
      );

      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      final loginResponse = LoginResponse.fromJson(data);

      // Refresh token: body-first (mobile), Set-Cookie fallback.
      final refreshToken = resolveRefreshToken(response);
      if (refreshToken == null || refreshToken.isEmpty) {
        throw const ApiException(
          statusCode: 0,
          message: 'Refresh token missing from Google sign-in response.',
        );
      }

      await _tokenStore.saveTokens(
        accessToken: loginResponse.accessToken,
        refreshToken: refreshToken,
      );
      await _tokenStore.saveAccessEnvelope(loginResponse.access.toJson());

      return loginResponse;
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Requests a password-reset email via `POST /auth/forgot-password`.
  ///
  /// The endpoint is intentionally neutral — it always returns 200 regardless
  /// of whether the email exists (no account enumeration). Throws
  /// [ApiException] only on transport/server errors.
  Future<void> forgotPassword(String email) async {
    await IdempotentMutation.run('auth:forgot-password:$email', (operationKey) async {
      try {
        await _dio.post<Map<String, dynamic>>(
          '/auth/forgot-password',
          data: {'email': email},
          options: Options(headers: {'Idempotency-Key': operationKey}),
        );
      } on DioException catch (e) {
        throw ApiException.fromDioException(e);
      }
    });
  }

  /// Re-sends the email-verification message via `POST /auth/resend-verification`.
  ///
  /// Anonymous and intentionally neutral — the API always returns 200 regardless
  /// of whether the email exists or is already verified (no account enumeration).
  /// Throws [ApiException] only on transport/server errors.
  Future<void> resendVerification(String email) async {
    await IdempotentMutation.run('auth:resend-verification:$email', (operationKey) async {
      try {
        await _dio.post<Map<String, dynamic>>(
          '/auth/resend-verification',
          data: {'email': email},
          options: Options(headers: {'Idempotency-Key': operationKey}),
        );
      } on DioException catch (e) {
        throw ApiException.fromDioException(e);
      }
    });
  }

  /// Confirms an email address via `POST /auth/confirm-email` using the `userId`
  /// + `token` from the emailed verification link.
  Future<void> confirmEmail({
    required String userId,
    required String token,
  }) async {
    await IdempotentMutation.run(
      'auth:confirm-email:${Object.hash(userId, token)}',
      (operationKey) async {
        try {
          await _dio.post<Map<String, dynamic>>(
            '/auth/confirm-email',
            data: {'userId': userId, 'token': token},
            options: Options(headers: {'Idempotency-Key': operationKey}),
          );
        } on DioException catch (e) {
          throw ApiException.fromDioException(e);
        }
      },
    );
  }

  /// Completes a password reset via `POST /auth/reset-password` using the
  /// `userId` + `token` from the emailed link. Throws [ApiException] on an
  /// invalid/expired token or a password-policy failure (message folded in).
  Future<void> resetPassword({
    required String userId,
    required String token,
    required String newPassword,
  }) async {
    await IdempotentMutation.run(
      'auth:reset-password:${Object.hash(userId, token, newPassword)}',
      (operationKey) async {
        try {
          await _dio.post<Map<String, dynamic>>(
            '/auth/reset-password',
            data: {'userId': userId, 'token': token, 'newPassword': newPassword},
            options: Options(headers: {'Idempotency-Key': operationKey}),
          );
        } on DioException catch (e) {
          throw ApiException.fromDioException(e);
        }
      },
    );
  }

  /// Changes the signed-in user's password via `POST /auth/change-password`
  /// (Bearer-authenticated). Throws [ApiException] when the current password is
  /// wrong, the new one fails policy, or the account is external-login only.
  Future<void> changePassword({
    required String currentPassword,
    required String newPassword,
  }) async {
    final request = {
      'currentPassword': currentPassword,
      'newPassword': newPassword,
    };
    await IdempotentMutation.run(
      'auth:change-password:${Object.hash(currentPassword, newPassword)}',
      (operationKey) async {
        try {
          await _dio.post<Map<String, dynamic>>(
            '/auth/change-password',
            data: request,
            options: Options(headers: {'Idempotency-Key': operationKey}),
          );
        } on DioException catch (e) {
          throw ApiException.fromDioException(e);
        }
      },
    );
  }

  /// Fetches the current authenticated user via `GET /auth/me`.
  Future<AuthUser> currentUser() async {
    try {
      final response = await _dio.get<Map<String, dynamic>>('/auth/me');
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty response from server.',
        );
      }
      return AuthUser.fromJson(data);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<AccessEnvelope> currentAccess() async {
    try {
      final response = await _dio.get<Map<String, dynamic>>('/auth/access');
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty access response from server.',
        );
      }
      final access = AccessEnvelope.fromJson(data);
      await _tokenStore.saveAccessEnvelope(access.toJson());
      return access;
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<List<EffectiveAccessContextOption>> contexts() async {
    try {
      final response = await _dio.get<List<dynamic>>('/auth/contexts');
      return (response.data ?? const [])
          .map(
            (item) => EffectiveAccessContextOption.fromJson(
              Map<String, dynamic>.from(item as Map),
            ),
          )
          .toList(growable: false);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<AccessContextSwitchResponse> selectContext(int accessContextId) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/auth/contexts/select',
        data: {'accessContextId': accessContextId},
      );
      final result = AccessContextSwitchResponse.fromJson(
        response.data ?? <String, dynamic>{},
      );
      final refreshToken = await _tokenStore.getRefreshToken();
      if (refreshToken == null) {
        throw const ApiException(
          statusCode: 401,
          message: 'Session expired. Please sign in again.',
        );
      }
      await _tokenStore.saveTokens(
        accessToken: result.accessToken,
        refreshToken: refreshToken,
      );
      await _tokenStore.saveAccessEnvelope(result.access.toJson());
      return result;
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  Future<AccessEnvelope> selectExperience(
    WorkspaceExperience experience,
  ) => IdempotentMutation.run('auth:experience:${experience.name}', (
    operationKey,
  ) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/auth/experience/select',
        data: {'experience': experience.name},
        options: Options(headers: {'Idempotency-Key': operationKey}),
      );
      final data = response.data;
      if (data == null) {
        throw const ApiException(
          statusCode: 0,
          message: 'Empty access response from server.',
        );
      }
      final access = AccessEnvelope.fromJson(data);
      await _tokenStore.saveAccessEnvelope(access.toJson());
      return access;
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  });

  /// Logs out by revoking the server-side refresh token and clearing local storage.
  Future<void> logout() async {
    try {
      final refreshToken = await _tokenStore.getRefreshToken();
      if (refreshToken != null && refreshToken.isNotEmpty) {
        await _dio.post<dynamic>(
          '/auth/logout',
          options: Options(
            headers: {'Cookie': 'rc_refresh_token=$refreshToken'},
          ),
        );
      }
    } on DioException {
      // Best-effort logout: always clear local tokens even if the server call fails.
    } finally {
      await _tokenStore.clearTokens();
    }
  }
}

class AccessContextSelectionRequiredException extends ApiException {
  const AccessContextSelectionRequiredException(this.contexts)
    : super(statusCode: 409, message: 'Select a workspace to continue.');
  final List<EffectiveAccessContextOption> contexts;
}

/// Internal counter incremented by [AuthInterceptor] when a refresh fails.
/// The [authControllerProvider] listens to this and calls logout().
final logoutSignalProvider = _LogoutSignalNotifier.provider;
final accessChangeSignalProvider = _AccessChangeSignalNotifier.provider;

class _LogoutSignalNotifier extends Notifier<int> {
  static final provider = NotifierProvider<_LogoutSignalNotifier, int>(
    _LogoutSignalNotifier.new,
  );

  @override
  int build() => 0;

  void signal() => state++;
}

class _AccessChangeSignalNotifier extends Notifier<AccessEnvelope?> {
  static final provider =
      NotifierProvider<_AccessChangeSignalNotifier, AccessEnvelope?>(
        _AccessChangeSignalNotifier.new,
      );

  @override
  AccessEnvelope? build() => null;

  void signal(Map<String, dynamic> json) {
    state = AccessEnvelope.fromJson(json);
  }
}

final authRepositoryProvider = Provider<AuthRepository>((ref) {
  final baseDio = ref.watch(dioProvider);
  final tokenStore = ref.watch(tokenStoreProvider);

  // Build a second Dio instance (no auth interceptor) used internally by
  // AuthInterceptor for refresh calls — prevents infinite interceptor recursion.
  final refreshDio = _buildRefreshDio(baseDio);

  final interceptor = AuthInterceptor(
    tokenStore: tokenStore,
    dio: refreshDio,
    onLogout: () {
      // Signal logout — the auth controller's listener handles the state change.
      ref.read(logoutSignalProvider.notifier).signal();
    },
    onAccessChanged: (access) {
      ref.read(accessChangeSignalProvider.notifier).signal(access);
    },
  );

  // Outermost: measures total wall-clock time including the auth interceptor.
  baseDio.interceptors.add(_TimingInterceptor());
  baseDio.interceptors.add(interceptor);

  return AuthRepository(dio: baseDio, tokenStore: tokenStore);
});

final accessContextsProvider =
    FutureProvider.autoDispose<List<EffectiveAccessContextOption>>((ref) {
      return ref.watch(authRepositoryProvider).contexts();
    });

Dio _buildRefreshDio(Dio source) {
  final dio = Dio(source.options.copyWith());
  dio.httpClientAdapter = source.httpClientAdapter;
  return dio;
}

/// Logs the total wall-clock duration of each HTTP request so slow ones are
/// visible during on-device testing. debugPrint is stripped from release builds.
class _TimingInterceptor extends Interceptor {
  @override
  void onRequest(RequestOptions options, RequestInterceptorHandler handler) {
    options.extra['_t0'] = DateTime.now().millisecondsSinceEpoch;
    handler.next(options);
  }

  @override
  void onResponse(
    Response<dynamic> response,
    ResponseInterceptorHandler handler,
  ) {
    _log(response.requestOptions, response.statusCode);
    handler.next(response);
  }

  @override
  void onError(DioException err, ErrorInterceptorHandler handler) {
    _log(err.requestOptions, err.response?.statusCode);
    handler.next(err);
  }

  void _log(RequestOptions o, int? status) {
    final t0 = o.extra['_t0'];
    if (t0 is int) {
      final ms = DateTime.now().millisecondsSinceEpoch - t0;
      if (ms > 150) {
        debugPrint(
          '[HTTP ${ms}ms] ${o.method} ${o.path} -> ${status ?? 'ERR'}',
        );
      }
    }
  }
}
