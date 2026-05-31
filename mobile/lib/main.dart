import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'core/auth/auth_controller.dart';
import 'core/router/app_router.dart';
import 'core/theme/app_theme.dart';

void main() {
  runApp(const ProviderScope(child: _AppStartup()));
}

/// Bootstraps the app by restoring any persisted auth session before rendering
/// the main router.
///
/// Shows a minimal loading screen while [restoreSession] runs (auth state is
/// [AuthStateUnknown]). Once the state transitions the router's
/// [refreshListenable] picks up the change and redirects appropriately.
class _AppStartup extends ConsumerStatefulWidget {
  const _AppStartup();

  @override
  ConsumerState<_AppStartup> createState() => _AppStartupState();
}

class _AppStartupState extends ConsumerState<_AppStartup> {
  bool _sessionRestored = false;

  @override
  void initState() {
    super.initState();
    // Kick off session restore on first frame without blocking UI.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!_sessionRestored) {
        _sessionRestored = true;
        ref.read(authControllerProvider.notifier).restoreSession();
      }
    });
  }

  @override
  Widget build(BuildContext context) {
    final authState = ref.watch(authControllerProvider);

    if (authState is AuthStateUnknown) {
      // Minimal splash while checking stored tokens.
      return MaterialApp(
        theme: AppTheme.light,
        darkTheme: AppTheme.dark,
        themeMode: ThemeMode.system,
        home: const Scaffold(
          body: Center(child: CircularProgressIndicator()),
        ),
      );
    }

    return const RentalCommandApp();
  }
}

/// Root app widget once auth state is resolved.
class RentalCommandApp extends ConsumerWidget {
  const RentalCommandApp({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final router = ref.watch(appRouterProvider);
    return MaterialApp.router(
      title: 'Rental Command',
      theme: AppTheme.light,
      darkTheme: AppTheme.dark,
      themeMode: ThemeMode.system,
      routerConfig: router,
    );
  }
}
