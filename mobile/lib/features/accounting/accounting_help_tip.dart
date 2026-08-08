import 'package:flutter/material.dart';
import 'package:url_launcher/url_launcher.dart';

import 'accounting_help.dart';

typedef AccountingHelpLinkOpener = Future<bool> Function(Uri uri);

class AccountingHelpTipButton extends StatelessWidget {
  const AccountingHelpTipButton({
    super.key,
    required this.topic,
    this.openLink,
  });

  final AccountingHelpTopic topic;
  final AccountingHelpLinkOpener? openLink;

  @override
  Widget build(BuildContext context) {
    final help = accountingHelpFor(topic);
    return IconButton(
      key: ValueKey('accounting-help-${topic.name}'),
      tooltip: 'Help: ${help.title}',
      visualDensity: VisualDensity.compact,
      icon: const Icon(Icons.help_outline, size: 20),
      onPressed: () => _showTip(context, help),
    );
  }

  Future<void> _showTip(BuildContext context, AccountingHelpEntry help) async {
    await showModalBottomSheet<void>(
      context: context,
      showDragHandle: true,
      useSafeArea: true,
      builder: (sheetContext) => Padding(
        padding: const EdgeInsets.fromLTRB(20, 4, 20, 24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              help.title,
              style: Theme.of(
                sheetContext,
              ).textTheme.titleLarge?.copyWith(fontWeight: FontWeight.w700),
            ),
            const SizedBox(height: 10),
            Text(help.tip),
            const SizedBox(height: 16),
            Align(
              alignment: Alignment.centerLeft,
              child: FilledButton.tonalIcon(
                key: const Key('accounting-help-learn-more'),
                icon: const Icon(Icons.open_in_new, size: 18),
                label: const Text('Learn more'),
                onPressed: () => _openGuide(sheetContext, help),
              ),
            ),
          ],
        ),
      ),
    );
  }

  Future<void> _openGuide(
    BuildContext context,
    AccountingHelpEntry help,
  ) async {
    var opened = false;
    try {
      opened = await (openLink ?? _launchExternally)(help.uri);
    } catch (_) {
      opened = false;
    }
    if (opened || !context.mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(
      const SnackBar(content: Text('Could not open the accounting guide.')),
    );
  }
}

Future<bool> _launchExternally(Uri uri) =>
    launchUrl(uri, mode: LaunchMode.externalApplication);
