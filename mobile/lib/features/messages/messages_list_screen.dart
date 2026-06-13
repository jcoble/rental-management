import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/auth/auth_controller.dart';
import '../../core/models/models.dart';
import '../tenants/tenants_repository.dart';
import 'message_models.dart';
import 'message_detail_screen.dart';
import 'messages_repository.dart';

// ── Helpers ───────────────────────────────────────────────────────────────────

const _months = [
  '',
  'Jan',
  'Feb',
  'Mar',
  'Apr',
  'May',
  'Jun',
  'Jul',
  'Aug',
  'Sep',
  'Oct',
  'Nov',
  'Dec',
];

/// Relative-ish timestamp for the thread list: "9:30 AM" today, "Mon" this
/// week, otherwise "Jun 1".
String _fmtRelative(DateTime d) {
  final now = DateTime.now();
  final local = d.toLocal();
  final today = DateTime(now.year, now.month, now.day);
  final that = DateTime(local.year, local.month, local.day);
  final diffDays = today.difference(that).inDays;

  if (diffDays == 0) {
    final h = local.hour > 12
        ? local.hour - 12
        : (local.hour == 0 ? 12 : local.hour);
    final min = local.minute.toString().padLeft(2, '0');
    final ampm = local.hour >= 12 ? 'PM' : 'AM';
    return '$h:$min $ampm';
  }
  if (diffDays == 1) return 'Yesterday';
  if (diffDays < 7) {
    const wd = ['', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
    return wd[local.weekday];
  }
  return '${_months[local.month]} ${local.day}';
}

String _tenantDisplayName(Tenant t) {
  final full = t.fullName;
  if (full != null && full.trim().isNotEmpty) return full;
  return '${t.firstName} ${t.lastName}'.trim();
}

// ── Screen ────────────────────────────────────────────────────────────────────

/// Landlord inbox — a list of conversation threads (Google Messages style).
/// Each row shows the tenant + subject, last-message preview, relative time,
/// and an unread badge. Tap opens the thread; "+" starts a new conversation.
class MessagesListScreen extends ConsumerStatefulWidget {
  const MessagesListScreen({super.key});

  @override
  ConsumerState<MessagesListScreen> createState() => _MessagesListScreenState();
}

class _MessagesListScreenState extends ConsumerState<MessagesListScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(() => ref.read(conversationsProvider.notifier).load());
  }

  Future<void> _refresh() => ref.read(conversationsProvider.notifier).refresh();

  void _openThread(BuildContext context, Conversation convo) {
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => MessageDetailScreen(
          conversationId: convo.id,
          title: convo.tenantName,
          subtitle: convo.subject,
        ),
      ),
    );
  }

  Future<void> _startNewConversation(BuildContext context) async {
    final created = await showModalBottomSheet<Conversation>(
      context: context,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => const _ComposeConversationSheet(),
    );
    if (created == null || !context.mounted) return;
    // Refresh the inbox and open the freshly created thread.
    await ref.read(conversationsProvider.notifier).refresh();
    if (!context.mounted) return;
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(
        builder: (_) => MessageDetailScreen(
          conversationId: created.id,
          title: created.tenantName,
          subtitle: created.subject,
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final convosAsync = ref.watch(conversationsProvider);
    final auth = ref.watch(authControllerProvider);
    final tenantMode = auth is AuthStateAuthenticated && auth.user.isTenant;
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return Scaffold(
      appBar: AppBar(title: const Text('Messages')),
      floatingActionButton: tenantMode
          ? null
          : FloatingActionButton(
        heroTag: 'messages-fab',
              onPressed: () => _startNewConversation(context),
              tooltip: 'New conversation',
              child: const Icon(Icons.edit_outlined),
            ),
      body: RefreshIndicator(
        onRefresh: _refresh,
        child: convosAsync.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (e, _) => _ErrorBody(
            message: e is ApiException ? e.message : e.toString(),
            onRetry: _refresh,
          ),
          data: (list) {
            if (list.isEmpty) {
              return const _EmptyBody();
            }
            return ListView.separated(
              physics: const AlwaysScrollableScrollPhysics(),
              padding: const EdgeInsets.fromLTRB(8, 8, 8, 88),
              itemCount: list.length,
              separatorBuilder: (_, _) => const SizedBox(height: 4),
              itemBuilder: (ctx, i) => _ConversationTile(
                conversation: list[i],
                colorScheme: colorScheme,
                theme: theme,
                onTap: () => _openThread(ctx, list[i]),
              ),
            );
          },
        ),
      ),
    );
  }
}

// ── Conversation Tile ─────────────────────────────────────────────────────────

class _ConversationTile extends StatelessWidget {
  const _ConversationTile({
    required this.conversation,
    required this.colorScheme,
    required this.theme,
    required this.onTap,
  });

  final Conversation conversation;
  final ColorScheme colorScheme;
  final ThemeData theme;
  final VoidCallback onTap;

  String _avatarInitials() {
    final name = conversation.tenantName.trim();
    if (name.isEmpty) return '?';
    final parts = name.split(RegExp(r'\s+'));
    if (parts.length == 1) return parts.first.characters.first.toUpperCase();
    return (parts.first.characters.first + parts.last.characters.first)
        .toUpperCase();
  }

  @override
  Widget build(BuildContext context) {
    final unread = conversation.hasUnread;
    final preview = conversation.lastMessagePreview ?? '';

    return Material(
      color: Colors.transparent,
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 10),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              // Avatar
              CircleAvatar(
                radius: 24,
                backgroundColor: colorScheme.primaryContainer,
                child: Text(
                  _avatarInitials(),
                  style: theme.textTheme.titleMedium?.copyWith(
                    color: colorScheme.onPrimaryContainer,
                    fontWeight: FontWeight.w600,
                  ),
                ),
              ),
              const SizedBox(width: 12),
              // Name + subject + preview
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        Expanded(
                          child: Text(
                            conversation.tenantName,
                            style: theme.textTheme.titleSmall?.copyWith(
                              fontWeight: unread
                                  ? FontWeight.w700
                                  : FontWeight.w600,
                            ),
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                          ),
                        ),
                        const SizedBox(width: 8),
                        Text(
                          _fmtRelative(conversation.lastMessageAt),
                          style: theme.textTheme.bodySmall?.copyWith(
                            color: unread
                                ? colorScheme.primary
                                : colorScheme.onSurfaceVariant,
                            fontWeight: unread
                                ? FontWeight.w700
                                : FontWeight.w400,
                          ),
                        ),
                      ],
                    ),
                    const SizedBox(height: 2),
                    Text(
                      conversation.subject,
                      style: theme.textTheme.bodyMedium?.copyWith(
                        color: colorScheme.onSurface,
                        fontWeight: unread ? FontWeight.w600 : FontWeight.w500,
                      ),
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                    const SizedBox(height: 2),
                    Row(
                      crossAxisAlignment: CrossAxisAlignment.center,
                      children: [
                        Expanded(
                          child: Text(
                            preview.isEmpty ? 'No messages yet' : preview,
                            style: theme.textTheme.bodySmall?.copyWith(
                              color: unread
                                  ? colorScheme.onSurface
                                  : colorScheme.onSurfaceVariant,
                              fontWeight: unread
                                  ? FontWeight.w600
                                  : FontWeight.w400,
                            ),
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                          ),
                        ),
                        if (unread) ...[
                          const SizedBox(width: 8),
                          _UnreadBadge(
                            count: conversation.unreadCount,
                            colorScheme: colorScheme,
                          ),
                        ],
                      ],
                    ),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _UnreadBadge extends StatelessWidget {
  const _UnreadBadge({required this.count, required this.colorScheme});

  final int count;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    final label = count > 99 ? '99+' : '$count';
    return Container(
      constraints: const BoxConstraints(minWidth: 20),
      height: 20,
      padding: const EdgeInsets.symmetric(horizontal: 6),
      decoration: BoxDecoration(
        color: colorScheme.primary,
        borderRadius: BorderRadius.circular(10),
      ),
      alignment: Alignment.center,
      child: Text(
        label,
        style: TextStyle(
          color: colorScheme.onPrimary,
          fontSize: 11,
          fontWeight: FontWeight.w700,
        ),
      ),
    );
  }
}

// ── Empty / Error ─────────────────────────────────────────────────────────────

class _EmptyBody extends StatelessWidget {
  const _EmptyBody();

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    return ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      children: [
        SizedBox(
          height: 320,
          child: Center(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(
                  Icons.forum_outlined,
                  size: 48,
                  color: colorScheme.onSurfaceVariant,
                ),
                const SizedBox(height: 12),
                Text(
                  'No conversations yet',
                  style: Theme.of(context).textTheme.titleMedium?.copyWith(
                    color: colorScheme.onSurfaceVariant,
                  ),
                ),
                const SizedBox(height: 4),
                Text(
                  'Tap the pencil to message a tenant.',
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
            FilledButton.tonal(onPressed: onRetry, child: const Text('Retry')),
          ],
        ),
      ),
    );
  }
}

// ── Compose New Conversation Sheet ────────────────────────────────────────────

/// Bottom sheet to start a new conversation: pick a tenant, enter a subject and
/// first message, choose channels (Portal on by default). On success pops with
/// the created [Conversation].
class _ComposeConversationSheet extends ConsumerStatefulWidget {
  const _ComposeConversationSheet();

  @override
  ConsumerState<_ComposeConversationSheet> createState() =>
      _ComposeConversationSheetState();
}

class _ComposeConversationSheetState
    extends ConsumerState<_ComposeConversationSheet> {
  final _formKey = GlobalKey<FormState>();
  final _subjectCtrl = TextEditingController();
  final _bodyCtrl = TextEditingController();

  int? _selectedTenantId;

  // Channel toggles. Portal is the always-on base channel; Email/SMS default
  // off (portfolio messaging defaults aren't loaded on mobile yet).
  bool _portal = true;
  bool _email = false;
  bool _sms = false;

  bool _saving = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    Future.microtask(() => ref.read(tenantsProvider.notifier).load());
  }

  @override
  void dispose() {
    _subjectCtrl.dispose();
    _bodyCtrl.dispose();
    super.dispose();
  }

  List<String> _selectedChannels() {
    return [if (_portal) 'Portal', if (_email) 'Email', if (_sms) 'Sms'];
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;
    if (_selectedTenantId == null) {
      setState(() => _error = 'Please choose a tenant.');
      return;
    }
    final channels = _selectedChannels();
    if (channels.isEmpty) {
      setState(() => _error = 'Choose at least one channel.');
      return;
    }

    setState(() {
      _saving = true;
      _error = null;
    });

    try {
      final convo = await ref
          .read(messagesRepositoryProvider)
          .startConversation(
            tenantId: _selectedTenantId!,
            subject: _subjectCtrl.text.trim(),
            body: _bodyCtrl.text.trim(),
            channels: channels,
          );
      if (mounted) Navigator.of(context).pop<Conversation>(convo);
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final tenantsAsync = ref.watch(tenantsProvider);
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final bottomPadding = MediaQuery.viewInsetsOf(context).bottom;

    return Padding(
      padding: EdgeInsets.fromLTRB(20, 20, 20, 20 + bottomPadding),
      child: Form(
        key: _formKey,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              // Header
              Row(
                children: [
                  Expanded(
                    child: Text(
                      'New Conversation',
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
              const SizedBox(height: 16),

              // Tenant picker
              tenantsAsync.when(
                loading: () => const Center(
                  child: Padding(
                    padding: EdgeInsets.symmetric(vertical: 12),
                    child: CircularProgressIndicator(),
                  ),
                ),
                error: (e, _) => Text(
                  'Could not load tenants: ${e is ApiException ? e.message : e}',
                  style: TextStyle(color: colorScheme.error, fontSize: 13),
                ),
                data: (tenants) => DropdownButtonFormField<int>(
                  initialValue: _selectedTenantId,
                  isExpanded: true,
                  decoration: const InputDecoration(labelText: 'To (tenant)'),
                  items: tenants
                      .map(
                        (t) => DropdownMenuItem(
                          value: t.id,
                          child: Text(
                            _tenantDisplayName(t),
                            overflow: TextOverflow.ellipsis,
                          ),
                        ),
                      )
                      .toList(),
                  onChanged: (v) => setState(() => _selectedTenantId = v),
                  validator: (v) => v == null ? 'Please choose a tenant' : null,
                ),
              ),
              const SizedBox(height: 12),

              // Subject
              TextFormField(
                controller: _subjectCtrl,
                textInputAction: TextInputAction.next,
                decoration: const InputDecoration(labelText: 'Subject'),
                validator: (v) => (v == null || v.trim().isEmpty)
                    ? 'Subject is required'
                    : null,
              ),
              const SizedBox(height: 12),

              // First message
              TextFormField(
                controller: _bodyCtrl,
                maxLines: 4,
                minLines: 2,
                textInputAction: TextInputAction.newline,
                decoration: const InputDecoration(
                  labelText: 'Message',
                  alignLabelWithHint: true,
                ),
                validator: (v) => (v == null || v.trim().isEmpty)
                    ? 'Message is required'
                    : null,
              ),
              const SizedBox(height: 16),

              // Channels
              Text(
                'Send via',
                style: theme.textTheme.labelLarge?.copyWith(
                  fontWeight: FontWeight.w700,
                  color: colorScheme.onSurfaceVariant,
                ),
              ),
              const SizedBox(height: 8),
              _ChannelChips(
                portal: _portal,
                email: _email,
                sms: _sms,
                onPortal: (v) => setState(() => _portal = v),
                onEmail: (v) => setState(() => _email = v),
                onSms: (v) => setState(() => _sms = v),
              ),

              if (_error != null) ...[
                const SizedBox(height: 12),
                Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 12,
                    vertical: 10,
                  ),
                  decoration: BoxDecoration(
                    color: colorScheme.errorContainer,
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: Text(
                    _error!,
                    style: TextStyle(
                      color: colorScheme.onErrorContainer,
                      fontSize: 13,
                    ),
                  ),
                ),
              ],

              const SizedBox(height: 20),

              FilledButton.icon(
                onPressed: _saving ? null : _submit,
                icon: _saving
                    ? const SizedBox(
                        height: 18,
                        width: 18,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : const Icon(Icons.send, size: 18),
                label: const Text('Send'),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

// ── Shared: channel selector chips ────────────────────────────────────────────

/// Compact channel picker shared by the compose sheet and the thread compose
/// bar. Renders 'Sms' as 'Text' for the landlord-facing label.
class _ChannelChips extends StatelessWidget {
  const _ChannelChips({
    required this.portal,
    required this.email,
    required this.sms,
    required this.onPortal,
    required this.onEmail,
    required this.onSms,
  });

  final bool portal;
  final bool email;
  final bool sms;
  final ValueChanged<bool> onPortal;
  final ValueChanged<bool> onEmail;
  final ValueChanged<bool> onSms;

  @override
  Widget build(BuildContext context) {
    return Wrap(
      spacing: 8,
      runSpacing: 8,
      children: [
        _ChannelChip(
          icon: Icons.forum_outlined,
          label: 'Portal',
          selected: portal,
          onChanged: onPortal,
        ),
        _ChannelChip(
          icon: Icons.email_outlined,
          label: 'Email',
          selected: email,
          onChanged: onEmail,
        ),
        _ChannelChip(
          icon: Icons.sms_outlined,
          label: 'Text',
          selected: sms,
          onChanged: onSms,
        ),
      ],
    );
  }
}

class _ChannelChip extends StatelessWidget {
  const _ChannelChip({
    required this.icon,
    required this.label,
    required this.selected,
    required this.onChanged,
  });

  final IconData icon;
  final String label;
  final bool selected;
  final ValueChanged<bool> onChanged;

  @override
  Widget build(BuildContext context) {
    return FilterChip(
      avatar: Icon(icon, size: 16),
      label: Text(label),
      selected: selected,
      onSelected: onChanged,
      showCheckmark: false,
      visualDensity: VisualDensity.compact,
    );
  }
}
