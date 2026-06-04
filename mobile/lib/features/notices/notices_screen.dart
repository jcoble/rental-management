import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import 'notices_models.dart';
import 'notices_repository.dart';

class NoticesScreen extends ConsumerWidget {
  const NoticesScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final draftsAsync = ref.watch(noticeDraftsProvider);

    Future<void> refresh() async => ref.invalidate(noticeDraftsProvider);

    return Scaffold(
      appBar: AppBar(
        title: const Text('Notices'),
        actions: [
          IconButton(
            tooltip: 'Generate drafts',
            icon: const Icon(Icons.refresh),
            onPressed: () async {
              await ref.read(noticesRepositoryProvider).generate();
              ref.invalidate(noticeDraftsProvider);
            },
          ),
        ],
      ),
      body: RefreshIndicator(
        onRefresh: refresh,
        child: draftsAsync.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (e, _) => _ErrorBody(message: e is ApiException ? e.message : e.toString()),
          data: (drafts) {
            if (drafts.isEmpty) return const _EmptyBody();
            return ListView.separated(
              physics: const AlwaysScrollableScrollPhysics(),
              padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
              itemCount: drafts.length,
              separatorBuilder: (_, _) => const SizedBox(height: 8),
              itemBuilder: (_, i) => _NoticeCard(draft: drafts[i]),
            );
          },
        ),
      ),
    );
  }
}

class _NoticeCard extends ConsumerStatefulWidget {
  const _NoticeCard({required this.draft});

  final NoticeDraft draft;

  @override
  ConsumerState<_NoticeCard> createState() => _NoticeCardState();
}

class _NoticeCardState extends ConsumerState<_NoticeCard> {
  // Per-channel approve selection, mirroring the web notices page (all three on by default).
  bool _portal = true;
  bool _email = true;
  bool _sms = true;
  bool _busy = false;

  NoticeDraft get draft => widget.draft;

  List<String> get _selectedChannels => [
        if (_portal) 'Portal',
        if (_email) 'Email',
        if (_sms) 'Sms',
      ];

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final channels = _selectedChannels;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(14),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Expanded(
                  child: Text(
                    '${_label(draft.noticeType)} - ${draft.tenantName}',
                    style: theme.textTheme.titleSmall?.copyWith(fontWeight: FontWeight.w700),
                  ),
                ),
                _StatusChip(status: draft.status),
              ],
            ),
            const SizedBox(height: 4),
            Text(
              [
                if (draft.propertyName != null) draft.propertyName!,
                if (draft.unitNumber != null) 'Unit ${draft.unitNumber}',
                draft.reason,
              ].join(' / '),
              style: theme.textTheme.bodySmall?.copyWith(color: cs.onSurfaceVariant),
            ),
            const SizedBox(height: 10),
            Text(draft.subject, style: theme.textTheme.bodyMedium?.copyWith(fontWeight: FontWeight.w600)),
            const SizedBox(height: 4),
            Text(
              draft.body,
              style: theme.textTheme.bodySmall?.copyWith(color: cs.onSurfaceVariant, height: 1.4),
            ),
            if (draft.status == 'Draft') ...[
              const SizedBox(height: 10),
              Text(
                'Send via',
                style: theme.textTheme.labelSmall?.copyWith(color: cs.onSurfaceVariant),
              ),
              Wrap(
                spacing: 4,
                children: [
                  FilterChip(
                    label: const Text('Portal'),
                    selected: _portal,
                    onSelected: _busy ? null : (v) => setState(() => _portal = v),
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
            ],
            const SizedBox(height: 12),
            Row(
              children: [
                Expanded(
                  child: FilledButton.tonalIcon(
                    onPressed: (_busy || channels.isEmpty) ? null : _approve,
                    icon: const Icon(Icons.send_outlined),
                    label: const Text('Approve'),
                  ),
                ),
                const SizedBox(width: 8),
                IconButton.outlined(
                  tooltip: 'Dismiss',
                  onPressed: _busy ? null : _dismiss,
                  icon: const Icon(Icons.close),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }

  Future<void> _approve() async {
    final channels = _selectedChannels;
    if (channels.isEmpty) return;
    setState(() => _busy = true);
    try {
      await ref.read(noticesRepositoryProvider).approve(draft.id, channels);
      ref.invalidate(noticeDraftsProvider);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _dismiss() async {
    setState(() => _busy = true);
    try {
      await ref.read(noticesRepositoryProvider).dismiss(draft.id);
      ref.invalidate(noticeDraftsProvider);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  String _label(String type) {
    switch (type) {
      case 'RenewalOffer':
        return 'Renewal';
      case 'LateRentNotice':
        return 'Late rent';
      case 'MoveOutReminder':
        return 'Move-out';
      default:
        return type;
    }
  }
}

class _StatusChip extends StatelessWidget {
  const _StatusChip({required this.status});

  final String status;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(
        color: cs.primaryContainer,
        borderRadius: BorderRadius.circular(999),
      ),
      child: Text(
        status,
        style: TextStyle(
          color: cs.onPrimaryContainer,
          fontSize: 11,
          fontWeight: FontWeight.w700,
        ),
      ),
    );
  }
}

class _ErrorBody extends StatelessWidget {
  const _ErrorBody({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) {
    return Center(child: Text(message, style: TextStyle(color: Theme.of(context).colorScheme.error)));
  }
}

class _EmptyBody extends StatelessWidget {
  const _EmptyBody();

  @override
  Widget build(BuildContext context) {
    return const Center(
      child: Padding(
        padding: EdgeInsets.all(24),
        child: Text('No draft notices. Tap refresh to generate lease lifecycle drafts.'),
      ),
    );
  }
}
