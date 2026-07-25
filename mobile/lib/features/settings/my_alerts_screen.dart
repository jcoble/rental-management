import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import 'notification_help_action.dart';
import 'notification_foundation_repository.dart';

class MyAlertsScreen extends ConsumerStatefulWidget {
  const MyAlertsScreen({super.key});

  @override
  ConsumerState<MyAlertsScreen> createState() => _MyAlertsScreenState();
}

class _MyAlertsScreenState extends ConsumerState<MyAlertsScreen> {
  bool _saving = false;

  Future<void> _save() async {
    setState(() => _saving = true);
    try {
      await ref.read(myAlertsProvider.notifier).save();
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(content: Text('Your alert preferences were saved.')),
        );
      }
    } catch (error) {
      if (mounted) _showError(error);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  void _showError(Object error) {
    final message = error is ApiException
        ? error.message
        : 'We couldn\'t save your alert preferences.';
    ScaffoldMessenger.of(
      context,
    ).showSnackBar(SnackBar(content: Text(message)));
  }

  @override
  Widget build(BuildContext context) {
    final alerts = ref.watch(myAlertsProvider);
    return Scaffold(
      appBar: AppBar(
        title: const Text('My alerts'),
        actions: const [NotificationHelpAction()],
      ),
      body: alerts.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (error, _) => _ErrorBody(
          message: error is ApiException
              ? error.message
              : 'We couldn\'t load your alert preferences.',
          onRetry: () => ref.read(myAlertsProvider.notifier).load(),
        ),
        data: (value) => ListView(
          padding: const EdgeInsets.fromLTRB(16, 16, 16, 104),
          children: [
            Text(
              'These choices apply only to your signed-in account. They do '
              'not change team responsibilities or tenant delivery.',
              style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                color: Theme.of(context).colorScheme.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 16),
            Card(
              child: Padding(
                padding: const EdgeInsets.all(16),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      'Configuring alerts for ${value.displayName}',
                      style: Theme.of(context).textTheme.titleSmall,
                    ),
                    const SizedBox(height: 6),
                    Text('Email: ${value.email ?? 'No email on this account'}'),
                    Text(
                      'SMS: ${value.phoneNumber ?? 'No phone number on this account'}',
                    ),
                  ],
                ),
              ),
            ),
            const SizedBox(height: 12),
            Card(
              child: Column(
                children: [
                  _AlertSwitch(
                    title: 'In-app',
                    subtitle: 'Shows in your Rental Command bell and inbox.',
                    value: value.enableInApp,
                    onChanged: (enabled) => _update(
                      (current) => current.copyWith(enableInApp: enabled),
                    ),
                  ),
                  const Divider(height: 1),
                  _AlertSwitch(
                    title: 'Mobile push',
                    subtitle:
                        'Goes to mobile devices registered to your account.',
                    value: value.enableMobilePush,
                    onChanged: (enabled) => _update(
                      (current) => current.copyWith(enableMobilePush: enabled),
                    ),
                  ),
                  const Divider(height: 1),
                  _AlertSwitch(
                    title: 'Email',
                    subtitle: 'Goes to the account email shown above.',
                    value: value.enableEmail,
                    onChanged: (enabled) => _update(
                      (current) => current.copyWith(enableEmail: enabled),
                    ),
                  ),
                  const Divider(height: 1),
                  _AlertSwitch(
                    title: 'SMS',
                    subtitle:
                        'Uses the account phone number when one is available.',
                    value: value.enableSms,
                    onChanged: (enabled) => _update(
                      (current) => current.copyWith(enableSms: enabled),
                    ),
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
      bottomNavigationBar: alerts.value != null
          ? SafeArea(
              minimum: const EdgeInsets.all(16),
              child: FilledButton.icon(
                onPressed: _saving ? null : _save,
                icon: _saving
                    ? const SizedBox.square(
                        dimension: 18,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : const Icon(Icons.save_outlined),
                label: const Text('Save my alerts'),
              ),
            )
          : null,
    );
  }

  void _update(MyAlerts Function(MyAlerts current) edit) {
    ref.read(myAlertsProvider.notifier).update(edit);
  }
}

class _AlertSwitch extends StatelessWidget {
  const _AlertSwitch({
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
  Widget build(BuildContext context) => SwitchListTile(
    title: Text(title),
    subtitle: Text(subtitle),
    value: value,
    onChanged: onChanged,
  );
}

class _ErrorBody extends StatelessWidget {
  const _ErrorBody({required this.message, required this.onRetry});

  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) => Center(
    child: Padding(
      padding: const EdgeInsets.all(24),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(message, textAlign: TextAlign.center),
          const SizedBox(height: 12),
          OutlinedButton(onPressed: onRetry, child: const Text('Retry')),
        ],
      ),
    ),
  );
}
