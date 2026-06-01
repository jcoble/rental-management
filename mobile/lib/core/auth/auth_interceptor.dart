import 'dart:async';

import 'package:dio/dio.dart';

import 'token_store.dart';

/// Dio interceptor that:
///  1. Attaches `Authorization: Bearer <accessToken>` to every request.
///  2. On 401, refreshes the access token once (single-flight) and retries.
///  3. On refresh failure, clears stored tokens and triggers the logout callback.
///
/// The refresh endpoint reads the refresh token from an httpOnly cookie.
/// Since the mobile client cannot rely on a browser cookie jar, the refresh
/// token is stored in secure storage and sent as a `Cookie` header.
///
/// TODO(api): Add a body-based refresh token parameter so mobile doesn't need
/// to manually send the cookie header. Track with API team.
class AuthInterceptor extends Interceptor {
  AuthInterceptor({
    required this.tokenStore,
    required this.dio,
    required this.onLogout,
  });

  final TokenStore tokenStore;

  /// A separate, interceptor-free Dio instance used exclusively for refresh
  /// calls to avoid infinite interceptor recursion.
  final Dio dio;

  /// Called when a token refresh fails — implementors should clear auth state
  /// and navigate to the login screen.
  final void Function() onLogout;

  /// Single-flight guard: ensures only one refresh is in-flight at a time.
  Completer<String?>? _refreshCompleter;

  @override
  Future<void> onRequest(
    RequestOptions options,
    RequestInterceptorHandler handler,
  ) async {
    final token = await tokenStore.getAccessToken();
    if (token != null) {
      options.headers['Authorization'] = 'Bearer $token';
    }
    handler.next(options);
  }

  @override
  Future<void> onError(
    DioException err,
    ErrorInterceptorHandler handler,
  ) async {
    final response = err.response;
    if (response == null || response.statusCode != 401) {
      handler.next(err);
      return;
    }

    // Avoid retrying the refresh endpoint itself.
    if (err.requestOptions.path.contains('/auth/refresh')) {
      await tokenStore.clearTokens();
      onLogout();
      handler.next(err);
      return;
    }

    try {
      final newToken = await _singleFlightRefresh();
      if (newToken == null) {
        await tokenStore.clearTokens();
        onLogout();
        handler.next(err);
        return;
      }

      // Retry the original request with the new access token.
      final opts = err.requestOptions;
      opts.headers['Authorization'] = 'Bearer $newToken';
      final retryResponse = await dio.fetch<dynamic>(opts);
      handler.resolve(retryResponse);
    } catch (_) {
      await tokenStore.clearTokens();
      onLogout();
      handler.next(err);
    }
  }

  /// Ensures only one refresh call happens even when multiple 401s arrive
  /// concurrently. All callers await the same [Completer].
  Future<String?> _singleFlightRefresh() async {
    if (_refreshCompleter != null) {
      return _refreshCompleter!.future;
    }

    _refreshCompleter = Completer<String?>();
    try {
      final refreshToken = await tokenStore.getRefreshToken();
      if (refreshToken == null) {
        final c = _refreshCompleter!;
        _refreshCompleter = null;
        c.complete(null);
        return null;
      }

      // Send the refresh token as a Cookie header because the server reads it
      // from the `rc_refresh_token` httpOnly cookie.
      // TODO(api): Use a request body field once the API supports it.
      final response = await dio.post<Map<String, dynamic>>(
        '/auth/refresh',
        options: Options(
          headers: {'Cookie': 'rc_refresh_token=$refreshToken'},
          // Don't let this call go through AuthInterceptor again.
          extra: {'skipAuthInterceptor': true},
        ),
      );

      final data = response.data;
      if (data == null) {
        final c = _refreshCompleter!;
        _refreshCompleter = null;
        c.complete(null);
        return null;
      }

      final newAccessToken = data['accessToken'] as String?;
      final newRefreshToken = _extractRefreshTokenFromCookies(response);

      if (newAccessToken == null) {
        final c = _refreshCompleter!;
        _refreshCompleter = null;
        c.complete(null);
        return null;
      }

      await tokenStore.saveTokens(
        accessToken: newAccessToken,
        refreshToken: newRefreshToken ?? refreshToken,
      );

      final c = _refreshCompleter!;
      _refreshCompleter = null;
      c.complete(newAccessToken);
      return newAccessToken;
    } catch (e) {
      final c = _refreshCompleter;
      _refreshCompleter = null;
      c?.complete(null);
      return null;
    }
  }

  /// Extracts the rotated refresh token from the `Set-Cookie` header of the
  /// refresh response.
  String? _extractRefreshTokenFromCookies(Response<dynamic> response) {
    final setCookies = response.headers['set-cookie'];
    if (setCookies == null) return null;
    for (final cookie in setCookies) {
      if (cookie.contains('rc_refresh_token=')) {
        final start = cookie.indexOf('rc_refresh_token=') +
            'rc_refresh_token='.length;
        final end = cookie.indexOf(';', start);
        return end == -1 ? cookie.substring(start) : cookie.substring(start, end);
      }
    }
    return null;
  }
}
