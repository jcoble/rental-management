import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:signalr_netcore/signalr_client.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/auth/token_store.dart';
import '../../core/config/app_config.dart';
import 'notifications_repository.dart';

/// Manages a single SignalR connection to the **notification** hub at
/// `/api/v1/hubs/notifications`.
///
/// Contract (from `RentalCommand.Api/Hubs/NotificationHub.cs`):
///   * On connect the server auto-joins the caller's `user-{userId}` and
///     `portfolio-{portfolioId}` groups from JWT claims — the client sends no
///     join message.
///   * The server broadcasts two events: `ReceiveNotification` (a notification
///     payload object) and `ReceiveAlert` ({ severity, message, timestamp }).
///
/// We don't need the payload shape here: any inbound event means "your
/// notifications changed", so we just emit a tick and let the watcher refresh
/// the unread count + inbox providers (FCM already handles OS-level display).
class NotificationRealtimeService {
  NotificationRealtimeService(this._tokenStore);

  final TokenStore _tokenStore;
  HubConnection? _hub;

  final _controller = StreamController<void>.broadcast();

  /// Fires once per inbound `ReceiveNotification` / `ReceiveAlert` event.
  Stream<void> get events => _controller.stream;

  Future<void> connect() async {
    if (_hub != null) {
      final s = _hub!.state;
      if (s == HubConnectionState.Connected ||
          s == HubConnectionState.Connecting ||
          s == HubConnectionState.Reconnecting) {
        return;
      }
    }

    // kApiBaseUrl ends with /api/v1; the hub is at <base>/hubs/notifications.
    final hubUrl = '$kApiBaseUrl/hubs/notifications';
    _hub = _buildConnection(hubUrl);
    try {
      await _hub!.start();
      if (kDebugMode) debugPrint('[NotifHub] connected state=${_hub!.state}');
    } catch (e) {
      if (kDebugMode) debugPrint('[NotifHub] connect failed: $e');
    }
  }

  Future<void> disconnect() async {
    final hub = _hub;
    _hub = null;
    if (hub != null) {
      try {
        await hub.stop();
      } catch (_) {
        // best effort
      }
    }
  }

  HubConnection _buildConnection(String hubUrl) {
    final options = HttpConnectionOptions(
      accessTokenFactory: () async =>
          await _tokenStore.getAccessToken() ?? '',
    );

    final hub = HubConnectionBuilder()
        .withUrl(hubUrl, options: options)
        .withAutomaticReconnect(
            retryDelays: [0, 2000, 5000, 10000, 30000, 60000])
        .build();

    hub.on('ReceiveNotification', (_) => _controller.add(null));
    hub.on('ReceiveAlert', (_) => _controller.add(null));

    return hub;
  }

  void dispose() {
    _controller.close();
    disconnect();
  }
}

final notificationRealtimeServiceProvider =
    Provider<NotificationRealtimeService>((ref) {
  final service = NotificationRealtimeService(ref.watch(tokenStoreProvider));
  ref.onDispose(service.dispose);
  return service;
});

/// Keeps the notification hub connected while authenticated and refreshes the
/// unread-count + inbox providers on each inbound event. Kept alive by watching
/// it from [HomeShell] (same pattern as `realtimeWatcherProvider`).
final notificationRealtimeWatcherProvider = Provider<void>((ref) {
  final authState = ref.watch(authControllerProvider);
  final service = ref.watch(notificationRealtimeServiceProvider);

  if (authState is AuthStateAuthenticated) {
    service.connect();
    final sub = service.events.listen((_) {
      ref.read(unreadCountProvider.notifier).refresh();
      if (ref.exists(inboxProvider)) {
        ref.read(inboxProvider.notifier).refresh();
      }
    });
    ref.onDispose(sub.cancel);
  } else if (authState is AuthStateUnauthenticated) {
    service.disconnect();
  }
});
