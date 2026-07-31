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
const String _requestAccessCoordinatesKey = 'requestAccessCoordinates';

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
///  3. Logs out ONLY when the refresh endpoint definitively rejects the stored
///     refresh token. Transport, timeout, 5xx, and malformed-response failures
///     are propagated without clearing the session because they do not prove
///     its credentials are invalid.
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

  /// Called when the refresh endpoint definitively rejects the saved session —
  /// implementors should clear auth state and navigate to the login screen.
  final void Function() onLogout;

  /// Called after refresh returns a new canonical access envelope. The auth
  /// controller uses this signal to replace shell authority, purge scoped
  /// caches, and reconnect realtime before any further mutation is allowed.
  final void Function(Map<String, dynamic> access) onAccessChanged;

  /// Single-flight guard: ensures only one refresh is in-flight at a time.
  Completer<_RefreshAttempt>? _refreshCompleter;

  @override
  Future<void> onRequest(
    RequestOptions options,
    RequestInterceptorHandler handler,
  ) async {
    final values = await Future.wait<Object?>([
      tokenStore.getAccessToken(),
      tokenStore.getAccessEnvelope(),
    ]);
    final token = values[0] as String?;
    if (token != null) {
      options.headers['Authorization'] = 'Bearer $token';
    }
    final envelope = values[1] as Map<String, dynamic>?;
    final coordinates = _AccessCoordinates.fromEnvelope(envelope);
    if (coordinates != null) {
      options.extra[_requestAccessCoordinatesKey] = coordinates;
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

    // Refresh the access token (single-flight). Only a definitive refresh-token
    // rejection means the session is dead. Availability failures are surfaced
    // to the caller while the saved session remains intact.
    final refreshAttempt = await _singleFlightRefresh();
    if (refreshAttempt is _RefreshTransientFailure) {
      handler.next(refreshAttempt.error);
      return;
    }
    if (refreshAttempt is _RefreshRejected) {
      await tokenStore.clearTokens();
      onLogout();
      handler.next(err);
      return;
    }
    final refresh = (refreshAttempt as _RefreshSucceeded).result;

    // Refresh succeeded; retry the original request with the new access token.
    // If THIS retry throws (a non-token 401, a transient network error, a
    // timeout, a 5xx, …) the session is still valid — propagate the error to the
    // caller instead of logging the user out. Logging out here is the bug that
    // killed valid sessions on any flaky retry.
    onAccessChanged(refresh.access);

    final opts = err.requestOptions;
    final staleRevisionRecovery = _requiresAccessRefresh(response);
    final requestCoordinates =
        opts.extra[_requestAccessCoordinatesKey] as _AccessCoordinates?;
    final refreshedCoordinates = _AccessCoordinates.fromEnvelope(
      refresh.access,
    );
    if (!_canReplayRequestAfterRefresh(
      method: opts.method,
      requestCoordinates: requestCoordinates,
      refreshedCoordinates: refreshedCoordinates,
      staleRevisionRecovery: staleRevisionRecovery,
    )) {
      final accessBoundaryChanged =
          requestCoordinates == null ||
          refreshedCoordinates == null ||
          requestCoordinates != refreshedCoordinates;
      handler.next(
        DioException.badResponse(
          statusCode: 409,
          requestOptions: opts,
          response: Response<dynamic>(
            requestOptions: opts,
            statusCode: 409,
            data: {
              'code': staleRevisionRecovery
                  ? 'ACCESS_REVISION_CHANGED'
                  : 'ACCESS_CONTEXT_CHANGED',
              'error': accessBoundaryChanged
                  ? 'Your workspace or access changed while this action was open. Nothing was submitted; review the refreshed screen and try again.'
                  : 'Your access changed while this action was open. Nothing was submitted; review the refreshed screen and try again.',
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

  /// Safe reads can follow a refreshed token into the new canonical envelope.
  /// Mutations are replayed only for ordinary token expiry while the exact
  /// request-start authority coordinates remain unchanged. Stale-revision
  /// recovery and missing/changed coordinates always return control to the UI.
  @visibleForTesting
  static bool canReplayRequestAfterRefresh({
    required String method,
    required Map<String, dynamic>? requestStartAccess,
    required Map<String, dynamic>? refreshedAccess,
    required bool staleRevisionRecovery,
  }) => _canReplayRequestAfterRefresh(
    method: method,
    requestCoordinates: _AccessCoordinates.fromEnvelope(requestStartAccess),
    refreshedCoordinates: _AccessCoordinates.fromEnvelope(refreshedAccess),
    staleRevisionRecovery: staleRevisionRecovery,
  );

  static bool _canReplayRequestAfterRefresh({
    required String method,
    required _AccessCoordinates? requestCoordinates,
    required _AccessCoordinates? refreshedCoordinates,
    required bool staleRevisionRecovery,
  }) {
    if (!_isMutation(method)) return true;
    if (staleRevisionRecovery ||
        requestCoordinates == null ||
        refreshedCoordinates == null) {
      return false;
    }
    return requestCoordinates == refreshedCoordinates;
  }

  /// Ensures only one refresh call happens even when multiple 401s arrive
  /// concurrently. All callers await the same [Completer].
  Future<_RefreshAttempt> _singleFlightRefresh() async {
    if (_refreshCompleter != null) {
      return _refreshCompleter!.future;
    }

    final completer = Completer<_RefreshAttempt>();
    _refreshCompleter = completer;
    try {
      final refreshToken = await tokenStore.getRefreshToken();
      if (refreshToken == null) {
        return _completeRefreshAttempt(const _RefreshRejected(), completer);
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
        return _completeRefreshAttempt(
          _RefreshTransientFailure(
            DioException(
              requestOptions: response.requestOptions,
              response: response,
              type: DioExceptionType.unknown,
              message: 'The refresh endpoint returned an empty response.',
            ),
          ),
          completer,
        );
      }

      final newAccessToken = data['accessToken'] as String?;
      final access = data['access'];
      // Body-first (mobile), Set-Cookie fallback.
      final newRefreshToken = resolveRefreshToken(response);

      if (newAccessToken == null || access is! Map) {
        return _completeRefreshAttempt(
          _RefreshTransientFailure(
            DioException(
              requestOptions: response.requestOptions,
              response: response,
              type: DioExceptionType.unknown,
              message: 'The refresh endpoint returned an invalid response.',
            ),
          ),
          completer,
        );
      }

      await tokenStore.saveTokens(
        accessToken: newAccessToken,
        refreshToken: newRefreshToken ?? refreshToken,
      );
      final accessJson = Map<String, dynamic>.from(access);
      await tokenStore.saveAccessEnvelope(accessJson);

      final result = _RefreshResult(newAccessToken, accessJson);
      return _completeRefreshAttempt(_RefreshSucceeded(result), completer);
    } on DioException catch (error) {
      final attempt = error.response?.statusCode == 401
          ? const _RefreshRejected()
          : _RefreshTransientFailure(error);
      return _completeRefreshAttempt(attempt, completer);
    } catch (error) {
      return _completeRefreshAttempt(
        _RefreshTransientFailure(
          DioException(
            requestOptions: RequestOptions(path: '/auth/refresh'),
            type: DioExceptionType.unknown,
            error: error,
            message: 'Token refresh could not be completed.',
          ),
        ),
        completer,
      );
    }
  }

  _RefreshAttempt _completeRefreshAttempt(
    _RefreshAttempt attempt,
    Completer<_RefreshAttempt> completer,
  ) {
    if (identical(_refreshCompleter, completer)) {
      _refreshCompleter = null;
    }
    completer.complete(attempt);
    return attempt;
  }
}

sealed class _RefreshAttempt {
  const _RefreshAttempt();
}

final class _RefreshSucceeded extends _RefreshAttempt {
  const _RefreshSucceeded(this.result);

  final _RefreshResult result;
}

final class _RefreshRejected extends _RefreshAttempt {
  const _RefreshRejected();
}

final class _RefreshTransientFailure extends _RefreshAttempt {
  const _RefreshTransientFailure(this.error);

  final DioException error;
}

final class _RefreshResult {
  const _RefreshResult(this.accessToken, this.access);

  final String accessToken;
  final Map<String, dynamic> access;
}

final class _AccessCoordinates {
  const _AccessCoordinates({
    required this.userId,
    required this.accessContextId,
    required this.portfolioId,
    required this.accessRevision,
    required this.activeExperience,
  });

  final int userId;
  final int accessContextId;
  final int portfolioId;
  final int accessRevision;
  final String activeExperience;

  static _AccessCoordinates? fromEnvelope(Map<String, dynamic>? envelope) {
    if (envelope == null) return null;
    final identity = envelope['identity'];
    final selectedContext = envelope['selectedContext'];
    if (identity is! Map || selectedContext is! Map) return null;

    final userId = identity['userId'];
    final accessContextId = selectedContext['accessContextId'];
    final portfolioId = selectedContext['portfolioId'];
    final accessRevision = selectedContext['accessRevision'];
    final activeExperience = selectedContext['activeExperience'];
    if (userId is! num ||
        accessContextId is! num ||
        portfolioId is! num ||
        accessRevision is! num ||
        activeExperience is! String) {
      return null;
    }

    return _AccessCoordinates(
      userId: userId.toInt(),
      accessContextId: accessContextId.toInt(),
      portfolioId: portfolioId.toInt(),
      accessRevision: accessRevision.toInt(),
      activeExperience: activeExperience.toLowerCase(),
    );
  }

  @override
  bool operator ==(Object other) =>
      other is _AccessCoordinates &&
      userId == other.userId &&
      accessContextId == other.accessContextId &&
      portfolioId == other.portfolioId &&
      accessRevision == other.accessRevision &&
      activeExperience == other.activeExperience;

  @override
  int get hashCode => Object.hash(
    userId,
    accessContextId,
    portfolioId,
    accessRevision,
    activeExperience,
  );
}
