import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../api/api_exception.dart';
import '../push/push_service.dart';
import '../../features/onboarding/onboarding_repository.dart';
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
///
/// [onboardingPending] is true when the account has not yet made the first-login Sandbox-vs-Live
/// choice; while true the router gates the user onto the choice screen. It is resolved (via
/// `GET /portfolio/sandbox-state`) before this state is emitted, so the router can read it
/// synchronously and never flashes the dashboard before the gate decision.
///
/// [onboardingResolved] is false when the `sandbox-state` lookup itself failed (offline /
/// transient 5xx) so we could NOT determine whether the choice is owed. In that case
/// [onboardingPending] is left false so a *returning* user is never trapped behind a flaky
/// network — but the home shell re-resolves on its first build (mirroring the web's
/// per-navigation re-check) so a genuinely-new account whose lookup hiccupped at login is
/// re-checked and gated almost immediately rather than slipping past once. See I7.
final class AuthStateAuthenticated extends AuthState {
  const AuthStateAuthenticated(
    this.user,
    this.access, {
    required this.activeExperience,
    this.onboardingPending = false,
    this.onboardingResolved = true,
  });
  final AuthUser user;
  final AccessEnvelope access;
  final WorkspaceExperience activeExperience;
  final bool onboardingPending;
  final bool onboardingResolved;

  Set<String> get capabilities => access.capabilitiesFor(activeExperience);
  bool hasCapability(String capability) => capabilities.contains(capability);
  bool get isTenantExperience => activeExperience == WorkspaceExperience.tenant;
}

/// No valid session.
final class AuthStateUnauthenticated extends AuthState {
  const AuthStateUnauthenticated({this.error});
  final String? error;
}

bool accessAuthorityChanged(AuthState? previous, AuthState next) {
  if (previous is! AuthStateAuthenticated || next is! AuthStateAuthenticated) {
    return false;
  }
  final previousContext = previous.access.selectedContext;
  final nextContext = next.access.selectedContext;
  return previous.access.identity.userId != next.access.identity.userId ||
      previousContext.accessContextId != nextContext.accessContextId ||
      previousContext.portfolioId != nextContext.portfolioId ||
      previousContext.accessRevision != nextContext.accessRevision ||
      previous.activeExperience != next.activeExperience;
}

/// Manages authentication state for the app.
///
/// On startup, call [restoreSession] to check for a stored access token and
/// verify it against `GET /auth/me`. If valid, transitions to
/// [AuthStateAuthenticated]; otherwise [AuthStateUnauthenticated].
class AuthController extends Notifier<AuthState> {
  Future<void>? _accessSelectionInFlight;

  @override
  AuthState build() {
    // React to forced logout signals from the AuthInterceptor.
    ref.listen<int>(logoutSignalProvider, (previous, next) {
      if (previous != null && next > previous) {
        notifyLogout();
      }
    });
    ref.listen<AccessEnvelope?>(accessChangeSignalProvider, (previous, next) {
      if (next != null) notifyAccessChanged(next);
    });
    return const AuthStateUnknown();
  }

  AuthRepository get _repository => ref.read(authRepositoryProvider);
  TokenStore get _tokenStore => ref.read(tokenStoreProvider);
  OnboardingRepository get _onboarding =>
      ref.read(onboardingRepositoryProvider);

  /// Resolves whether the freshly-authenticated user still owes the first-login Sandbox-vs-Live
  /// choice.
  ///
  /// Returns ([pending], [resolved]). On a failed lookup (offline, transient 5xx) [resolved] is
  /// false and [pending] is false — so a returning user is never trapped behind the gate by a
  /// flaky network. The unresolved flag lets the home shell re-resolve on its first build so a
  /// genuinely-new account is still gated almost immediately (mirrors the web's per-navigation
  /// re-check). See I7.
  Future<({bool pending, bool resolved})> _resolveOnboardingPending() async {
    try {
      final state = await _onboarding.sandboxState();
      return (pending: state.onboardingChoicePending, resolved: true);
    } on ApiException {
      return (pending: false, resolved: false);
    }
  }

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
      final access = await _repository.currentAccess();
      final onboarding = await _resolveOnboardingPending();
      state = AuthStateAuthenticated(
        user,
        access,
        activeExperience: access.selectedContext.activeExperience,
        onboardingPending: onboarding.pending,
        onboardingResolved: onboarding.resolved,
      );
    } on ApiException {
      // Stored token is invalid or expired and refresh also failed.
      await _tokenStore.clearTokens();
      state = const AuthStateUnauthenticated();
    }
  }

  /// Signs in with [email] and [password].
  ///
  /// Throws [ApiException] on failure so the UI can display the error.
  Future<void> login(
    String email,
    String password, {
    int? accessContextId,
  }) async {
    // Do NOT flip to AuthStateUnknown here: the router shows a full-screen splash
    // for that state, which unmounts the login screen. On failure the router then
    // rebuilds a FRESH /login with no error, swallowing the message. The login
    // screen owns its own `_isLoading` spinner and stays mounted, so its local
    // error (and our AuthStateUnauthenticated.error backstop) render correctly.
    try {
      final response = await _repository.login(
        email,
        password,
        accessContextId: accessContextId,
      );
      final onboarding = await _resolveOnboardingPending();
      state = AuthStateAuthenticated(
        response.user,
        response.access,
        activeExperience: response.access.selectedContext.activeExperience,
        onboardingPending: onboarding.pending,
        onboardingResolved: onboarding.resolved,
      );
    } on ApiException catch (e) {
      // User is already on /login, so setting this does not trigger a redirect;
      // it just provides an error backstop the screen can watch.
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

  /// Re-sends the email-verification message. Neutral by design (no account
  /// enumeration). Throws [ApiException] only on transport error.
  Future<void> resendVerification(String email) {
    return _repository.resendVerification(email);
  }

  /// Completes a password reset using the emailed userId + token. Does NOT
  /// establish a session — the user signs in afterward. Throws [ApiException]
  /// on an invalid/expired token or password-policy failure.
  Future<void> resetPassword({
    required String userId,
    required String token,
    required String newPassword,
  }) {
    return _repository.resetPassword(
      userId: userId,
      token: token,
      newPassword: newPassword,
    );
  }

  /// Changes the signed-in user's password (Bearer-authenticated). Does not
  /// alter [AuthState]. Throws [ApiException] on failure so the UI can show it.
  Future<void> changePassword({
    required String currentPassword,
    required String newPassword,
  }) {
    return _repository.changePassword(
      currentPassword: currentPassword,
      newPassword: newPassword,
    );
  }

  /// Completes sign-in using a Google **id_token** obtained on-device.
  ///
  /// Mirrors [login]: hands the token to `/auth/google`, then transitions to
  /// [AuthStateAuthenticated] so the router redirect drives navigation.
  Future<void> signInWithGoogle(String idToken) async {
    // Same rationale as [login]: do NOT flip to AuthStateUnknown (it shows the
    // splash and unmounts the login screen, dropping the error on failure). The
    // login screen's `_isGoogleLoading` drives the spinner while it stays mounted.
    try {
      final response = await _repository.signInWithGoogle(idToken);
      final onboarding = await _resolveOnboardingPending();
      state = AuthStateAuthenticated(
        response.user,
        response.access,
        activeExperience: response.access.selectedContext.activeExperience,
        onboardingPending: onboarding.pending,
        onboardingResolved: onboarding.resolved,
      );
    } on ApiException catch (e) {
      state = AuthStateUnauthenticated(error: e.message);
      rethrow;
    }
  }

  /// Re-resolves the first-login gate when the initial lookup was undetermined
  /// (offline / transient 5xx at login). Called from the home shell's first
  /// build to mirror the web's per-navigation re-check (I7): a returning user is
  /// let in immediately and re-checked here; a genuinely-new account whose
  /// `sandbox-state` call hiccupped is gated as soon as the state becomes
  /// readable, instead of slipping past the choice once. No-op unless currently
  /// authenticated AND the gate is still unresolved.
  Future<void> reresolveOnboardingIfUnresolved() async {
    final current = state;
    if (current is! AuthStateAuthenticated || current.onboardingResolved) {
      return;
    }
    final onboarding = await _resolveOnboardingPending();
    if (!onboarding.resolved) return; // still can't tell — try again next mount
    // Re-read state in case it changed while awaiting (e.g. logout).
    final latest = state;
    if (latest is! AuthStateAuthenticated) return;
    state = AuthStateAuthenticated(
      latest.user,
      latest.access,
      activeExperience: latest.activeExperience,
      onboardingPending: onboarding.pending,
      onboardingResolved: true,
    );
  }

  /// Clears the first-login gate after the user has made (and the server has recorded) the
  /// Sandbox-vs-Live choice, so the router stops redirecting to the choice screen and lets them into
  /// the app. No-op unless currently authenticated and still flagged pending.
  void markOnboardingComplete() {
    final current = state;
    if (current is AuthStateAuthenticated && current.onboardingPending) {
      state = AuthStateAuthenticated(
        current.user,
        current.access,
        activeExperience: current.activeExperience,
        onboardingPending: false,
        onboardingResolved: true,
      );
    }
  }

  /// Signs out and clears all local auth state.
  Future<void> logout() async {
    // Best-effort device-token removal BEFORE tokens are cleared, so the
    // DELETE /devices call is still authenticated. Failure must not block
    // logout.
    try {
      await ref.read(pushServiceProvider).unregisterOnLogout();
    } catch (_) {
      // ignore — logout proceeds regardless
    }
    await _repository.logout();
    state = const AuthStateUnauthenticated();
  }

  /// Called by the interceptor's logout signal — resets state without an
  /// extra server call (tokens are already invalid).
  void notifyLogout() {
    state = const AuthStateUnauthenticated();
  }

  /// Replaces the client authority snapshot after a refresh detects that the
  /// server-side access revision changed. Shell listeners own cache and
  /// realtime cleanup; this state transition supplies the new revision.
  void notifyAccessChanged(AccessEnvelope access) {
    final current = state;
    if (current is! AuthStateAuthenticated) return;
    if (current.access.selectedContext.accessContextId ==
            access.selectedContext.accessContextId &&
        current.access.selectedContext.accessRevision ==
            access.selectedContext.accessRevision &&
        current.activeExperience == access.selectedContext.activeExperience) {
      return;
    }
    state = AuthStateAuthenticated(
      current.user,
      access,
      activeExperience: access.selectedContext.activeExperience,
      onboardingPending: current.onboardingPending,
      onboardingResolved: current.onboardingResolved,
    );
  }

  Future<void> selectExperience(WorkspaceExperience experience) =>
      _runAccessSelection(() async {
        final current = state;
        if (current is! AuthStateAuthenticated ||
            !current.access.availableExperiences.contains(experience)) {
          return;
        }
        final access = await _repository.selectExperience(experience);
        final latest = state;
        if (latest is! AuthStateAuthenticated) return;
        state = AuthStateAuthenticated(
          latest.user,
          access,
          activeExperience: access.selectedContext.activeExperience,
          onboardingPending: latest.onboardingPending,
          onboardingResolved: latest.onboardingResolved,
        );
      });

  Future<void> selectContext(int accessContextId) =>
      _runAccessSelection(() async {
        final current = state;
        if (current is! AuthStateAuthenticated) return;
        final result = await _repository.selectContext(accessContextId);
        final latest = state;
        if (latest is! AuthStateAuthenticated) return;
        state = AuthStateAuthenticated(
          latest.user,
          result.access,
          activeExperience: result.access.selectedContext.activeExperience,
          onboardingPending: latest.onboardingPending,
          onboardingResolved: latest.onboardingResolved,
        );
      });

  Future<void> _runAccessSelection(Future<void> Function() selection) {
    final inFlight = _accessSelectionInFlight;
    if (inFlight != null) return inFlight;

    late final Future<void> operation;
    operation = selection().whenComplete(() {
      if (identical(_accessSelectionInFlight, operation)) {
        _accessSelectionInFlight = null;
      }
    });
    _accessSelectionInFlight = operation;
    return operation;
  }
}

final authControllerProvider = NotifierProvider<AuthController, AuthState>(
  AuthController.new,
);
