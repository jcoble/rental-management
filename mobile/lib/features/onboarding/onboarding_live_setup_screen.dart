import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/models/models.dart';
import '../home/mobile_quick_action_helpers.dart';
import '../properties/properties_list_screen.dart';
import '../properties/properties_repository.dart';
import '../units/unit_command_center_screen.dart';

enum _LiveSetupNextStep { scanLease, continueManually, finish }

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
    final setup = await showAddPropertySheet(context);
    if (setup == null || !context.mounted) return;

    final nextStep = await _showNextStep(context, setup);
    if (nextStep == null || !context.mounted) return;

    switch (nextStep) {
      case _LiveSetupNextStep.scanLease:
        await openMobileScan(
          context,
          initialTargetEntityType: 'LeaseAgreement',
          lockTargetEntityType: true,
          propertyId: setup.property.id,
          unitId: setup.units.length == 1 ? setup.units.single.id : null,
          sourceLabel: setup.property.name,
        );
        break;
      case _LiveSetupNextStep.continueManually:
        final unit = await _chooseUnit(context, setup.units);
        if (unit == null || !context.mounted) return;
        await Navigator.of(context).push<void>(
          MaterialPageRoute<void>(
            builder: (_) => UnitCommandCenterLoaderScreen(
              unitId: unit.id,
              initialTab: UnitCommandCenterTab.tenantLease,
            ),
          ),
        );
        break;
      case _LiveSetupNextStep.finish:
        break;
    }

    if (context.mounted) context.go('/');
  }

  Future<_LiveSetupNextStep?> _showNextStep(
    BuildContext context,
    PropertySetupResult setup,
  ) {
    final scheme = Theme.of(context).colorScheme;
    return showModalBottomSheet<_LiveSetupNextStep>(
      context: context,
      isScrollControlled: true,
      isDismissible: false,
      enableDrag: false,
      useSafeArea: true,
      builder: (sheetContext) => Padding(
        padding: const EdgeInsets.fromLTRB(20, 12, 20, 24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Center(
              child: Container(
                width: 36,
                height: 4,
                decoration: BoxDecoration(
                  color: scheme.outlineVariant,
                  borderRadius: BorderRadius.circular(2),
                ),
              ),
            ),
            const SizedBox(height: 20),
            Text(
              '${setup.property.name} is ready',
              style: Theme.of(sheetContext).textTheme.headlineSmall,
            ),
            const SizedBox(height: 6),
            Text(
              setup.units.length == 1
                  ? 'The property and its rental were saved together. What would you like to do next?'
                  : 'The property and ${setup.units.length} units were saved together. What would you like to do next?',
              style: Theme.of(
                sheetContext,
              ).textTheme.bodyMedium?.copyWith(color: scheme.onSurfaceVariant),
            ),
            const SizedBox(height: 20),
            _NextStepCard(
              key: const Key('live-setup-scan-lease'),
              icon: Icons.document_scanner_outlined,
              title: 'Scan an existing lease',
              subtitle:
                  'Rental Command fills in the tenant and agreement details for you to review.',
              primary: true,
              onTap: () =>
                  Navigator.of(sheetContext).pop(_LiveSetupNextStep.scanLease),
            ),
            const SizedBox(height: 10),
            _NextStepCard(
              key: const Key('live-setup-manual-lease'),
              icon: Icons.people_alt_outlined,
              title: 'Set up tenants and lease manually',
              subtitle:
                  'Open the rental workspace and use its tenant and lease tools.',
              onTap: () => Navigator.of(
                sheetContext,
              ).pop(_LiveSetupNextStep.continueManually),
            ),
            const SizedBox(height: 10),
            TextButton(
              key: const Key('live-setup-finish'),
              onPressed: () =>
                  Navigator.of(sheetContext).pop(_LiveSetupNextStep.finish),
              child: const Text('Finish for now'),
            ),
          ],
        ),
      ),
    );
  }

  Future<Unit?> _chooseUnit(BuildContext context, List<Unit> units) async {
    if (units.length == 1) return units.single;
    if (units.isEmpty) return null;

    return showModalBottomSheet<Unit>(
      context: context,
      useSafeArea: true,
      builder: (sheetContext) => ListView(
        shrinkWrap: true,
        padding: const EdgeInsets.fromLTRB(16, 12, 16, 24),
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(8, 8, 8, 12),
            child: Text(
              'Choose a unit to continue',
              style: Theme.of(sheetContext).textTheme.titleLarge,
            ),
          ),
          for (final unit in units)
            ListTile(
              leading: const Icon(Icons.door_front_door_outlined),
              title: Text(
                unit.unitNumber.trim().isEmpty
                    ? 'Rental'
                    : 'Unit ${unit.unitNumber}',
              ),
              trailing: const Icon(Icons.chevron_right),
              onTap: () => Navigator.of(sheetContext).pop(unit),
            ),
        ],
      ),
    );
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
                    // A1: don't claim "all set up" on a deliberately-empty
                    // account — invite the first real step instead.
                    firstName.isEmpty
                        ? "You're in — let's add your first property"
                        : "You're in — let's add your first property, $firstName",
                    style: theme.textTheme.headlineSmall?.copyWith(
                      fontWeight: FontWeight.w700,
                      color: scheme.onSurface,
                    ),
                    textAlign: TextAlign.center,
                  ),
                  const SizedBox(height: 8),
                  Text(
                    "Your account is empty and ready for your real portfolio. "
                    "Start by choosing whether the address is one rental or a "
                    "building with units. Rental Command saves the property and "
                    "its rentals together.",
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
                      child: Text('Add your first rental'),
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

class _NextStepCard extends StatelessWidget {
  const _NextStepCard({
    super.key,
    required this.icon,
    required this.title,
    required this.subtitle,
    required this.onTap,
    this.primary = false,
  });

  final IconData icon;
  final String title;
  final String subtitle;
  final VoidCallback onTap;
  final bool primary;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return Card(
      color: primary ? scheme.primaryContainer : null,
      margin: EdgeInsets.zero,
      child: ListTile(
        contentPadding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
        leading: Icon(
          icon,
          color: primary ? scheme.onPrimaryContainer : scheme.primary,
        ),
        title: Text(title),
        subtitle: Text(subtitle),
        trailing: const Icon(Icons.chevron_right),
        onTap: onTap,
      ),
    );
  }
}
