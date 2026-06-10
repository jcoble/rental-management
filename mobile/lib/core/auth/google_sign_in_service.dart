import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:google_sign_in/google_sign_in.dart';

/// Thin wrapper around the `google_sign_in` (v7.x) singleton.
///
/// v7 split the old monolithic `signIn()` into:
///   1. one-time [GoogleSignIn.initialize] (clientId / serverClientId), then
///   2. [GoogleSignIn.authenticate] which returns a [GoogleSignInAccount].
///
/// We only need the **id_token** to hand to our own backend (`POST /auth/google`),
/// which validates it and mints our JWT pair. [serverClientId] is the *Web*
/// OAuth client id: it tells Google to mint an id_token whose `aud` is that web
/// client, which is exactly what the server expects to validate against.
class GoogleSignInService {
  GoogleSignInService({required String serverClientId})
    : _serverClientId = serverClientId;

  final String _serverClientId;

  bool _initialized = false;

  /// Lazily runs the one-time [GoogleSignIn.initialize] before the first call.
  Future<void> _ensureInitialized() async {
    if (_initialized) return;
    await GoogleSignIn.instance.initialize(serverClientId: _serverClientId);
    _initialized = true;
  }

  /// Launches the interactive Google sign-in flow and returns the resulting
  /// **id_token**, or `null` if the user cancelled.
  ///
  /// Throws [GoogleSignInUnavailable] when the platform has no interactive
  /// authenticate method (so the caller can show a sensible message instead of
  /// a raw exception). Any other [GoogleSignInException] is rethrown.
  Future<String?> signIn() async {
    await _ensureInitialized();

    if (!GoogleSignIn.instance.supportsAuthenticate()) {
      throw const GoogleSignInUnavailable();
    }

    try {
      final GoogleSignInAccount account = await GoogleSignIn.instance
          .authenticate();
      final String? idToken = account.authentication.idToken;
      if (idToken == null || idToken.isEmpty) {
        throw const GoogleSignInUnavailable(
          'Google did not return an ID token. Check the server client ID '
          'configuration.',
        );
      }
      return idToken;
    } on GoogleSignInException catch (e) {
      // User backed out of the Google sheet — treat as a silent no-op.
      if (e.code == GoogleSignInExceptionCode.canceled) {
        return null;
      }
      debugPrint('GoogleSignInException ${e.code}: ${e.description}');
      rethrow;
    }
  }

  /// Clears the cached Google session so the next [signIn] re-prompts for an
  /// account. Best-effort; failures are ignored.
  Future<void> signOut() async {
    try {
      if (_initialized) {
        await GoogleSignIn.instance.signOut();
      }
    } catch (_) {
      // Best-effort.
    }
  }
}

/// Raised when Google sign-in cannot be performed on this platform/build.
class GoogleSignInUnavailable implements Exception {
  const GoogleSignInUnavailable([
    this.message = 'Google sign-in is not available on this device.',
  ]);
  final String message;

  @override
  String toString() => 'GoogleSignInUnavailable: $message';
}

/// The Web OAuth 2.0 client ID. Tells Google to issue an id_token whose audience
/// is this client, which the backend validates. Public by design (no secret).
const String kGoogleServerClientId =
    '81049991396-1mtrm181dp0el5gjm03r6gq81m91non8.apps.googleusercontent.com';

final googleSignInServiceProvider = Provider<GoogleSignInService>((ref) {
  return GoogleSignInService(serverClientId: kGoogleServerClientId);
});
