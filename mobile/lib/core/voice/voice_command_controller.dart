import 'dart:async';

import 'package:app_links/app_links.dart';
import 'package:flutter/foundation.dart'
    show debugPrint, kDebugMode, visibleForTesting;
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../router/app_router.dart';
import 'voice_command.dart';

/// Holds the most recent voice command that the UI has not yet acted on.
///
/// The flow is a small one-slot bus:
///
///   [VoiceLinkService]  --set(cmd)-->  [pendingVoiceCommandProvider]
///                                              |
///                                       HomeShell reads/listens, acts,
///                                       then calls consume()
///
/// A one-slot bus (rather than a raw stream) handles the cold-start case for
/// free: when Assistant launches the app, the deep link is parsed *before*
/// `HomeShell` mounts, so the command simply waits in this slot until the shell
/// reads it on first build.
class PendingVoiceCommand extends Notifier<VoiceCommand?> {
  @override
  VoiceCommand? build() => null;

  /// Records a freshly-received command for the UI to pick up.
  void set(VoiceCommand command) => state = command;

  /// Clears the slot once the UI has handled the command.
  void consume() => state = null;
}

final pendingVoiceCommandProvider =
    NotifierProvider<PendingVoiceCommand, VoiceCommand?>(
      PendingVoiceCommand.new,
    );

/// Listens for incoming voice/App-Actions deep links and feeds parsed commands
/// into [pendingVoiceCommandProvider].
///
/// Uses the `app_links` plugin, which surfaces both:
///   * the **cold-start** link (the app was launched by the command), and
///   * **warm** links delivered to a running app (via `onNewIntent`, which
///     fires because `MainActivity` is `launchMode="singleTop"`).
///
/// Deliberately independent of Flutter's engine-level deep linking (which we
/// leave disabled) so go_router's auth redirect never sees these URIs.
class VoiceLinkService {
  VoiceLinkService(this._ref);

  final Ref _ref;
  final AppLinks _appLinks = AppLinks();
  StreamSubscription<Uri>? _subscription;
  bool _started = false;

  /// Begins listening. Safe to call more than once; subsequent calls are
  /// no-ops. Should be invoked once at app startup.
  Future<void> start() async {
    if (_started) return;
    _started = true;

    // Warm links for the lifetime of the app.
    _subscription = _appLinks.uriLinkStream.listen(
      _handle,
      onError: (Object error) {
        if (kDebugMode) debugPrint('[voice] link stream error: $error');
      },
    );

    // The link that cold-started the app, if any.
    try {
      final initial = await _appLinks.getInitialLink();
      if (initial != null) _handle(initial);
    } catch (error) {
      if (kDebugMode) debugPrint('[voice] getInitialLink failed: $error');
    }
  }

  void _handle(Uri uri) {
    // Auth-email links should complete in-app when the user has the app
    // installed; otherwise the same https URL still falls back to the web page.
    final authPath = authEmailLinkPath(uri);
    if (authPath != null) {
      final query = Uri(
        queryParameters: {
          'userId': uri.queryParameters['userId'] ?? '',
          'token': uri.queryParameters['token'] ?? '',
        },
      ).query;
      if (kDebugMode) debugPrint('[deeplink] $authPath received');
      _ref.read(appRouterProvider).go('/$authPath?$query');
      return;
    }

    final command = parseVoiceCommand(uri);
    if (command == null) {
      if (kDebugMode) debugPrint('[voice] ignored non-command link: $uri');
      return;
    }
    if (kDebugMode) debugPrint('[voice] received $command');
    _ref.read(pendingVoiceCommandProvider.notifier).set(command);
  }

  void dispose() => _subscription?.cancel();
}

/// Returns the supported auth route for either a custom-scheme form
/// (`rentalcommand://reset-password?...`) or an https path form
/// (`https://rentalcommand.net/reset-password?...`).
@visibleForTesting
String? authEmailLinkPath(Uri uri) {
  const supported = {'reset-password', 'verify-email'};
  if (supported.contains(uri.host)) return uri.host;
  for (final segment in uri.pathSegments) {
    if (supported.contains(segment)) return segment;
  }
  return null;
}

final voiceLinkServiceProvider = Provider<VoiceLinkService>((ref) {
  final service = VoiceLinkService(ref);
  ref.onDispose(service.dispose);
  return service;
});
