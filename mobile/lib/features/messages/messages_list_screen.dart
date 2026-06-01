import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import 'message_models.dart';
import 'message_detail_screen.dart';
import 'messages_repository.dart';

// ── Helpers ───────────────────────────────────────────────────────────────────

const _months = [
  '', 'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun',
  'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec',
];

String _fmtDate(DateTime d) => '${_months[d.month]} ${d.day}';

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

/// Landlord inbox — lists messages with open/all filter, taps to detail.
class MessagesListScreen extends ConsumerStatefulWidget {
  const MessagesListScreen({super.key});

  @override
  ConsumerState<MessagesListScreen> createState() => _MessagesListScreenState();
}

class _MessagesListScreenState extends ConsumerState<MessagesListScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(
      () => ref.read(messagesProvider.notifier).load(),
    );
  }

  Future<void> _refresh() => ref.read(messagesProvider.notifier).refresh();

  void _openDetail(BuildContext context, Message msg) {
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => MessageDetailScreen(messageId: msg.id),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final messagesAsync = ref.watch(messagesProvider);
    final notifier = ref.read(messagesProvider.notifier);
    final currentFilter = notifier.filter;
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return Scaffold(
      appBar: AppBar(
        title: const Text('Messages'),
        actions: [
          Padding(
            padding: const EdgeInsets.only(right: 8),
            child: SegmentedButton<MessageFilter>(
              segments: const [
                ButtonSegment(
                  value: MessageFilter.open,
                  label: Text('Open'),
                ),
                ButtonSegment(
                  value: MessageFilter.all,
                  label: Text('All'),
                ),
              ],
              selected: {currentFilter},
              onSelectionChanged: (s) {
                notifier.setFilter(s.first);
              },
              style: ButtonStyle(
                tapTargetSize: MaterialTapTargetSize.shrinkWrap,
                visualDensity: VisualDensity.compact,
              ),
            ),
          ),
        ],
      ),
      body: RefreshIndicator(
        onRefresh: _refresh,
        child: messagesAsync.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (e, _) => _ErrorBody(
            message: e is ApiException ? e.message : e.toString(),
            onRetry: _refresh,
          ),
          data: (list) {
            if (list.isEmpty) {
              return _EmptyBody(filter: currentFilter);
            }
            return ListView.separated(
              physics: const AlwaysScrollableScrollPhysics(),
              padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
              itemCount: list.length,
              separatorBuilder: (_, _) => const SizedBox(height: 8),
              itemBuilder: (ctx, i) => _MessageCard(
                message: list[i],
                colorScheme: colorScheme,
                theme: theme,
                onTap: () => _openDetail(ctx, list[i]),
              ),
            );
          },
        ),
      ),
    );
  }
}

// ── Message Card ──────────────────────────────────────────────────────────────

class _MessageCard extends StatelessWidget {
  const _MessageCard({
    required this.message,
    required this.colorScheme,
    required this.theme,
    required this.onTap,
  });

  final Message message;
  final ColorScheme colorScheme;
  final ThemeData theme;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final hasReply = message.reply != null && message.reply!.isNotEmpty;

    return Card(
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
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
                      message.subject,
                      style: theme.textTheme.titleSmall?.copyWith(
                        fontWeight: FontWeight.w600,
                      ),
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                  const SizedBox(width: 8),
                  Text(
                    _fmtDate(message.createdAt),
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: colorScheme.onSurfaceVariant,
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 4),
              // Sender / property subtitle
              _buildSubtitle(theme, colorScheme),
              const SizedBox(height: 8),
              Row(
                children: [
                  _StatusChip(
                    status: message.status,
                    colorScheme: colorScheme,
                  ),
                  if (hasReply) ...[
                    const SizedBox(width: 8),
                    Icon(
                      Icons.reply,
                      size: 14,
                      color: colorScheme.onSurfaceVariant,
                    ),
                    const SizedBox(width: 2),
                    Text(
                      'Replied',
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: colorScheme.onSurfaceVariant,
                        fontSize: 11,
                      ),
                    ),
                  ],
                  const Spacer(),
                  Icon(
                    Icons.chevron_right,
                    size: 18,
                    color: colorScheme.onSurfaceVariant,
                  ),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _buildSubtitle(ThemeData theme, ColorScheme cs) {
    final parts = <String>[];
    if (message.senderName != null && message.senderName!.isNotEmpty) {
      parts.add(message.senderName!);
    }
    if (message.propertyName != null && message.propertyName!.isNotEmpty) {
      parts.add(message.propertyName!);
    }
    if (message.unitLabel != null && message.unitLabel!.isNotEmpty) {
      parts.add('Unit ${message.unitLabel!}');
    }
    if (parts.isEmpty) return const SizedBox.shrink();
    return Text(
      parts.join(' · '),
      style: theme.textTheme.bodySmall?.copyWith(
        color: cs.onSurfaceVariant,
      ),
      maxLines: 1,
      overflow: TextOverflow.ellipsis,
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
      padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 3),
      decoration: BoxDecoration(
        color: _statusColor(status, colorScheme),
        borderRadius: BorderRadius.circular(20),
      ),
      child: Text(
        _statusLabel(status),
        style: TextStyle(
          fontSize: 11,
          fontWeight: FontWeight.w600,
          color: _statusTextColor(status, colorScheme),
        ),
      ),
    );
  }
}

// ── Empty / Error ─────────────────────────────────────────────────────────────

class _EmptyBody extends StatelessWidget {
  const _EmptyBody({required this.filter});

  final MessageFilter filter;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    final msg = filter == MessageFilter.open
        ? 'No open messages'
        : 'No messages yet';
    final sub = filter == MessageFilter.open
        ? 'All caught up! Nothing needs a reply.'
        : 'Tenant messages will appear here.';
    return ListView(
      children: [
        SizedBox(
          height: 300,
          child: Center(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(
                  Icons.inbox_outlined,
                  size: 48,
                  color: colorScheme.onSurfaceVariant,
                ),
                const SizedBox(height: 12),
                Text(
                  msg,
                  style: Theme.of(context).textTheme.titleMedium?.copyWith(
                        color: colorScheme.onSurfaceVariant,
                      ),
                ),
                const SizedBox(height: 4),
                Text(
                  sub,
                  style: TextStyle(color: colorScheme.onSurfaceVariant),
                ),
              ],
            ),
          ),
        ),
      ],
    );
  }
}

class _ErrorBody extends StatelessWidget {
  const _ErrorBody({required this.message, required this.onRetry});

  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.error_outline, size: 40, color: colorScheme.error),
            const SizedBox(height: 12),
            Text(
              message,
              textAlign: TextAlign.center,
              style: TextStyle(color: colorScheme.error),
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
