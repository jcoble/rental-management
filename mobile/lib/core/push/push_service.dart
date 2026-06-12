import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:dio/dio.dart';
import 'package:firebase_core/firebase_core.dart';
import 'package:firebase_messaging/firebase_messaging.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_local_notifications/flutter_local_notifications.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../api/dio_client.dart';
import '../auth/auth_controller.dart';
import '../router/app_router.dart';
import 'notification_routing.dart';

/// Android notification channel the foreground/local notifications post to. Its
/// id is also referenced from `AndroidManifest.xml`
/// (`com.google.firebase.messaging.default_notification_channel_id`) so
/// system-tray (background/killed) FCM notifications land on the same channel.
const String kAndroidNotificationChannelId = 'rental_command_default';
const String _androidChannelName = 'Notifications';
const String _androidChannelDescription =
    'Rent, maintenance, lease and account alerts.';

/// Holds a deep-link route extracted from a notification tap that arrived
/// before the app was authenticated (cold start). [HomeShell] / the auth flow
/// drains it once authenticated so the link isn't lost. Mirrors the one-slot
/// `pendingVoiceCommandProvider` bus.
class PendingPushLink extends Notifier<String?> {
  @override
  String? build() => null;

  void set(String route) => state = route;
  void consume() => state = null;
}

final pendingPushLinkProvider =
    NotifierProvider<PendingPushLink, String?>(PendingPushLink.new);

/// Coordinates Firebase Cloud Messaging + local notifications.
///
/// FAIL-SOFT BY DESIGN: with no `google-services.json` / `firebase_options.dart`
/// present, [Firebase.initializeApp] throws (no default options) — we catch it,
/// mark push unavailable, and the rest of the app runs normally with push
/// disabled. There is intentionally NO generated `firebase_options.dart` and NO
/// `com.google.gms.google-services` gradle plugin yet; a future
/// `flutterfire configure` run will generate `firebase_options.dart`, after
/// which the init below should pass `DefaultFirebaseOptions.currentPlatform`
/// (see the marked hook).
class PushService {
  PushService(this._ref);

  final Ref _ref;

  final FlutterLocalNotificationsPlugin _localNotifications =
      FlutterLocalNotificationsPlugin();

  bool _firebaseReady = false;
  bool _registered = false;
  String? _currentToken;
  StreamSubscription<String>? _tokenRefreshSub;
  StreamSubscription<RemoteMessage>? _onMessageSub;
  StreamSubscription<RemoteMessage>? _onMessageOpenedSub;
  ProviderSubscription<AuthState>? _authSub;

  Dio get _dio => _ref.read(dioProvider);

  /// Whether Firebase initialized successfully this session.
  bool get isAvailable => _firebaseReady;

  /// Call once at app startup. Safe no-op on repeat. Never throws.
  Future<void> init() async {
    try {
      // ── Firebase init hook ────────────────────────────────────────────────
      // No-arg init reads native config (google-services.json / Info.plist).
      // When `flutterfire configure` is run later it generates
      // `firebase_options.dart`; switch this to:
      //   await Firebase.initializeApp(
      //       options: DefaultFirebaseOptions.currentPlatform);
      await Firebase.initializeApp();
      _firebaseReady = true;
    } catch (e) {
      // Expected with no Firebase config (e.g. Android with no gradle plugin /
      // no google-services.json): run with push disabled.
      if (kDebugMode) {
        debugPrint('[Push] Firebase unavailable; push disabled. ($e)');
      }
      _firebaseReady = false;
      return;
    }

    await _initLocalNotifications();
    await _wireMessageHandlers();

    // Register with the backend now if already authenticated, and re-register
    // whenever auth transitions to authenticated.
    _authSub = _ref.listen<AuthState>(
      authControllerProvider,
      (previous, next) {
        if (next is AuthStateAuthenticated) {
          unawaited(_registerDevice());
        } else if (next is AuthStateUnauthenticated) {
          _registered = false;
        }
      },
    );
    if (_ref.read(authControllerProvider) is AuthStateAuthenticated) {
      unawaited(_registerDevice());
    }
  }

  // ── Local notifications ──────────────────────────────────────────────────

  Future<void> _initLocalNotifications() async {
    const androidInit =
        AndroidInitializationSettings('@mipmap/ic_launcher');
    const iosInit = DarwinInitializationSettings(
      requestAlertPermission: false,
      requestBadgePermission: false,
      requestSoundPermission: false,
    );
    const settings =
        InitializationSettings(android: androidInit, iOS: iosInit);

    await _localNotifications.initialize(
      settings,
      onDidReceiveNotificationResponse: (response) {
        _routeFromPayload(response.payload);
      },
    );

    // Create the Android channel up front so background FCM notifications and
    // our foreground local notifications share it.
    final androidPlugin =
        _localNotifications.resolvePlatformSpecificImplementation<
            AndroidFlutterLocalNotificationsPlugin>();
    await androidPlugin?.createNotificationChannel(
      const AndroidNotificationChannel(
        kAndroidNotificationChannelId,
        _androidChannelName,
        description: _androidChannelDescription,
        importance: Importance.high,
      ),
    );
  }

  // ── FCM message handlers ──────────────────────────────────────────────────

  Future<void> _wireMessageHandlers() async {
    final messaging = FirebaseMessaging.instance;

    await messaging.requestPermission();

    // iOS: show notifications while the app is in the foreground. (APNs key not
    // yet provisioned — documented-only; this call is harmless without it.)
    await messaging.setForegroundNotificationPresentationOptions(
      alert: true,
      badge: true,
      sound: true,
    );

    // Foreground messages → surface via a local notification so the user sees
    // them while using the app (FCM only auto-displays in background/killed).
    _onMessageSub = FirebaseMessaging.onMessage.listen(_showLocalNotification);

    // Background → tapped (app was alive in the background).
    _onMessageOpenedSub =
        FirebaseMessaging.onMessageOpenedApp.listen((message) {
      _routeFromData(message.data);
    });

    // Cold start: the notification that launched the app from killed state.
    final initial = await messaging.getInitialMessage();
    if (initial != null) {
      _routeFromData(initial.data);
    }
  }

  Future<void> _showLocalNotification(RemoteMessage message) async {
    final notification = message.notification;
    // Encode the data map so a tap can route (the local-notification path has
    // no access to the original RemoteMessage).
    final payload = jsonEncode(message.data);

    final title = notification?.title ??
        (message.data['title'] as String?) ??
        'Rental Command';
    final body = notification?.body ?? (message.data['message'] as String?) ?? '';

    const androidDetails = AndroidNotificationDetails(
      kAndroidNotificationChannelId,
      _androidChannelName,
      channelDescription: _androidChannelDescription,
      importance: Importance.high,
      priority: Priority.high,
      icon: '@mipmap/ic_launcher',
    );
    const iosDetails = DarwinNotificationDetails();
    const details =
        NotificationDetails(android: androidDetails, iOS: iosDetails);

    await _localNotifications.show(
      // Use the message hashCode for a stable-enough id within a session.
      message.hashCode,
      title,
      body,
      details,
      payload: payload,
    );
  }

  // ── Device registration ────────────────────────────────────────────────────

  Future<void> _registerDevice() async {
    if (!_firebaseReady || _registered) return;
    try {
      final token = await FirebaseMessaging.instance.getToken();
      if (token == null || token.isEmpty) return;
      _currentToken = token;

      await _postToken(token);
      _registered = true;

      // Re-register on token rotation.
      _tokenRefreshSub ??=
          FirebaseMessaging.instance.onTokenRefresh.listen((newToken) {
        _currentToken = newToken;
        unawaited(_postToken(newToken));
      });
    } catch (e) {
      if (kDebugMode) debugPrint('[Push] device registration failed: $e');
    }
  }

  Future<void> _postToken(String token) async {
    final platform = Platform.isIOS ? 'ios' : 'android';
    await _dio.post<void>('/devices', data: {
      'token': token,
      'platform': platform,
    });
  }

  /// Best-effort token removal on logout. Never throws; failure must not block
  /// logout.
  Future<void> unregisterOnLogout() async {
    _registered = false;
    final token = _currentToken;
    if (!_firebaseReady || token == null || token.isEmpty) return;
    try {
      await _dio.delete<void>('/devices/$token');
    } catch (e) {
      if (kDebugMode) debugPrint('[Push] device unregister failed: $e');
    }
  }

  // ── Tap routing ────────────────────────────────────────────────────────────

  void _routeFromPayload(String? payload) {
    if (payload == null || payload.isEmpty) {
      _navigate(resolveNotificationRoute(null));
      return;
    }
    try {
      final data = jsonDecode(payload);
      if (data is Map) {
        _routeFromData(Map<String, dynamic>.from(data));
        return;
      }
    } catch (_) {
      // fall through
    }
    _navigate(resolveNotificationRoute(null));
  }

  void _routeFromData(Map<String, dynamic> data) {
    final actionUrl = data['actionUrl'] as String?;
    _navigate(resolveNotificationRoute(actionUrl));
  }

  /// Navigates to [route] if authenticated; otherwise stashes it so it runs
  /// once the user signs in (mirrors the pending-voice-command bus).
  void _navigate(String route) {
    final authState = _ref.read(authControllerProvider);
    if (authState is AuthStateAuthenticated) {
      _ref.read(appRouterProvider).go(route);
    } else {
      _ref.read(pendingPushLinkProvider.notifier).set(route);
    }
  }

  void dispose() {
    _tokenRefreshSub?.cancel();
    _onMessageSub?.cancel();
    _onMessageOpenedSub?.cancel();
    _authSub?.close();
  }
}

final pushServiceProvider = Provider<PushService>((ref) {
  final service = PushService(ref);
  ref.onDispose(service.dispose);
  return service;
});
