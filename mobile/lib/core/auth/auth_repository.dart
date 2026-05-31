import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../api/api_exception.dart';
import '../api/dio_client.dart';
import 'auth_interceptor.dart';
import 'auth_models.dart';
import 'token_store.dart';

/// Repository for all authentication API calls.
///
/// Endpoints (all under /api/v1/auth):
///   POST /login   — body: { email, password }; response: LoginResponse
///   GET  /me      — bearer-authenticated; response: UserDto
///   POST /logout  — clears server-side refresh token (cookie-based)
///   POST /refresh — cookie-based only (no body option)
///
/// TODO(api): The refresh endpoint reads the refresh token exclusively from
/// an httpOnly cookie. Add a request body field so mobile clients don't need
/// to manually craft a Cookie header.
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
  Future<LoginResponse> login(String email, String password) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/auth/login',
        data: {'email': email, 'password': password},
      );

      final data = response.data!;
      final loginResponse = LoginResponse.fromJson(data);

      // Extract refresh token from Set-Cookie header.
      final refreshToken = _extractRefreshToken(response);

      await _tokenStore.saveTokens(
        accessToken: loginResponse.accessToken,
        refreshToken: refreshToken ?? '',
      );

      return loginResponse;
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

  /// Fetches the current authenticated user via `GET /auth/me`.
  Future<AuthUser> currentUser() async {
    try {
      final response =
          await _dio.get<Map<String, dynamic>>('/auth/me');
      return AuthUser.fromJson(response.data!);
    } on DioException catch (e) {
      throw ApiException.fromDioException(e);
    }
  }

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

  String? _extractRefreshToken(Response<dynamic> response) {
    final setCookies = response.headers['set-cookie'];
    if (setCookies == null) return null;
    for (final cookie in setCookies) {
      if (cookie.contains('rc_refresh_token=')) {
        final start =
            cookie.indexOf('rc_refresh_token=') + 'rc_refresh_token='.length;
        final end = cookie.indexOf(';', start);
        return end == -1
            ? cookie.substring(start)
            : cookie.substring(start, end);
      }
    }
    return null;
  }
}

/// Internal counter incremented by [AuthInterceptor] when a refresh fails.
/// The [authControllerProvider] listens to this and calls logout().
final logoutSignalProvider = _LogoutSignalNotifier.provider;

class _LogoutSignalNotifier extends Notifier<int> {
  static final provider = NotifierProvider<_LogoutSignalNotifier, int>(
    _LogoutSignalNotifier.new,
  );

  @override
  int build() => 0;

  void signal() => state++;
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
  );

  // Outermost: measures total wall-clock time including the auth interceptor.
  baseDio.interceptors.add(_TimingInterceptor());
  baseDio.interceptors.add(interceptor);

  return AuthRepository(dio: baseDio, tokenStore: tokenStore);
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
  void onResponse(Response<dynamic> response, ResponseInterceptorHandler handler) {
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
        debugPrint('[HTTP ${ms}ms] ${o.method} ${o.path} -> ${status ?? 'ERR'}');
      }
    }
  }
}
