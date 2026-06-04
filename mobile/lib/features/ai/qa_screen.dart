import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import 'ai_models.dart';
import 'ai_repository.dart';

// ── State ─────────────────────────────────────────────────────────────────────

class _QaState {
  const _QaState({
    this.history = const [],
    this.pending = false,
    this.llmUnavailable = false,
    this.lastResponse,
    this.lastQuestion,
    this.deliveringChannel,
    this.deliveryNote,
    this.error,
  });

  final List<QaTurn> history;
  final bool pending;
  final bool llmUnavailable;
  final AskResponse? lastResponse;

  /// The question that produced [lastResponse], so it can be re-delivered.
  final String? lastQuestion;

  /// Channel currently being delivered (`'email'` | `'sms'`), or null when idle.
  final String? deliveringChannel;

  /// Short status note shown under the latest answer after a delivery attempt.
  final String? deliveryNote;

  final String? error;

  _QaState copyWith({
    List<QaTurn>? history,
    bool? pending,
    bool? llmUnavailable,
    AskResponse? lastResponse,
    String? lastQuestion,
    String? deliveringChannel,
    String? deliveryNote,
    String? error,
    bool clearError = false,
    bool clearLastResponse = false,
    bool clearDeliveringChannel = false,
    bool clearDeliveryNote = false,
  }) {
    return _QaState(
      history: history ?? this.history,
      pending: pending ?? this.pending,
      llmUnavailable: llmUnavailable ?? this.llmUnavailable,
      lastResponse: clearLastResponse ? null : (lastResponse ?? this.lastResponse),
      lastQuestion: clearLastResponse ? null : (lastQuestion ?? this.lastQuestion),
      deliveringChannel:
          clearDeliveringChannel ? null : (deliveringChannel ?? this.deliveringChannel),
      deliveryNote: clearDeliveryNote ? null : (deliveryNote ?? this.deliveryNote),
      error: clearError ? null : (error ?? this.error),
    );
  }
}

class _QaNotifier extends Notifier<_QaState> {
  @override
  _QaState build() => const _QaState();

  Future<void> ask(String question) async {
    if (question.trim().isEmpty || state.pending) return;

    final userTurn = QaTurn(role: 'user', content: question.trim());
    final historyForRequest = List<QaTurn>.from(state.history);

    state = state.copyWith(
      history: [...state.history, userTurn],
      pending: true,
      clearError: true,
      clearDeliveryNote: true,
    );

    try {
      final response = await ref
          .read(aiRepositoryProvider)
          .ask(question.trim(), historyForRequest);

      final assistantTurn = QaTurn(role: 'assistant', content: response.answer);
      state = state.copyWith(
        history: [...state.history, assistantTurn],
        pending: false,
        lastResponse: response,
        lastQuestion: question.trim(),
        llmUnavailable: !response.llmAvailable,
      );
    } on ApiException catch (e) {
      // Remove the optimistic user turn.
      final trimmed = List<QaTurn>.from(state.history)..removeLast();
      state = state.copyWith(
        history: trimmed,
        pending: false,
        error: e.message,
        clearLastResponse: true,
      );
    } catch (e) {
      final trimmed = List<QaTurn>.from(state.history)..removeLast();
      state = state.copyWith(
        history: trimmed,
        pending: false,
        error: e.toString(),
        clearLastResponse: true,
      );
    }
  }

  /// Re-runs the last question with delivery turned on so the landlord gets the
  /// answer as a text or email. [channel] is `'email'` or `'sms'`.
  Future<void> deliver(String channel) async {
    final question = state.lastQuestion;
    if (question == null || state.pending || state.deliveringChannel != null) {
      return;
    }

    // History excludes the final assistant turn (the answer being delivered).
    final historyForRequest = state.history.length >= 2
        ? state.history.sublist(0, state.history.length - 2)
        : <QaTurn>[];

    final delivery = channel == 'email'
        ? const AskDelivery(viaEmail: true)
        : const AskDelivery(viaSms: true);

    state = state.copyWith(deliveringChannel: channel, clearDeliveryNote: true);

    try {
      final response = await ref
          .read(aiRepositoryProvider)
          .ask(question, historyForRequest, delivery: delivery);

      final ok = channel == 'email'
          ? response.deliveredChannels.contains('Email')
          : response.deliveredChannels.contains('Sms');
      final note = ok
          ? (channel == 'email' ? 'Emailed to you.' : 'Texted to you.')
          : (channel == 'email'
              ? "Couldn't email — no address on file."
              : "Couldn't text — no phone on file.");
      state = state.copyWith(deliveryNote: note, clearDeliveringChannel: true);
    } on ApiException catch (e) {
      state = state.copyWith(deliveryNote: e.message, clearDeliveringChannel: true);
    } catch (e) {
      state = state.copyWith(
        deliveryNote: 'Delivery failed.',
        clearDeliveringChannel: true,
      );
    }
  }
}

final _qaProvider = NotifierProvider.autoDispose<_QaNotifier, _QaState>(
  _QaNotifier.new,
);

// ── Constants ─────────────────────────────────────────────────────────────────

const _examplePrompts = [
  "Who's late on rent?",
  'How much did I collect?',
  'Any leases expiring soon?',
  'What needs maintenance?',
];

/// Converts a raw API tool name into a human-readable label.
///
/// Rules (applied in order):
///   1. Strip a leading `get_`, `list_`, or `fetch_` prefix.
///   2. Replace every `_` with a space.
///
/// Examples:
///   `get_financial_summary` → `financial summary`
///   `list_late_payments`    → `late payments`
///   `property_overview`     → `property overview`
String friendlyToolName(String raw) {
  var name = raw;
  if (name.startsWith('get_')) {
    name = name.substring(4);
  } else if (name.startsWith('list_')) {
    name = name.substring(5);
  } else if (name.startsWith('fetch_')) {
    name = name.substring(6);
  }
  return name.replaceAll('_', ' ');
}

// ── Screen ────────────────────────────────────────────────────────────────────

/// Portfolio Q&A chat screen.
class QaScreen extends ConsumerStatefulWidget {
  const QaScreen({super.key});

  @override
  ConsumerState<QaScreen> createState() => _QaScreenState();
}

class _QaScreenState extends ConsumerState<QaScreen> {
  final _inputController = TextEditingController();
  final _scrollController = ScrollController();
  final _focusNode = FocusNode();

  @override
  void dispose() {
    _inputController.dispose();
    _scrollController.dispose();
    _focusNode.dispose();
    super.dispose();
  }

  void _submit() {
    final text = _inputController.text.trim();
    if (text.isEmpty) return;
    _inputController.clear();
    ref.read(_qaProvider.notifier).ask(text);
    _scrollToBottom();
  }

  void _usePrompt(String prompt) {
    _inputController.text = prompt;
    _focusNode.requestFocus();
  }

  void _scrollToBottom() {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (_scrollController.hasClients) {
        _scrollController.animateTo(
          _scrollController.position.maxScrollExtent,
          duration: const Duration(milliseconds: 250),
          curve: Curves.easeOut,
        );
      }
    });
  }

  @override
  Widget build(BuildContext context) {
    final qa = ref.watch(_qaProvider);

    // Scroll to bottom after new turns land.
    ref.listen<_QaState>(_qaProvider, (prev, next) {
      if (next.history.length != prev?.history.length || next.pending != prev?.pending) {
        _scrollToBottom();
      }
    });

    final theme = Theme.of(context);

    return Column(
      children: [
        // AI-off banner.
        if (qa.llmUnavailable)
          _AiOffBanner(theme: theme),

        // Error snack-like inline banner.
        if (qa.error != null)
          _ErrorBanner(message: qa.error!, theme: theme),

        // Chat area.
        Expanded(
          child: qa.history.isEmpty && !qa.pending
              ? _EmptyChat(onPromptTap: _usePrompt)
              : _ChatList(
                  history: qa.history,
                  pending: qa.pending,
                  lastResponse: qa.lastResponse,
                  canDeliver: qa.lastQuestion != null && !qa.pending,
                  deliveringChannel: qa.deliveringChannel,
                  deliveryNote: qa.deliveryNote,
                  onDeliver: (channel) =>
                      ref.read(_qaProvider.notifier).deliver(channel),
                  scrollController: _scrollController,
                  onPromptTap: _usePrompt,
                ),
        ),

        // Input row.
        _InputRow(
          controller: _inputController,
          focusNode: _focusNode,
          pending: qa.pending,
          onSubmit: _submit,
        ),
      ],
    );
  }
}

// ── Banners ───────────────────────────────────────────────────────────────────

class _AiOffBanner extends StatelessWidget {
  const _AiOffBanner({required this.theme});

  final ThemeData theme;

  @override
  Widget build(BuildContext context) {
    return Container(
      width: double.infinity,
      color: Colors.amber.shade50,
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(Icons.warning_amber_rounded,
              size: 16, color: Colors.amber.shade800),
          const SizedBox(width: 8),
          Expanded(
            child: Text(
              'AI is off — no API key configured. Answers are rule-based only.',
              style: theme.textTheme.bodySmall?.copyWith(
                color: Colors.amber.shade900,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _ErrorBanner extends StatelessWidget {
  const _ErrorBanner({required this.message, required this.theme});

  final String message;
  final ThemeData theme;

  @override
  Widget build(BuildContext context) {
    return Container(
      width: double.infinity,
      color: theme.colorScheme.errorContainer,
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
      child: Text(
        message,
        style: theme.textTheme.bodySmall?.copyWith(
          color: theme.colorScheme.onErrorContainer,
        ),
      ),
    );
  }
}

// ── Empty state ───────────────────────────────────────────────────────────────

class _EmptyChat extends StatelessWidget {
  const _EmptyChat({required this.onPromptTap});

  final ValueChanged<String> onPromptTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Center(
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              Icons.chat_bubble_outline_rounded,
              size: 40,
              color: theme.colorScheme.onSurfaceVariant.withValues(alpha: 0.5),
            ),
            const SizedBox(height: 12),
            Text(
              'Try one of these, or ask your own question.',
              textAlign: TextAlign.center,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 20),
            Wrap(
              spacing: 8,
              runSpacing: 8,
              alignment: WrapAlignment.center,
              children: _examplePrompts
                  .map((p) => _PromptChip(label: p, onTap: onPromptTap))
                  .toList(),
            ),
          ],
        ),
      ),
    );
  }
}

// ── Chat list ─────────────────────────────────────────────────────────────────

class _ChatList extends StatelessWidget {
  const _ChatList({
    required this.history,
    required this.pending,
    required this.lastResponse,
    required this.canDeliver,
    required this.deliveringChannel,
    required this.deliveryNote,
    required this.onDeliver,
    required this.scrollController,
    required this.onPromptTap,
  });

  final List<QaTurn> history;
  final bool pending;
  final AskResponse? lastResponse;
  final bool canDeliver;
  final String? deliveringChannel;
  final String? deliveryNote;
  final ValueChanged<String> onDeliver;
  final ScrollController scrollController;
  final ValueChanged<String> onPromptTap;

  @override
  Widget build(BuildContext context) {
    // Rows: all history turns + optional typing indicator.
    final extraCount = pending ? 1 : 0;
    final total = history.length + extraCount;

    return ListView.builder(
      controller: scrollController,
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
      itemCount: total,
      itemBuilder: (context, index) {
        if (index == history.length && pending) {
          return const _TypingIndicator();
        }
        final turn = history[index];
        final isLastAssistant =
            turn.role == 'assistant' && index == history.length - 1;

        return _TurnBubble(
          turn: turn,
          isLastAssistant: isLastAssistant,
          lastResponse: isLastAssistant ? lastResponse : null,
          canDeliver: isLastAssistant && canDeliver,
          deliveringChannel: isLastAssistant ? deliveringChannel : null,
          deliveryNote: isLastAssistant ? deliveryNote : null,
          onDeliver: onDeliver,
        );
      },
    );
  }
}

// ── Bubbles ───────────────────────────────────────────────────────────────────

class _TurnBubble extends StatelessWidget {
  const _TurnBubble({
    required this.turn,
    required this.isLastAssistant,
    this.lastResponse,
    this.canDeliver = false,
    this.deliveringChannel,
    this.deliveryNote,
    this.onDeliver,
  });

  final QaTurn turn;
  final bool isLastAssistant;
  final AskResponse? lastResponse;
  final bool canDeliver;
  final String? deliveringChannel;
  final String? deliveryNote;
  final ValueChanged<String>? onDeliver;

  @override
  Widget build(BuildContext context) {
    return turn.role == 'user'
        ? _UserBubble(content: turn.content)
        : _AssistantBubble(
            content: turn.content,
            lastResponse: isLastAssistant ? lastResponse : null,
            canDeliver: canDeliver,
            deliveringChannel: deliveringChannel,
            deliveryNote: deliveryNote,
            onDeliver: onDeliver,
          );
  }
}

class _UserBubble extends StatelessWidget {
  const _UserBubble({required this.content});

  final String content;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.only(bottom: 10),
      child: Row(
        mainAxisAlignment: MainAxisAlignment.end,
        children: [
          Flexible(
            child: Container(
              constraints: BoxConstraints(
                maxWidth: MediaQuery.sizeOf(context).width * 0.75,
              ),
              decoration: BoxDecoration(
                color: theme.colorScheme.primary,
                borderRadius: const BorderRadius.only(
                  topLeft: Radius.circular(18),
                  topRight: Radius.circular(18),
                  bottomLeft: Radius.circular(18),
                  bottomRight: Radius.circular(4),
                ),
              ),
              padding:
                  const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
              child: Text(
                content,
                style: theme.textTheme.bodyMedium?.copyWith(
                  color: theme.colorScheme.onPrimary,
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _AssistantBubble extends StatelessWidget {
  const _AssistantBubble({
    required this.content,
    this.lastResponse,
    this.canDeliver = false,
    this.deliveringChannel,
    this.deliveryNote,
    this.onDeliver,
  });

  final String content;
  final AskResponse? lastResponse;
  final bool canDeliver;
  final String? deliveringChannel;
  final String? deliveryNote;
  final ValueChanged<String>? onDeliver;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final resp = lastResponse;

    return Padding(
      padding: const EdgeInsets.only(bottom: 10),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Flexible(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Container(
                  constraints: BoxConstraints(
                    maxWidth: MediaQuery.sizeOf(context).width * 0.82,
                  ),
                  decoration: BoxDecoration(
                    color: theme.colorScheme.surfaceContainerHighest
                        .withValues(alpha: 0.5),
                    border: Border.all(
                      color: theme.colorScheme.outlineVariant,
                      width: 0.5,
                    ),
                    borderRadius: const BorderRadius.only(
                      topLeft: Radius.circular(4),
                      topRight: Radius.circular(18),
                      bottomLeft: Radius.circular(18),
                      bottomRight: Radius.circular(18),
                    ),
                  ),
                  padding: const EdgeInsets.symmetric(
                      horizontal: 14, vertical: 10),
                  child: Text(
                    content,
                    style: theme.textTheme.bodyMedium,
                  ),
                ),
                if (resp != null && resp.toolsUsed.isNotEmpty) ...[
                  const SizedBox(height: 4),
                  _ToolsUsedLine(response: resp),
                ],
                if (canDeliver && onDeliver != null) ...[
                  const SizedBox(height: 4),
                  _DeliverActions(
                    deliveringChannel: deliveringChannel,
                    onDeliver: onDeliver!,
                  ),
                ],
                if (deliveryNote != null) ...[
                  const SizedBox(height: 2),
                  Padding(
                    padding: const EdgeInsets.only(left: 4),
                    child: Text(
                      deliveryNote!,
                      style: theme.textTheme.labelSmall?.copyWith(
                        color: theme.colorScheme.onSurfaceVariant
                            .withValues(alpha: 0.8),
                      ),
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

// Small "Text me this" / "Email me this" action row under the latest answer.
class _DeliverActions extends StatelessWidget {
  const _DeliverActions({
    required this.deliveringChannel,
    required this.onDeliver,
  });

  final String? deliveringChannel;
  final ValueChanged<String> onDeliver;

  @override
  Widget build(BuildContext context) {
    final busy = deliveringChannel != null;
    return Padding(
      padding: const EdgeInsets.only(left: 4),
      child: Wrap(
        spacing: 4,
        children: [
          _DeliverButton(
            icon: Icons.sms_outlined,
            label: deliveringChannel == 'sms' ? 'Texting…' : 'Text me this',
            onPressed: busy ? null : () => onDeliver('sms'),
          ),
          _DeliverButton(
            icon: Icons.mail_outline_rounded,
            label: deliveringChannel == 'email' ? 'Emailing…' : 'Email me this',
            onPressed: busy ? null : () => onDeliver('email'),
          ),
        ],
      ),
    );
  }
}

class _DeliverButton extends StatelessWidget {
  const _DeliverButton({
    required this.icon,
    required this.label,
    required this.onPressed,
  });

  final IconData icon;
  final String label;
  final VoidCallback? onPressed;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return TextButton.icon(
      onPressed: onPressed,
      icon: Icon(icon, size: 16),
      label: Text(label),
      style: TextButton.styleFrom(
        foregroundColor: theme.colorScheme.onSurfaceVariant,
        padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
        minimumSize: Size.zero,
        tapTargetSize: MaterialTapTargetSize.shrinkWrap,
        textStyle: theme.textTheme.labelMedium,
      ),
    );
  }
}

// Small muted line: "Looked at: {tools} · {tokens} tokens"
class _ToolsUsedLine extends StatelessWidget {
  const _ToolsUsedLine({required this.response});

  final AskResponse response;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final toolLabels =
        response.toolsUsed.map(friendlyToolName).join(', ');
    final tokenPart =
        response.tokensUsed > 0 ? ' · ${response.tokensUsed} tokens' : '';

    return Padding(
      padding: const EdgeInsets.only(left: 4),
      child: Text(
        'Looked at: $toolLabels$tokenPart',
        style: theme.textTheme.labelSmall?.copyWith(
          color: theme.colorScheme.onSurfaceVariant.withValues(alpha: 0.7),
        ),
      ),
    );
  }
}

// ── Typing indicator ──────────────────────────────────────────────────────────

class _TypingIndicator extends StatefulWidget {
  const _TypingIndicator();

  @override
  State<_TypingIndicator> createState() => _TypingIndicatorState();
}

class _TypingIndicatorState extends State<_TypingIndicator>
    with SingleTickerProviderStateMixin {
  late final AnimationController _controller;

  @override
  void initState() {
    super.initState();
    _controller = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 900),
    )..repeat();
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.only(bottom: 10),
      child: Row(
        children: [
          Container(
            decoration: BoxDecoration(
              color: theme.colorScheme.surfaceContainerHighest
                  .withValues(alpha: 0.5),
              border: Border.all(
                color: theme.colorScheme.outlineVariant,
                width: 0.5,
              ),
              borderRadius: const BorderRadius.only(
                topLeft: Radius.circular(4),
                topRight: Radius.circular(18),
                bottomLeft: Radius.circular(18),
                bottomRight: Radius.circular(18),
              ),
            ),
            padding:
                const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
            child: AnimatedBuilder(
              animation: _controller,
              builder: (context, _) {
                return Row(
                  mainAxisSize: MainAxisSize.min,
                  children: List.generate(3, (i) {
                    // Each dot peaks at a different phase: 0, 0.33, 0.66.
                    final phase = i / 3.0;
                    final t =
                        ((_controller.value - phase + 1.0) % 1.0);
                    // Bounce: rise for first half, fall for second.
                    final offset = t < 0.5
                        ? -4.0 * (t / 0.5)
                        : -4.0 * (1.0 - (t - 0.5) / 0.5);
                    return Padding(
                      padding: EdgeInsets.only(
                          left: i == 0 ? 0 : 4),
                      child: Transform.translate(
                        offset: Offset(0, offset),
                        child: Container(
                          width: 6,
                          height: 6,
                          decoration: BoxDecoration(
                            shape: BoxShape.circle,
                            color: theme.colorScheme.onSurfaceVariant
                                .withValues(alpha: 0.6),
                          ),
                        ),
                      ),
                    );
                  }),
                );
              },
            ),
          ),
        ],
      ),
    );
  }
}

// ── Input row ─────────────────────────────────────────────────────────────────

class _InputRow extends StatelessWidget {
  const _InputRow({
    required this.controller,
    required this.focusNode,
    required this.pending,
    required this.onSubmit,
  });

  final TextEditingController controller;
  final FocusNode focusNode;
  final bool pending;
  final VoidCallback onSubmit;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return SafeArea(
      top: false,
      child: Container(
        decoration: BoxDecoration(
          border: Border(
            top: BorderSide(
              color: theme.colorScheme.outlineVariant,
              width: 0.5,
            ),
          ),
        ),
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
        child: Row(
          children: [
            Expanded(
              child: ValueListenableBuilder<TextEditingValue>(
                valueListenable: controller,
                builder: (context, value, _) {
                  return TextField(
                    controller: controller,
                    focusNode: focusNode,
                    enabled: !pending,
                    maxLines: 4,
                    minLines: 1,
                    textInputAction: TextInputAction.send,
                    onSubmitted: (_) => onSubmit(),
                    decoration: InputDecoration(
                      hintText: 'Ask anything about your properties…',
                      hintStyle: theme.textTheme.bodyMedium?.copyWith(
                        color: theme.colorScheme.onSurfaceVariant,
                      ),
                      contentPadding: const EdgeInsets.symmetric(
                        horizontal: 14,
                        vertical: 10,
                      ),
                      filled: true,
                      fillColor:
                          theme.colorScheme.surfaceContainerHighest.withValues(alpha: 0.4),
                      border: OutlineInputBorder(
                        borderRadius: BorderRadius.circular(24),
                        borderSide: BorderSide.none,
                      ),
                      enabledBorder: OutlineInputBorder(
                        borderRadius: BorderRadius.circular(24),
                        borderSide: BorderSide(
                          color: theme.colorScheme.outlineVariant,
                          width: 0.5,
                        ),
                      ),
                      focusedBorder: OutlineInputBorder(
                        borderRadius: BorderRadius.circular(24),
                        borderSide: BorderSide(
                          color: theme.colorScheme.primary,
                          width: 1.5,
                        ),
                      ),
                    ),
                  );
                },
              ),
            ),
            const SizedBox(width: 8),
            ValueListenableBuilder<TextEditingValue>(
              valueListenable: controller,
              builder: (context, value, _) {
                final canSend = value.text.trim().isNotEmpty && !pending;
                return IconButton.filled(
                  onPressed: canSend ? onSubmit : null,
                  icon: pending
                      ? SizedBox(
                          width: 18,
                          height: 18,
                          child: CircularProgressIndicator(
                            strokeWidth: 2,
                            color: theme.colorScheme.onPrimary,
                          ),
                        )
                      : const Icon(Icons.send_rounded),
                );
              },
            ),
          ],
        ),
      ),
    );
  }
}

// ── Prompt chip ───────────────────────────────────────────────────────────────

class _PromptChip extends StatelessWidget {
  const _PromptChip({required this.label, required this.onTap});

  final String label;
  final ValueChanged<String> onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return InkWell(
      onTap: () => onTap(label),
      borderRadius: BorderRadius.circular(20),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 8),
        decoration: BoxDecoration(
          border: Border.all(color: theme.colorScheme.outlineVariant),
          borderRadius: BorderRadius.circular(20),
          color: theme.colorScheme.surfaceContainerHighest.withValues(alpha: 0.4),
        ),
        child: Text(label, style: theme.textTheme.labelMedium),
      ),
    );
  }
}
