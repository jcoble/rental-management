import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/api/api_exception.dart';
import '../../core/push/notification_routing.dart';
import '../../core/widgets/mobile_m3_list.dart';
import '../home/mobile_domain_chrome.dart';
import 'notification_models.dart';
import 'notifications_repository.dart';

/// Landlord notification inbox: paginated list of notifications with unread
/// emphasis, severity icons, relative timestamps, pull-to-refresh and
/// mark-all-read. Tapping a row marks it read and navigates via its
/// `actionUrl`.
class NotificationsInboxScreen extends ConsumerStatefulWidget {
  const NotificationsInboxScreen({super.key});

  @override
  ConsumerState<NotificationsInboxScreen> createState() =>
      _NotificationsInboxScreenState();
}

class _NotificationsInboxScreenState
    extends ConsumerState<NotificationsInboxScreen> {
  final _scrollController = ScrollController();
  late final UnreadCountNotifier _unreadCountNotifier;

  @override
  void initState() {
    super.initState();
    _unreadCountNotifier = ref.read(unreadCountProvider.notifier);
    _scrollController.addListener(_onScroll);
  }

  @override
  void dispose() {
    _scrollController.removeListener(_onScroll);
    _scrollController.dispose();
    // Refresh the badge when leaving the inbox (reads may have happened).
    _unreadCountNotifier.refresh();
    super.dispose();
  }

  void _onScroll() {
    if (_scrollController.position.pixels >=
        _scrollController.position.maxScrollExtent - 240) {
      ref.read(inboxProvider.notifier).loadMore();
    }
  }

  Future<void> _open(AppNotification n) async {
    await ref.read(inboxProvider.notifier).markRead(n.id);
    if (!mounted) return;
    // A14: PUSH the target detail screen onto the stack (not `go`, which
    // REPLACES it) so the detail screen keeps a working back button and the
    // user lands back here on `pop()` instead of being stranded.
    final route = resolveNotificationRoute(n.actionUrl);
    context.push(route);
  }

  @override
  Widget build(BuildContext context) {
    final inboxAsync = ref.watch(inboxProvider);
    final markAllReadButton = IconButton(
      icon: const Icon(Icons.done_all),
      tooltip: 'Mark all read',
      onPressed: () => ref.read(inboxProvider.notifier).markAllRead(),
    );

    return Scaffold(
      appBar: mobileDomainRootAppBar(
        context,
        title: const Text('Notifications'),
        actions: [markAllReadButton],
      ),
      body: Column(
        children: [
          MobileDomainEmbeddedToolbar(children: [markAllReadButton]),
          Expanded(
            child: RefreshIndicator(
              onRefresh: () => ref.read(inboxProvider.notifier).refresh(),
              child: inboxAsync.when(
                loading: () => const Center(child: CircularProgressIndicator()),
                error: (e, _) => _ErrorBody(
                  message: e is ApiException
                      ? e.message
                      : 'Could not load notifications.',
                  onRetry: () => ref.read(inboxProvider.notifier).refresh(),
                ),
                data: (items) {
                  if (items.isEmpty) return const _EmptyBody();
                  final hasMore = ref.read(inboxProvider.notifier).hasMore;
                  return ListView.separated(
                    controller: _scrollController,
                    physics: const AlwaysScrollableScrollPhysics(),
                    padding: const EdgeInsets.fromLTRB(16, 8, 16, 24),
                    itemCount: items.length + (hasMore ? 1 : 0),
                    separatorBuilder: (_, index) {
                      if (index >= items.length - 1) {
                        return const SizedBox(height: 12);
                      }
                      return const MobileM3ListDivider();
                    },
                    itemBuilder: (context, index) {
                      if (index >= items.length) {
                        return const Padding(
                          padding: EdgeInsets.all(16),
                          child: Center(child: CircularProgressIndicator()),
                        );
                      }
                      return _NotificationTile(
                        notification: items[index],
                        position: MobileM3ListItemPositionForIndex.forIndex(
                          index,
                          items.length,
                        ),
                        onTap: () => _open(items[index]),
                      );
                    },
                  );
                },
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _NotificationTile extends StatelessWidget {
  const _NotificationTile({
    required this.notification,
    required this.position,
    required this.onTap,
  });

  final AppNotification notification;
  final MobileM3ListItemPosition position;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final unread = !notification.isRead;
    final (icon, tint) = _severityVisual(notification.severity, cs);

    return MobileM3ListItem(
      position: position,
      onTap: onTap,
      promoted: unread,
      leading: MobileM3LeadingIcon(
        icon: icon,
        backgroundColor: tint.withValues(alpha: 0.16),
        foregroundColor: tint,
      ),
      title: Text(
        notification.title.isEmpty ? notification.type : notification.title,
        style: theme.textTheme.titleSmall?.copyWith(
          fontWeight: unread ? FontWeight.w700 : FontWeight.w500,
        ),
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
      ),
      supporting: [
        if (notification.message.isNotEmpty)
          Text(
            notification.message,
            maxLines: 2,
            overflow: TextOverflow.ellipsis,
            style: theme.textTheme.bodyMedium?.copyWith(
              color: cs.onSurfaceVariant,
            ),
          ),
        Text(
          _relativeTime(notification.createdAt),
          style: theme.textTheme.labelSmall?.copyWith(
            color: cs.onSurfaceVariant,
          ),
        ),
      ],
      trailing: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (unread) ...[
            Container(
              width: 10,
              height: 10,
              decoration: BoxDecoration(
                color: cs.primary,
                shape: BoxShape.circle,
              ),
            ),
            const SizedBox(width: 12),
          ],
          Icon(Icons.chevron_right, color: cs.onSurfaceVariant),
        ],
      ),
    );
  }
}

(IconData, Color) _severityVisual(String severity, ColorScheme cs) {
  switch (severity) {
    case 'Success':
      return (Icons.check_circle_outline, cs.tertiary);
    case 'Warning':
      return (Icons.warning_amber_rounded, cs.secondary);
    case 'Error':
      return (Icons.error_outline, cs.error);
    case 'Info':
    default:
      return (Icons.notifications_none, cs.primary);
  }
}

/// Compact relative timestamp: "just now", "5m", "3h", "2d", else a date.
String _relativeTime(DateTime when) {
  final now = DateTime.now();
  final local = when.toLocal();
  final diff = now.difference(local);
  if (diff.inMinutes < 1) return 'Just now';
  if (diff.inMinutes < 60) return '${diff.inMinutes}m ago';
  if (diff.inHours < 24) return '${diff.inHours}h ago';
  if (diff.inDays < 7) return '${diff.inDays}d ago';
  final m = local.month.toString().padLeft(2, '0');
  final d = local.day.toString().padLeft(2, '0');
  return '${local.year}-$m-$d';
}

class _EmptyBody extends StatelessWidget {
  const _EmptyBody();

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    // Wrap in a scroll view so pull-to-refresh still works when empty.
    return LayoutBuilder(
      builder: (context, constraints) => SingleChildScrollView(
        physics: const AlwaysScrollableScrollPhysics(),
        child: ConstrainedBox(
          constraints: BoxConstraints(minHeight: constraints.maxHeight),
          child: Center(
            child: Column(
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                Icon(
                  Icons.notifications_none,
                  size: 56,
                  color: theme.colorScheme.onSurfaceVariant,
                ),
                const SizedBox(height: 12),
                Text(
                  "You're all caught up",
                  style: theme.textTheme.titleMedium,
                ),
                const SizedBox(height: 4),
                Text(
                  'New alerts will show up here.',
                  style: theme.textTheme.bodyMedium?.copyWith(
                    color: theme.colorScheme.onSurfaceVariant,
                  ),
                ),
              ],
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
    return LayoutBuilder(
      builder: (context, constraints) => SingleChildScrollView(
        physics: const AlwaysScrollableScrollPhysics(),
        child: ConstrainedBox(
          constraints: BoxConstraints(minHeight: constraints.maxHeight),
          child: Center(
            child: Padding(
              padding: const EdgeInsets.all(24),
              child: Column(
                mainAxisAlignment: MainAxisAlignment.center,
                children: [
                  const Icon(Icons.error_outline, size: 48),
                  const SizedBox(height: 12),
                  Text(message, textAlign: TextAlign.center),
                  const SizedBox(height: 16),
                  FilledButton(onPressed: onRetry, child: const Text('Retry')),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}
