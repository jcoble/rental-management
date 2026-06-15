import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/auth/auth_controller.dart';
import '../properties/properties_list_screen.dart';

/// Guided first step after a brand-new landlord chooses "Live" (a clean, empty
/// real portfolio).
///
/// Web routes Live users into a multi-step setup wizard; mobile previously
/// dropped them on a bare dashboard with no next step (I8). This screen restores
/// parity at the most important step: a prominent "Add your first property"
/// entry that reuses the existing add-property flow, plus an escape hatch to the
/// dashboard. It is shown once, immediately after the Live choice; the dashboard
/// is always reachable afterwards.
class OnboardingLiveSetupScreen extends ConsumerWidget {
  const OnboardingLiveSetupScreen({super.key});

  Future<void> _addFirstProperty(BuildContext context) async {
    final saved = await showAddPropertySheet(context);
    if (saved == true && context.mounted) {
      // First property created — head into the app; they can keep adding from
      // the Properties screen or via Capture.
      context.go('/');
    }
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;

    final authState = ref.watch(authControllerProvider);
    final displayName = authState is AuthStateAuthenticated
        ? authState.user.displayName
        : '';
    final firstName = displayName.trim().isEmpty
        ? ''
        : displayName.trim().split(RegExp(r'\s+')).first;

    return Scaffold(
      body: SafeArea(
        child: Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 32),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 480),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Center(
                    child: Container(
                      width: 60,
                      height: 60,
                      decoration: BoxDecoration(
                        color: scheme.primaryContainer,
                        borderRadius: BorderRadius.circular(18),
                      ),
                      child: Icon(
                        Icons.rocket_launch_rounded,
                        size: 30,
                        color: scheme.onPrimaryContainer,
                      ),
                    ),
                  ),
                  const SizedBox(height: 20),
                  Text(
                    firstName.isEmpty
                        ? "You're all set up"
                        : "You're all set up, $firstName",
                    style: theme.textTheme.headlineSmall?.copyWith(
                      fontWeight: FontWeight.w700,
                      color: scheme.onSurface,
                    ),
                    textAlign: TextAlign.center,
                  ),
                  const SizedBox(height: 8),
                  Text(
                    "Your account is empty and ready for your real portfolio. "
                    "Start by adding your first property — you can add units, "
                    "tenants and leases to it next.",
                    style: theme.textTheme.bodyMedium?.copyWith(
                      color: scheme.onSurfaceVariant,
                    ),
                    textAlign: TextAlign.center,
                  ),
                  const SizedBox(height: 28),

                  // Primary guided step.
                  FilledButton.icon(
                    key: const Key('live-add-first-property'),
                    onPressed: () => _addFirstProperty(context),
                    icon: const Icon(Icons.add_home_work_rounded),
                    label: const Padding(
                      padding: EdgeInsets.symmetric(vertical: 6),
                      child: Text('Add your first property'),
                    ),
                  ),
                  const SizedBox(height: 12),

                  // Escape hatch — the dashboard is always available.
                  TextButton(
                    onPressed: () => context.go('/'),
                    child: const Text("I'll do this later"),
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
