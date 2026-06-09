# Conversational Voice Intake Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans. Steps use `- [ ]` tracking. Executed inline this session.

**Goal:** Speak an expense in plain words → the app fills what it can, asks (spoken + on-screen) for any missing required field, then one-tap confirm → Expense created.

**Architecture:** Reuse the existing voice stack (`VoiceController` → `VoiceIntakeService`: Whisper transcript → grounded LLM classification → `ScanDraft`) and the `/scans/{id}/confirm` save path. Add (1) server-side required-slot evaluation + next-question, (2) a `/voice/drafts/{id}/answer` turn that appends the answer to the running transcript and re-classifies, (3) a Flutter "Tell me" conversation screen with `flutter_tts` speak-back.

**Tech Stack:** .NET 10 / ASP.NET (RentalCommand.Api), SQLite-backed unit tests (RentalCommand.Api.Tests), Flutter (Riverpod, dio, record, flutter_tts).

**Contract — `VoiceTurnResponse` (both voice endpoints return this):**
```
{ draftId:int, recordType:"Expense", fields:[{name,value,confidence}],
  missingRequired:[string], nextPrompt:string|null, complete:bool }
```
**v1 required slots:** Expense → `amount` (alias `total`) + `property_id` (answer "none/general" satisfies it). Missing required (by rule) → `nextPrompt` from a template; `complete` when none missing. Confidence/ambiguity handled by the final confirm card (not asked in v1).

---

## Server (RentalCommand.Api)

### Task 1: Slot rules + evaluation (`VoiceSlots`)
**Files:** Create `RentalCommand.Api/Services/Voice/VoiceSlots.cs`; Test `RentalCommand.Api.Tests/Voice/VoiceSlotsTests.cs`.

- [ ] Define required slots per record type and prompt templates; `Evaluate(recordType, fieldValues)` → `(IReadOnlyList<string> missing, string? nextPrompt, bool complete)`.
  - Required: `Expense → ["amount","property_id"]` (others deferred → no required slots ⇒ always complete, preserving today's single-shot behavior).
  - `amount` satisfied if `amount` OR `total` non-empty. `property_id` satisfied if non-empty OR equals the sentinel `"none"`.
  - Templates: `amount → "How much was it?"`, `property_id → "Which property is this for? You can also say it isn't for a specific property."`, default `→ "What's the {slot}?"`. `nextPrompt` = template for the first missing slot, else null.
- [ ] Tests: amount missing → missing=["amount"], prompt="How much was it?", complete=false. amount+property present → complete=true, prompt=null. `total` present satisfies amount. `property_id="none"` satisfies property. Unknown recordType → complete=true.

### Task 2: `VoiceTurnResponse` DTO + slot wiring in `VoiceIntakeService`
**Files:** Modify `RentalCommand.Api/DTOs/ScanDtos.cs` (add `VoiceTurnResponse`); Modify `RentalCommand.Api/Services/Voice/VoiceIntakeService.cs` + `IVoiceIntakeService`.
- [ ] Add `VoiceTurnResponse` record + `FromDraft(ScanDraft d)` that parses fields (reuse `ScanDraftResponse.ParseFields` logic — extract a shared helper or duplicate minimally), builds a `name→value` map, calls `VoiceSlots.Evaluate(d.TargetEntityType, map)`.
- [ ] `CreateDraftAsync` unchanged (still returns `ScanDraft`); controller wraps with `VoiceTurnResponse.FromDraft`.
- [ ] Add `IVoiceIntakeService.AnswerAsync(portfolioId, draftId, audioBytes, contentType, transcript, ct) → ScanDraft`:
  - Load draft (portfolio-scoped; 404 if missing). Transcribe answer (or use `transcript`). Read prior transcript from `fields["transcript"].value`. `combined = (prior + " " + answer).Trim()`. `ClassifyTranscriptAsync(combined)`. Merge: start from new classification fields; for any field absent/empty in new but present in prior, keep prior (so a re-class drop never loses a confirmed slot). Set `ExtractedFields`, `TargetEntityType`, `ReviewedAt`. Save. Return draft.

### Task 3: `/voice/drafts/{id}/answer` endpoint
**Files:** Modify `RentalCommand.Api/Controllers/VoiceController.cs`; Test `RentalCommand.Api.Tests/Voice/VoiceConversationTests.cs`.
- [ ] `CreateDraft` returns `VoiceTurnResponse.FromDraft(draft)` (status 201) instead of `ScanDraftResponse`.
- [ ] Add `POST drafts/{id}/answer` `[Consumes multipart/form-data]` (`audio?`, `transcript?`) → `AnswerAsync` → `Ok(VoiceTurnResponse.FromDraft(draft))`; 400 when both empty; 404 when draft not in portfolio.
- [ ] Test (transcript path, fake `ILlmProvider`/transcriber per existing `VoiceIntakeServiceTests`): create draft "log a plumbing expense for 123 Main" → missing `amount`; answer "forty dollars" → amount filled, complete=true.

### Task 4: build + server tests green
- [ ] `dotnet build` then `dotnet test RentalCommand.Api.Tests` (filter Voice). Commit.

---

## Client (mobile/)

### Task 5: dependency + models
**Files:** Modify `mobile/pubspec.yaml`; Create `mobile/lib/features/voice/voice_intake_models.dart`; Test `mobile/test/voice_intake_test.dart`.
- [ ] Add `flutter_tts: ^4.2.0`.
- [ ] `VoiceTurn` model: `draftId,int`, `recordType`, `fields: Map<String,String>` (+ confidence optional), `missingRequired: List<String>`, `nextPrompt: String?`, `complete: bool`; `fromJson`. `summary()` → plain line ("Plumbing expense · 123 Main · amount: —").
- [ ] Tests: parse a turn JSON; summary renders filled + missing fields.

### Task 6: repository
**Files:** Create `mobile/lib/features/voice/voice_intake_repository.dart`.
- [ ] `start({Uint8List? audio, String? transcript})` → POST `/voice/drafts` (multipart) → `VoiceTurn`.
- [ ] `answer(draftId, {audio, transcript})` → POST `/voice/drafts/{id}/answer` → `VoiceTurn`.
- [ ] `confirm(draftId, overrides)` → reuse `scanRepositoryProvider.confirm` (don't duplicate).

### Task 7: turn-state controller
**Files:** Create `mobile/lib/features/voice/voice_intake_controller.dart`; extend test.
- [ ] Riverpod `Notifier` holding `VoicePhase { idle, listening, thinking, asking, review, saving, done, error }` + current `VoiceTurn?` + error msg.
- [ ] Pure reducer `phaseFor(VoiceTurn)` → `review` when `complete`, else `asking`. Unit-test the reducer.

### Task 8: "Tell me" screen + TTS
**Files:** Create `mobile/lib/features/voice/tell_me_screen.dart`.
- [ ] Big mic (reuse `AudioRecorder` from `scan_capture.dart` pattern: aacLc m4a). Gate mic while TTS speaks (await `flutter_tts` completion).
- [ ] On open: idle → tap mic → record → stop → `start(audio)` → thinking. If `!complete`: speak+show `nextPrompt`, phase=asking; tap mic → record → `answer(draftId, audio)`. Loop. On `complete`: review card with `summary()` + **[Save it]/[Fix]**. Save → `confirm(draftId, {})` → toast + pop. Fix → push existing scan review screen for that draft id. Errors → friendly retry.

### Task 9: entry point on Home
**Files:** Modify `mobile/lib/features/home/home_shell.dart`.
- [ ] Add a prominent "Tell me" mic quick-action (alongside Scan/Ask AI/Add expense) that pushes `TellMeScreen`. (App Actions doorway wiring is a later, separate task.)

### Task 10: analyze + tests + verify
- [ ] `flutter analyze` clean; `flutter test`.
- [ ] Verify server conversation end-to-end against the running dev API via `curl` using the `transcript=` form field (no audio needed): start → missing amount + prompt; answer → complete. Commit.

---

## Self-review notes
- Spec coverage: slot-awareness (T1–2), answer turn (T2–3), client conversation + TTS + confirm (T5–9), tests (T1,3,5,7,10). ✓
- Backwards-compat: empty required ⇒ complete ⇒ scan capture's single-shot voice note unaffected. ✓
- Reuse: confirm via existing scan repo; transcription/classification/grounding untouched. ✓
- Deferred (not in this plan): Payment/WorkOrder slots, ambiguity options, hands-free, App Actions doorway, spoken Q&A.
