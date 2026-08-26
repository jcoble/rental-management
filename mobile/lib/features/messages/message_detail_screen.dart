import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../home/mobile_domain_navigation.dart';
import 'message_models.dart';
import 'messages_repository.dart';
import '../../core/presentation/formatting.dart';

// ── Helpers ───────────────────────────────────────────────────────────────────

/// Short timestamp under a bubble: "9:30 AM" or "Jun 1, 9:30 AM" if not today.
String _fmtBubbleTime(DateTime d) {
  final local = d.toLocal();
  final now = DateTime.now();
  final h = local.hour > 12
      ? local.hour - 12
      : (local.hour == 0 ? 12 : local.hour);
  final min = local.minute.toString().padLeft(2, '0');
  final ampm = local.hour >= 12 ? 'PM' : 'AM';
  final time = '$h:$min $ampm';
  final sameDay =
      local.year == now.year &&
      local.month == now.month &&
      local.day == now.day;
  if (sameDay) return time;
  return '${shortDateFmt(local)}, $time';
}

/// Landlord-facing label for a channel string ('Sms' → 'Text').
String _channelLabel(String channel) {
  switch (channel) {
    case 'Sms':
      return 'Text';
    default:
      return channel;
  }
}

/// The quiet line above a compose box: "Sending in the tenant app and by text".
String sendingChannelsLine(List<String> channels) {
  final parts = [
    for (final channel in channels)
      switch (channel) {
        'Portal' => 'in the tenant app',
        'Email' => 'by email',
        'Sms' => 'by text',
        _ => channel,
      },
  ];
  if (parts.isEmpty) return 'Choose how to send this';
  if (parts.length == 1) return 'Sending ${parts.first}';
  final lead = parts.sublist(0, parts.length - 1).join(', ');
  return 'Sending $lead and ${parts.last}';
}

/// The quiet "Sending …" line plus a "Change" button that reveals the chips.
class SendingChannelsLine extends StatelessWidget {
  const SendingChannelsLine({
    super.key,
    required this.channels,
    required this.onChange,
  });

  final List<String> channels;
  final VoidCallback onChange;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Flexible(
          child: Text(
            sendingChannelsLine(channels),
            style: theme.textTheme.bodySmall?.copyWith(
              color: theme.colorScheme.onSurfaceVariant,
            ),
            overflow: TextOverflow.ellipsis,
          ),
        ),
        TextButton(
          onPressed: onChange,
          style: TextButton.styleFrom(
            visualDensity: VisualDensity.compact,
            padding: const EdgeInsets.symmetric(horizontal: 8),
            minimumSize: const Size(0, 32),
          ),
          child: const Text('Change'),
        ),
      ],
    );
  }
}

// ── Screen ────────────────────────────────────────────────────────────────────

/// Conversation thread screen — chat bubbles plus a pinned compose bar.
class MessageDetailScreen extends ConsumerStatefulWidget {
  const MessageDetailScreen({
    super.key,
    required this.conversationId,
    this.title,
    this.subtitle,
  });

  final int conversationId;

  /// Optional header text shown immediately (tenant name) while the thread
  /// loads, so the app bar isn't blank.
  final String? title;

  /// Optional subtitle (the conversation subject).
  final String? subtitle;

  @override
  ConsumerState<MessageDetailScreen> createState() =>
      _MessageDetailScreenState();
}

class _MessageDetailScreenState extends ConsumerState<MessageDetailScreen> {
  final _scrollCtrl = ScrollController();
  final _composeCtrl = TextEditingController();
  final _quickActionHiddenOwner = Object();
  MobileShellNavigator? _shellNavigator;

  // Inline channel toggles for the NEXT send only. They start as the way the
  // last reply in this thread went out, and are hidden behind "Change" until
  // the landlord wants something different.
  bool _portal = true;
  bool _email = false;
  bool _sms = false;
  bool _channelsRevealed = false;
  bool _channelsSeeded = false;

  bool _sending = false;
  String? _sendOperationKey;
  String? _sendOperationPayload;

  @override
  void initState() {
    super.initState();
    _composeCtrl.addListener(_invalidateSendOperation);
    Future.microtask(() {
      if (mounted) {
        ref
            .read(conversationProvider(widget.conversationId).notifier)
            .refresh();
      }
    });
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    final nextShellNavigator = mobileShellNavigatorOf(context);
    if (identical(nextShellNavigator, _shellNavigator)) return;
    _shellNavigator?.setTabQuickActionsHidden?.call(
      MobileShellTabId.inbox,
      _quickActionHiddenOwner,
      false,
    );
    _shellNavigator = nextShellNavigator;
    _shellNavigator?.setTabQuickActionsHidden?.call(
      MobileShellTabId.inbox,
      _quickActionHiddenOwner,
      true,
    );
  }

  @override
  void dispose() {
    _shellNavigator?.setTabQuickActionsHidden?.call(
      MobileShellTabId.inbox,
      _quickActionHiddenOwner,
      false,
    );
    _scrollCtrl.dispose();
    _composeCtrl.removeListener(_invalidateSendOperation);
    _composeCtrl.dispose();
    super.dispose();
  }

  void _scrollToBottom({bool animated = false}) {
    // Defer until after the frame so the list has laid out.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!_scrollCtrl.hasClients) return;
      final target = _scrollCtrl.position.maxScrollExtent;
      if (animated) {
        _scrollCtrl.animateTo(
          target,
          duration: const Duration(milliseconds: 250),
          curve: Curves.easeOut,
        );
      } else {
        _scrollCtrl.jumpTo(target);
      }
    });
  }

  List<String> _selectedChannels() => [
    if (_portal) 'Portal',
    if (_email) 'Email',
    if (_sms) 'Sms',
  ];

  void _invalidateSendOperation() {
    _sendOperationKey = null;
    _sendOperationPayload = null;
  }

  /// Start the reply on the same channels the last reply in this thread used.
  void _seedChannelsFromThread(Conversation conversation) {
    if (_channelsSeeded || conversation.messages.isEmpty) return;
    _channelsSeeded = true;
    List<String>? lastSent;
    for (final message in conversation.messages) {
      if (message.isFromLandlord && message.channels.isNotEmpty) {
        lastSent = message.channels;
      }
    }
    if (lastSent == null) return;
    final channels = lastSent;
    setState(() {
      _portal = channels.contains('Portal');
      _email = channels.contains('Email');
      _sms = channels.contains('Sms');
      _invalidateSendOperation();
    });
  }

  void _setChannel(void Function() update) {
    setState(() {
      update();
      _invalidateSendOperation();
    });
  }

  Future<void> _send() async {
    final text = _composeCtrl.text.trim();
    if (text.isEmpty) return;
    final channels = _selectedChannels();
    if (channels.isEmpty) {
      _showError('Choose at least one channel.');
      return;
    }

    // A retry key represents exactly one immutable send payload. Unchanged retries reuse it;
    // any text/channel edit invalidates it (listeners above), and this fingerprint is a final
    // guard against a payload change that did not originate from the visible controls.
    final payload = jsonEncode([text, channels]);
    if (_sendOperationKey == null || _sendOperationPayload != payload) {
      _sendOperationKey = MessagesRepository.createOperationKey();
      _sendOperationPayload = payload;
    }
    setState(() => _sending = true);
    try {
      await ref
          .read(conversationProvider(widget.conversationId).notifier)
          .sendMessage(text, channels, operationKey: _sendOperationKey);
      if (!mounted) return;
      _invalidateSendOperation();
      _composeCtrl.clear();
      _scrollToBottom(animated: true);
    } on ApiException catch (e) {
      _showError(e.message);
    } finally {
      if (mounted) setState(() => _sending = false);
    }
  }

  void _showError(String message) {
    if (!mounted) return;
    ScaffoldMessenger.of(
      context,
    ).showSnackBar(SnackBar(content: Text(message)));
  }

  @override
  Widget build(BuildContext context) {
    final convoAsync = ref.watch(conversationProvider(widget.conversationId));
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    // Auto-scroll to newest whenever the thread (re)loads with messages.
    ref.listen<AsyncValue<Conversation>>(
      conversationProvider(widget.conversationId),
      (prev, next) {
        next.whenData((convo) {
          _seedChannelsFromThread(convo);
          if (convo.messages.isNotEmpty) _scrollToBottom();
        });
      },
    );

    final headerTitle = convoAsync.maybeWhen(
      data: (c) => c.displayName,
      orElse: () => widget.title ?? 'Conversation',
    );
    final headerSubtitle = convoAsync.maybeWhen(
      data: (c) => c.subject,
      orElse: () => widget.subtitle,
    );

    return Scaffold(
      appBar: AppBar(
        title: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(
              headerTitle,
              style: theme.textTheme.titleMedium?.copyWith(
                fontWeight: FontWeight.w600,
              ),
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
            ),
            if (headerSubtitle != null && headerSubtitle.isNotEmpty)
              Text(
                headerSubtitle,
                style: theme.textTheme.bodySmall?.copyWith(
                  color: colorScheme.onSurfaceVariant,
                ),
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
              ),
          ],
        ),
      ),
      body: Column(
        children: [
          Expanded(
            child: convoAsync.when(
              loading: () => const Center(child: CircularProgressIndicator()),
              error: (e, _) => _ErrorBody(
                message: e is ApiException ? e.message : e.toString(),
                onRetry: () => ref
                    .read(conversationProvider(widget.conversationId).notifier)
                    .refresh(),
              ),
              data: (convo) => _MessageThread(
                conversation: convo,
                scrollController: _scrollCtrl,
                theme: theme,
                colorScheme: colorScheme,
              ),
            ),
          ),
          _ComposeBar(
            controller: _composeCtrl,
            sending: _sending,
            channelsRevealed: _channelsRevealed,
            channels: _selectedChannels(),
            onRevealChannels: () => setState(() => _channelsRevealed = true),
            portal: _portal,
            email: _email,
            sms: _sms,
            onPortal: (v) => _setChannel(() => _portal = v),
            onEmail: (v) => _setChannel(() => _email = v),
            onSms: (v) => _setChannel(() => _sms = v),
            onSend: _send,
          ),
        ],
      ),
    );
  }
}

// ── Message thread (chat bubbles) ─────────────────────────────────────────────

class _MessageThread extends StatelessWidget {
  const _MessageThread({
    required this.conversation,
    required this.scrollController,
    required this.theme,
    required this.colorScheme,
  });

  final Conversation conversation;
  final ScrollController scrollController;
  final ThemeData theme;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    final messages = conversation.messages;
    if (messages.isEmpty) {
      return Center(
        child: Text(
          'No messages yet',
          style: theme.textTheme.bodyMedium?.copyWith(
            color: colorScheme.onSurfaceVariant,
          ),
        ),
      );
    }

    return ListView.builder(
      controller: scrollController,
      physics: const AlwaysScrollableScrollPhysics(),
      padding: const EdgeInsets.fromLTRB(12, 16, 12, 16),
      itemCount: messages.length,
      itemBuilder: (ctx, i) => _MessageBubble(
        message: messages[i],
        theme: theme,
        colorScheme: colorScheme,
      ),
    );
  }
}

class _MessageBubble extends StatelessWidget {
  const _MessageBubble({
    required this.message,
    required this.theme,
    required this.colorScheme,
  });

  final ConversationMessage message;
  final ThemeData theme;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    final mine = message.isFromLandlord;
    final align = mine ? CrossAxisAlignment.end : CrossAxisAlignment.start;
    final bubbleColor = mine
        ? colorScheme.primary
        : colorScheme.surfaceContainerHighest;
    final textColor = mine ? colorScheme.onPrimary : colorScheme.onSurface;

    final radius = BorderRadius.only(
      topLeft: const Radius.circular(16),
      topRight: const Radius.circular(16),
      bottomLeft: Radius.circular(mine ? 16 : 4),
      bottomRight: Radius.circular(mine ? 4 : 16),
    );

    // Landlord bubbles show which channels were used (rendering Sms → Text).
    final channelText = mine && message.channels.isNotEmpty
        ? message.channels.map(_channelLabel).join(' · ')
        : null;

    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Column(
        crossAxisAlignment: align,
        children: [
          ConstrainedBox(
            constraints: BoxConstraints(
              maxWidth: MediaQuery.sizeOf(context).width * 0.78,
            ),
            child: Container(
              padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
              decoration: BoxDecoration(
                color: bubbleColor,
                borderRadius: radius,
              ),
              child: Text(
                message.body,
                style: theme.textTheme.bodyMedium?.copyWith(color: textColor),
              ),
            ),
          ),
          const SizedBox(height: 2),
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 6),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Text(
                  _fmtBubbleTime(message.createdAt),
                  style: theme.textTheme.labelSmall?.copyWith(
                    color: colorScheme.onSurfaceVariant,
                  ),
                ),
                if (channelText != null) ...[
                  Text(
                    '  ·  ',
                    style: theme.textTheme.labelSmall?.copyWith(
                      color: colorScheme.onSurfaceVariant,
                    ),
                  ),
                  Icon(
                    Icons.check,
                    size: 12,
                    color: colorScheme.onSurfaceVariant,
                  ),
                  const SizedBox(width: 2),
                  Text(
                    channelText,
                    style: theme.textTheme.labelSmall?.copyWith(
                      color: colorScheme.onSurfaceVariant,
                    ),
                  ),
                ],
              ],
            ),
          ),
        ],
      ),
    );
  }
}

// ── Compose bar (pinned bottom) ───────────────────────────────────────────────

class _ComposeBar extends StatelessWidget {
  const _ComposeBar({
    required this.controller,
    required this.sending,
    required this.channelsRevealed,
    required this.channels,
    required this.onRevealChannels,
    required this.portal,
    required this.email,
    required this.sms,
    required this.onPortal,
    required this.onEmail,
    required this.onSms,
    required this.onSend,
  });

  final TextEditingController controller;
  final bool sending;
  final bool channelsRevealed;
  final List<String> channels;
  final VoidCallback onRevealChannels;
  final bool portal;
  final bool email;
  final bool sms;
  final ValueChanged<bool> onPortal;
  final ValueChanged<bool> onEmail;
  final ValueChanged<bool> onSms;
  final VoidCallback onSend;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return SafeArea(
      top: false,
      child: Container(
        decoration: BoxDecoration(
          color: colorScheme.surface,
          border: Border(top: BorderSide(color: colorScheme.outlineVariant)),
        ),
        padding: const EdgeInsets.fromLTRB(8, 8, 8, 8),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            // How this reply goes out. Quiet line by default; the chips only
            // appear once the landlord taps "Change".
            Align(
              alignment: Alignment.centerLeft,
              child: channelsRevealed
                  ? Wrap(
                      spacing: 6,
                      children: [
                        _MiniChannelChip(
                          icon: Icons.forum_outlined,
                          label: 'Portal',
                          selected: portal,
                          onChanged: onPortal,
                        ),
                        _MiniChannelChip(
                          icon: Icons.email_outlined,
                          label: 'Email',
                          selected: email,
                          onChanged: onEmail,
                        ),
                        _MiniChannelChip(
                          icon: Icons.sms_outlined,
                          label: 'Text',
                          selected: sms,
                          onChanged: onSms,
                        ),
                      ],
                    )
                  : SendingChannelsLine(
                      channels: channels,
                      onChange: onRevealChannels,
                    ),
            ),
            const SizedBox(height: 6),
            TextField(
              controller: controller,
              minLines: 1,
              maxLines: 5,
              textInputAction: TextInputAction.newline,
              keyboardType: TextInputType.multiline,
              decoration: InputDecoration(
                hintText: 'Message…',
                filled: true,
                fillColor: colorScheme.surfaceContainerHighest,
                contentPadding: const EdgeInsetsDirectional.fromSTEB(
                  16,
                  10,
                  8,
                  10,
                ),
                suffixIcon: Padding(
                  padding: const EdgeInsetsDirectional.only(end: 4),
                  child: _SendButton(
                    sending: sending,
                    onSend: onSend,
                    colorScheme: colorScheme,
                  ),
                ),
                suffixIconConstraints: const BoxConstraints(
                  minWidth: 52,
                  minHeight: 48,
                ),
                border: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(24),
                  borderSide: BorderSide.none,
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _SendButton extends StatelessWidget {
  const _SendButton({
    required this.sending,
    required this.onSend,
    required this.colorScheme,
  });

  final bool sending;
  final VoidCallback onSend;
  final ColorScheme colorScheme;

  @override
  Widget build(BuildContext context) {
    return SizedBox(
      width: 48,
      height: 48,
      child: Material(
        color: colorScheme.primary,
        shape: const CircleBorder(),
        child: InkWell(
          key: const Key('message-send-button'),
          customBorder: const CircleBorder(),
          onTap: sending ? null : onSend,
          child: Center(
            child: sending
                ? SizedBox(
                    width: 20,
                    height: 20,
                    child: CircularProgressIndicator(
                      strokeWidth: 2,
                      color: colorScheme.onPrimary,
                    ),
                  )
                : Icon(Icons.send, color: colorScheme.onPrimary, size: 20),
          ),
        ),
      ),
    );
  }
}

class _MiniChannelChip extends StatelessWidget {
  const _MiniChannelChip({
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
      avatar: Icon(icon, size: 14),
      label: Text(label),
      selected: selected,
      onSelected: onChanged,
      showCheckmark: false,
      visualDensity: VisualDensity.compact,
      materialTapTargetSize: MaterialTapTargetSize.shrinkWrap,
      labelStyle: const TextStyle(fontSize: 12),
    );
  }
}

// ── Error ─────────────────────────────────────────────────────────────────────

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
