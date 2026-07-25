import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_controller.dart';
import '../../core/auth/mobile_access_policy.dart';
import 'ai_provider_settings_screen.dart';
import 'change_password_screen.dart';
import 'my_alerts_screen.dart';
import 'team_routing_screen.dart';
import 'tenant_notices_screen.dart';

/// A simple settings index. Personal alerts are intentionally separate from
/// team responsibility routing and tenant-facing automation.
class SettingsScreen extends ConsumerWidget {
  const SettingsScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final auth = ref.watch(authControllerProvider);
    final capabilities = auth is AuthStateAuthenticated
        ? auth.capabilities
        : const <String>{};
    final canManageNotifications =
        auth is AuthStateAuthenticated &&
        canManageMobileNotificationFoundation(
          experience: auth.activeExperience,
          capabilities: capabilities,
        );
    final canManageMessagingProvider = capabilities.contains(
      'integrations.manage',
    );
    final canManageAiProvider = capabilities.contains('integrations.manage');

    return Scaffold(
      appBar: AppBar(title: const Text('Settings')),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
        children: [
          const _SectionHeader(
            title: 'Notifications',
            subtitle:
                'Your alerts, team responsibilities, and tenant delivery are '
                'configured separately.',
          ),
          const SizedBox(height: 12),
          _SettingsCard(
            icon: Icons.notifications_outlined,
            title: 'My alerts',
            subtitle:
                'Choose where alerts for your signed-in account are delivered.',
            onTap: () => _open(context, const MyAlertsScreen()),
          ),
          if (canManageNotifications) ...[
            const SizedBox(height: 10),
            _SettingsCard(
              icon: Icons.alt_route_outlined,
              title: 'Team routing',
              subtitle:
                  'Name who is responsible for money, leasing, work, owners, '
                  'and account security.',
              onTap: () => _open(context, const TeamRoutingScreen()),
            ),
            const SizedBox(height: 10),
            _SettingsCard(
              icon: Icons.campaign_outlined,
              title: 'Tenant notices',
              subtitle:
                  'Set each notice to Off, Draft for review, or automatic '
                  'delivery and edit its supplied template.',
              onTap: () => _open(context, const TenantNoticesScreen()),
            ),
          ],
          if (canManageMessagingProvider) ...[
            const SizedBox(height: 24),
            const _SectionHeader(
              title: 'Messaging provider',
              subtitle:
                  'Workspace delivery infrastructure is separate from alert '
                  'and notice choices.',
            ),
            const SizedBox(height: 12),
            const _ProviderBoundaryCard(),
          ],
          if (canManageAiProvider) ...[
            const SizedBox(height: 10),
            _SettingsCard(
              icon: Icons.auto_awesome_outlined,
              title: 'AI provider',
              subtitle:
                  'Connect and manage the workspace AI provider used by '
                  'Scan / Add.',
              onTap: () => _open(context, const AiProviderSettingsScreen()),
            ),
          ],
          const SizedBox(height: 24),
          const _SectionHeader(
            title: 'Account',
            subtitle: 'Manage your sign-in and security.',
          ),
          const SizedBox(height: 12),
          _SettingsCard(
            icon: Icons.lock_outline,
            title: 'Change password',
            subtitle: 'Update the password you use to sign in.',
            onTap: () => _open(context, const ChangePasswordScreen()),
          ),
        ],
      ),
    );
  }

  void _open(BuildContext context, Widget screen) {
    Navigator.of(
      context,
    ).push<void>(MaterialPageRoute<void>(builder: (_) => screen));
  }
}

class _ProviderBoundaryCard extends StatelessWidget {
  const _ProviderBoundaryCard();

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Icon(Icons.sms_outlined, color: theme.colorScheme.primary),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text('SMS provider setup', style: theme.textTheme.titleSmall),
                  const SizedBox(height: 4),
                  Text(
                    'Workspace Administrators configure the SMS provider on '
                    'the web app. Turning on SMS in My alerts or a tenant '
                    'notice does not configure a provider.',
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: theme.colorScheme.onSurfaceVariant,
                    ),
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _SettingsCard extends StatelessWidget {
  const _SettingsCard({
    required this.icon,
    required this.title,
    required this.subtitle,
    required this.onTap,
  });

  final IconData icon;
  final String title;
  final String subtitle;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card(
      child: ListTile(
        minVerticalPadding: 14,
        leading: Icon(icon, color: theme.colorScheme.primary),
        title: Text(title, style: theme.textTheme.titleSmall),
        subtitle: Text(subtitle),
        trailing: const Icon(Icons.chevron_right),
        onTap: onTap,
      ),
    );
  }
}

class _SectionHeader extends StatelessWidget {
  const _SectionHeader({required this.title, required this.subtitle});

  final String title;
  final String subtitle;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(title, style: theme.textTheme.titleMedium),
        const SizedBox(height: 3),
        Text(
          subtitle,
          style: theme.textTheme.bodySmall?.copyWith(
            color: theme.colorScheme.onSurfaceVariant,
          ),
        ),
      ],
    );
  }
}
