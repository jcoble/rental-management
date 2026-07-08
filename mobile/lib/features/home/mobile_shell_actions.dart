import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:material_symbols_icons/symbols.dart';

import '../../core/auth/auth_controller.dart';
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
                  _AccountMenuRow(
                    icon: Symbols.rocket_launch_rounded,
                    label: 'Getting started',
                    subtitle: 'Set-up checklist',
                    onTap: () => closeAndOpen(gettingStartedDestination),
                  ),
                  const Divider(height: 20),
                  _AccountMenuRow(
                    icon: Symbols.groups_rounded,
                    label: 'Team',
                    subtitle: 'Members and access',
                    onTap: () => closeAndOpen(teamDestination),
                  ),
                  _AccountMenuRow(
                    icon: Symbols.settings_rounded,
                    label: 'Settings',
                    subtitle: 'Notifications and security',
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
