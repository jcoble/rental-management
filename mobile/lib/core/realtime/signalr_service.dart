import 'dart:async';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:signalr_netcore/signalr_client.dart';

import '../auth/token_store.dart';
import '../config/app_config.dart';

// ---------------------------------------------------------------------------
// RealtimeEvent
// ---------------------------------------------------------------------------

/// The two server-side hub event names.
enum RealtimeEventType { entityUpdated, entityDeleted }

/// Parsed payload from an `EntityUpdated` or `EntityDeleted` hub message.
class RealtimeEvent {
  const RealtimeEvent({
    required this.type,
    required this.entityType,
    required this.entityId,
  });

  final RealtimeEventType type;

  /// e.g. "Payment", "WorkOrder", "LeaseManagement", "Property", "Tenant",
  ///      "Appointment", "ScanDraft"
  final String entityType;

  /// Integer primary key of the affected entity.
  final int entityId;

  @override
  String toString() =>
      'RealtimeEvent(type: $type, entityType: $entityType, entityId: $entityId)';
}

// ---------------------------------------------------------------------------
// SignalrService
// ---------------------------------------------------------------------------

/// Manages a single SignalR HubConnection to `/api/v1/hubs/updates`.
///
/// Call [connect] once the user is authenticated; call [disconnect] on logout.
/// Subscribe to [events] to react to server-side data changes.
///
/// Self-signed cert (mkcert, local dev): the HTTP negotiate request and the
/// WebSocket upgrade both go through [dart:io]'s [HttpClient].  Setting
/// [HttpOverrides.global] via [installDebugCertBypass] in [main] applies to
/// both transports, mirroring how [DioClient] does it for REST calls.
class SignalrService {
  SignalrService(this._tokenStore);

  final TokenStore _tokenStore;

  HubConnection? _hub;

  final _controller = StreamController<RealtimeEvent>.broadcast();

  /// Broadcast stream of realtime events from the updates hub.
  Stream<RealtimeEvent> get events => _controller.stream;

  bool get isConnected => _hub?.state == HubConnectionState.Connected;

  // ── Public API ─────────────────────────────────────────────────────────────

  /// Opens the connection to the hub.  No-ops if already connecting/connected.
  Future<void> connect() async {
    if (_hub != null) {
      final s = _hub!.state;
      if (s == HubConnectionState.Connected ||
          s == HubConnectionState.Connecting ||
          s == HubConnectionState.Reconnecting) {
        return;
      }
    }

    final hubUrl = _resolveHubUrl();
    debugPrint('[SignalR] connecting to $hubUrl');
    _hub = _buildConnection(hubUrl);
    try {
      await _hub!.start();
      debugPrint('[SignalR] CONNECTED state=${_hub!.state}');
    } catch (e, st) {
      debugPrint('[SignalR] connect() failed: $e\n$st');
      // withAutomaticReconnect() will keep retrying after the initial failure
      // only if start() succeeds first.  Log and let the caller retry by
      // calling connect() again (the realtimeWatcher does this on re-auth).
    }
  }

  /// Closes the connection.  Called on logout.
  Future<void> disconnect() async {
    final hub = _hub;
    _hub = null; // prevent callbacks from re-wiring
    if (hub != null) {
      try {
        await hub.stop();
      } catch (_) {
        // best effort
      }
    }
  }

  // ── Connection building ────────────────────────────────────────────────────

  String _resolveHubUrl() {
    // kApiBaseUrl ends with /api/v1 (e.g. https://10.0.2.2:5666/api/v1).
    // The hub is at <origin>/api/v1/hubs/updates.
    return '$kApiBaseUrl/hubs/updates';
  }

  HubConnection _buildConnection(String hubUrl) {
    final options = HttpConnectionOptions(
      accessTokenFactory: () async {
        return await _tokenStore.getAccessToken() ?? '';
      },
    );

    final hub = HubConnectionBuilder()
        .withUrl(hubUrl, options: options)
        .withAutomaticReconnect(
          retryDelays: [0, 2000, 5000, 10000, 30000, 60000],
        )
        .build();

    hub.on(
      'EntityUpdated',
      (args) => _handleEvent(RealtimeEventType.entityUpdated, args),
    );
    hub.on(
      'EntityDeleted',
      (args) => _handleEvent(RealtimeEventType.entityDeleted, args),
    );

    hub.onreconnecting(({error}) {
      debugPrint('[SignalR] reconnecting… error: $error');
    });

    hub.onreconnected(({connectionId}) {
      debugPrint('[SignalR] reconnected. connectionId: $connectionId');
    });

    hub.onclose(({error}) {
      debugPrint('[SignalR] connection closed. error: $error');
    });

    return hub;
  }

  void _handleEvent(RealtimeEventType type, List<Object?>? args) {
    if (args == null || args.isEmpty) return;

    final raw = args[0];
    if (raw is! Map) return;

    final entityType = raw['entityType'] as String? ?? '';
    final entityId = (raw['entityId'] as num?)?.toInt() ?? 0;
    if (entityType.isEmpty) return;

    _controller.add(
      RealtimeEvent(type: type, entityType: entityType, entityId: entityId),
    );
  }

  void dispose() {
    _controller.close();
    disconnect();
  }
}

// ---------------------------------------------------------------------------
// Debug self-signed cert bypass
// ---------------------------------------------------------------------------

/// Installs a global [HttpOverrides] that accepts the mkcert self-signed
/// certificate used in local development, so the SignalR negotiate + WebSocket
/// upgrade can connect to the dev API over the LAN.
///
/// Must be called before [runApp] in [main]. Gated on `!kReleaseMode` (NOT an
/// `assert`): asserts are stripped in **profile** builds, which would leave
/// HttpOverrides unset and SignalR unable to accept the dev cert — exactly how
/// `DioClient` guards its own bypass. Disabled in release so production keeps
/// full certificate validation.
void installDebugCertBypass() {
  if (!kReleaseMode && kAllowSelfSignedCertInDebug) {
    HttpOverrides.global = _SelfSignedCertHttpOverrides();
  }
}

class _SelfSignedCertHttpOverrides extends HttpOverrides {
  @override
  HttpClient createHttpClient(SecurityContext? context) {
    final client = super.createHttpClient(context);
    // Accept any certificate — local dev only; scoped to debug builds.
    client.badCertificateCallback =
        (X509Certificate cert, String host, int port) => true;
    return client;
  }
}
