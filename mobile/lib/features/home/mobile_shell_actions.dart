import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:material_symbols_icons/symbols.dart';

import '../../core/api/api_exception.dart';
import '../../core/auth/auth_controller.dart';
import '../../core/auth/mobile_access_policy.dart';
import '../../core/auth/auth_models.dart';
import '../../core/auth/auth_repository.dart';
import '../notifications/notifications_repository.dart';
import 'mobile_destination.dart';
import 'mobile_domain_navigation.dart';

/// AppBar notification bell with an unread-count badge. Tapping opens the
/// addressable notification inbox so notification deep-link behavior remains
/// unchanged while Inbox becomes a first-class bottom-nav area.
class MobileNotificationBell extends ConsumerWidget {
  const MobileNotificationBell({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final auth = ref.watch(authControllerProvider);
    if (auth is! AuthStateAuthenticated ||
        (!auth.isTenantExperience && !canOpenInboxHub(auth.capabilities))) {
      return const SizedBox.shrink();
    }
    final count = ref
        .watch(unreadCountProvider)
        .maybeWhen(data: (c) => c, orElse: () => 0);
    return IconButton(
      tooltip: 'Notifications',
      onPressed: () {
        final shellNavigator = mobileShellNavigatorOf(context);
        if (shellNavigator?.openRoute('/notifications') == true) {
          revealMobileShellIfDetached(context);
          return;
        }

        context.push('/notifications');
      },
      icon: Badge(
        isLabelVisible: count > 0,
        label: Text(count > 99 ? '99+' : '$count'),
        child: const Icon(Icons.notifications_none),
      ),
    );
  }
}

/// Account flyout for low-frequency navigation and session actions. This
/// replaces the old top-right Browse grid plus exposed sign-out button.
class MobileAccountMenu extends ConsumerWidget {
  const MobileAccountMenu({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final auth = ref.watch(authControllerProvider);
    if (auth is! AuthStateAuthenticated) return const SizedBox.shrink();
    final capabilities = auth.capabilities;
    final managementMode =
        auth.activeExperience == WorkspaceExperience.management;
    final canOpenGettingStarted =
        managementMode && capabilities.contains('rentals.manage');
    final canOpenTeam =
        managementMode &&
        (capabilities.contains('team.read') ||
            capabilities.contains('team.manage'));
    final canOpenSettings = canManageOwnMobileAlerts(auth.activeExperience);

    return IconButton(
      tooltip: 'Account',
      icon: const Icon(Symbols.account_circle_rounded),
      onPressed: () {
        showModalBottomSheet<void>(
          context: context,
          showDragHandle: true,
          builder: (sheetContext) {
            void closeAndOpen(MobileDestination destination) {
              Navigator.of(sheetContext).pop();
              destination.open(context);
            }

            return SafeArea(
              child: ListView(
                shrinkWrap: true,
                padding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
                children: [
                  const _AccessSelectors(),
                  if (canOpenGettingStarted) ...[
                    _AccountMenuRow(
                      icon: Symbols.rocket_launch_rounded,
                      label: 'Getting started',
                      subtitle: 'Set-up checklist',
                      onTap: () => closeAndOpen(gettingStartedDestination),
                    ),
                  ],
                  if (canOpenTeam || canOpenSettings) const Divider(height: 20),
                  if (canOpenTeam)
                    _AccountMenuRow(
                      icon: Symbols.groups_rounded,
                      label: 'Team',
                      subtitle: 'Members and access',
                      onTap: () => closeAndOpen(teamDestination),
                    ),
                  if (canOpenSettings)
                    _AccountMenuRow(
                      icon: Symbols.settings_rounded,
                      label: 'Settings',
                      subtitle: 'My alerts and account',
                      onTap: () => closeAndOpen(settingsDestination),
                    ),
                  const Divider(height: 20),
                  _AccountMenuRow(
                    icon: Symbols.logout_rounded,
                    label: 'Sign out',
                    subtitle: 'Leave this device',
                    onTap: () async {
                      Navigator.of(sheetContext).pop();
                      await ref.read(authControllerProvider.notifier).logout();
                    },
                  ),
                ],
              ),
            );
          },
        );
      },
    );
  }
}

class _AccessSelectors extends ConsumerStatefulWidget {
  const _AccessSelectors();

  @override
  ConsumerState<_AccessSelectors> createState() => _AccessSelectorsState();
}

class _AccessSelectorsState extends ConsumerState<_AccessSelectors> {
  bool _selectionInFlight = false;

  Future<void> _runSelection(Future<void> Function() selection) async {
    if (_selectionInFlight) return;
    setState(() => _selectionInFlight = true);
    try {
      await selection();
    } on ApiException catch (error) {
      if (mounted) {
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(SnackBar(content: Text(error.message)));
      }
    } finally {
      if (mounted) setState(() => _selectionInFlight = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final auth = ref.watch(authControllerProvider);
    if (auth is! AuthStateAuthenticated) return const SizedBox.shrink();
    final contexts =
        ref.watch(accessContextsProvider).asData?.value ??
        const <EffectiveAccessContextOption>[];
    final showContext = contexts.length > 1;
    final showExperience = auth.access.availableExperiences.length > 1;
    if (!showContext && !showExperience) return const SizedBox.shrink();

    return Column(
      children: [
        if (showContext)
          DropdownButtonFormField<int>(
            initialValue: auth.access.selectedContext.accessContextId,
            decoration: const InputDecoration(labelText: 'Workspace'),
            items: contexts
                .map(
                  (item) => DropdownMenuItem<int>(
                    value: item.accessContextId,
                    child: Text(item.workspaceName),
                  ),
                )
                .toList(growable: false),
            onChanged: _selectionInFlight
                ? null
                : (value) async {
                    if (value != null &&
                        value != auth.access.selectedContext.accessContextId) {
                      await _runSelection(
                        () => ref
                            .read(authControllerProvider.notifier)
                            .selectContext(value),
                      );
                    }
                  },
          ),
        if (showContext && showExperience) const SizedBox(height: 12),
        if (showExperience)
          DropdownButtonFormField<WorkspaceExperience>(
            initialValue: auth.activeExperience,
            decoration: const InputDecoration(labelText: 'Work area'),
            items: auth.access.availableExperiences
                .map(
                  (experience) => DropdownMenuItem<WorkspaceExperience>(
                    value: experience,
                    child: Text(_experienceLabel(experience)),
                  ),
                )
                .toList(growable: false),
            onChanged: _selectionInFlight
                ? null
                : (value) async {
                    if (value != null && value != auth.activeExperience) {
                      await _runSelection(
                        () => ref
                            .read(authControllerProvider.notifier)
                            .selectExperience(value),
                      );
                    }
                  },
          ),
        const Divider(height: 20),
      ],
    );
  }

  String _experienceLabel(WorkspaceExperience experience) =>
      switch (experience) {
        WorkspaceExperience.management => 'Management',
        WorkspaceExperience.leasing => 'Leasing',
        WorkspaceExperience.maintenance => 'Maintenance',
        WorkspaceExperience.owner => 'Owner',
        WorkspaceExperience.tenant => 'Tenant',
      };
}

class _AccountMenuRow extends StatelessWidget {
  const _AccountMenuRow({
    required this.icon,
    required this.label,
    required this.subtitle,
    required this.onTap,
  });

  final IconData icon;
  final String label;
  final String subtitle;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;

    return ListTile(
      titleAlignment: ListTileTitleAlignment.center,
      leading: Icon(icon, color: scheme.primary, fill: 1),
      title: Text(label, style: theme.textTheme.titleSmall),
      subtitle: Text(
        subtitle,
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
        style: theme.textTheme.bodySmall?.copyWith(
          color: scheme.onSurfaceVariant,
        ),
      ),
      trailing: Icon(
        Symbols.chevron_right_rounded,
        color: scheme.onSurfaceVariant,
      ),
      onTap: onTap,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.all(Radius.circular(14)),
      ),
    );
  }
}
