import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart' show visibleForTesting;

import 'token_store.dart';

/// Header the mobile client sends on auth calls to opt into receiving the
/// rotated refresh token in the response **body** (mobile has no httpOnly-cookie
/// jar it can read). The server only populates `LoginResponse.refreshToken` when
/// it sees this header; browsers never send it, so web bodies stay token-free.
const String clientTypeHeader = 'X-Client-Type';
const String mobileClientType = 'mobile';

/// Resolves the refresh token from a `/auth/login`, `/auth/google`, or
/// `/auth/refresh` response: the JSON **body** (`refreshToken`) is authoritative
/// for mobile; the `Set-Cookie` header is kept only as a fallback for servers
/// that haven't been updated to echo the token in the body.
String? resolveRefreshToken(Response<dynamic> response) {
  final data = response.data;
  if (data is Map) {
    final bodyToken = data['refreshToken'];
    if (bodyToken is String && bodyToken.isNotEmpty) {
      return bodyToken;
    }
  }
  return extractRefreshTokenFromCookies(response);
}

/// Extracts the `rc_refresh_token` value from a response's `Set-Cookie` header,
/// or `null` when absent. Fallback channel only — see [resolveRefreshToken].
String? extractRefreshTokenFromCookies(Response<dynamic> response) {
  final setCookies = response.headers['set-cookie'];
  if (setCookies == null) return null;
  for (final cookie in setCookies) {
    if (cookie.contains('rc_refresh_token=')) {
      final start =
          cookie.indexOf('rc_refresh_token=') + 'rc_refresh_token='.length;
      final end = cookie.indexOf(';', start);
      return end == -1 ? cookie.substring(start) : cookie.substring(start, end);
    }
  }
  return null;
}

/// Dio interceptor that:
///  1. Attaches `Authorization: Bearer <accessToken>` to every request.
///  2. On 401, refreshes the access token once (single-flight) and retries.
///  3. Logs out ONLY when the refresh itself fails. If the refresh succeeds but
///     the retried original request then errors (a non-token 401, a transient
///     network error, a timeout, a 5xx, …), that error is propagated — the
///     session is still valid, so we must not log the user out.
///
/// The refresh endpoint accepts the refresh token either from the httpOnly
/// `rc_refresh_token` cookie (web) or from a JSON body `{ refreshToken }`. The
/// mobile client stores the token in secure storage and sends it in the body,
/// so it never has to craft a `Cookie` header. It also sends `X-Client-Type:
/// mobile` so the server echoes the rotated token in the response body; the
/// `Set-Cookie` header is kept only as a fallback (see [resolveRefreshToken]).
class AuthInterceptor extends Interceptor {
  AuthInterceptor({
    required this.tokenStore,
    required this.dio,
    required this.onLogout,
    required this.onAccessChanged,
  });

  final TokenStore tokenStore;

  /// A separate, interceptor-free Dio instance used exclusively for refresh
  /// calls to avoid infinite interceptor recursion.
  final Dio dio;

  /// Called when a token refresh fails — implementors should clear auth state
  /// and navigate to the login screen.
  final void Function() onLogout;

  /// Called after refresh returns a new canonical access envelope. The auth
  /// controller uses this signal to replace shell authority, purge scoped
  /// caches, and reconnect realtime before any further mutation is allowed.
  final void Function(Map<String, dynamic> access) onAccessChanged;

  /// Single-flight guard: ensures only one refresh is in-flight at a time.
  Completer<_RefreshResult?>? _refreshCompleter;

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

    // Refresh the access token (single-flight). Only a FAILED refresh means the
    // session is dead — that is the sole condition under which we log out.
    final refresh = await _singleFlightRefresh();
    if (refresh == null) {
      await tokenStore.clearTokens();
      onLogout();
      handler.next(err);
      return;
    }

    // Refresh succeeded; retry the original request with the new access token.
    // If THIS retry throws (a non-token 401, a transient network error, a
    // timeout, a 5xx, …) the session is still valid — propagate the error to the
    // caller instead of logging the user out. Logging out here is the bug that
    // killed valid sessions on any flaky retry.
    onAccessChanged(refresh.access);

    final opts = err.requestOptions;
    if (_requiresAccessRefresh(response) && _isMutation(opts.method)) {
      handler.next(
        DioException.badResponse(
          statusCode: 409,
          requestOptions: opts,
          response: Response<dynamic>(
            requestOptions: opts,
            statusCode: 409,
            data: const {
              'code': 'ACCESS_REVISION_CHANGED',
              'error':
                  'Your access changed while this action was open. Nothing was submitted; review the refreshed screen and try again.',
            },
          ),
        ),
      );
      return;
    }

    opts.headers['Authorization'] = 'Bearer ${refresh.accessToken}';
    // A multipart `FormData` body is single-use: dispatching the original
    // request finalized it (and each of its `MultipartFile`s) in place, so
    // re-sending the same instance throws `StateError: already finalized` and
    // the upload is lost. Rebuild a fresh, replayable `FormData` for the retry.
    // This is exactly the case the flagship photo/voice/inspection capture
    // uploads hit when the access token lapses mid-session. `FormData.clone()`
    // re-uses each file's stream-builder closure (our uploads use
    // `MultipartFile.fromBytes`, whose builder yields a fresh in-memory stream
    // every call), so no file stream is double-read.
    opts.data = _replayableBody(opts.data);
    try {
      final retryResponse = await dio.fetch<dynamic>(opts);
      handler.resolve(retryResponse);
    } on DioException catch (retryErr) {
      handler.next(retryErr);
    } catch (_) {
      // Non-Dio failure on the retry — surface the original error, do NOT log out.
      handler.next(err);
    }
  }

  /// Returns a body safe to re-send on a retry. A consumed (finalized)
  /// [FormData] cannot be re-dispatched, so it is cloned into a fresh instance;
  /// all other body shapes (JSON maps, strings, lists, streams owned by the
  /// caller, `null`) are replayable as-is and returned unchanged.
  @visibleForTesting
  static Object? replayableBody(Object? data) => _replayableBody(data);

  static Object? _replayableBody(Object? data) {
    if (data is FormData) {
      return data.clone();
    }
    return data;
  }

  static bool _requiresAccessRefresh(Response<dynamic> response) =>
      response.headers.value('X-Access-Envelope-Refresh') == 'required';

  static bool _isMutation(String method) {
    final normalized = method.toUpperCase();
    return normalized != 'GET' &&
        normalized != 'HEAD' &&
        normalized != 'OPTIONS';
  }

  @visibleForTesting
  static bool canReplayAfterAccessRefresh(String method) =>
      !_isMutation(method);

  /// Ensures only one refresh call happens even when multiple 401s arrive
  /// concurrently. All callers await the same [Completer].
  Future<_RefreshResult?> _singleFlightRefresh() async {
    if (_refreshCompleter != null) {
      return _refreshCompleter!.future;
    }

    _refreshCompleter = Completer<_RefreshResult?>();
    try {
      final refreshToken = await tokenStore.getRefreshToken();
      if (refreshToken == null) {
        final c = _refreshCompleter!;
        _refreshCompleter = null;
        c.complete(null);
        return null;
      }

      // Send the stored refresh token in the request body. The API resolves it
      // from `{ refreshToken }` first, falling back to the cookie for web. The
      // X-Client-Type header opts us into getting the rotated token back in the
      // response body (mobile can't read an httpOnly Set-Cookie reliably).
      final response = await dio.post<Map<String, dynamic>>(
        '/auth/refresh',
        data: {'refreshToken': refreshToken},
        options: Options(
          headers: {clientTypeHeader: mobileClientType},
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
      final access = data['access'];
      // Body-first (mobile), Set-Cookie fallback.
      final newRefreshToken = resolveRefreshToken(response);

      if (newAccessToken == null || access is! Map) {
        final c = _refreshCompleter!;
        _refreshCompleter = null;
        c.complete(null);
        return null;
      }

      await tokenStore.saveTokens(
        accessToken: newAccessToken,
        refreshToken: newRefreshToken ?? refreshToken,
      );
      final accessJson = Map<String, dynamic>.from(access);
      await tokenStore.saveAccessEnvelope(accessJson);

      final c = _refreshCompleter!;
      _refreshCompleter = null;
      final result = _RefreshResult(newAccessToken, accessJson);
      c.complete(result);
      return result;
    } catch (e) {
      final c = _refreshCompleter;
      _refreshCompleter = null;
      c?.complete(null);
      return null;
    }
  }
}

final class _RefreshResult {
  const _RefreshResult(this.accessToken, this.access);

  final String accessToken;
  final Map<String, dynamic> access;
}
