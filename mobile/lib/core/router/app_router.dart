import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../auth/auth_controller.dart';
import '../voice/voice_command.dart';
import '../../features/auth/forgot_password_screen.dart';
import '../../features/auth/login_screen.dart';
import '../../features/auth/register_screen.dart';
import '../../features/auth/reset_password_screen.dart';
import '../../features/home/home_shell.dart';
import '../../features/home/mobile_domain_hub.dart';
import '../../features/onboarding/onboarding_choice_screen.dart';
import '../../features/onboarding/onboarding_live_setup_screen.dart';
import '../../features/onboarding/onboarding_seeding_screen.dart';
import '../../features/maintenance/work_order_unit_aware_loader.dart';
import '../../features/messages/message_detail_screen.dart';
import '../../features/money/expense_detail_screen.dart';
import '../../features/notifications/notifications_inbox_screen.dart';
import '../../features/payments/payment_detail_screen.dart';
import '../../features/scan/scan_review_screen.dart';
import '../../features/units/unit_command_center_screen.dart';

const _loginPath = '/login';
const _registerPath = '/register';
const _forgotPasswordPath = '/forgot-password';
const _resetPasswordPath = '/reset-password';
const _homePath = '/';
const _chooseSetupPath = '/choose-setup';
const _settingUpPath = '/setting-up';
const _liveSetupPath = '/live-setup';

/// Routes an unauthenticated user is allowed to sit on without being bounced
/// back to `/login`.
const _publicAuthPaths = <String>{
  _loginPath,
  _registerPath,
  _forgotPasswordPath,
  _resetPasswordPath,
};

/// First-login onboarding gate routes — an authenticated-but-undecided user is
/// allowed to sit on these (and only these); everything else bounces here.
const _onboardingGatePaths = <String>{_chooseSetupPath, _settingUpPath};

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
  final authNotifier = ValueNotifier<AuthState>(
    ref.read(authControllerProvider),
  );

  ref.listen<AuthState>(authControllerProvider, (_, next) {
    authNotifier.value = next;
  });

  ref.onDispose(authNotifier.dispose);

  return GoRouter(
    initialLocation: _homePath,
    refreshListenable: authNotifier,
    redirect: (BuildContext context, GoRouterState state) {
      final authState = authNotifier.value;
      final onPublicAuthPage = _publicAuthPaths.contains(state.matchedLocation);

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
        // Allow login / register / forgot-password; bounce everything else.
        return onPublicAuthPage ? null : _loginPath;
      }

      // Authenticated from here on.
      final onGatePage = _onboardingGatePaths.contains(state.matchedLocation);

      // First-login Sandbox-vs-Live gate: an undecided account is kept on the choice/seeding
      // screens until it chooses. This catches login landing, deep links and cold starts alike.
      if (authState is AuthStateAuthenticated && authState.onboardingPending) {
        return onGatePage ? null : _chooseSetupPath;
      }

      // Decided (or returning) user must not linger on the auth pages or the onboarding gate.
      if (onPublicAuthPage || onGatePage) {
        return _homePath;
      }
      return null;
    },
    routes: [
      GoRoute(
        path: _loginPath,
        builder: (context, state) => const LoginScreen(),
      ),
      GoRoute(
        path: _registerPath,
        builder: (context, state) => const RegisterScreen(),
      ),
      GoRoute(
        path: _forgotPasswordPath,
        builder: (context, state) => const ForgotPasswordScreen(),
      ),
      GoRoute(
        // Reached from the emailed reset link's userId+token query params (and
        // from in-app navigation). The screen handles a missing/blank pair.
        path: _resetPasswordPath,
        builder: (context, state) => ResetPasswordScreen(
          userId: state.uri.queryParameters['userId'] ?? '',
          token: state.uri.queryParameters['token'] ?? '',
        ),
      ),
      GoRoute(path: _homePath, builder: (context, state) => const HomeShell()),

      // ── First-login Sandbox-vs-Live gate ──────────────────────────────────
      GoRoute(
        path: _chooseSetupPath,
        builder: (context, state) => const OnboardingChoiceScreen(),
      ),
      GoRoute(
        path: _settingUpPath,
        builder: (context, state) => const OnboardingSeedingScreen(),
      ),
      GoRoute(
        // Guided first step after the Live choice (I8): "add your first
        // property". Not a gate path — reached once the gate is cleared.
        path: _liveSetupPath,
        builder: (context, state) => const OnboardingLiveSetupScreen(),
      ),

      // ── Addressable detail / section routes ───────────────────────────────
      // These render on top of the shell so push notifications and in-app
      // deep links (`context.go('/work-orders/142')`) resolve to the right
      // screen by id. The bottom-nav shell itself stays an IndexedStack.
      GoRoute(
        path: '/rentals',
        builder: (context, state) => const RentalsHubScreen(),
      ),
      GoRoute(
        path: '/work',
        builder: (context, state) => const WorkHubScreen(),
      ),
      GoRoute(
        path: '/work-orders/:id',
        builder: (context, state) =>
            WorkOrderShellTargetLoaderScreen(workOrderId: _idParam(state)),
      ),
      GoRoute(
        path: '/units/:id',
        builder: (context, state) => UnitCommandCenterLoaderScreen(
          unitId: _idParam(state),
          initialTab: unitCommandCenterTabFromName(
            state.uri.queryParameters['tab'],
          ),
        ),
      ),
      GoRoute(
        path: '/money',
        builder: (context, state) => const MoneyHubScreen(),
      ),
      GoRoute(
        path: '/payments/:id',
        builder: (context, state) =>
            PaymentDetailScreen(paymentId: _idParam(state)),
      ),
      GoRoute(
        path: '/expenses/:id',
        builder: (context, state) =>
            ExpenseDetailScreen(expenseId: _idParam(state)),
      ),
      GoRoute(
        path: '/scan/:draftId',
        builder: (context, state) =>
            ScanReviewScreen(draftId: _idParam(state, 'draftId')),
      ),
      GoRoute(
        path: '/messages/:id',
        builder: (context, state) =>
            MessageDetailScreen(conversationId: _idParam(state)),
      ),
      GoRoute(
        path: '/notifications',
        builder: (context, state) => const NotificationsInboxScreen(),
      ),
      GoRoute(
        path: '/inbox',
        builder: (context, state) => const InboxHubScreen(),
      ),
    ],
    errorBuilder: (context, state) =>
        Scaffold(body: Center(child: Text('Page not found: ${state.uri}'))),
  );
});

/// Parses an int path parameter (defaults to `id`), falling back to 0 so a
/// malformed deep link renders the screen's own not-found/error state rather
/// than throwing during routing.
int _idParam(GoRouterState state, [String name = 'id']) {
  return int.tryParse(state.pathParameters[name] ?? '') ?? 0;
}
