import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../team/team_repository.dart';
import 'notification_help_action.dart';
import 'notification_foundation_repository.dart';

class TeamRoutingScreen extends ConsumerStatefulWidget {
  const TeamRoutingScreen({super.key});

  @override
  ConsumerState<TeamRoutingScreen> createState() => _TeamRoutingScreenState();
}

class _TeamRoutingScreenState extends ConsumerState<TeamRoutingScreen> {
  late Future<List<TeamRoutingRule>> _rules;
  late Future<MorningBriefingSettings> _briefing;

  @override
  void initState() {
    super.initState();
    _reload();
  }

  void _reload() {
    _rules = ref
        .read(notificationFoundationRepositoryProvider)
        .listTeamRouting();
    _briefing = ref
        .read(notificationFoundationRepositoryProvider)
        .getMorningBriefingSettings();
  }

  Future<void> _refresh() async {
    setState(_reload);
    await _rules;
    await _briefing;
  }

  Future<void> _editBriefing(MorningBriefingSettings settings) async {
    final saved = await showModalBottomSheet<bool>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      builder: (_) => _MorningBriefingEditorSheet(settings: settings),
    );
    if (saved == true && mounted) await _refresh();
  }

  Future<void> _edit(TeamRoutingRule rule) async {
    final saved = await showModalBottomSheet<bool>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      builder: (_) => _TeamRoutingEditorSheet(rule: rule),
    );
    if (saved == true && mounted) await _refresh();
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: const Text('Who gets told what'),
        actions: const [NotificationHelpAction()],
      ),
      body: FutureBuilder<List<TeamRoutingRule>>(
        future: _rules,
        builder: (context, snapshot) {
          if (snapshot.connectionState == ConnectionState.waiting) {
            return const Center(child: CircularProgressIndicator());
          }
          if (snapshot.hasError) {
            return _RoutingError(
              error: snapshot.error!,
              onRetry: () => setState(_reload),
            );
          }
          final rules = snapshot.data ?? const <TeamRoutingRule>[];
          return RefreshIndicator(
            onRefresh: _refresh,
            child: ListView(
              physics: const AlwaysScrollableScrollPhysics(),
              padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
              children: [
                Text(
                  'Choose named team members for each responsibility. Each '
                  'recipient sees why they receive the topic; their delivery '
                  'channels come from My alerts.',
                  style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                    color: Theme.of(context).colorScheme.onSurfaceVariant,
                  ),
                ),
                const SizedBox(height: 16),
                FutureBuilder<MorningBriefingSettings>(
                  future: _briefing,
                  builder: (context, briefingSnapshot) {
                    if (briefingSnapshot.connectionState ==
                        ConnectionState.waiting) {
                      return const Card(
                        child: Padding(
                          padding: EdgeInsets.all(20),
                          child: LinearProgressIndicator(),
                        ),
                      );
                    }
                    if (briefingSnapshot.hasError ||
                        briefingSnapshot.data == null) {
                      return Card(
                        child: ListTile(
                          title: const Text(
                            'Morning Briefing schedule unavailable',
                          ),
                          subtitle: const Text(
                            'Pull to refresh and try loading it again.',
                          ),
                          trailing: const Icon(Icons.error_outline),
                        ),
                      );
                    }
                    final settings = briefingSnapshot.data!;
                    return _MorningBriefingCard(
                      settings: settings,
                      onEdit: () => _editBriefing(settings),
                    );
                  },
                ),
                const SizedBox(height: 12),
                if (rules.isEmpty)
                  _MissingRouting(onRetry: () => setState(_reload))
                else
                  for (final rule in rules)
                    _RoutingRuleCard(rule: rule, onEdit: () => _edit(rule)),
              ],
            ),
          );
        },
      ),
    );
  }
}

class _MorningBriefingCard extends StatelessWidget {
  const _MorningBriefingCard({required this.settings, required this.onEdit});

  final MorningBriefingSettings settings;
  final VoidCallback onEdit;

  @override
  Widget build(BuildContext context) => Card(
    child: Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  'Morning Briefing schedule',
                  style: Theme.of(context).textTheme.titleSmall,
                ),
              ),
              IconButton(
                tooltip: 'Edit Morning Briefing schedule',
                onPressed: onEdit,
                icon: const Icon(Icons.schedule_outlined),
              ),
            ],
          ),
          Text(
            settings.enabled
                ? 'Sends at ${settings.sendHourLocal.toString().padLeft(2, '0')}:00 in ${settings.timeZone}.'
                : 'Scheduled delivery is paused.',
          ),
          const SizedBox(height: 4),
          Text(
            settings.includeEmpty
                ? 'All-clear days are included.'
                : 'Only sends when something needs attention.',
            style: Theme.of(context).textTheme.bodySmall,
          ),
        ],
      ),
    ),
  );
}

class _MorningBriefingEditorSheet extends ConsumerStatefulWidget {
  const _MorningBriefingEditorSheet({required this.settings});

  final MorningBriefingSettings settings;

  @override
  ConsumerState<_MorningBriefingEditorSheet> createState() =>
      _MorningBriefingEditorSheetState();
}

class _MorningBriefingEditorSheetState
    extends ConsumerState<_MorningBriefingEditorSheet> {
  late bool _enabled;
  late bool _includeEmpty;
  late final TextEditingController _hour;
  bool _saving = false;

  @override
  void initState() {
    super.initState();
    _enabled = widget.settings.enabled;
    _includeEmpty = widget.settings.includeEmpty;
    _hour = TextEditingController(
      text: widget.settings.sendHourLocal.toString(),
    );
  }

  @override
  void dispose() {
    _hour.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    final hour = int.tryParse(_hour.text);
    if (hour == null || hour < 0 || hour > 23) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text('Send hour must be between 0 and 23.')),
      );
      return;
    }
    setState(() => _saving = true);
    try {
      await ref
          .read(notificationFoundationRepositoryProvider)
          .updateMorningBriefingSettings(
            MorningBriefingSettings(
              enabled: _enabled,
              sendHourLocal: hour,
              includeEmpty: _includeEmpty,
              timeZone: widget.settings.timeZone,
            ),
          );
      if (mounted) Navigator.of(context).pop(true);
    } catch (error) {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: Text(
              error is ApiException
                  ? error.message
                  : 'We couldn\'t save the Morning Briefing schedule.',
            ),
          ),
        );
      }
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(
      title: const Text('Morning Briefing'),
      leading: IconButton(
        onPressed: () => Navigator.of(context).pop(),
        icon: const Icon(Icons.close),
      ),
    ),
    body: ListView(
      padding: EdgeInsets.fromLTRB(
        16,
        16,
        16,
        32 + MediaQuery.viewInsetsOf(context).bottom,
      ),
      children: [
        const Text(
          'Sends the routed team member a daily list of rent, lease, '
          'appointment, inspection, and urgent work that needs attention.',
        ),
        const SizedBox(height: 16),
        SwitchListTile(
          contentPadding: EdgeInsets.zero,
          title: const Text('Send every morning'),
          subtitle: const Text('Turn off to pause scheduled briefings.'),
          value: _enabled,
          onChanged: (value) => setState(() => _enabled = value),
        ),
        TextField(
          controller: _hour,
          keyboardType: TextInputType.number,
          decoration: InputDecoration(
            labelText: 'Local send hour (0–23)',
            helperText: widget.settings.timeZone,
          ),
        ),
        SwitchListTile(
          contentPadding: EdgeInsets.zero,
          title: const Text('Send all-clear days'),
          subtitle: const Text(
            'Also send when nothing currently needs attention.',
          ),
          value: _includeEmpty,
          onChanged: (value) => setState(() => _includeEmpty = value),
        ),
        const SizedBox(height: 16),
        FilledButton.icon(
          onPressed: _saving ? null : _save,
          icon: _saving
              ? const SizedBox.square(
                  dimension: 18,
                  child: CircularProgressIndicator(strokeWidth: 2),
                )
              : const Icon(Icons.save_outlined),
          label: const Text('Save Morning Briefing'),
        ),
      ],
    ),
  );
}

class _RoutingRuleCard extends StatelessWidget {
  const _RoutingRuleCard({required this.rule, required this.onEdit});

  final TeamRoutingRule rule;
  final VoidCallback onEdit;

  @override
  Widget build(BuildContext context) => Card(
    margin: const EdgeInsets.only(bottom: 12),
    child: Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      _topicLabel(rule.topic),
                      style: Theme.of(context).textTheme.titleSmall,
                    ),
                    const SizedBox(height: 3),
                    Text(rule.scope),
                  ],
                ),
              ),
              IconButton(
                tooltip: 'Edit ${_topicLabel(rule.topic)}',
                onPressed: onEdit,
                icon: const Icon(Icons.edit_outlined),
              ),
            ],
          ),
          const SizedBox(height: 8),
          Text(rule.namedRecipientSummary),
          const SizedBox(height: 5),
          Text(
            rule.routingExplanation,
            style: Theme.of(context).textTheme.bodySmall?.copyWith(
              color: Theme.of(context).colorScheme.onSurfaceVariant,
            ),
          ),
        ],
      ),
    ),
  );
}

class _TeamRoutingEditorSheet extends ConsumerStatefulWidget {
  const _TeamRoutingEditorSheet({required this.rule});

  final TeamRoutingRule rule;

  @override
  ConsumerState<_TeamRoutingEditorSheet> createState() =>
      _TeamRoutingEditorSheetState();
}

class _TeamRoutingEditorSheetState
    extends ConsumerState<_TeamRoutingEditorSheet> {
  late final Future<_RoutingEditorData> _data;
  late String _topic;
  late bool _useFallback;
  final _recipients = <int, _EditableRecipient>{};
  final _searchController = TextEditingController();
  TeamMemberPage? _searchResults;
  bool _initialized = false;
  bool _searching = false;
  bool _saving = false;

  @override
  void initState() {
    super.initState();
    _topic = widget.rule.topic;
    _useFallback = widget.rule.useWorkspaceAdministratorFallback;
    _data = _loadData();
  }

  Future<_RoutingEditorData> _loadData() async {
    final rule = widget.rule;
    final repository = ref.read(notificationFoundationRepositoryProvider);
    final values = await Future.wait<Object>([
      repository.listTeamRoutingRecipients(rule.id),
      repository.previewTeamRouting(rule.id),
    ]);
    return _RoutingEditorData(
      values[0] as List<TeamRoutingRecipient>,
      values[1] as List<TeamRoutingRecipientPreview>,
    );
  }

  void _initialize(_RoutingEditorData data) {
    if (_initialized) return;
    for (final recipient in data.recipients) {
      _recipients[recipient.userId] = _EditableRecipient(
        userId: recipient.userId,
        displayName: recipient.displayName,
        email: recipient.email,
        reason: recipient.reason,
      );
    }
    _initialized = true;
  }

  Future<void> _search() async {
    final query = _searchController.text.trim();
    if (query.length < 2) {
      _showMessage('Type at least two characters.');
      return;
    }
    setState(() => _searching = true);
    try {
      final page = await ref
          .read(teamRepositoryProvider)
          .listMembers(take: 20, search: query, sort: 'name');
      if (mounted) setState(() => _searchResults = page);
    } catch (error) {
      if (mounted) _showError(error);
    } finally {
      if (mounted) setState(() => _searching = false);
    }
  }

  void _add(TeamMember member) {
    if (!member.isActive || _recipients.containsKey(member.userId)) return;
    setState(() {
      _recipients[member.userId] = _EditableRecipient(
        userId: member.userId,
        displayName: member.displayName,
        email: member.email,
        reason:
            '${member.displayName} is the named ${_topicLabel(_topic).toLowerCase()} contact.',
      );
    });
  }

  Future<void> _save() async {
    if (_recipients.isEmpty && !_useFallback) {
      _showMessage('Add a recipient or enable administrator fallback.');
      return;
    }
    setState(() => _saving = true);
    try {
      await ref
          .read(notificationFoundationRepositoryProvider)
          .replaceTeamRouting(
            topic: _topic,
            propertyId: widget.rule.propertyId,
            useWorkspaceAdministratorFallback: _useFallback,
            recipients: [
              for (final recipient in _recipients.values)
                TeamRoutingRecipientUpdate(
                  userId: recipient.userId,
                  reason: recipient.reason.trim(),
                ),
            ],
          );
      if (mounted) Navigator.of(context).pop(true);
    } catch (error) {
      if (mounted) _showError(error);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  void dispose() {
    _searchController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<_RoutingEditorData>(
      future: _data,
      builder: (context, snapshot) {
        if (snapshot.connectionState == ConnectionState.waiting) {
          return const Center(child: CircularProgressIndicator());
        }
        if (snapshot.hasError) {
          return _RoutingError(
            error: snapshot.error!,
            onRetry: () => Navigator.of(context).pop(),
          );
        }
        final data = snapshot.data ?? const _RoutingEditorData([], []);
        _initialize(data);
        return Scaffold(
          appBar: AppBar(
            title: Text('Configure ${_topicLabel(_topic)}'),
            leading: IconButton(
              onPressed: () => Navigator.of(context).pop(),
              icon: const Icon(Icons.close),
            ),
          ),
          body: ListView(
            padding: EdgeInsets.fromLTRB(
              16,
              16,
              16,
              32 + MediaQuery.viewInsetsOf(context).bottom,
            ),
            children: [
              Text(
                widget.rule.scope,
                style: Theme.of(context).textTheme.titleSmall,
              ),
              Text(
                'Named recipients receive this topic for the reason saved '
                'beside their name.',
                style: Theme.of(context).textTheme.bodySmall,
              ),
              const SizedBox(height: 16),
              for (final recipient in _recipients.values)
                _EditableRecipientCard(
                  key: ValueKey(recipient.userId),
                  recipient: recipient,
                  onReasonChanged: (reason) => recipient.reason = reason,
                  onRemove: () =>
                      setState(() => _recipients.remove(recipient.userId)),
                ),
              Card(
                child: Padding(
                  padding: const EdgeInsets.all(16),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        'Add a team member',
                        style: Theme.of(context).textTheme.titleSmall,
                      ),
                      const SizedBox(height: 8),
                      TextField(
                        controller: _searchController,
                        textInputAction: TextInputAction.search,
                        onSubmitted: (_) => _search(),
                        decoration: InputDecoration(
                          labelText: 'Search active team members',
                          suffixIcon: IconButton(
                            onPressed: _searching ? null : _search,
                            icon: _searching
                                ? const Padding(
                                    padding: EdgeInsets.all(12),
                                    child: CircularProgressIndicator(
                                      strokeWidth: 2,
                                    ),
                                  )
                                : const Icon(Icons.search),
                          ),
                        ),
                      ),
                      if (_searchResults case final results?) ...[
                        const SizedBox(height: 8),
                        for (final member in results.items)
                          ListTile(
                            contentPadding: EdgeInsets.zero,
                            title: Text(member.displayName),
                            subtitle: Text(
                              '${member.email} · ${member.roleSummary.isEmpty ? member.membershipStatus : member.roleSummary}',
                            ),
                            trailing: TextButton(
                              onPressed:
                                  member.isActive &&
                                      !_recipients.containsKey(member.userId)
                                  ? () => _add(member)
                                  : null,
                              child: const Text('Add'),
                            ),
                          ),
                      ],
                    ],
                  ),
                ),
              ),
              SwitchListTile(
                contentPadding: const EdgeInsets.symmetric(horizontal: 8),
                title: const Text('Fall back to Workspace Administrators'),
                subtitle: const Text(
                  'Used only when this saved rule has no named recipient.',
                ),
                value: _useFallback,
                onChanged: (value) => setState(() => _useFallback = value),
              ),
              if (data.preview.isNotEmpty) ...[
                const SizedBox(height: 12),
                Text(
                  'Current recipient preview',
                  style: Theme.of(context).textTheme.titleSmall,
                ),
                const SizedBox(height: 6),
                for (final recipient in data.preview)
                  ListTile(
                    contentPadding: EdgeInsets.zero,
                    leading: Icon(
                      recipient.isAdministratorFallback
                          ? Icons.admin_panel_settings_outlined
                          : Icons.person_outline,
                    ),
                    title: Text(recipient.displayName),
                    subtitle: Text(
                      '${recipient.scope}\n'
                      'Email: ${recipient.email ?? 'Not available'} · '
                      'Phone: ${recipient.phoneNumber ?? 'Not available'}\n'
                      'Enabled channels: ${_teamRecipientChannelSummary(recipient)}\n'
                      '${recipient.reason}',
                    ),
                    isThreeLine: true,
                  ),
              ],
              const SizedBox(height: 16),
              FilledButton.icon(
                onPressed: _saving ? null : _save,
                icon: _saving
                    ? const SizedBox.square(
                        dimension: 18,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : const Icon(Icons.save_outlined),
                label: const Text('Save assignments'),
              ),
            ],
          ),
        );
      },
    );
  }

  void _showError(Object error) => _showMessage(
    error is ApiException ? error.message : 'We couldn\'t save those assignments.',
  );

  void _showMessage(String message) {
    ScaffoldMessenger.of(
      context,
    ).showSnackBar(SnackBar(content: Text(message)));
  }
}

class _EditableRecipientCard extends StatelessWidget {
  const _EditableRecipientCard({
    super.key,
    required this.recipient,
    required this.onReasonChanged,
    required this.onRemove,
  });

  final _EditableRecipient recipient;
  final ValueChanged<String> onReasonChanged;
  final VoidCallback onRemove;

  @override
  Widget build(BuildContext context) => Card(
    child: Padding(
      padding: const EdgeInsets.fromLTRB(16, 12, 8, 16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(recipient.displayName),
                    Text(
                      recipient.email ?? 'No email destination',
                      style: Theme.of(context).textTheme.bodySmall,
                    ),
                  ],
                ),
              ),
              IconButton(
                tooltip: 'Remove ${recipient.displayName}',
                onPressed: onRemove,
                icon: const Icon(Icons.close),
              ),
            ],
          ),
          TextFormField(
            initialValue: recipient.reason,
            onChanged: onReasonChanged,
            decoration: const InputDecoration(
              labelText: 'Why this person receives the topic',
            ),
          ),
        ],
      ),
    ),
  );
}

class _MissingRouting extends StatelessWidget {
  const _MissingRouting({required this.onRetry});

  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) => Card(
    child: Padding(
      padding: const EdgeInsets.all(20),
      child: Column(
        children: [
          const Icon(Icons.alt_route_outlined, size: 36),
          const SizedBox(height: 8),
          const Text('Routing defaults are unavailable.'),
          const SizedBox(height: 6),
          const Text(
            'Every new workspace receives one rule for each responsibility. '
            'Reload; if the rules are still missing, workspace setup needs attention.',
            textAlign: TextAlign.center,
          ),
          const SizedBox(height: 12),
          OutlinedButton(
            onPressed: onRetry,
            child: const Text('Reload routing'),
          ),
        ],
      ),
    ),
  );
}

class _RoutingError extends StatelessWidget {
  const _RoutingError({required this.error, required this.onRetry});

  final Object error;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) => Center(
    child: Padding(
      padding: const EdgeInsets.all(24),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(
            error is ApiException
                ? (error as ApiException).message
                : 'We couldn\'t load those assignments.',
            textAlign: TextAlign.center,
          ),
          const SizedBox(height: 12),
          OutlinedButton(onPressed: onRetry, child: const Text('Retry')),
        ],
      ),
    ),
  );
}

class _EditableRecipient {
  _EditableRecipient({
    required this.userId,
    required this.displayName,
    required this.email,
    required this.reason,
  });

  final int userId;
  final String displayName;
  final String? email;
  String reason;
}

class _RoutingEditorData {
  const _RoutingEditorData(this.recipients, this.preview);

  final List<TeamRoutingRecipient> recipients;
  final List<TeamRoutingRecipientPreview> preview;
}

String _teamRecipientChannelSummary(TeamRoutingRecipientPreview recipient) {
  final channels = <String>[];
  if (recipient.enableInApp) channels.add('In-app');
  if (recipient.enableMobilePush) channels.add('Mobile push');
  if (recipient.enableEmail) channels.add('Email');
  if (recipient.enableSms) channels.add('SMS');
  return channels.isEmpty ? 'No enabled channels' : channels.join(', ');
}

String _topicLabel(String topic) => switch (topic) {
  'RentAndMoney' => 'Rent and money',
  'ApplicationsAndLeasing' => 'Applications and leasing',
  'WorkOrders' => 'Repairs',
  'OwnerStatementsAndDecisions' => 'Owner statements and decisions',
  'AccountAndSecurity' => 'Account and security',
  'MorningBriefing' => 'Morning Briefing',
  _ => topic,
};
