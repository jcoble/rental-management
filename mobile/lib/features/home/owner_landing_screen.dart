import 'package:flutter/material.dart';
import 'package:material_symbols_icons/symbols.dart';

import 'mobile_shell_actions.dart';

/// Safe, data-free shell for the Owner work area.
///
/// Owner-facing projections are not available on mobile yet. Keeping this
/// landing separate from the management shell prevents an Owner access
/// envelope from inheriting management's Today dashboard or repositories.
class OwnerLandingScreen extends StatelessWidget {
  const OwnerLandingScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Scaffold(
      appBar: AppBar(
        title: const Text('Owner'),
        actions: const [MobileAccountMenu()],
      ),
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
                    Symbols.account_balance_rounded,
                    size: 48,
                    color: theme.colorScheme.primary,
                  ),
                  const SizedBox(height: 16),
                  Text(
                    'Your owner workspace',
                    textAlign: TextAlign.center,
                    style: theme.textTheme.headlineSmall,
                  ),
                  const SizedBox(height: 8),
                  Text(
                    'Owner statements and property performance will appear '
                    'here when the mobile owner experience is available.',
                    textAlign: TextAlign.center,
                    style: theme.textTheme.bodyLarge?.copyWith(
                      color: theme.colorScheme.onSurfaceVariant,
                    ),
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
