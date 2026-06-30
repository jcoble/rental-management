/**
 * Pure helpers for the "Record voice note" capture flow on the Scan / Add page.
 *
 * Kept free of Svelte/DOM state so each piece is unit-testable: the
 * mimeType→filename mapping (the crux of the Safari/iOS transcription bug), the
 * getUserMedia + draft error → friendly-message mappings, and the elapsed-time
 * formatter / recording limits the page enforces.
 */

/** Hard cap on a single voice note. Auto-stop at this point keeps a forgotten
 * recording from growing past Whisper's size limit and keeps notes short. */
export const VOICE_MAX_RECORDING_SECONDS = 120;

/** OpenAI Whisper rejects audio larger than 25 MB; guard the blob before upload. */
export const VOICE_MAX_AUDIO_BYTES = 25 * 1024 * 1024;

/**
 * Filename (with extension) to send with a recorded voice note.
 *
 * Whisper picks its audio decoder from the upload's file extension, so the
 * extension MUST match the bytes' real format. Safari/iOS records `audio/mp4`,
 * not the `audio/webm` other browsers use — labelling mp4 bytes `.webm` makes
 * Whisper silently fail to transcribe on the exact phone browser the landlord
 * uses. Derive the extension from the recorder/blob mimeType (codec parameters
 * like `;codecs=opus` stripped), falling back to `.webm`.
 */
export function voiceUploadFileName(mimeType: string | null | undefined): string {
	const base = (mimeType ?? '').split(';', 1)[0].trim().toLowerCase();
	const ext =
		base === 'audio/webm' ? '.webm'
		: base === 'audio/ogg' ? '.ogg'
		: base === 'audio/mp4' ? '.mp4'
		: base === 'audio/x-m4a' || base === 'audio/m4a' ? '.m4a'
		: base === 'audio/mpeg' ? '.mp3'
		: base === 'audio/wav' || base === 'audio/wave' || base === 'audio/x-wav' ? '.wav'
		: '.webm';
	return `voice${ext}`;
}

/**
 * Friendly message for a `getUserMedia` rejection. Maps the DOMException name to
 * guidance the landlord can act on (enable the mic, free it up) rather than the
 * raw `err.message`. A blocked permission and a denied-by-user prompt both arrive
 * as `NotAllowedError`; insecure-context / unsupported-browser is handled by the
 * caller before getUserMedia is ever reached.
 */
export function voiceCaptureErrorMessage(err: unknown): string {
	const name = err instanceof Error ? err.name : '';
	switch (name) {
		case 'NotAllowedError':
		case 'SecurityError':
			return "Microphone access was blocked — enable it in your browser's site settings to record a voice note.";
		case 'NotFoundError':
		case 'DevicesNotFoundError':
			return 'No microphone was found. Connect a microphone and try again.';
		case 'NotReadableError':
		case 'TrackStartError':
			return 'Your microphone is busy or unavailable. Close other apps using it and try again.';
		case 'OverconstrainedError':
			return "Your microphone doesn't support the requested settings.";
		default:
			return 'Could not start recording. Please try again.';
	}
}

/**
 * Friendly message for a failed voice-draft upload. The backend answers a `400`
 * when the audio produced no usable transcript (silence / too short / inaudible),
 * so steer the landlord to retry instead of surfacing the developer-facing
 * validation text — and never navigate to a blank draft. Other failures fall back
 * to the error's own message.
 */
export function voiceDraftErrorMessage(err: unknown): string {
	const status = (err as { status?: unknown } | null | undefined)?.status;
	if (status === 400) {
		return "Couldn't hear that — try again. Speak a little longer and closer to the mic.";
	}
	if (status === 503) {
		return "Voice transcription isn't configured yet. Set up the voice provider, then try again.";
	}
	if (err instanceof Error && err.message) return err.message;
	return 'Voice capture failed';
}

/** Format an elapsed/limit second count as `m:ss` (e.g. 72 → "1:12"). */
export function formatRecordingTime(totalSeconds: number): string {
	const safe = Number.isFinite(totalSeconds) ? Math.max(0, Math.floor(totalSeconds)) : 0;
	const minutes = Math.floor(safe / 60);
	const seconds = safe % 60;
	return `${minutes}:${seconds.toString().padStart(2, '0')}`;
}
