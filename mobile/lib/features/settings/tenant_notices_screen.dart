import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import 'notification_help_action.dart';
import 'notification_foundation_repository.dart';

class TenantNoticesScreen extends ConsumerStatefulWidget {
  const TenantNoticesScreen({super.key});

  @override
  ConsumerState<TenantNoticesScreen> createState() =>
      _TenantNoticesScreenState();
}

class _TenantNoticesScreenState extends ConsumerState<TenantNoticesScreen> {
  late Future<List<TenantNoticePolicy>> _policies;
  late Future<List<NoticeDeliveryStatus>> _deliveries;

  NotificationFoundationRepository get _repository =>
      ref.read(notificationFoundationRepositoryProvider);

  @override
  void initState() {
    super.initState();
    _reloadAll();
  }

  void _reloadAll() {
    _policies = _repository.listTenantNoticePolicies();
    _deliveries = _repository.listTenantNoticeDeliveries(take: 50);
  }

  void _reloadPolicies() {
    _policies = _repository.listTenantNoticePolicies();
  }

  void _reloadDeliveries() {
    _deliveries = _repository.listTenantNoticeDeliveries(take: 50);
  }

  Future<void> _editPolicy(TenantNoticePolicy policy) async {
    final saved = await showModalBottomSheet<bool>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      builder: (_) => _TenantPolicyEditorSheet(policy: policy),
    );
    if (saved == true && mounted) setState(_reloadPolicies);
  }

  Future<void> _editTemplate(TenantNoticePolicy policy) async {
    final saved = await showModalBottomSheet<bool>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      builder: (_) => _TenantTemplateEditorSheet(policy: policy),
    );
    if (saved == true && mounted) setState(_reloadPolicies);
  }

  @override
  Widget build(BuildContext context) {
    return DefaultTabController(
      length: 2,
      child: Scaffold(
        appBar: AppBar(
          title: const Text('Tenant notices'),
          actions: const [NotificationHelpAction()],
          bottom: const TabBar(
            tabs: [
              Tab(text: 'Automations'),
              Tab(text: 'Delivery status'),
            ],
          ),
        ),
        body: TabBarView(
          children: [
            _PoliciesTab(
              future: _policies,
              onRetry: () => setState(_reloadPolicies),
              onEditPolicy: _editPolicy,
              onEditTemplate: _editTemplate,
            ),
            _DeliveriesTab(
              future: _deliveries,
              onRetry: () => setState(_reloadDeliveries),
            ),
          ],
        ),
      ),
    );
  }
}

class _PoliciesTab extends StatelessWidget {
  const _PoliciesTab({
    required this.future,
    required this.onRetry,
    required this.onEditPolicy,
    required this.onEditTemplate,
  });

  final Future<List<TenantNoticePolicy>> future;
  final VoidCallback onRetry;
  final ValueChanged<TenantNoticePolicy> onEditPolicy;
  final ValueChanged<TenantNoticePolicy> onEditTemplate;

  @override
  Widget build(BuildContext context) => FutureBuilder<List<TenantNoticePolicy>>(
    future: future,
    builder: (context, snapshot) {
      if (snapshot.connectionState == ConnectionState.waiting) {
        return const Center(child: CircularProgressIndicator());
      }
      if (snapshot.hasError) {
        return _NoticeError(error: snapshot.error!, onRetry: onRetry);
      }
      final policies = snapshot.data ?? const <TenantNoticePolicy>[];
      if (policies.length != 5) {
        return _MissingNotices(onRetry: onRetry);
      }
      return ListView(
        padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
        children: [
          const _ModeExplanation(),
          const SizedBox(height: 12),
          Text(
            'Each server-shaped row contains its automation and currently '
            'bound immutable template. Tenant channels never inherit from '
            'staff My alerts or Team routing.',
            style: Theme.of(context).textTheme.bodySmall?.copyWith(
              color: Theme.of(context).colorScheme.onSurfaceVariant,
            ),
          ),
          const SizedBox(height: 12),
          for (final policy in policies)
            _PolicyCard(
              policy: policy,
              onEditPolicy: () => onEditPolicy(policy),
              onEditTemplate: () => onEditTemplate(policy),
            ),
        ],
      );
    },
  );
}

class _PolicyCard extends StatelessWidget {
  const _PolicyCard({
    required this.policy,
    required this.onEditPolicy,
    required this.onEditTemplate,
  });

  final TenantNoticePolicy policy;
  final VoidCallback onEditPolicy;
  final VoidCallback onEditTemplate;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card(
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
                        _automationLabel(policy.automationKey),
                        style: theme.textTheme.titleSmall,
                      ),
                      const SizedBox(height: 4),
                      Text(_automationDetail(policy.automationKey)),
                    ],
                  ),
                ),
                _StatusChip(label: _modeLabel(policy.mode)),
              ],
            ),
            const SizedBox(height: 12),
            Text(
              '${policy.classification} · ${policy.leadDays} lead days · '
              '${_channelSummary(policy)}',
              style: theme.textTheme.bodySmall?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 5),
            Text(
              'Template v${policy.templateVersion}: '
              '${policy.templateSubject}',
              maxLines: 2,
              overflow: TextOverflow.ellipsis,
            ),
            const SizedBox(height: 3),
            Text(policy.templateProvenance, style: theme.textTheme.bodySmall),
            if (policy.templateUpdateAvailable)
              const Padding(
                padding: EdgeInsets.only(top: 4),
                child: Text('A newer supplied baseline is available.'),
              ),
            const SizedBox(height: 12),
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                FilledButton.tonalIcon(
                  onPressed: onEditPolicy,
                  icon: const Icon(Icons.tune),
                  label: const Text('Edit automation'),
                ),
                OutlinedButton.icon(
                  onPressed: onEditTemplate,
                  icon: const Icon(Icons.edit_note),
                  label: const Text('Review template'),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

class _TenantPolicyEditorSheet extends ConsumerStatefulWidget {
  const _TenantPolicyEditorSheet({required this.policy});

  final TenantNoticePolicy policy;

  @override
  ConsumerState<_TenantPolicyEditorSheet> createState() =>
      _TenantPolicyEditorSheetState();
}

class _TenantPolicyEditorSheetState
    extends ConsumerState<_TenantPolicyEditorSheet> {
  late String _mode;
  late String _failureBehavior;
  late bool _sendTenantPortal;
  late bool _sendMobilePush;
  late bool _sendEmail;
  late bool _sendSms;
  late bool _includePrimaryTenant;
  late bool _includeCoTenant;
  late bool _includeEligibleGuarantor;
  late bool _includeOccupant;
  late final TextEditingController _leadDays;
  late final TextEditingController _sendHour;
  bool _saving = false;

  @override
  void initState() {
    super.initState();
    final policy = widget.policy;
    _mode = policy.mode;
    _failureBehavior = policy.failureBehavior;
    _sendTenantPortal = policy.sendTenantPortal;
    _sendMobilePush = policy.sendMobilePush;
    _sendEmail = policy.sendEmail;
    _sendSms = policy.sendSms;
    _includePrimaryTenant = policy.includePrimaryTenant;
    _includeCoTenant = policy.includeCoTenant;
    _includeEligibleGuarantor = policy.includeEligibleGuarantor;
    _includeOccupant = policy.includeOccupant;
    _leadDays = TextEditingController(text: policy.leadDays.toString());
    _sendHour = TextEditingController(text: policy.sendHourLocal.toString());
  }

  @override
  void dispose() {
    _leadDays.dispose();
    _sendHour.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    final leadDays = int.tryParse(_leadDays.text);
    final sendHour = int.tryParse(_sendHour.text);
    if (leadDays == null || leadDays < 0 || leadDays > 365) {
      _showMessage('Lead days must be between 0 and 365.');
      return;
    }
    if (sendHour == null || sendHour < 0 || sendHour > 23) {
      _showMessage('Local send hour must be between 0 and 23.');
      return;
    }
    setState(() => _saving = true);
    try {
      final policy = widget.policy;
      await ref
          .read(notificationFoundationRepositoryProvider)
          .updateTenantNoticePolicy(
            policy,
            policy.toUpdateJson(
              updatedMode: _mode,
              updatedLeadDays: leadDays,
              updatedSendHourLocal: sendHour,
              updatedSendTenantPortal: _sendTenantPortal,
              updatedSendMobilePush: _sendMobilePush,
              updatedSendEmail: _sendEmail,
              updatedSendSms: _sendSms,
              updatedIncludePrimaryTenant: _includePrimaryTenant,
              updatedIncludeCoTenant: _includeCoTenant,
              updatedIncludeEligibleGuarantor: _includeEligibleGuarantor,
              updatedIncludeOccupant: _includeOccupant,
              updatedFailureBehavior: _failureBehavior,
            ),
          );
      if (mounted) Navigator.of(context).pop(true);
    } catch (error) {
      if (mounted) _showError(error, 'We couldn\'t save this automation.');
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final policy = widget.policy;
    final legal = policy.classification == 'Legal';
    return Scaffold(
      appBar: AppBar(
        title: Text(_automationLabel(policy.automationKey)),
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
          Text(_automationDetail(policy.automationKey)),
          const SizedBox(height: 16),
          Text(
            'Automation mode',
            style: Theme.of(context).textTheme.titleSmall,
          ),
          const SizedBox(height: 8),
          SegmentedButton<String>(
            segments: [
              const ButtonSegment(value: 'Off', label: Text('Off')),
              const ButtonSegment(value: 'Draft', label: Text('Draft')),
              const ButtonSegment(value: 'Auto', label: Text('Automatic')),
            ],
            selected: {_mode},
            onSelectionChanged: (values) {
              final nextMode = values.single;
              if (nextMode == 'Auto' && !policy.automaticDeliveryIsEligible) {
                return;
              }
              setState(() => _mode = nextMode);
            },
          ),
          if (legal && !policy.automaticDeliveryIsEligible)
            Padding(
              padding: const EdgeInsets.only(top: 8),
              child: Text(
                'Automatic delivery stays unavailable until the bound '
                'template is jurisdiction-reviewed.',
                style: TextStyle(color: Theme.of(context).colorScheme.error),
              ),
            ),
          const SizedBox(height: 20),
          Row(
            children: [
              Expanded(
                child: TextField(
                  controller: _leadDays,
                  keyboardType: TextInputType.number,
                  decoration: const InputDecoration(labelText: 'Lead days'),
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: TextField(
                  controller: _sendHour,
                  keyboardType: TextInputType.number,
                  decoration: const InputDecoration(
                    labelText: 'Local send hour',
                  ),
                ),
              ),
            ],
          ),
          const SizedBox(height: 20),
          Text(
            'Tenant delivery channels',
            style: Theme.of(context).textTheme.titleSmall,
          ),
          _Check(
            label: 'Portal / in-app',
            value: _sendTenantPortal,
            onChanged: (value) => setState(() => _sendTenantPortal = value),
          ),
          _Check(
            label: 'Mobile push',
            value: _sendMobilePush,
            onChanged: (value) => setState(() => _sendMobilePush = value),
          ),
          _Check(
            label: 'Email',
            value: _sendEmail,
            onChanged: (value) => setState(() => _sendEmail = value),
          ),
          _Check(
            label: 'SMS',
            value: _sendSms,
            onChanged: (value) => setState(() => _sendSms = value),
          ),
          const SizedBox(height: 12),
          Text(
            'Eligible relationship roles',
            style: Theme.of(context).textTheme.titleSmall,
          ),
          _Check(
            label: 'Effective primary tenant',
            value: _includePrimaryTenant,
            onChanged: (value) => setState(() => _includePrimaryTenant = value),
          ),
          _Check(
            label: 'Effective co-tenant',
            value: _includeCoTenant,
            onChanged: (value) => setState(() => _includeCoTenant = value),
          ),
          _Check(
            label: 'Explicitly eligible guarantor',
            value: _includeEligibleGuarantor,
            onChanged: (value) =>
                setState(() => _includeEligibleGuarantor = value),
          ),
          _Check(
            label: legal
                ? 'Occupant (not eligible for legal notices)'
                : 'Occupant',
            value: legal ? false : _includeOccupant,
            onChanged: legal
                ? null
                : (value) => setState(() => _includeOccupant = value),
          ),
          const SizedBox(height: 12),
          DropdownButtonFormField<String>(
            initialValue: _failureBehavior,
            decoration: const InputDecoration(
              labelText: 'If delivery cannot complete',
            ),
            items: const [
              DropdownMenuItem(
                value: 'StopAndRequireReview',
                child: Text('Stop and require review'),
              ),
              DropdownMenuItem(
                value: 'RetryThenDraft',
                child: Text('Retry, then keep a draft'),
              ),
              DropdownMenuItem(
                value: 'RetryThenFail',
                child: Text('Retry, then mark failed'),
              ),
            ],
            onChanged: (value) {
              if (value != null) setState(() => _failureBehavior = value);
            },
          ),
          const SizedBox(height: 24),
          _SaveButton(saving: _saving, label: 'Save automation', onSave: _save),
        ],
      ),
    );
  }

  void _showError(Object error, String fallback) {
    _showMessage(error is ApiException ? error.message : fallback);
  }

  void _showMessage(String message) {
    ScaffoldMessenger.of(
      context,
    ).showSnackBar(SnackBar(content: Text(message)));
  }
}

class _TenantTemplateEditorSheet extends ConsumerStatefulWidget {
  const _TenantTemplateEditorSheet({required this.policy});

  final TenantNoticePolicy policy;

  @override
  ConsumerState<_TenantTemplateEditorSheet> createState() =>
      _TenantTemplateEditorSheetState();
}

class _TenantTemplateEditorSheetState
    extends ConsumerState<_TenantTemplateEditorSheet> {
  late final TextEditingController _subject;
  late final TextEditingController _body;
  late final TextEditingController _jurisdiction;
  late bool _confirmed;
  bool _saving = false;
  bool _restoring = false;

  @override
  void initState() {
    super.initState();
    final policy = widget.policy;
    _subject = TextEditingController(text: policy.templateSubject);
    _body = TextEditingController(text: policy.templateBody);
    _jurisdiction = TextEditingController(
      text: policy.templateJurisdictionCode ?? '',
    );
    _confirmed = policy.templateJurisdictionReviewedAtUtc != null;
  }

  @override
  void dispose() {
    _subject.dispose();
    _body.dispose();
    _jurisdiction.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    if (_subject.text.trim().isEmpty || _body.text.trim().isEmpty) {
      _showMessage('Add a subject and message first.');
      return;
    }
    if (widget.policy.classification == 'Legal' &&
        (_jurisdiction.text.trim().isEmpty || !_confirmed)) {
      _showMessage('Confirm the jurisdiction review before saving.');
      return;
    }
    setState(() => _saving = true);
    try {
      await ref
          .read(notificationFoundationRepositoryProvider)
          .createTenantNoticeTemplateVersion(
            systemKey: widget.policy.templateSystemKey,
            subject: _subject.text.trim(),
            body: _body.text.trim(),
            jurisdictionCode: _jurisdiction.text.trim().isEmpty
                ? null
                : _jurisdiction.text.trim(),
            confirmJurisdictionReviewed: _confirmed,
          );
      if (mounted) Navigator.of(context).pop(true);
    } catch (error) {
      if (mounted) _showError(error, 'We couldn\'t save this template.');
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  Future<void> _restore() async {
    setState(() => _restoring = true);
    try {
      await ref
          .read(notificationFoundationRepositoryProvider)
          .restoreTenantNoticeTemplate(widget.policy.templateSystemKey);
      if (mounted) Navigator.of(context).pop(true);
    } catch (error) {
      if (mounted) _showError(error, 'We couldn\'t restore the template.');
    } finally {
      if (mounted) setState(() => _restoring = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final policy = widget.policy;
    final legal = policy.classification == 'Legal';
    return Scaffold(
      appBar: AppBar(
        title: Text(_automationLabel(policy.automationKey)),
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
            'Template v${policy.templateVersion} is bound to this automation. '
            'Saving appends an immutable version and atomically makes it the '
            'bound version; prior versions remain in history.',
          ),
          const SizedBox(height: 8),
          Text(
            policy.templateIsCustomized
                ? '${policy.templateProvenance}. Your edits are stored as a '
                      'new workspace version.'
                : '${policy.templateProvenance}. This complete supplied copy '
                      'can be edited without changing the Rental Command default.',
            style: Theme.of(context).textTheme.bodySmall,
          ),
          if (policy.templateUpdateAvailable)
            const Padding(
              padding: EdgeInsets.only(top: 6),
              child: Text('A newer supplied baseline is available.'),
            ),
          const SizedBox(height: 16),
          TextField(
            controller: _subject,
            decoration: const InputDecoration(labelText: 'Subject'),
          ),
          const SizedBox(height: 12),
          TextField(
            controller: _body,
            minLines: 6,
            maxLines: null,
            decoration: const InputDecoration(
              labelText: 'Message',
              alignLabelWithHint: true,
            ),
          ),
          const SizedBox(height: 8),
          Text(
            'Merge fields such as {{tenant_name}} and {{property_address}} '
            'fill from the notice\'s Lease Management context.',
            style: Theme.of(context).textTheme.bodySmall,
          ),
          if (legal) ...[
            const SizedBox(height: 16),
            TextField(
              controller: _jurisdiction,
              decoration: const InputDecoration(
                labelText: 'Reviewed jurisdiction',
                hintText: 'State or local code',
              ),
            ),
            CheckboxListTile(
              contentPadding: EdgeInsets.zero,
              value: _confirmed,
              title: const Text(
                'I reviewed this template for that jurisdiction',
              ),
              onChanged: (value) => setState(() => _confirmed = value ?? false),
            ),
          ],
          const SizedBox(height: 24),
          _SaveButton(
            saving: _saving,
            label: 'Save and use new version',
            onSave: _restoring ? null : _save,
          ),
          TextButton(
            onPressed: _saving || _restoring ? null : _restore,
            child: Text(
              _restoring ? 'Restoring…' : 'Restore supplied default and use it',
            ),
          ),
        ],
      ),
    );
  }

  void _showError(Object error, String fallback) {
    _showMessage(error is ApiException ? error.message : fallback);
  }

  void _showMessage(String message) {
    ScaffoldMessenger.of(
      context,
    ).showSnackBar(SnackBar(content: Text(message)));
  }
}

class _DeliveriesTab extends StatelessWidget {
  const _DeliveriesTab({required this.future, required this.onRetry});

  final Future<List<NoticeDeliveryStatus>> future;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) =>
      FutureBuilder<List<NoticeDeliveryStatus>>(
        future: future,
        builder: (context, snapshot) {
          if (snapshot.connectionState == ConnectionState.waiting) {
            return const Center(child: CircularProgressIndicator());
          }
          if (snapshot.hasError) {
            return _NoticeError(error: snapshot.error!, onRetry: onRetry);
          }
          final deliveries = snapshot.data ?? const <NoticeDeliveryStatus>[];
          return RefreshIndicator(
            onRefresh: () async => onRetry(),
            child: ListView(
              physics: const AlwaysScrollableScrollPhysics(),
              padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
              children: [
                Text(
                  'The 50 most recent records are returned in server order, '
                  'so the status below reflects the durable delivery queue.',
                  style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                    color: Theme.of(context).colorScheme.onSurfaceVariant,
                  ),
                ),
                const SizedBox(height: 12),
                if (deliveries.isEmpty)
                  const Card(
                    child: Padding(
                      padding: EdgeInsets.all(20),
                      child: Text('No tenant notice deliveries yet.'),
                    ),
                  )
                else
                  for (final delivery in deliveries)
                    _DeliveryCard(delivery: delivery),
              ],
            ),
          );
        },
      );
}

class _DeliveryCard extends StatelessWidget {
  const _DeliveryCard({required this.delivery});

  final NoticeDeliveryStatus delivery;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card(
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
                  child: Text(
                    delivery.subject,
                    style: theme.textTheme.titleSmall,
                  ),
                ),
                _StatusChip(label: delivery.status),
              ],
            ),
            const SizedBox(height: 8),
            Text(
              _deliveryStatusExplanation(delivery.status),
              style: theme.textTheme.bodySmall?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 8),
            Text(
              '${_recipientRoleLabel(delivery.recipientRole)} · '
              '${_channelLabel(delivery.channel)} · ${delivery.destination}',
            ),
            const SizedBox(height: 5),
            Text(
              'Attempts: ${delivery.attemptCount} · '
              '${_shortDate(delivery.createdAtUtc)}',
              style: theme.textTheme.bodySmall?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
            ),
            if (delivery.nextAttemptAtUtc case final next?)
              Text('Next retry: ${_shortDate(next)}'),
            if (delivery.lastError case final error?) ...[
              const SizedBox(height: 6),
              Text(error, style: TextStyle(color: theme.colorScheme.error)),
            ],
          ],
        ),
      ),
    );
  }
}

class _Check extends StatelessWidget {
  const _Check({
    required this.label,
    required this.value,
    required this.onChanged,
  });

  final String label;
  final bool value;
  final ValueChanged<bool>? onChanged;

  @override
  Widget build(BuildContext context) => CheckboxListTile(
    value: value,
    title: Text(label),
    onChanged: onChanged == null ? null : (value) => onChanged!(value ?? false),
  );
}

class _SaveButton extends StatelessWidget {
  const _SaveButton({
    required this.saving,
    required this.label,
    required this.onSave,
  });

  final bool saving;
  final String label;
  final VoidCallback? onSave;

  @override
  Widget build(BuildContext context) => FilledButton.icon(
    onPressed: saving ? null : onSave,
    icon: saving
        ? const SizedBox.square(
            dimension: 18,
            child: CircularProgressIndicator(strokeWidth: 2),
          )
        : const Icon(Icons.save_outlined),
    label: Text(saving ? 'Saving…' : label),
  );
}

class _ModeExplanation extends StatelessWidget {
  const _ModeExplanation();

  @override
  Widget build(BuildContext context) => const Card(
    child: Padding(
      padding: EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text('Off — no draft or delivery is created.'),
          SizedBox(height: 6),
          Text('Draft — an authorized team member reviews it first.'),
          SizedBox(height: 6),
          Text('Automatic — sends only when policy and legal review allow.'),
        ],
      ),
    ),
  );
}

class _MissingNotices extends StatelessWidget {
  const _MissingNotices({required this.onRetry});

  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) => Center(
    child: Padding(
      padding: const EdgeInsets.all(24),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          const Icon(Icons.campaign_outlined, size: 40),
          const SizedBox(height: 12),
          const Text(
            'Every new workspace receives five complete editable templates '
            'and safe Draft-for-review policies. Reload; if they are still '
            'missing, workspace setup needs attention.',
            textAlign: TextAlign.center,
          ),
          const SizedBox(height: 16),
          OutlinedButton(
            onPressed: onRetry,
            child: const Text('Reload notices'),
          ),
        ],
      ),
    ),
  );
}

class _NoticeError extends StatelessWidget {
  const _NoticeError({required this.error, required this.onRetry});

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
                : 'We couldn\'t load tenant notice settings.',
            textAlign: TextAlign.center,
          ),
          const SizedBox(height: 12),
          OutlinedButton(onPressed: onRetry, child: const Text('Retry')),
        ],
      ),
    ),
  );
}

class _StatusChip extends StatelessWidget {
  const _StatusChip({required this.label});

  final String label;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
    decoration: BoxDecoration(
      color: Theme.of(context).colorScheme.secondaryContainer,
      borderRadius: BorderRadius.circular(999),
    ),
    child: Text(label, style: Theme.of(context).textTheme.labelSmall),
  );
}

String _automationLabel(String key) => switch (key) {
  'rent-reminder' => 'Rent reminder',
  'lease-renewal-offer' => 'Lease renewal offer',
  'month-to-month-offer' => 'Month-to-month offer',
  'lease-non-renewal' => 'Lease expiration / non-renewal',
  'late-rent-late-fee' => 'Past-due rent / late fee',
  _ => key,
};

String _automationDetail(String key) => switch (key) {
  'rent-reminder' => 'A courtesy reminder before rent is due.',
  'lease-renewal-offer' =>
    'An offer after the lease-ending decision is Offer renewal.',
  'month-to-month-offer' =>
    'An offer after the lease-ending decision is Offer month-to-month.',
  'lease-non-renewal' =>
    'A legal notice after the decision is Non-renewal / move-out.',
  'late-rent-late-fee' =>
    'A legal notice when rent remains past due and fee facts are known.',
  _ => '',
};

String _modeLabel(String mode) => switch (mode) {
  'Off' => 'Off',
  'Auto' => 'Automatic',
  _ => 'Draft',
};

String _channelSummary(TenantNoticePolicy policy) {
  final labels = <String>[];
  if (policy.sendTenantPortal) labels.add('Portal');
  if (policy.sendMobilePush) labels.add('Push');
  if (policy.sendEmail) labels.add('Email');
  if (policy.sendSms) labels.add('SMS');
  return labels.isEmpty ? 'No delivery channels' : labels.join(', ');
}

String _recipientRoleLabel(String role) => switch (role) {
  'PrimaryTenant' => 'Primary tenant',
  'CoTenant' => 'Co-tenant',
  'Guarantor' => 'Guarantor',
  'Occupant' => 'Occupant',
  _ => role,
};

String _channelLabel(String channel) => switch (channel) {
  'TenantPortal' => 'Portal / in-app',
  'MobilePush' => 'Mobile push',
  'Sms' => 'SMS',
  _ => channel,
};

String _deliveryStatusExplanation(String status) => switch (status) {
  'Queued' => 'Waiting for the delivery worker to make the first attempt.',
  'Accepted' =>
    'The provider accepted the message; final delivery is not confirmed yet.',
  'Retrying' =>
    'A provider attempt failed temporarily and another attempt is scheduled.',
  'Delivered' => 'The provider confirmed delivery.',
  'Failed' => 'Delivery retries ended. Review the error and recipient details.',
  _ => 'Current status reported by the delivery provider.',
};

String _shortDate(DateTime value) {
  final local = value.toLocal();
  final minute = local.minute.toString().padLeft(2, '0');
  return '${local.month}/${local.day}/${local.year} '
      '${local.hour}:$minute';
}
