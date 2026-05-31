import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../auth/auth_controller.dart';
import '../../features/auth/login_screen.dart';
import '../../features/home/home_shell.dart';

const _loginPath = '/login';
const _homePath = '/';

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
