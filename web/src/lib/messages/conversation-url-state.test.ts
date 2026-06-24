import { describe, it } from 'node:test';
import assert from 'node:assert/strict';

import { conversationSelectionUrl, readConversationId } from './conversation-url-state.ts';

describe('conversation URL state', () => {
	it('reads only positive integer conversation ids', () => {
		assert.equal(readConversationId(new URLSearchParams('conversation=42')), 42);
		assert.equal(readConversationId(new URLSearchParams('conversation=0')), null);
		assert.equal(readConversationId(new URLSearchParams('conversation=-1')), null);
		assert.equal(readConversationId(new URLSearchParams('conversation=1.5')), null);
		assert.equal(readConversationId(new URLSearchParams('conversation=abc')), null);
		assert.equal(readConversationId(new URLSearchParams('')), null);
	});

	it('sets the selected conversation while replacing stale ids', () => {
		const current = new URL('https://localhost:6042/portal/messages?conversation=999999&filter=open#top');

		assert.equal(
			conversationSelectionUrl(current, 2),
			'/portal/messages?conversation=2&filter=open#top'
		);
	});

	it('clears the selected conversation without dropping unrelated state', () => {
		const current = new URL('https://localhost:6042/messages?conversation=2&filter=open#thread-list');

		assert.equal(conversationSelectionUrl(current, null), '/messages?filter=open#thread-list');
	});
});
