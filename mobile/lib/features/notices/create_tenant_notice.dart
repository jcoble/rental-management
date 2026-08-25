import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../settings/notification_foundation_repository.dart';
import 'notice_content_safety.dart';
import 'notices_models.dart';
import 'notices_repository.dart';

/// The notice types a landlord can create for a single tenant, with friendly
/// labels and a one-line description of what each does.
const _noticeTypeChoices =
    <({String type, String label, String hint, IconData icon})>[
      (
        type: 'rent-reminder',
        label: 'Rent reminder (coming due)',
        hint: 'Friendly heads-up that rent is coming due.',
        icon: Icons.event_available_outlined,
      ),
      (
        type: 'lease-renewal-offer',
        label: 'Lease renewal offer',
        hint: 'Offer to extend the lease for another term.',
        icon: Icons.event_repeat_outlined,
      ),
      (
        type: 'month-to-month-offer',
        label: 'Convert to month-to-month',
        hint: 'Offer to continue month-to-month after the lease ends.',
        icon: Icons.sync_alt_outlined,
      ),
      (
        type: 'lease-non-renewal',
        label: 'Lease expiration / move-out',
        hint: 'Let them know the lease is ending and coordinate move-out.',
        icon: Icons.logout_outlined,
      ),
      (
        type: 'late-rent-late-fee',
        label: 'Late rent / late fee',
        hint: 'Remind the tenant about an overdue balance.',
        icon: Icons.warning_amber_outlined,
      ),
    ];

/// Runs the per-tenant "Create / Send notice" flow from the tenant page:
/// pick a notice type → draft it (scoped to this tenant) → review and send (or
/// keep as a draft). Reuses the existing generate + approve notice endpoints.
Future<void> showCreateTenantNoticeFlow(
  BuildContext context,
  WidgetRef ref, {
  int? recipientTenantId,
  String? initialNoticeType,
  required String tenantName,
}) async {
  final choice =
      initialNoticeType ??
      await showModalBottomSheet<String>(
        context: context,
        isScrollControlled: true,
        shape: const RoundedRectangleBorder(
          borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
        ),
        builder: (sheetCtx) {
          final theme = Theme.of(sheetCtx);
          final maxHeight = MediaQuery.sizeOf(sheetCtx).height * 0.82;
          return SafeArea(
            child: ConstrainedBox(
              constraints: BoxConstraints(maxHeight: maxHeight),
              child: ListView(
                shrinkWrap: true,
                padding: const EdgeInsets.symmetric(vertical: 8),
                children: [
                  Padding(
                    padding: const EdgeInsets.fromLTRB(20, 12, 20, 4),
                    child: Text(
                      'Create notice',
                      style: theme.textTheme.titleLarge?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                  ),
                  Padding(
                    padding: const EdgeInsets.fromLTRB(20, 0, 20, 8),
                    child: Text(
                      'For $tenantName',
                      style: theme.textTheme.bodyMedium?.copyWith(
                        color: theme.colorScheme.onSurfaceVariant,
                      ),
                    ),
                  ),
                  for (final c in _noticeTypeChoices)
                    ListTile(
                      leading: Icon(c.icon),
                      title: Text(c.label),
                      subtitle: Text(c.hint),
                      onTap: () => Navigator.of(sheetCtx).pop(c.type),
                    ),
                ],
              ),
            ),
          );
        },
      );

  if (choice == null || !context.mounted) return;

  final messenger = ScaffoldMessenger.of(context);

  // Generate the draft(s) for this tenant + chosen type.
  List<NoticeDraft> drafts;
  try {
    drafts = await ref
        .read(noticesRepositoryProvider)
        .generateScoped(recipientTenantId, noticeType: choice);
  } on ApiException catch (e) {
    messenger
      ..hideCurrentSnackBar()
      ..showSnackBar(SnackBar(content: Text(e.message)));
    return;
  }

  if (!context.mounted) return;

  if (drafts.isEmpty) {
    final why = switch (choice) {
      'late-rent-late-fee' =>
        'No overdue payment to base a late-rent notice on.',
      'rent-reminder' || 'month-to-month-offer' =>
        'This tenant needs an active lease to create that notice.',
      _ => 'There is already an open draft of this notice for this tenant.',
    };
    messenger
      ..hideCurrentSnackBar()
      ..showSnackBar(SnackBar(content: Text(why)));
    return;
  }

  // Review + send the freshly created draft(s).
  await showModalBottomSheet<void>(
    context: context,
    isScrollControlled: true,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
    ),
    builder: (_) => _ReviewNoticeSheet(drafts: drafts),
  );
}

/// Review sheet for one or more freshly generated drafts — shows the copy and
/// lets the landlord send it now (approve with selected channels) or keep it as
/// a draft for the Notices queue.
class _ReviewNoticeSheet extends ConsumerStatefulWidget {
  const _ReviewNoticeSheet({required this.drafts});

  final List<NoticeDraft> drafts;

  @override
  ConsumerState<_ReviewNoticeSheet> createState() => _ReviewNoticeSheetState();
}

class _ReviewNoticeSheetState extends ConsumerState<_ReviewNoticeSheet> {
  final Map<int, TextEditingController> _subjectCtrls = {};
  final Map<int, TextEditingController> _bodyCtrls = {};
  final Map<int, List<TenantNoticeRecipientPreview>> _recipientPreviews = {};

  bool _portal = true;
  bool _email = true;
  bool _sms = true;
  bool _busy = false;
  String? _error;
  int? _previewingDraftId;

  @override
  void initState() {
    super.initState();
    for (final d in widget.drafts) {
      _subjectCtrls[d.id] = TextEditingController(text: d.subject);
      _bodyCtrls[d.id] = TextEditingController(text: d.body);
    }
  }

  @override
  void dispose() {
    for (final c in _subjectCtrls.values) {
      c.dispose();
    }
    for (final c in _bodyCtrls.values) {
      c.dispose();
    }
    super.dispose();
  }

  List<String> get _channels => [
    if (_portal) 'Portal',
    if (_email) 'Email',
    if (_sms) 'Sms',
  ];

  List<NoticeDraft> get _sendableDrafts =>
      widget.drafts.where((d) => d.status == 'Draft').toList();

  String? get _contentSafetyIssue {
    for (final draft in _sendableDrafts) {
      final issue = noticeContentSafetyIssue(
        subject: _subjectCtrls[draft.id]?.text ?? draft.subject,
        body: _bodyCtrls[draft.id]?.text ?? draft.body,
      );
      if (issue != null) return issue;
    }
    return null;
  }

  Future<void> _send() async {
    final channels = _channels;
    final sendable = _sendableDrafts;
    if (channels.isEmpty || sendable.isEmpty) return;
    setState(() {
      _busy = true;
      _error = null;
    });
    final messenger = ScaffoldMessenger.of(context);
    try {
      for (final d in sendable) {
        final subject = _subjectCtrls[d.id]?.text.trim() ?? d.subject;
        final body = _bodyCtrls[d.id]?.text.trim() ?? d.body;
        if (subject != d.subject || body != d.body) {
          await ref
              .read(noticesRepositoryProvider)
              .update(d.id, subject: subject, body: body);
        }
        await ref.read(noticesRepositoryProvider).approve(d.id, channels);
      }
      // Refresh the standalone notices queue if it's listening.
      ref.invalidate(noticeDraftsProvider);
      if (mounted) Navigator.of(context).pop();
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(const SnackBar(content: Text('Notice sent.')));
    } on ApiException catch (e) {
      setState(() => _error = e.message);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  void _keepAsDraft() {
    // The draft already exists (it was just generated). Just close and point the
    // landlord at the Notices queue.
    ref.invalidate(noticeDraftsProvider);
    Navigator.of(context).pop();
    ScaffoldMessenger.of(context)
      ..hideCurrentSnackBar()
      ..showSnackBar(
        const SnackBar(content: Text('Saved to your Notices queue.')),
      );
  }

  Future<void> _previewRecipients(NoticeDraft draft) async {
    setState(() {
      _previewingDraftId = draft.id;
      _error = null;
    });
    try {
      final recipients = await ref
          .read(notificationFoundationRepositoryProvider)
          .previewTenantNoticeRecipients(
            automationKey: draft.noticeType,
            leaseManagementId: draft.leaseManagementId,
          );
      if (mounted) {
        setState(() => _recipientPreviews[draft.id] = recipients);
      }
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = error.message);
    } finally {
      if (mounted) setState(() => _previewingDraftId = null);
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final bottom = MediaQuery.viewInsetsOf(context).bottom;
    final hasSendableDraft = _sendableDrafts.isNotEmpty;
    final contentSafetyIssue = _contentSafetyIssue;

    return Padding(
      padding: EdgeInsets.fromLTRB(20, 20, 20, 20 + bottom),
      child: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    widget.drafts.length == 1
                        ? 'Review notice'
                        : 'Review ${widget.drafts.length} notices',
                    style: theme.textTheme.titleLarge?.copyWith(
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                ),
                IconButton(
                  icon: const Icon(Icons.close),
                  onPressed: () => Navigator.of(context).pop(),
                ),
              ],
            ),
            const SizedBox(height: 8),
            for (final d in widget.drafts) ...[
              Builder(
                builder: (context) {
                  final editable = d.status == 'Draft';
                  return Card(
                    margin: const EdgeInsets.only(bottom: 8),
                    color: cs.surfaceContainerHighest.withValues(alpha: 0.4),
                    elevation: 0,
                    child: Padding(
                      padding: const EdgeInsets.all(14),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          TextField(
                            controller: _subjectCtrls[d.id],
                            readOnly: !editable,
                            onChanged: editable ? (_) => setState(() {}) : null,
                            decoration: const InputDecoration(
                              labelText: 'Subject',
                              border: OutlineInputBorder(),
                            ),
                          ),
                          const SizedBox(height: 8),
                          TextField(
                            controller: _bodyCtrls[d.id],
                            readOnly: !editable,
                            onChanged: editable ? (_) => setState(() {}) : null,
                            minLines: 4,
                            maxLines: 10,
                            decoration: const InputDecoration(
                              labelText: 'Message',
                              border: OutlineInputBorder(),
                            ),
                          ),
                          const SizedBox(height: 10),
                          Row(
                            children: [
                              Expanded(
                                child: Text(
                                  'Effective recipients and available destinations',
                                  style: theme.textTheme.labelMedium,
                                ),
                              ),
                              OutlinedButton.icon(
                                onPressed: _previewingDraftId == d.id
                                    ? null
                                    : () => _previewRecipients(d),
                                icon: const Icon(Icons.visibility_outlined),
                                label: Text(
                                  _previewingDraftId == d.id
                                      ? 'Loading…'
                                      : 'Preview',
                                ),
                              ),
                            ],
                          ),
                          if (_recipientPreviews[d.id] != null) ...[
                            const SizedBox(height: 6),
                            for (final recipient in _recipientPreviews[d.id]!)
                              ListTile(
                                contentPadding: EdgeInsets.zero,
                                dense: true,
                                title: Text(
                                  '${recipient.displayName} · ${recipient.role}',
                                ),
                                subtitle: Text(
                                  'Channels: ${recipient.availableChannels.isEmpty ? 'None' : recipient.availableChannels.join(', ')}\n'
                                  'Email: ${recipient.email ?? 'Not available'} · Phone: ${recipient.phone ?? 'Not available'}\n'
                                  '${recipient.reason}',
                                ),
                                trailing: Text(
                                  recipient.eligible ? 'Eligible' : 'Excluded',
                                  style: theme.textTheme.labelSmall?.copyWith(
                                    color: recipient.eligible
                                        ? Colors.green
                                        : cs.onSurfaceVariant,
                                  ),
                                ),
                                isThreeLine: true,
                              ),
                          ],
                          if (!editable) ...[
                            const SizedBox(height: 8),
                            Text(
                              'This notice already exists and will not be sent again.',
                              style: theme.textTheme.bodySmall?.copyWith(
                                color: cs.onSurfaceVariant,
                              ),
                            ),
                          ],
                        ],
                      ),
                    ),
                  );
                },
              ),
            ],
            if (hasSendableDraft) ...[
              const SizedBox(height: 4),
              Text(
                'Send via',
                style: theme.textTheme.labelMedium?.copyWith(
                  color: cs.onSurfaceVariant,
                ),
              ),
              const SizedBox(height: 4),
              Wrap(
                spacing: 6,
                children: [
                  FilterChip(
                    label: const Text('Portal'),
                    selected: _portal,
                    onSelected: _busy
                        ? null
                        : (v) => setState(() => _portal = v),
                  ),
                  FilterChip(
                    label: const Text('Email'),
                    selected: _email,
                    onSelected: _busy
                        ? null
                        : (v) => setState(() => _email = v),
                  ),
                  FilterChip(
                    label: const Text('SMS'),
                    selected: _sms,
                    onSelected: _busy ? null : (v) => setState(() => _sms = v),
                  ),
                ],
              ),
            ],
            if (_error != null) ...[
              const SizedBox(height: 12),
              Text(_error!, style: TextStyle(color: cs.error, fontSize: 13)),
            ],
            if (contentSafetyIssue != null) ...[
              const SizedBox(height: 12),
              Text(
                contentSafetyIssue,
                style: TextStyle(color: cs.error, fontSize: 13),
              ),
            ],
            const SizedBox(height: 16),
            FilledButton.icon(
              onPressed:
                  (_busy ||
                      _channels.isEmpty ||
                      !hasSendableDraft ||
                      contentSafetyIssue != null)
                  ? null
                  : _send,
              icon: _busy
                  ? const SizedBox(
                      height: 18,
                      width: 18,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : const Icon(Icons.send_outlined),
              label: Text(hasSendableDraft ? 'Send notice' : 'Already sent'),
            ),
            if (hasSendableDraft) ...[
              const SizedBox(height: 8),
              TextButton(
                onPressed: _busy ? null : _keepAsDraft,
                child: const Text('Keep as draft'),
              ),
            ],
          ],
        ),
      ),
    );
  }
}
