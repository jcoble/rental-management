import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
	CONVERSATION_SUBJECT_MAX_LENGTH,
	conversationSubjectError
} from './conversation-compose.ts';

test('S26-POT-2 mirrors the API conversation subject maximum', () => {
	assert.equal(CONVERSATION_SUBJECT_MAX_LENGTH, 200);
	assert.equal(conversationSubjectError('A'.repeat(200)), null);
	assert.match(conversationSubjectError('A'.repeat(201)) ?? '', /200/);
});
