import 'package:flutter/material.dart';
import 'package:url_launcher/url_launcher.dart';

const _notificationHelpUrl =
    'https://rentalcommand.net/docs/settings-and-notifications';

/// A keyboard, screen-reader, and touch-accessible route to the shared
/// notification guide. Field-level explanations stay beside the fields that
/// need them; this action is the single section-level help entry point.
class NotificationHelpAction extends StatelessWidget {
  const NotificationHelpAction({super.key});

  @override
  Widget build(BuildContext context) => IconButton(
    tooltip: 'How notification settings work',
    icon: const Icon(Icons.help_outline),
    onPressed: () => _open(context),
  );

  Future<void> _open(BuildContext context) async {
    try {
      final opened = await launchUrl(
        Uri.parse(_notificationHelpUrl),
        mode: LaunchMode.externalApplication,
      );
      if (opened || !context.mounted) return;
    } catch (_) {
      if (!context.mounted) return;
    }
    if (context.mounted) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text('Could not open the notification guide.')),
      );
    }
  }
}
