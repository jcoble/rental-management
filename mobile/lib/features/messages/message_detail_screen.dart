import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import 'message_models.dart';
import 'messages_repository.dart';

// ── Helpers ───────────────────────────────────────────────────────────────────

const _months = [
  '', 'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun',
  'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec',
];

String _fmtDateTime(DateTime d) {
  final h = d.hour > 12 ? d.hour - 12 : (d.hour == 0 ? 12 : d.hour);
  final min = d.minute.toString().padLeft(2, '0');
  final ampm = d.hour >= 12 ? 'PM' : 'AM';
  return '${_months[d.month]} ${d.day}, ${d.year}  $h:$min $ampm';
}

Color _statusColor(String status, ColorScheme cs) {
  switch (status.toLowerCase()) {
    case 'open':
      return cs.secondaryContainer;
    case 'inprogress':
      return cs.tertiaryContainer;
    case 'resolved':
      return cs.primaryContainer;
    case 'closed':
      return cs.surfaceContainerHighest;
    default:
      return cs.surfaceContainerHighest;
  }
}

Color _statusTextColor(String status, ColorScheme cs) {
  switch (status.toLowerCase()) {
    case 'open':
      return cs.onSecondaryContainer;
    case 'inprogress':
      return cs.onTertiaryContainer;
    case 'resolved':
      return cs.onPrimaryContainer;
    case 'closed':
      return cs.onSurfaceVariant;
    default:
      return cs.onSurfaceVariant;
  }
}

String _statusLabel(String s) {
  switch (s) {
    case 'InProgress':
      return 'In Progress';
    default:
      return s;
  }
}

// ── Screen ────────────────────────────────────────────────────────────────────

/// Detail screen for a single message.
///
/// Shows subject, body, sender/property, existing reply, a reply text field,
/// and status action buttons (Mark Resolved / Reopen / Close).
class MessageDetailScreen extends ConsumerStatefulWidget {
  const MessageDetailScreen({super.key, required this.messageId});

  final int messageId;

  @override
  ConsumerState<MessageDetailScreen> createState() =>
      _MessageDetailScreenState();
}

class _MessageDetailScreenState extends ConsumerState<MessageDetailScreen> {
  bool _statusUpdating = false;

  Future<void> _setStatus(String status) async {
    setState(() => _statusUpdating = true);
    try {
      await ref
          .read(messageDetailProvider(widget.messageId).notifier)
          .setStatus(status);
    } finally {
      if (mounted) setState(() => _statusUpdating = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final msgAsync = ref.watch(messageDetailProvider(widget.messageId));
    final colorScheme = Theme.of(context).colorScheme;

    return Scaffold(
      appBar: AppBar(
        title: const Text('Message'),
      ),
      body: RefreshIndicator(
        onRefresh: () => ref
            .read(messageDetailProvider(widget.messageId).notifier)
            .refresh(),
        child: msgAsync.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (e, _) => Center(
            child: Padding(
              padding: const EdgeInsets.all(24),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Icon(Icons.error_outline,
                      size: 40, color: colorScheme.error),
                  const SizedBox(height: 12),
                  Text(
                    e is ApiException ? e.message : e.toString(),
                    textAlign: TextAlign.center,
                    style: TextStyle(color: colorScheme.error),
                  ),
                  const SizedBox(height: 16),
                  FilledButton.tonal(
                    onPressed: () => ref
                        .read(messageDetailProvider(widget.messageId).notifier)
                        .refresh(),
                    child: const Text('Retry'),
                  ),
                ],
              ),
            ),
          ),
          data: (msg) => _DetailBody(
            message: msg,
            messageId: widget.messageId,
            statusUpdating: _statusUpdating,
            onSetStatus: _setStatus,
          ),
        ),
      ),
    );
  }
}

// ── Detail body ───────────────────────────────────────────────────────────────

class _DetailBody extends ConsumerStatefulWidget {
  const _DetailBody({
    required this.message,
    required this.messageId,
    required this.statusUpdating,
    required this.onSetStatus,
  });

  final Message message;
  final int messageId;
  final bool statusUpdating;
  final Future<void> Function(String) onSetStatus;

  @override
  ConsumerState<_DetailBody> createState() => _DetailBodyState();
}

class _DetailBodyState extends ConsumerState<_DetailBody> {
  final _replyCtrl = TextEditingController();
  bool _replySending = false;
  String? _replyError;

  @override
  void dispose() {
    _replyCtrl.dispose();
    super.dispose();
  }

  Future<void> _sendReply() async {
    final text = _replyCtrl.text.trim();
    if (text.isEmpty) {
      setState(() => _replyError = 'Reply cannot be empty.');
      return;
    }
    setState(() {
      _replySending = true;
      _replyError = null;
    });
    try {
      await ref
          .read(messageDetailProvider(widget.messageId).notifier)
          .reply(text);
      if (mounted) _replyCtrl.clear();
    } on ApiException catch (e) {
      if (mounted) setState(() => _replyError = e.message);
    } finally {
      if (mounted) setState(() => _replySending = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final msg = widget.message;
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    final isResolved = msg.status == 'Resolved';
    final isClosed = msg.status == 'Closed';
    final isDone = isResolved || isClosed;

    return ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
      children: [
        // ── Subject + status chip ──────────────────────────────────────
        Text(
          msg.subject,
          style: theme.textTheme.headlineSmall
              ?.copyWith(fontWeight: FontWeight.w700),
        ),
        const SizedBox(height: 8),
        Row(
          children: [
            _StatusChip(status: msg.status, colorScheme: cs),
          ],
        ),

        const SizedBox(height: 20),

        // ── Meta grid ─────────────────────────────────────────────────
        _SectionLabel(label: 'Details', theme: theme),
        const SizedBox(height: 8),
        _MetaGrid(message: msg, theme: theme, colorScheme: cs),

        const SizedBox(height: 20),

        // ── Message body ───────────────────────────────────────────────
        _SectionLabel(label: 'Message', theme: theme),
        const SizedBox(height: 8),
        Container(
          width: double.infinity,
          padding: const EdgeInsets.all(14),
          decoration: BoxDecoration(
            color: cs.surfaceContainerLowest,
            borderRadius: BorderRadius.circular(12),
            border: Border.all(color: cs.outlineVariant),
          ),
          child: Text(msg.body, style: theme.textTheme.bodyMedium),
        ),

        // ── Existing reply ─────────────────────────────────────────────
        if (msg.reply != null && msg.reply!.isNotEmpty) ...[
          const SizedBox(height: 20),
          _SectionLabel(label: 'Your Reply', theme: theme),
          const SizedBox(height: 8),
          Container(
            width: double.infinity,
            padding: const EdgeInsets.all(14),
            decoration: BoxDecoration(
              color: cs.primaryContainer.withValues(alpha: 0.4),
              borderRadius: BorderRadius.circular(12),
              border: Border.all(color: cs.primary.withValues(alpha: 0.3)),
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Icon(Icons.reply, size: 14, color: cs.primary),
                    const SizedBox(width: 6),
                    Text(
                      'Replied',
                      style: theme.textTheme.labelSmall?.copyWith(
                        color: cs.primary,
                        fontWeight: FontWeight.w600,
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 6),
                Text(msg.reply!, style: theme.textTheme.bodyMedium),
              ],
            ),
          ),
        ],

        const SizedBox(height: 24),

        // ── Reply field ────────────────────────────────────────────────
        _SectionLabel(label: 'Send a Reply', theme: theme),
        const SizedBox(height: 8),
        TextFormField(
          controller: _replyCtrl,
          maxLines: 4,
          textInputAction: TextInputAction.newline,
          decoration: const InputDecoration(
            hintText: 'Type your reply…',
            border: OutlineInputBorder(),
          ),
          enabled: !isDone && !_replySending,
        ),
        if (_replyError != null) ...[
          const SizedBox(height: 8),
          Container(
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
            decoration: BoxDecoration(
              color: cs.errorContainer,
              borderRadius: BorderRadius.circular(8),
            ),
            child: Text(
              _replyError!,
              style:
                  TextStyle(color: cs.onErrorContainer, fontSize: 13),
            ),
          ),
        ],
        const SizedBox(height: 10),
        FilledButton.icon(
          onPressed: (isDone || _replySending) ? null : _sendReply,
          icon: _replySending
              ? SizedBox(
                  width: 16,
                  height: 16,
                  child: CircularProgressIndicator(
                    strokeWidth: 2,
                    color: cs.onPrimary,
                  ),
                )
              : const Icon(Icons.send, size: 18),
          label: const Text('Send Reply'),
        ),

        const SizedBox(height: 24),

        // ── Status actions ─────────────────────────────────────────────
        _SectionLabel(label: 'Status Actions', theme: theme),
        const SizedBox(height: 8),
        if (widget.statusUpdating)
          const Center(child: CircularProgressIndicator())
        else
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              if (!isDone)
                OutlinedButton.icon(
                  onPressed: () => widget.onSetStatus('Resolved'),
                  icon: const Icon(Icons.check_circle_outline, size: 16),
                  label: const Text('Mark Resolved'),
                ),
              if (isDone)
                OutlinedButton.icon(
                  onPressed: () => widget.onSetStatus('Open'),
                  icon: const Icon(Icons.refresh, size: 16),
                  label: const Text('Reopen'),
                ),
              if (!isClosed)
                OutlinedButton.icon(
                  onPressed: () => widget.onSetStatus('Closed'),
                  icon: const Icon(Icons.archive_outlined, size: 16),
                  label: const Text('Close'),
                  style: OutlinedButton.styleFrom(
                    foregroundColor: cs.onSurfaceVariant,
                  ),
                ),
            ],
          ),
      ],
    );
  }
}

// ── Meta grid ─────────────────────────────────────────────────────────────────

class _MetaGrid extends StatelessWidget {
  const _MetaGrid({
    required this.message,
    required this.theme,
    required this.colorScheme,
  });

  final Message message;
  final ThemeData theme;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    final rows = <({String label, String value})>[];

    if (message.senderName != null && message.senderName!.isNotEmpty) {
      rows.add((label: 'From', value: message.senderName!));
    }
    if (message.propertyName != null && message.propertyName!.isNotEmpty) {
      rows.add((label: 'Property', value: message.propertyName!));
    }
    if (message.unitLabel != null && message.unitLabel!.isNotEmpty) {
      rows.add((label: 'Unit', value: message.unitLabel!));
    }
    rows.add((label: 'Received', value: _fmtDateTime(message.createdAt)));
    rows.add((label: 'Updated', value: _fmtDateTime(message.updatedAt)));

    return Container(
      decoration: BoxDecoration(
        color: colorScheme.surfaceContainerLowest,
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: colorScheme.outlineVariant),
      ),
      child: Column(
        children: [
          for (var i = 0; i < rows.length; i++)
            _DetailRow(
              label: rows[i].label,
              value: rows[i].value,
              theme: theme,
              colorScheme: colorScheme,
              isLast: i == rows.length - 1,
            ),
        ],
      ),
    );
  }
}

class _DetailRow extends StatelessWidget {
  const _DetailRow({
    required this.label,
    required this.value,
    required this.theme,
    required this.colorScheme,
    this.isLast = false,
  });

  final String label;
  final String value;
  final ThemeData theme;
  final ColorScheme colorScheme;
  final bool isLast;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
      decoration: BoxDecoration(
        border: isLast
            ? null
            : Border(
                bottom: BorderSide(color: colorScheme.outlineVariant),
              ),
      ),
      child: Row(
        children: [
          SizedBox(
            width: 80,
            child: Text(
              label,
              style: theme.textTheme.bodySmall?.copyWith(
                color: colorScheme.onSurfaceVariant,
              ),
            ),
          ),
          Expanded(
            child: Text(
              value,
              style: theme.textTheme.bodyMedium
                  ?.copyWith(fontWeight: FontWeight.w500),
            ),
          ),
        ],
      ),
    );
  }
}

// ── Shared small widgets ──────────────────────────────────────────────────────

class _SectionLabel extends StatelessWidget {
  const _SectionLabel({required this.label, required this.theme});

  final String label;
  final ThemeData theme;

  @override
  Widget build(BuildContext context) {
    return Text(
      label,
      style: theme.textTheme.labelLarge?.copyWith(
        fontWeight: FontWeight.w700,
        color: Theme.of(context).colorScheme.onSurfaceVariant,
        letterSpacing: 0.5,
      ),
    );
  }
}

class _StatusChip extends StatelessWidget {
  const _StatusChip({required this.status, required this.colorScheme});

  final String status;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
      decoration: BoxDecoration(
        color: _statusColor(status, colorScheme),
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        _statusLabel(status),
        style: TextStyle(
          fontSize: 12,
          fontWeight: FontWeight.w600,
          color: _statusTextColor(status, colorScheme),
        ),
      ),
    );
  }
}
