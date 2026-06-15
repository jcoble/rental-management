import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import 'change_password_screen.dart';
import 'notification_settings_repository.dart';

/// Notification settings screen.
///
/// Lets the landlord control which notifications fire and on which channels
/// (In-app / Email / SMS), the master automation toggles, and the lead/grace/
/// reminder day windows. Designed for a non-technical phone user: large tap
/// targets, plain-language labels, one Save action.
class SettingsScreen extends ConsumerStatefulWidget {
  const SettingsScreen({super.key});

  @override
  ConsumerState<SettingsScreen> createState() => _SettingsScreenState();
}

class _SettingsScreenState extends ConsumerState<SettingsScreen> {
  bool _saving = false;

  @override
  void initState() {
    super.initState();
    Future.microtask(
      () => ref.read(notificationSettingsProvider.notifier).load(),
    );
  }

  Future<void> _refresh() =>
      ref.read(notificationSettingsProvider.notifier).refresh();

  Future<void> _save() async {
    setState(() => _saving = true);
    try {
      final ok = await ref.read(notificationSettingsProvider.notifier).save();
      if (!mounted) return;
      if (ok) {
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(content: Text('Settings saved.')),
        );
      }
    } on ApiException catch (e) {
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(e.message)),
      );
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final settingsAsync = ref.watch(notificationSettingsProvider);

    return Scaffold(
      appBar: AppBar(title: const Text('Settings')),
      body: settingsAsync.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => _ErrorBody(
          message: e is ApiException ? e.message : e.toString(),
          onRetry: _refresh,
        ),
        data: (settings) => RefreshIndicator(
          onRefresh: _refresh,
          child: ListView(
            physics: const AlwaysScrollableScrollPhysics(),
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 96),
            children: [
              _SectionHeader(
                title: 'Notifications',
                subtitle:
                    'Choose how you get each kind of alert. In-app shows in '
                    'the app, Email and SMS go to your phone, and Push pops up '
                    'on your phone even when the app is closed.',
              ),
              const SizedBox(height: 12),
              _ChannelMatrixCard(settings: settings),
              const SizedBox(height: 24),
              _SectionHeader(
                title: 'Automation',
                subtitle: 'Turn the automatic reminders on or off.',
              ),
              const SizedBox(height: 12),
              _AutomationCard(settings: settings),
              const SizedBox(height: 24),
              _SectionHeader(
                title: 'Timing',
                subtitle: 'How many days before or after to send each reminder.',
              ),
              const SizedBox(height: 12),
              _TimingCard(settings: settings),
              if (settings.signalWireFromNumber != null ||
                  settings.signalWireProjectId != null ||
                  settings.signalWireTokenSet) ...[
                const SizedBox(height: 24),
                _SectionHeader(
                  title: 'Text messaging',
                  subtitle: 'SMS provider status (managed on the web app).',
                ),
                const SizedBox(height: 12),
                _ProviderStatusCard(settings: settings),
              ],
              const SizedBox(height: 24),
              _SectionHeader(
                title: 'Account',
                subtitle: 'Manage your sign-in and security.',
              ),
              const SizedBox(height: 12),
              const _AccountSecurityCard(),
            ],
          ),
        ),
      ),
      bottomNavigationBar: settingsAsync.maybeWhen(
        data: (_) => _SaveBar(saving: _saving, onSave: _save),
        orElse: () => null,
      ),
    );
  }
}

// ── Channel matrix ──────────────────────────────────────────────────────────

class _ChannelMatrixCard extends ConsumerWidget {
  const _ChannelMatrixCard({required this.settings});

  final NotificationSettings settings;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final notifier = ref.read(notificationSettingsProvider.notifier);

    return Card(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(14, 12, 8, 12),
        child: Column(
          children: [
            // Column header row.
            Padding(
              padding: const EdgeInsets.only(bottom: 4),
              child: Row(
                children: [
                  const Expanded(child: SizedBox.shrink()),
                  _HeaderCell(label: 'In-app', icon: Icons.notifications_none),
                  _HeaderCell(label: 'Email', icon: Icons.mail_outline),
                  _HeaderCell(label: 'SMS', icon: Icons.sms_outlined),
                  _HeaderCell(label: 'Push', icon: Icons.phone_iphone),
                ],
              ),
            ),
            for (var i = 0; i < settings.channelPreferences.length; i++) ...[
              if (i > 0)
                Divider(height: 1, color: cs.outlineVariant.withValues(alpha: 0.5)),
              _MatrixRow(
                pref: settings.channelPreferences[i],
                onChanged: (updated) =>
                    notifier.patch((s) => s.withPreference(updated)),
              ),
            ],
          ],
        ),
      ),
    );
  }
}

class _HeaderCell extends StatelessWidget {
  const _HeaderCell({required this.label, required this.icon});

  final String label;
  final IconData icon;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return SizedBox(
      width: 48,
      child: Column(
        children: [
          Icon(icon, size: 18, color: theme.colorScheme.onSurfaceVariant),
          const SizedBox(height: 2),
          Text(
            label,
            style: theme.textTheme.labelSmall?.copyWith(
              color: theme.colorScheme.onSurfaceVariant,
            ),
          ),
        ],
      ),
    );
  }
}

class _MatrixRow extends StatelessWidget {
  const _MatrixRow({required this.pref, required this.onChanged});

  final ChannelPreference pref;
  final ValueChanged<ChannelPreference> onChanged;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Row(
        children: [
          Expanded(
            child: Text(
              pref.label,
              style: theme.textTheme.bodyMedium
                  ?.copyWith(fontWeight: FontWeight.w600),
            ),
          ),
          _CheckCell(
            value: pref.enableInApp,
            semanticLabel: '${pref.label} in-app',
            onChanged: (v) => onChanged(pref.copyWith(enableInApp: v)),
          ),
          _CheckCell(
            value: pref.enableEmail,
            semanticLabel: '${pref.label} email',
            onChanged: (v) => onChanged(pref.copyWith(enableEmail: v)),
          ),
          _CheckCell(
            value: pref.enableSms,
            semanticLabel: '${pref.label} SMS',
            onChanged: (v) => onChanged(pref.copyWith(enableSms: v)),
          ),
          _CheckCell(
            value: pref.enablePush,
            semanticLabel: '${pref.label} push',
            onChanged: (v) => onChanged(pref.copyWith(enablePush: v)),
          ),
        ],
      ),
    );
  }
}

class _CheckCell extends StatelessWidget {
  const _CheckCell({
    required this.value,
    required this.semanticLabel,
    required this.onChanged,
  });

  final bool value;
  final String semanticLabel;
  final ValueChanged<bool> onChanged;

  @override
  Widget build(BuildContext context) {
    return SizedBox(
      width: 48,
      child: Semantics(
        label: semanticLabel,
        checked: value,
        child: Checkbox(
          value: value,
          // Big tap target for non-technical phone use.
          materialTapTargetSize: MaterialTapTargetSize.padded,
          visualDensity: VisualDensity.compact,
          onChanged: (v) => onChanged(v ?? false),
        ),
      ),
    );
  }
}

// ── Automation toggles ──────────────────────────────────────────────────────

class _AutomationCard extends ConsumerWidget {
  const _AutomationCard({required this.settings});

  final NotificationSettings settings;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final notifier = ref.read(notificationSettingsProvider.notifier);

    return Card(
      child: Column(
        children: [
          _ToggleTile(
            title: 'Rent charges',
            subtitle: 'Automatically post rent charges each period.',
            value: settings.enableRentCharges,
            onChanged: (v) =>
                notifier.patch((s) => s.copyWith(enableRentCharges: v)),
          ),
          const _TileDivider(),
          _ToggleTile(
            title: 'Late fees',
            subtitle: 'Apply late fees after the grace period.',
            value: settings.enableLateFees,
            onChanged: (v) =>
                notifier.patch((s) => s.copyWith(enableLateFees: v)),
          ),
          const _TileDivider(),
          _ToggleTile(
            title: 'Lease reminders',
            subtitle: 'Remind you before a lease expires.',
            value: settings.enableLeaseExpiryReminders,
            onChanged: (v) => notifier
                .patch((s) => s.copyWith(enableLeaseExpiryReminders: v)),
          ),
          const _TileDivider(),
          _ToggleTile(
            title: 'Notify tenants',
            subtitle: 'Also send these messages to tenants, not just you.',
            value: settings.notifyTenants,
            onChanged: (v) =>
                notifier.patch((s) => s.copyWith(notifyTenants: v)),
          ),
        ],
      ),
    );
  }
}

class _ToggleTile extends StatelessWidget {
  const _ToggleTile({
    required this.title,
    required this.subtitle,
    required this.value,
    required this.onChanged,
  });

  final String title;
  final String subtitle;
  final bool value;
  final ValueChanged<bool> onChanged;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return SwitchListTile(
      contentPadding: const EdgeInsets.symmetric(horizontal: 14, vertical: 4),
      title: Text(
        title,
        style: theme.textTheme.bodyLarge?.copyWith(fontWeight: FontWeight.w600),
      ),
      subtitle: Text(
        subtitle,
        style: theme.textTheme.bodySmall?.copyWith(
          color: theme.colorScheme.onSurfaceVariant,
        ),
      ),
      value: value,
      onChanged: onChanged,
    );
  }
}

// ── Timing (day fields) ─────────────────────────────────────────────────────

class _TimingCard extends ConsumerWidget {
  const _TimingCard({required this.settings});

  final NotificationSettings settings;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final notifier = ref.read(notificationSettingsProvider.notifier);

    return Card(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(14, 4, 14, 8),
        child: Column(
          children: [
            _DayStepper(
              label: 'Rent charge lead time',
              suffix: 'days before',
              value: settings.rentChargeLeadDays,
              min: 0,
              max: 31,
              onChanged: (v) =>
                  notifier.patch((s) => s.copyWith(rentChargeLeadDays: v)),
            ),
            const _TileDivider(),
            _DayStepper(
              label: 'Late fee grace period',
              suffix: 'days after',
              value: settings.lateFeeGraceDays,
              min: 0,
              max: 31,
              onChanged: (v) =>
                  notifier.patch((s) => s.copyWith(lateFeeGraceDays: v)),
            ),
            const _TileDivider(),
            _DayStepper(
              label: 'Lease expiry reminder',
              suffix: 'days before',
              value: settings.leaseExpiryReminderDays,
              min: 0,
              max: 180,
              onChanged: (v) =>
                  notifier.patch((s) => s.copyWith(leaseExpiryReminderDays: v)),
            ),
          ],
        ),
      ),
    );
  }
}

/// Plus/minus day stepper — easier than typing for a non-technical user.
class _DayStepper extends StatelessWidget {
  const _DayStepper({
    required this.label,
    required this.suffix,
    required this.value,
    required this.min,
    required this.max,
    required this.onChanged,
  });

  final String label;
  final String suffix;
  final int value;
  final int min;
  final int max;
  final ValueChanged<int> onChanged;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Row(
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  label,
                  style: theme.textTheme.bodyLarge
                      ?.copyWith(fontWeight: FontWeight.w600),
                ),
                Text(
                  suffix,
                  style: theme.textTheme.bodySmall
                      ?.copyWith(color: cs.onSurfaceVariant),
                ),
              ],
            ),
          ),
          IconButton.filledTonal(
            tooltip: 'Decrease',
            iconSize: 22,
            onPressed:
                value > min ? () => onChanged(value - 1) : null,
            icon: const Icon(Icons.remove),
          ),
          SizedBox(
            width: 40,
            child: Text(
              '$value',
              textAlign: TextAlign.center,
              style: theme.textTheme.titleMedium
                  ?.copyWith(fontWeight: FontWeight.w700),
            ),
          ),
          IconButton.filledTonal(
            tooltip: 'Increase',
            iconSize: 22,
            onPressed:
                value < max ? () => onChanged(value + 1) : null,
            icon: const Icon(Icons.add),
          ),
        ],
      ),
    );
  }
}

// ── Provider status (read-only) ─────────────────────────────────────────────

class _ProviderStatusCard extends StatelessWidget {
  const _ProviderStatusCard({required this.settings});

  final NotificationSettings settings;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final configured = settings.signalWireConfigured;

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(14),
        child: Row(
          children: [
            Icon(
              configured ? Icons.check_circle : Icons.info_outline,
              color: configured ? Colors.green.shade600 : cs.onSurfaceVariant,
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    configured
                        ? 'SMS is set up'
                        : 'SMS is not fully set up',
                    style: theme.textTheme.bodyLarge
                        ?.copyWith(fontWeight: FontWeight.w600),
                  ),
                  const SizedBox(height: 2),
                  Text(
                    settings.signalWireFromNumber != null
                        ? 'Sending from ${settings.signalWireFromNumber}'
                        : 'Finish setup on the web app to send texts.',
                    style: theme.textTheme.bodySmall
                        ?.copyWith(color: cs.onSurfaceVariant),
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

// ── Account / security ──────────────────────────────────────────────────────

class _AccountSecurityCard extends StatelessWidget {
  const _AccountSecurityCard();

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card(
      child: ListTile(
        contentPadding: const EdgeInsets.symmetric(horizontal: 14, vertical: 4),
        leading: Icon(Icons.lock_outline, color: theme.colorScheme.primary),
        title: Text(
          'Change password',
          style: theme.textTheme.bodyLarge?.copyWith(fontWeight: FontWeight.w600),
        ),
        subtitle: Text(
          'Update the password you use to sign in.',
          style: theme.textTheme.bodySmall
              ?.copyWith(color: theme.colorScheme.onSurfaceVariant),
        ),
        trailing: const Icon(Icons.chevron_right),
        onTap: () => Navigator.of(context).push(
          MaterialPageRoute<void>(
            builder: (_) => const ChangePasswordScreen(),
          ),
        ),
      ),
    );
  }
}

// ── Shared bits ─────────────────────────────────────────────────────────────

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
        Text(
          title,
          style: theme.textTheme.titleMedium
              ?.copyWith(fontWeight: FontWeight.w700),
        ),
        const SizedBox(height: 2),
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

class _TileDivider extends StatelessWidget {
  const _TileDivider();

  @override
  Widget build(BuildContext context) {
    return Divider(
      height: 1,
      color: Theme.of(context).colorScheme.outlineVariant.withValues(alpha: 0.5),
    );
  }
}

class _SaveBar extends StatelessWidget {
  const _SaveBar({required this.saving, required this.onSave});

  final bool saving;
  final VoidCallback onSave;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return SafeArea(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 8, 16, 12),
        child: SizedBox(
          height: 52,
          child: FilledButton.icon(
            onPressed: saving ? null : onSave,
            icon: saving
                ? const SizedBox(
                    width: 18,
                    height: 18,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.save_outlined),
            label: Text(
              saving ? 'Saving…' : 'Save changes',
              style: theme.textTheme.titleMedium
                  ?.copyWith(fontWeight: FontWeight.w600),
            ),
          ),
        ),
      ),
    );
  }
}

class _ErrorBody extends StatelessWidget {
  const _ErrorBody({required this.message, required this.onRetry});

  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.error_outline, size: 40, color: cs.error),
            const SizedBox(height: 12),
            Text(
              message,
              textAlign: TextAlign.center,
              style: TextStyle(color: cs.error),
            ),
            const SizedBox(height: 16),
            FilledButton.tonal(
              onPressed: onRetry,
              child: const Text('Retry'),
            ),
          ],
        ),
      ),
    );
  }
}
