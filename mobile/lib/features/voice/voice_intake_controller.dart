import 'dart:typed_data';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/idempotent_mutation.dart';
import '../scan/scan_repository.dart';
import 'voice_error_message.dart';
import 'voice_intake_models.dart';
import 'voice_intake_repository.dart';

/// Immutable UI state for the "Tell me" conversation.
class VoiceState {
  const VoiceState({required this.phase, this.turn, this.error});

  final VoicePhase phase;
  final VoiceTurn? turn;
  final String? error;

  VoiceState copyWith({
    VoicePhase? phase,
    VoiceTurn? turn,
    String? error,
    bool clearError = false,
  }) {
    return VoiceState(
      phase: phase ?? this.phase,
      turn: turn ?? this.turn,
      error: clearError ? null : (error ?? this.error),
    );
  }
}

/// Drives the slot-filling conversation: start → (answer)* → review → save.
///
/// Platform side-effects (microphone, text-to-speech) live in the screen; this
/// controller only talks to the API and maps each server turn to a UI phase, so
/// it stays testable with a mocked repository.
class VoiceConversationController extends Notifier<VoiceState> {
  @override
  VoiceState build() => const VoiceState(phase: VoicePhase.idle);

  VoiceIntakeRepository get _repo => ref.read(voiceIntakeRepositoryProvider);

  /// Marks that recording has begun (screen sets this when the mic opens).
  void setListening() =>
      state = state.copyWith(phase: VoicePhase.listening, clearError: true);

  /// First utterance → create the draft.
  Future<void> start({Uint8List? audio, String? transcript}) async {
    final scope = 'voice:start:${_inputFingerprint(audio, transcript)}';
    await _run(
      () => IdempotentMutation.run(
        scope,
        (operationKey) => _repo.start(
          operationKey: operationKey,
          audio: audio,
          transcript: transcript,
        ),
      ),
    );
  }

  /// Subsequent answer → fill the next slot on the existing draft.
  Future<void> answer({Uint8List? audio, String? transcript}) async {
    final id = state.turn?.draftId;
    if (id == null) return;
    final scope = 'voice:answer:$id:${_inputFingerprint(audio, transcript)}';
    await _run(
      () => IdempotentMutation.run(
        scope,
        (operationKey) => _repo.answer(
          id,
          operationKey: operationKey,
          audio: audio,
          transcript: transcript,
        ),
      ),
    );
  }

  /// Confirms the completed draft via the shared scan confirm path (empty
  /// overrides ⇒ use the extracted fields as-is). Creates the record.
  Future<void> save() async {
    final id = state.turn?.draftId;
    if (id == null) return;
    state = state.copyWith(phase: VoicePhase.saving, clearError: true);
    try {
      await ref.read(scanRepositoryProvider).confirm(id, const {});
      state = state.copyWith(phase: VoicePhase.done);
    } on ApiException catch (e) {
      state = state.copyWith(phase: VoicePhase.error, error: e.message);
    }
  }

  /// Resets to a fresh conversation (call when the screen opens).
  void reset() => state = const VoiceState(phase: VoicePhase.idle);

  Future<void> _run(Future<VoiceTurn> Function() call) async {
    state = state.copyWith(phase: VoicePhase.thinking, clearError: true);
    try {
      final turn = await call();
      state = VoiceState(phase: phaseForTurn(turn), turn: turn);
    } on ApiException catch (e) {
      state = state.copyWith(
        phase: VoicePhase.error,
        error: voiceDraftErrorMessage(e),
      );
    }
  }

  static int _inputFingerprint(Uint8List? audio, String? transcript) {
    var hash = transcript?.hashCode ?? 0;
    if (audio == null) return hash;
    for (final byte in audio) {
      hash = 0x1fffffff & (hash * 31 + byte);
    }
    return Object.hash(audio.length, hash);
  }
}

final voiceConversationProvider =
    NotifierProvider<VoiceConversationController, VoiceState>(
      VoiceConversationController.new,
    );
