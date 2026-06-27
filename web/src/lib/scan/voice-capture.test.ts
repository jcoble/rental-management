import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import {
	VOICE_MAX_AUDIO_BYTES,
	VOICE_MAX_RECORDING_SECONDS,
	formatRecordingTime,
	voiceCaptureErrorMessage,
	voiceDraftErrorMessage,
	voiceUploadFileName
} from './voice-capture.ts';

describe('voiceUploadFileName', () => {
	it('maps each supported audio mime type to the right extension', () => {
		assert.equal(voiceUploadFileName('audio/webm'), 'voice.webm');
		assert.equal(voiceUploadFileName('audio/ogg'), 'voice.ogg');
		assert.equal(voiceUploadFileName('audio/mp4'), 'voice.mp4');
		assert.equal(voiceUploadFileName('audio/mpeg'), 'voice.mp3');
		assert.equal(voiceUploadFileName('audio/wav'), 'voice.wav');
		assert.equal(voiceUploadFileName('audio/x-m4a'), 'voice.m4a');
	});

	it('strips codec parameters and normalizes case/whitespace', () => {
		// Safari/iOS reports audio/mp4; Chrome reports audio/webm;codecs=opus.
		assert.equal(voiceUploadFileName('audio/mp4;codecs=mp4a.40.2'), 'voice.mp4');
		assert.equal(voiceUploadFileName('audio/webm;codecs=opus'), 'voice.webm');
		assert.equal(voiceUploadFileName('  AUDIO/WEBM  '), 'voice.webm');
	});

	it('falls back to .webm for unknown, empty, or missing mime types', () => {
		assert.equal(voiceUploadFileName('audio/flac'), 'voice.webm');
		assert.equal(voiceUploadFileName(''), 'voice.webm');
		assert.equal(voiceUploadFileName(null), 'voice.webm');
		assert.equal(voiceUploadFileName(undefined), 'voice.webm');
	});
});

describe('voiceCaptureErrorMessage', () => {
	it('explains a blocked / denied microphone permission', () => {
		const denied = new DOMException('denied', 'NotAllowedError');
		assert.match(voiceCaptureErrorMessage(denied), /Microphone access was blocked/);
		const security = new DOMException('insecure', 'SecurityError');
		assert.match(voiceCaptureErrorMessage(security), /Microphone access was blocked/);
	});

	it('distinguishes a missing mic and a busy mic', () => {
		assert.match(voiceCaptureErrorMessage(new DOMException('x', 'NotFoundError')), /No microphone was found/);
		assert.match(voiceCaptureErrorMessage(new DOMException('x', 'NotReadableError')), /busy or unavailable/);
	});

	it('falls back to a generic retry message for unknown errors', () => {
		assert.equal(voiceCaptureErrorMessage(new Error('boom')), 'Could not start recording. Please try again.');
		assert.equal(voiceCaptureErrorMessage('not an error'), 'Could not start recording. Please try again.');
	});
});

describe('voiceDraftErrorMessage', () => {
	it('turns a 400 (no usable transcript) into retry guidance', () => {
		assert.match(voiceDraftErrorMessage({ status: 400, message: 'A transcript is required.' }), /Couldn't hear that/);
	});

	it('surfaces the error message for non-400 failures', () => {
		const err = Object.assign(new Error('The server hit an error.'), { status: 500 });
		assert.equal(voiceDraftErrorMessage(err), 'The server hit an error.');
	});

	it('falls back to a generic message when there is none', () => {
		assert.equal(voiceDraftErrorMessage({}), 'Voice capture failed');
		assert.equal(voiceDraftErrorMessage(null), 'Voice capture failed');
	});
});

describe('formatRecordingTime', () => {
	it('formats seconds as m:ss with a zero-padded seconds field', () => {
		assert.equal(formatRecordingTime(0), '0:00');
		assert.equal(formatRecordingTime(9), '0:09');
		assert.equal(formatRecordingTime(72), '1:12');
		assert.equal(formatRecordingTime(VOICE_MAX_RECORDING_SECONDS), '2:00');
	});

	it('clamps negatives and non-finite input to 0:00', () => {
		assert.equal(formatRecordingTime(-5), '0:00');
		assert.equal(formatRecordingTime(Number.NaN), '0:00');
	});
});

describe('voice limits', () => {
	it('caps recordings at two minutes and 25 MB', () => {
		assert.equal(VOICE_MAX_RECORDING_SECONDS, 120);
		assert.equal(VOICE_MAX_AUDIO_BYTES, 25 * 1024 * 1024);
	});
});
