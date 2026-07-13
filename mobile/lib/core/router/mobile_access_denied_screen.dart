import 'package:flutter/material.dart';
import 'package:material_symbols_icons/symbols.dart';

class MobileAccessDeniedScreen extends StatelessWidget {
  const MobileAccessDeniedScreen({
    super.key,
    required this.onReturn,
    this.returnLabel = 'Return to home',
  });

  final VoidCallback onReturn;
  final String returnLabel;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Scaffold(
      appBar: AppBar(title: const Text('Access changed')),
      body: SafeArea(
        child: Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(24),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 440),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Icon(
                    Symbols.lock_rounded,
                    size: 48,
                    color: theme.colorScheme.primary,
                  ),
                  const SizedBox(height: 16),
                  Text(
                    "This destination isn't available in your current work area.",
                    textAlign: TextAlign.center,
                    style: theme.textTheme.headlineSmall,
                  ),
                  const SizedBox(height: 8),
                  Text(
                    'Your role, property scope, or assignment may have changed. '
                    'Return to the app to continue with the destinations you can use.',
                    textAlign: TextAlign.center,
                    style: theme.textTheme.bodyLarge?.copyWith(
                      color: theme.colorScheme.onSurfaceVariant,
                    ),
                  ),
                  const SizedBox(height: 24),
                  FilledButton.icon(
                    onPressed: onReturn,
                    icon: const Icon(Symbols.home_rounded),
                    label: Text(returnLabel),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}
