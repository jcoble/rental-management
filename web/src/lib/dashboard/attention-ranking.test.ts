import assert from 'node:assert/strict';
import { describe, test } from 'node:test';
import {
	ATTENTION_LIMIT,
	attentionKindForBriefingCategory,
	rankAttention,
	type AttentionKind
} from './attention-ranking.ts';

function item(kind: AttentionKind, key: string) {
	return { kind, key };
}

describe('needs-attention ranking', () => {
	test('puts money first, then repairs, messages, applications, signatures, move events', () => {
		const ranked = rankAttention([
			item('move', 'move-1'),
			item('signature', 'sign-1'),
			item('application', 'app-1'),
			item('message', 'msg-1'),
			item('repair', 'repair-1'),
			item('money', 'money-1')
		]);

		assert.deepEqual(
			ranked.map((entry) => entry.key),
			['money-1', 'repair-1', 'msg-1', 'app-1', 'sign-1', 'move-1']
		);
	});

	test('keeps the original order within one kind', () => {
		const ranked = rankAttention([
			item('money', 'money-1'),
			item('money', 'money-2'),
			item('money', 'money-3')
		]);

		assert.deepEqual(
			ranked.map((entry) => entry.key),
			['money-1', 'money-2', 'money-3']
		);
	});

	test('caps the list at eight rows by default', () => {
		const ranked = rankAttention(
			Array.from({ length: 12 }, (_, index) => item('repair', `repair-${index}`))
		);

		assert.equal(ATTENTION_LIMIT, 8);
		assert.equal(ranked.length, 8);
		assert.equal(ranked[7].key, 'repair-7');
	});

	test('shows every row when a bigger limit is asked for', () => {
		const all = Array.from({ length: 12 }, (_, index) => item('repair', `repair-${index}`));

		assert.equal(rankAttention(all, all.length).length, 12);
	});

	test('reads an empty list as nothing to do', () => {
		assert.deepEqual(rankAttention([]), []);
	});

	test('drops rent briefing lines because overdue tenants come from the who-is-behind list', () => {
		assert.equal(attentionKindForBriefingCategory('RentDue'), null);
		assert.equal(attentionKindForBriefingCategory('RentLate'), null);
	});

	test('maps the remaining briefing categories to their landlord kind', () => {
		assert.equal(attentionKindForBriefingCategory('Maintenance'), 'repair');
		assert.equal(attentionKindForBriefingCategory('Message'), 'message');
		assert.equal(attentionKindForBriefingCategory('Application'), 'application');
		assert.equal(attentionKindForBriefingCategory('LeaseExpiring'), 'signature');
		assert.equal(attentionKindForBriefingCategory('Appointment'), 'move');
		assert.equal(attentionKindForBriefingCategory('Inspection'), 'move');
		assert.equal(attentionKindForBriefingCategory('Something new'), 'move');
	});
});
