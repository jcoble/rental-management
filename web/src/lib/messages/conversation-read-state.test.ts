import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { conversationReadKey, markConversationRead } from './conversation-read-state.ts';

describe('conversation read state', () => {
	it('uses message freshness, not only id, when deciding whether a thread was read', () => {
		assert.equal(
			conversationReadKey({
				id: 7,
				messageCount: 2,
				lastMessageAt: '2026-06-24T01:00:00Z',
			}),
			'7:2:2026-06-24T01:00:00Z',
		);
		assert.notEqual(
			conversationReadKey({
				id: 7,
				messageCount: 2,
				lastMessageAt: '2026-06-24T01:00:00Z',
			}),
			conversationReadKey({
				id: 7,
				messageCount: 3,
				lastMessageAt: '2026-06-24T01:05:00Z',
			}),
		);
	});

	it('clears unread count only for the opened conversation', () => {
		const rows = [
			{ id: 7, subject: 'Open thread', unreadCount: 1 },
			{ id: 8, subject: 'Other thread', unreadCount: 2 },
		];

		assert.deepEqual(markConversationRead(rows, 7), [
			{ id: 7, subject: 'Open thread', unreadCount: 0 },
			{ id: 8, subject: 'Other thread', unreadCount: 2 },
		]);
	});
});
