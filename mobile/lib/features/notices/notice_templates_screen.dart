import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../settings/notification_settings_repository.dart';
import 'notice_templates_repository.dart';

/// Notice templates + per-type send-mode editor.
///
/// One expandable tile per notice type with:
///   - an Auto-send / Ask-first toggle (persisted via the notification-settings
///     update flow — the `autoSend*` fields),
///   - a subject + multiline body editor with merge-field chips that insert
///     `{{field}}` at the body cursor,
///   - a "Save template" action (PUT /notices/templates/{type}).
class NoticeTemplatesScreen extends ConsumerStatefulWidget {
  const NoticeTemplatesScreen({super.key});

  @override
  ConsumerState<NoticeTemplatesScreen> createState() =>
      _NoticeTemplatesScreenState();
}

class _NoticeTemplatesScreenState extends ConsumerState<NoticeTemplatesScreen> {
  /// Friendly labels for each supported notice type.
  static const Map<String, String> _labels = {
    'RentReminder': 'Rent reminders',
    'RenewalOffer': 'Lease renewal offers',
    'MonthToMonthConversion': 'Month-to-month conversions',
    'MoveOutReminder': 'Move-out reminders',
    'LateRentNotice': 'Late rent notices',
  };

  /// Recurring notices each carry their own auto-send toggle.
  static const Set<String> _recurringTypes = {
    'RentReminder',
    'LateRentNotice',
  };

  /// Lease-end notices are driven by the single [leaseEndAutoAction] selector:
  /// a notice type auto-sends when the selected action maps to it.
  static const Map<String, String> _leaseEndActionForType = {
    'RenewalOffer': 'Renewal',
    'MonthToMonthConversion': 'MonthToMonth',
    'MoveOutReminder': 'NonRenewal',
  };

  /// The single lease-end selector's options, in canonical order.
  static const List<(String value, String label)> _leaseEndOptions = [
    ('Draft', 'Just draft it'),
    ('Renewal', 'Offer renewal'),
    ('MonthToMonth', 'Offer month-to-month'),
    ('NonRenewal', 'Send non-renewal'),
  ];

  late Future<List<NoticeTemplate>> _future;
  final _subjectControllers = <String, TextEditingController>{};
  final _bodyControllers = <String, TextEditingController>{};
  final _savingTemplate = <String>{};

  @override
  void initState() {
    super.initState();
    _future = ref.read(noticeTemplatesRepositoryProvider).list();
    // Make sure the settings working copy is loaded for the send-mode toggles.
    Future.microtask(() {
      final state = ref.read(notificationSettingsProvider);
      if (state is! AsyncData) {
        ref.read(notificationSettingsProvider.notifier).load();
      }
    });
  }

  @override
  void dispose() {
    for (final c in _subjectControllers.values) {
      c.dispose();
    }
    for (final c in _bodyControllers.values) {
      c.dispose();
    }
    super.dispose();
  }

  void _reloadTemplates() {
    setState(() {
      _future = ref.read(noticeTemplatesRepositoryProvider).list();
    });
  }

  TextEditingController _subjectControllerFor(NoticeTemplate t) =>
      _subjectControllers.putIfAbsent(
        t.noticeType,
        () => TextEditingController(text: t.subject),
      );

  TextEditingController _bodyControllerFor(NoticeTemplate t) =>
      _bodyControllers.putIfAbsent(
        t.noticeType,
        () => TextEditingController(text: t.body),
      );

  /// Whether [type] is currently set to auto-send. Recurring notices read their
  /// own toggle; lease-end notices auto-send when the selected action maps here.
  static bool _autoSendOf(NotificationSettings s, String type) {
    switch (type) {
      case 'RentReminder':
        return s.autoSendRentReminder;
      case 'LateRentNotice':
        return s.autoSendLateRent;
      default:
        final action = _leaseEndActionForType[type];
        return action != null && s.leaseEndAutoAction == action;
    }
  }

  static NotificationSettings _withAutoSend(
    NotificationSettings s,
    String type,
    bool value,
  ) {
    switch (type) {
      case 'RentReminder':
        return s.copyWith(autoSendRentReminder: value);
      case 'LateRentNotice':
        return s.copyWith(autoSendLateRent: value);
      default:
        return s;
    }
  }

  Future<void> _setAutoSend(String type, bool value) async {
    final notifier = ref.read(notificationSettingsProvider.notifier);
    notifier.patch((s) => _withAutoSend(s, type, value));
    try {
      await notifier.save();
    } on ApiException catch (e) {
      if (!mounted) return;
      // Re-sync to the server's truth so the toggle reflects reality.
      await notifier.refresh();
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(e.message)),
      );
    }
  }

  /// Persists the single lease-end auto-send action via the settings flow.
  Future<void> _setLeaseEndAction(String value) async {
    final notifier = ref.read(notificationSettingsProvider.notifier);
    notifier.patch((s) => s.copyWith(leaseEndAutoAction: value));
    try {
      await notifier.save();
    } on ApiException catch (e) {
      if (!mounted) return;
      await notifier.refresh();
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(e.message)),
      );
    }
  }

  void _insertField(String type, String field) {
    final controller = _bodyControllers[type];
    if (controller == null) return;
    final text = controller.text;
    final selection = controller.selection;
    final token = '{{$field}}';
    final start = selection.start >= 0 ? selection.start : text.length;
    final end = selection.end >= 0 ? selection.end : text.length;
    final newText = text.replaceRange(start, end, token);
    controller.value = TextEditingValue(
      text: newText,
      selection: TextSelection.collapsed(offset: start + token.length),
    );
  }

  Future<void> _saveTemplate(String type) async {
    final subject = _subjectControllers[type]?.text.trim() ?? '';
    final body = _bodyControllers[type]?.text.trim() ?? '';
    if (subject.isEmpty || body.isEmpty) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text('Add a subject and a message first.')),
      );
      return;
    }
    setState(() => _savingTemplate.add(type));
    try {
      await ref
          .read(noticeTemplatesRepositoryProvider)
          .upsert(type, subject: subject, body: body);
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text('Template saved.')),
      );
      _reloadTemplates();
    } on ApiException catch (e) {
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(e.message)),
      );
    } finally {
      if (mounted) setState(() => _savingTemplate.remove(type));
    }
  }

  @override
  Widget build(BuildContext context) {
    final settingsAsync = ref.watch(notificationSettingsProvider);

    return Scaffold(
      appBar: AppBar(title: const Text('Notice templates')),
      body: FutureBuilder<List<NoticeTemplate>>(
        future: _future,
        builder: (context, snapshot) {
          if (snapshot.connectionState == ConnectionState.waiting) {
            return const Center(child: CircularProgressIndicator());
          }
          if (snapshot.hasError) {
            final e = snapshot.error;
            return _ErrorBody(
              message: e is ApiException ? e.message : e.toString(),
              onRetry: _reloadTemplates,
            );
          }
          final templates = snapshot.data ?? const <NoticeTemplate>[];
          return ListView(
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
            children: [
              Text(
                'Write the message we send for each kind of notice, and choose '
                'whether it goes out automatically or waits for you to review.',
                style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                      color: Theme.of(context).colorScheme.onSurfaceVariant,
                    ),
              ),
              const SizedBox(height: 12),
              _LeaseEndActionCard(
                value: settingsAsync.maybeWhen(
                  data: (s) => s.leaseEndAutoAction,
                  orElse: () => 'Draft',
                ),
                enabled: settingsAsync is AsyncData,
                options: _leaseEndOptions,
                onChanged: _setLeaseEndAction,
              ),
              for (final template in templates)
                _TemplateTile(
                  title: _labels[template.noticeType] ?? template.noticeType,
                  template: template,
                  autoSend: settingsAsync.maybeWhen(
                    data: (s) => _autoSendOf(s, template.noticeType),
                    orElse: () => false,
                  ),
                  autoSendReady: settingsAsync is AsyncData,
                  // Recurring notices keep an inline switch; lease-end notices
                  // are driven by the selector above.
                  showAutoSendSwitch:
                      _recurringTypes.contains(template.noticeType),
                  saving: _savingTemplate.contains(template.noticeType),
                  subjectController: _subjectControllerFor(template),
                  bodyController: _bodyControllerFor(template),
                  onAutoSendChanged: (v) =>
                      _setAutoSend(template.noticeType, v),
                  onInsertField: (f) => _insertField(template.noticeType, f),
                  onSave: () => _saveTemplate(template.noticeType),
                ),
            ],
          );
        },
      ),
    );
  }
}

class _TemplateTile extends StatelessWidget {
  const _TemplateTile({
    required this.title,
    required this.template,
    required this.autoSend,
    required this.autoSendReady,
    required this.showAutoSendSwitch,
    required this.saving,
    required this.subjectController,
    required this.bodyController,
    required this.onAutoSendChanged,
    required this.onInsertField,
    required this.onSave,
  });

  final String title;
  final NoticeTemplate template;
  final bool autoSend;
  final bool autoSendReady;
  final bool showAutoSendSwitch;
  final bool saving;
  final TextEditingController subjectController;
  final TextEditingController bodyController;
  final ValueChanged<bool> onAutoSendChanged;
  final ValueChanged<String> onInsertField;
  final VoidCallback onSave;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final needsTemplate = autoSend && !template.hasTemplate;

    return Card(
      clipBehavior: Clip.antiAlias,
      margin: const EdgeInsets.only(bottom: 12),
      child: ExpansionTile(
        title: Text(
          title,
          style: theme.textTheme.bodyLarge?.copyWith(fontWeight: FontWeight.w600),
        ),
        subtitle: Text(
          autoSend ? 'Auto-send' : 'Ask first',
          style: theme.textTheme.bodySmall?.copyWith(
            color: autoSend ? cs.primary : cs.onSurfaceVariant,
          ),
        ),
        childrenPadding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
        children: [
          if (showAutoSendSwitch)
            SwitchListTile(
              contentPadding: EdgeInsets.zero,
              title: const Text('Send automatically'),
              subtitle: Text(
                autoSend
                    ? 'Notices are sent without review.'
                    : 'We create a draft for you to review and send.',
                style: theme.textTheme.bodySmall
                    ?.copyWith(color: cs.onSurfaceVariant),
              ),
              value: autoSend,
              onChanged: autoSendReady ? onAutoSendChanged : null,
            ),
          if (needsTemplate)
            Container(
              width: double.infinity,
              margin: const EdgeInsets.only(bottom: 12),
              padding: const EdgeInsets.all(10),
              decoration: BoxDecoration(
                color: cs.errorContainer.withValues(alpha: 0.5),
                borderRadius: BorderRadius.circular(8),
              ),
              child: Text(
                'Add a template to enable auto-send.',
                style: theme.textTheme.bodySmall
                    ?.copyWith(color: cs.onErrorContainer),
              ),
            ),
          const SizedBox(height: 4),
          TextField(
            controller: subjectController,
            decoration: const InputDecoration(
              labelText: 'Subject',
              border: OutlineInputBorder(),
            ),
            textInputAction: TextInputAction.next,
          ),
          const SizedBox(height: 12),
          TextField(
            controller: bodyController,
            decoration: const InputDecoration(
              labelText: 'Message',
              border: OutlineInputBorder(),
              alignLabelWithHint: true,
            ),
            minLines: 4,
            maxLines: 10,
            keyboardType: TextInputType.multiline,
          ),
          if (template.availableFields.isNotEmpty) ...[
            const SizedBox(height: 12),
            Align(
              alignment: Alignment.centerLeft,
              child: Text(
                'Insert a field',
                style: theme.textTheme.labelMedium
                    ?.copyWith(color: cs.onSurfaceVariant),
              ),
            ),
            const SizedBox(height: 6),
            Wrap(
              spacing: 8,
              runSpacing: 4,
              children: [
                for (final field in template.availableFields)
                  ActionChip(
                    label: Text(field),
                    onPressed: () => onInsertField(field),
                  ),
              ],
            ),
          ],
          const SizedBox(height: 16),
          SizedBox(
            width: double.infinity,
            child: FilledButton.icon(
              onPressed: saving ? null : onSave,
              icon: saving
                  ? const SizedBox(
                      width: 18,
                      height: 18,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : const Icon(Icons.save_outlined),
              label: Text(saving ? 'Saving…' : 'Save template'),
            ),
          ),
        ],
      ),
    );
  }
}

/// The single "what happens when a lease is ending" selector, saved through the
/// shared notification-settings flow (same as the recurring auto-send switches).
class _LeaseEndActionCard extends StatelessWidget {
  const _LeaseEndActionCard({
    required this.value,
    required this.enabled,
    required this.options,
    required this.onChanged,
  });

  final String value;
  final bool enabled;
  final List<(String value, String label)> options;
  final ValueChanged<String> onChanged;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    return Card(
      margin: const EdgeInsets.only(bottom: 12),
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              'When a lease is ending, automatically',
              style: theme.textTheme.bodyLarge
                  ?.copyWith(fontWeight: FontWeight.w600),
            ),
            const SizedBox(height: 4),
            Text(
              'Choose what we do as a lease approaches its end date.',
              style: theme.textTheme.bodySmall
                  ?.copyWith(color: cs.onSurfaceVariant),
            ),
            const SizedBox(height: 12),
            DropdownButtonFormField<String>(
              initialValue: value,
              isExpanded: true,
              decoration: const InputDecoration(border: OutlineInputBorder()),
              items: [
                for (final o in options)
                  DropdownMenuItem(value: o.$1, child: Text(o.$2)),
              ],
              onChanged: enabled
                  ? (v) {
                      if (v != null) onChanged(v);
                    }
                  : null,
            ),
          ],
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
