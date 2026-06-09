import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../auth/auth_controller.dart';
import '../voice/voice_command.dart';
import '../../features/auth/login_screen.dart';
import '../../features/home/home_shell.dart';

const _loginPath = '/login';
const _homePath = '/';

/// True for voice / App Actions deep links (`rentalcommand://voice/...`).
///
/// The Flutter engine forwards these VIEW intents to the router in addition to
/// the app_links plugin that actually handles them (see [VoiceLinkService]).
/// We neutralize them here so they never hit `errorBuilder` ("page not found");
/// app_links remains the sole navigator for the command itself.
bool _isVoiceDeepLink(Uri uri) =>
    uri.scheme == 'rentalcommand' ||
    uri.host == 'voice' ||
    uri.pathSegments.contains('voice') ||
    parseVoiceCommand(uri) != null;

/// Application router with auth-based redirect guard.
///
/// Unauthenticated users are redirected to `/login`.
/// Authenticated users attempting `/login` are redirected to `/`.
/// While auth state is [AuthStateUnknown] (startup), a loading splash is shown.
final appRouterProvider = Provider<GoRouter>((ref) {
  final authNotifier = ValueNotifier<AuthState>(ref.read(authControllerProvider));

  ref.listen<AuthState>(authControllerProvider, (_, next) {
    authNotifier.value = next;
  });

  ref.onDispose(authNotifier.dispose);

  return GoRouter(
    initialLocation: _homePath,
    refreshListenable: authNotifier,
    redirect: (BuildContext context, GoRouterState state) {
      final authState = authNotifier.value;
      final onLoginPage = state.matchedLocation == _loginPath;

      if (authState is AuthStateUnknown) {
        // Still determining auth — show splash; don't redirect yet.
        return null;
      }

      // A voice deep link reached the router (engine-forwarded). Send it to the
      // auth-appropriate root so it never 404s; the app_links handler does the
      // real navigation once HomeShell drains the pending command.
      if (_isVoiceDeepLink(state.uri)) {
        return authState is AuthStateAuthenticated ? _homePath : _loginPath;
      }

      if (authState is AuthStateUnauthenticated) {
        return onLoginPage ? null : _loginPath;
      }

      // Authenticated.
      return onLoginPage ? _homePath : null;
    },
    routes: [
      GoRoute(
        path: _loginPath,
        builder: (context, state) => const LoginScreen(),
      ),
      GoRoute(
        path: _homePath,
        builder: (context, state) => const HomeShell(),
      ),
    ],
    errorBuilder: (context, state) => Scaffold(
      body: Center(
        child: Text('Page not found: ${state.uri}'),
      ),
    ),
  );
});
