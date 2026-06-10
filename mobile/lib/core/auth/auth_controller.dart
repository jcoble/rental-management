import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../api/api_exception.dart';
import 'auth_models.dart';
import 'auth_repository.dart';
import 'token_store.dart';

/// The three possible auth states for the app.
sealed class AuthState {
  const AuthState();
}

/// Initial state — we haven't checked stored tokens yet.
final class AuthStateUnknown extends AuthState {
  const AuthStateUnknown();
}

/// A valid session exists.
final class AuthStateAuthenticated extends AuthState {
  const AuthStateAuthenticated(this.user);
  final AuthUser user;
}

/// No valid session.
final class AuthStateUnauthenticated extends AuthState {
  const AuthStateUnauthenticated({this.error});
  final String? error;
}

/// Manages authentication state for the app.
///
/// On startup, call [restoreSession] to check for a stored access token and
/// verify it against `GET /auth/me`. If valid, transitions to
/// [AuthStateAuthenticated]; otherwise [AuthStateUnauthenticated].
class AuthController extends Notifier<AuthState> {
  @override
  AuthState build() {
    // React to forced logout signals from the AuthInterceptor.
    ref.listen<int>(logoutSignalProvider, (previous, next) {
      if (previous != null && next > previous) {
        notifyLogout();
      }
    });
    return const AuthStateUnknown();
  }

  AuthRepository get _repository => ref.read(authRepositoryProvider);
  TokenStore get _tokenStore => ref.read(tokenStoreProvider);

  /// Attempts to restore a session from secure storage.
  /// Should be called once on app startup.
  Future<void> restoreSession() async {
    final token = await _tokenStore.getAccessToken();
    if (token == null || token.isEmpty) {
      state = const AuthStateUnauthenticated();
      return;
    }

    try {
      final user = await _repository.currentUser();
      state = AuthStateAuthenticated(user);
    } on ApiException {
      // Stored token is invalid or expired and refresh also failed.
      await _tokenStore.clearTokens();
      state = const AuthStateUnauthenticated();
    }
  }

  /// Signs in with [email] and [password].
  ///
  /// Throws [ApiException] on failure so the UI can display the error.
  Future<void> login(String email, String password) async {
    state = const AuthStateUnknown(); // show loading
    try {
      final response = await _repository.login(email, password);
      state = AuthStateAuthenticated(response.user);
    } on ApiException catch (e) {
      state = AuthStateUnauthenticated(error: e.message);
      rethrow;
    }
  }

  /// Registers a new account.
  ///
  /// Registration does NOT establish a session (the backend gates on email
  /// confirmation), so this does not touch [AuthState] — it returns the
  /// server's [RegisterResult] for the screen to display. Throws [ApiException]
  /// on failure so the UI can show the error.
  Future<RegisterResult> register({
    required String email,
    required String password,
    required String displayName,
  }) {
    return _repository.register(
      email: email,
      password: password,
      displayName: displayName,
    );
  }

  /// Requests a password-reset email. Neutral by design — does not reveal
  /// whether the account exists. Throws [ApiException] only on transport error.
  Future<void> forgotPassword(String email) {
    return _repository.forgotPassword(email);
  }

  /// Completes sign-in using a Google **id_token** obtained on-device.
  ///
  /// Mirrors [login]: hands the token to `/auth/google`, then transitions to
  /// [AuthStateAuthenticated] so the router redirect drives navigation.
  Future<void> signInWithGoogle(String idToken) async {
    state = const AuthStateUnknown(); // show loading
    try {
      final response = await _repository.signInWithGoogle(idToken);
      state = AuthStateAuthenticated(response.user);
    } on ApiException catch (e) {
      state = AuthStateUnauthenticated(error: e.message);
      rethrow;
    }
  }

  /// Signs out and clears all local auth state.
  Future<void> logout() async {
    await _repository.logout();
    state = const AuthStateUnauthenticated();
  }

  /// Called by the interceptor's logout signal — resets state without an
  /// extra server call (tokens are already invalid).
  void notifyLogout() {
    state = const AuthStateUnauthenticated();
  }
}

final authControllerProvider = NotifierProvider<AuthController, AuthState>(
  AuthController.new,
);
