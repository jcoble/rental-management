import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'core/auth/auth_controller.dart';
import 'core/push/push_service.dart';
import 'core/realtime/signalr_service.dart';
import 'core/router/app_router.dart';
import 'core/theme/app_theme.dart';
import 'core/voice/voice_command_controller.dart';

void main() {
  // Replace Flutter's default red/grey "error box" with a friendly card so a
  // screen that fails to build degrades gracefully instead of flashing a raw
  // error. Self-contained (own Directionality + colors) because the failing
  // widget may sit above the MaterialApp, where no Theme is in scope.
  ErrorWidget.builder = (FlutterErrorDetails details) =>
      _FriendlyErrorWidget(details: details);

  // Allow the mkcert self-signed certificate in debug builds so that both the
  // Dio REST client and the SignalR WebSocket upgrade accept the local dev API.
  installDebugCertBypass();

  runApp(const ProviderScope(child: _AppStartup()));
}

/// On-theme fallback shown by [ErrorWidget.builder] when a widget fails to
/// build. Renders an icon + "Couldn't load this screen"; in debug builds it
/// also surfaces the exception text to aid diagnosis. In release/profile the
/// detail is hidden so users never see a raw stack trace.
class _FriendlyErrorWidget extends StatelessWidget {
  const _FriendlyErrorWidget({required this.details});

  final FlutterErrorDetails details;

  @override
  Widget build(BuildContext context) {
    // Rental Command's dark palette (violet seed #A36BFF). Hard-coded rather
    // than read from Theme.of(context): this widget can be inserted into the
    // tree before/above MaterialApp, so an inherited theme is not guaranteed.
    const surface = Color(0xFF141218);
    const onSurface = Color(0xFFE6E0E9);
    const onSurfaceVariant = Color(0xFFCAC4D0);
    const errorColor = Color(0xFFF2B8B5);

    return Directionality(
      textDirection: TextDirection.ltr,
      child: Material(
        color: surface,
        child: Center(
          child: Padding(
            padding: const EdgeInsets.all(24),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                const Icon(
                  Icons.error_outline_rounded,
                  size: 44,
                  color: errorColor,
                ),
                const SizedBox(height: 16),
                const Text(
                  "Couldn't load this screen",
                  textAlign: TextAlign.center,
                  style: TextStyle(
                    color: onSurface,
                    fontSize: 18,
                    fontWeight: FontWeight.w700,
                  ),
                ),
                const SizedBox(height: 8),
                const Text(
                  'Something went wrong. Try going back and opening it again.',
                  textAlign: TextAlign.center,
                  style: TextStyle(color: onSurfaceVariant, fontSize: 14),
                ),
                if (kDebugMode) ...[
                  const SizedBox(height: 16),
                  Text(
                    details.exceptionAsString(),
                    textAlign: TextAlign.center,
                    maxLines: 6,
                    overflow: TextOverflow.ellipsis,
                    style: const TextStyle(
                      color: onSurfaceVariant,
                      fontSize: 12,
                      fontFamily: 'monospace',
                    ),
                  ),
                ],
              ],
            ),
          ),
        ),
      ),
    );
  }
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
        // Start listening for voice/App-Actions deep links. Runs regardless of
        // auth state; a command that arrives before login simply waits in the
        // one-slot bus until HomeShell mounts.
        ref.read(voiceLinkServiceProvider).start();
        // Initialize push (FCM + local notifications). Fail-soft: with no
        // Firebase config the app runs normally with push disabled. A tap that
        // cold-starts the app before auth restore stashes its route in the
        // pending-push bus, drained once authenticated (see HomeShell).
        ref.read(pushServiceProvider).init();
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
        // Dark by default, matching the EdiPlatform web app (ModeWatcher
        // defaultMode="dark"). No in-app toggle exists yet.
        themeMode: ThemeMode.dark,
        home: const Scaffold(body: Center(child: CircularProgressIndicator())),
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
      // Dark by default, matching the EdiPlatform web app (ModeWatcher
      // defaultMode="dark"). No in-app toggle exists yet.
      themeMode: ThemeMode.dark,
      routerConfig: router,
    );
  }
}
