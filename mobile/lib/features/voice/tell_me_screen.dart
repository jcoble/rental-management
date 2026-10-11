import 'dart:async';
import 'dart:io';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_tts/flutter_tts.dart';
import 'package:record/record.dart';

import '../scan/scan_review_screen.dart';
import 'voice_intake_controller.dart';
import 'voice_intake_models.dart';

/// "Tell me" — the conversational voice intake. Tap the mic and speak a command
/// in plain words; the app fills what it can, asks (out loud + on screen) for
/// any missing required field, then shows a one-tap "Save it?" card.
///
/// Microphone (record) and text-to-speech live here; the API conversation and
/// phase logic live in [VoiceConversationController].
class TellMeScreen extends ConsumerStatefulWidget {
  const TellMeScreen({super.key});

  @override
  ConsumerState<TellMeScreen> createState() => _TellMeScreenState();
}

class _TellMeScreenState extends ConsumerState<TellMeScreen> {
  final AudioRecorder _recorder = AudioRecorder();
  final FlutterTts _tts = FlutterTts();

  bool _recording = false;
  bool _speaking = false;
  String? _recordingPath;
  String? _lastSpokenPrompt;

  @override
  void initState() {
    super.initState();
    _tts.setCompletionHandler(() {
      if (mounted) setState(() => _speaking = false);
    });
    _tts.setCancelHandler(() {
      if (mounted) setState(() => _speaking = false);
    });
    _tts.setErrorHandler((_) {
      if (mounted) setState(() => _speaking = false);
    });
    // Start every visit on a clean conversation.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) ref.read(voiceConversationProvider.notifier).reset();
    });
  }

  @override
  void dispose() {
    _recorder.dispose();
    _tts.stop();
    super.dispose();
  }

  Future<void> _speak(String text) async {
    _lastSpokenPrompt = text;
    try {
      await _tts.stop();
      setState(() => _speaking = true);
      await _tts.speak(text);
    } catch (_) {
      if (mounted) setState(() => _speaking = false);
    }
  }

  Future<void> _toggleMic() async {
    if (_speaking) return;
    if (_recording) {
      await _stopAndSend();
    } else {
      await _startRecording();
    }
  }

  Future<void> _startRecording() async {
    final messenger = ScaffoldMessenger.of(context);
    try {
      if (!await _recorder.hasPermission()) {
        messenger.showSnackBar(
          const SnackBar(content: Text('Microphone permission is required.')),
        );
        return;
      }
      await _tts.stop();
      final path =
          '${Directory.systemTemp.path}/rc-tellme-${DateTime.now().microsecondsSinceEpoch}.m4a';
      await _recorder.start(
        const RecordConfig(
          encoder: AudioEncoder.aacLc,
          bitRate: 64000,
          sampleRate: 44100,
          numChannels: 1,
          noiseSuppress: true,
        ),
        path: path,
      );
      if (!mounted) return;
      setState(() {
        _recording = true;
        _recordingPath = path;
      });
      ref.read(voiceConversationProvider.notifier).setListening();
    } catch (_) {
      if (mounted) {
        messenger.showSnackBar(
          const SnackBar(content: Text('Could not start recording.')),
        );
      }
    }
  }

  Future<void> _stopAndSend() async {
    final path = await _recorder.stop() ?? _recordingPath;
    if (!mounted) return;
    setState(() {
      _recording = false;
      _recordingPath = null;
    });
    if (path == null) return;

    final file = File(path);
    final bytes = Uint8List.fromList(await file.readAsBytes());
    if (!mounted) return;
    unawaited(file.delete().catchError((_) => file));

    final controller = ref.read(voiceConversationProvider.notifier);
    final hasDraft = ref.read(voiceConversationProvider).turn != null;
    if (hasDraft) {
      await controller.answer(audio: bytes);
    } else {
      await controller.start(audio: bytes);
    }
  }

  void _openFix() {
    final id = ref.read(voiceConversationProvider).turn?.draftId;
    if (id == null) return;
    Navigator.of(context).push<void>(
      MaterialPageRoute<void>(builder: (_) => ScanReviewScreen(draftId: id)),
    );
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(voiceConversationProvider);

    // Side-effects when the server turn changes phase.
    ref.listen<VoiceState>(voiceConversationProvider, (prev, next) {
      if (next.phase == VoicePhase.asking) {
        final prompt = next.turn?.nextPrompt;
        if (prompt != null && prompt != _lastSpokenPrompt) _speak(prompt);
      } else if (next.phase == VoicePhase.review &&
          prev?.phase != VoicePhase.review) {
        _speak('Got it. Does this look right?');
      } else if (next.phase == VoicePhase.done &&
          prev?.phase != VoicePhase.done) {
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(content: Text('Saved.')),
        );
        Navigator.of(context).pop();
      }
    });

    return Scaffold(
      appBar: AppBar(title: const Text('Tell me')),
      body: SafeArea(
        child: Padding(
          padding: const EdgeInsets.all(20),
          child: _body(state),
        ),
      ),
    );
  }

  Widget _body(VoiceState state) {
    final turn = state.turn;
    return Column(
      children: [
        Expanded(
          child: SingleChildScrollView(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Text(
                  _statusText(state),
                  style: Theme.of(context).textTheme.titleMedium,
                ),
                if (turn != null) ...[
                  const SizedBox(height: 16),
                  _DraftCard(turn: turn),
                ],
                if (state.phase == VoicePhase.error && state.error != null) ...[
                  const SizedBox(height: 16),
                  _ErrorCard(message: state.error!),
                ],
              ],
            ),
          ),
        ),
        if (state.phase == VoicePhase.review && turn != null)
          _reviewActions()
        else if (turn?.ambiguous == true)
          Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              _ambiguousActions(),
              _micArea(state),
            ],
          )
        else
          _micArea(state),
      ],
    );
  }

  String _statusText(VoiceState state) {
    switch (state.phase) {
      case VoicePhase.idle:
        return 'Tap the mic and tell me what to log — for example, '
            '"log a forty dollar plumbing expense for 123 Main."';
      case VoicePhase.listening:
        return "Listening… tap again when you're done.";
      case VoicePhase.thinking:
        return 'One moment…';
      case VoicePhase.asking:
        return state.turn?.nextPrompt ?? 'I need a bit more info.';
      case VoicePhase.review:
        return 'Here\'s what I heard. Save it?';
      case VoicePhase.saving:
        return 'Saving…';
      case VoicePhase.done:
        return 'Saved.';
      case VoicePhase.error:
        return 'Something went wrong.';
    }
  }

  Widget _micArea(VoiceState state) {
    final cs = Theme.of(context).colorScheme;
    final busy =
        state.phase == VoicePhase.thinking || state.phase == VoicePhase.saving;
    final canTap = !busy && !_speaking;

    return Padding(
      padding: const EdgeInsets.only(top: 16),
      child: Column(
        children: [
          GestureDetector(
            onTap: canTap ? _toggleMic : null,
            child: Container(
              width: 96,
              height: 96,
              decoration: BoxDecoration(
                shape: BoxShape.circle,
                color: !canTap
                    ? cs.surfaceContainerHighest
                    : _recording
                    ? cs.error
                    : cs.primary,
              ),
              child: busy
                  ? const Padding(
                      padding: EdgeInsets.all(28),
                      child: CircularProgressIndicator(
                        color: Colors.white,
                        strokeWidth: 3,
                      ),
                    )
                  : Icon(
                      _recording ? Icons.stop : Icons.mic,
                      color: canTap ? Colors.white : cs.onSurfaceVariant,
                      size: 40,
                    ),
            ),
          ),
          const SizedBox(height: 12),
          Text(_micLabel(state), style: Theme.of(context).textTheme.bodyMedium),
        ],
      ),
    );
  }

  String _micLabel(VoiceState state) {
    if (_speaking) return 'Speaking…';
    if (state.phase == VoicePhase.thinking) return 'Working…';
    if (state.phase == VoicePhase.saving) return 'Saving…';
    if (_recording) return 'Tap to stop';
    if (state.phase == VoicePhase.error) return 'Tap to try again';
    if (state.turn == null) return 'Tap to talk';
    return 'Tap to answer';
  }

  Widget _reviewActions() {
    return Padding(
      padding: const EdgeInsets.only(top: 12),
      child: Row(
        children: [
          Expanded(
            child: OutlinedButton(
              onPressed: _openFix,
              child: const Text('Fix'),
            ),
          ),
          const SizedBox(width: 12),
          Expanded(
            flex: 2,
            child: FilledButton.icon(
              onPressed: () =>
                  ref.read(voiceConversationProvider.notifier).save(),
              icon: const Icon(Icons.check),
              label: const Text('Save it'),
            ),
          ),
        ],
      ),
    );
  }

  Widget _ambiguousActions() {
    return Padding(
      padding: const EdgeInsets.only(top: 12),
      child: OutlinedButton.icon(
        onPressed: _openFix,
        icon: const Icon(Icons.edit_outlined),
        label: const Text('Review manually'),
      ),
    );
  }
}

/// Shows the draft captured so far: filled fields, what's still needed, and
/// the running transcript so the landlord can see it heard him right.
class _DraftCard extends StatelessWidget {
  const _DraftCard({required this.turn});

  final VoiceTurn turn;

  static String _slotLabel(String slot) {
    switch (slot) {
      case 'amount':
        return 'Amount';
      case 'property_id':
        return 'Property';
      default:
        return slot;
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final fields = turn.displayFields;

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              turn.recordType.isEmpty ? 'Draft' : turn.recordType,
              style: theme.textTheme.titleMedium?.copyWith(
                fontWeight: FontWeight.w700,
              ),
            ),
            if (turn.ambiguous) ...[
              const SizedBox(height: 8),
              Text(
                'I need clarification before this can be saved by voice.',
                style: TextStyle(
                  color: cs.error,
                  fontWeight: FontWeight.w600,
                ),
              ),
            ],
            const SizedBox(height: 8),
            if (fields.isEmpty)
              Text(
                'Listening for details…',
                style: TextStyle(color: cs.onSurfaceVariant),
              )
            else
              for (final field in fields)
                Padding(
                  padding: const EdgeInsets.symmetric(vertical: 2),
                  child: Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      SizedBox(
                        width: 92,
                        child: Text(
                          field.label,
                          style: TextStyle(color: cs.onSurfaceVariant),
                        ),
                      ),
                      Expanded(
                        child: Text(
                          field.value,
                          style: const TextStyle(fontWeight: FontWeight.w600),
                        ),
                      ),
                    ],
                  ),
                ),
            for (final slot in turn.missingRequired)
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 2),
                child: Row(
                  children: [
                    SizedBox(
                      width: 92,
                      child: Text(
                        _slotLabel(slot),
                        style: TextStyle(color: cs.onSurfaceVariant),
                      ),
                    ),
                    Text('— still needed', style: TextStyle(color: cs.error)),
                  ],
                ),
              ),
            if (turn.transcript != null) ...[
              const SizedBox(height: 10),
              Text(
                'You said: "${turn.transcript}"',
                style: theme.textTheme.bodySmall?.copyWith(
                  color: cs.onSurfaceVariant,
                  fontStyle: FontStyle.italic,
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }
}

class _ErrorCard extends StatelessWidget {
  const _ErrorCard({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Card(
      color: cs.errorContainer,
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Row(
          children: [
            Icon(Icons.error_outline, color: cs.onErrorContainer, size: 20),
            const SizedBox(width: 12),
            Expanded(
              child: Text(
                message,
                style: TextStyle(color: cs.onErrorContainer),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
