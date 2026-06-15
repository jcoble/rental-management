import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import 'notices_models.dart';
import 'notices_repository.dart';

/// The notice types a landlord can create for a single tenant, with friendly
/// labels and a one-line description of what each does.
const _noticeTypeChoices = <({String type, String label, String hint, IconData icon})>[
  (
    type: 'RenewalOffer',
    label: 'Lease renewal offer',
    hint: 'Offer to extend the lease for another term.',
    icon: Icons.event_repeat_outlined,
  ),
  (
    type: 'MoveOutReminder',
    label: 'Move-out reminder',
    hint: 'Coordinate keys, inspection, and deposit return.',
    icon: Icons.logout_outlined,
  ),
  (
    type: 'LateRentNotice',
    label: 'Late-rent notice',
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
  required int tenantId,
  required String tenantName,
}) async {
  final choice = await showModalBottomSheet<String>(
    context: context,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
    ),
    builder: (sheetCtx) {
      final theme = Theme.of(sheetCtx);
      return SafeArea(
        child: Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Padding(
                padding: const EdgeInsets.fromLTRB(20, 12, 20, 4),
                child: Text(
                  'Create notice',
                  style: theme.textTheme.titleLarge
                      ?.copyWith(fontWeight: FontWeight.w700),
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
        .generateForTenant(tenantId, noticeType: choice);
  } on ApiException catch (e) {
    messenger
      ..hideCurrentSnackBar()
      ..showSnackBar(SnackBar(content: Text(e.message)));
    return;
  }

  if (!context.mounted) return;

  if (drafts.isEmpty) {
    final why = choice == 'LateRentNotice'
        ? 'No overdue payment to base a late-rent notice on.'
        : 'There is already an open draft of this notice for this tenant.';
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
  ConsumerState<_ReviewNoticeSheet> createState() =>
      _ReviewNoticeSheetState();
}

class _ReviewNoticeSheetState extends ConsumerState<_ReviewNoticeSheet> {
  bool _portal = true;
  bool _email = true;
  bool _sms = true;
  bool _busy = false;
  String? _error;

  List<String> get _channels => [
        if (_portal) 'Portal',
        if (_email) 'Email',
        if (_sms) 'Sms',
      ];

  Future<void> _send() async {
    final channels = _channels;
    if (channels.isEmpty) return;
    setState(() {
      _busy = true;
      _error = null;
    });
    final messenger = ScaffoldMessenger.of(context);
    try {
      for (final d in widget.drafts) {
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

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final bottom = MediaQuery.viewInsetsOf(context).bottom;

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
                    style: theme.textTheme.titleLarge
                        ?.copyWith(fontWeight: FontWeight.w700),
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
              Card(
                margin: const EdgeInsets.only(bottom: 8),
                color: cs.surfaceContainerHighest.withValues(alpha: 0.4),
                elevation: 0,
                child: Padding(
                  padding: const EdgeInsets.all(14),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        d.subject,
                        style: theme.textTheme.bodyMedium
                            ?.copyWith(fontWeight: FontWeight.w700),
                      ),
                      const SizedBox(height: 6),
                      Text(
                        d.body,
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: cs.onSurfaceVariant,
                          height: 1.4,
                        ),
                      ),
                    ],
                  ),
                ),
              ),
            ],
            const SizedBox(height: 4),
            Text(
              'Send via',
              style: theme.textTheme.labelMedium
                  ?.copyWith(color: cs.onSurfaceVariant),
            ),
            const SizedBox(height: 4),
            Wrap(
              spacing: 6,
              children: [
                FilterChip(
                  label: const Text('Portal'),
                  selected: _portal,
                  onSelected:
                      _busy ? null : (v) => setState(() => _portal = v),
                ),
                FilterChip(
                  label: const Text('Email'),
                  selected: _email,
                  onSelected: _busy ? null : (v) => setState(() => _email = v),
                ),
                FilterChip(
                  label: const Text('SMS'),
                  selected: _sms,
                  onSelected: _busy ? null : (v) => setState(() => _sms = v),
                ),
              ],
            ),
            if (_error != null) ...[
              const SizedBox(height: 12),
              Text(
                _error!,
                style: TextStyle(color: cs.error, fontSize: 13),
              ),
            ],
            const SizedBox(height: 16),
            FilledButton.icon(
              onPressed: (_busy || _channels.isEmpty) ? null : _send,
              icon: _busy
                  ? const SizedBox(
                      height: 18,
                      width: 18,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : const Icon(Icons.send_outlined),
              label: const Text('Send notice'),
            ),
            const SizedBox(height: 8),
            TextButton(
              onPressed: _busy ? null : _keepAsDraft,
              child: const Text('Keep as draft'),
            ),
          ],
        ),
      ),
    );
  }
}
