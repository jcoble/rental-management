import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, test } from 'node:test';

const source = readFileSync(new URL('./+page.svelte', import.meta.url), 'utf8');

describe('provider payment dead-letter operator contract', () => {
	test('renders every minimum recovery field in the existing audit surface', () => {
		for (const label of [
			'Event id:',
			'PaymentIntent:',
			'Amount:',
			'Target charge:',
			'Payment attempt:',
			'Provider received:',
			'Dead-lettered:',
			'Recovery:',
			'Recovery action:',
		]) {
			assert.match(source, new RegExp(label.replace(':', '\\:')));
		}
		assert.match(source, /admin-audit-provider-dead-letter-/);
		assert.match(source, /admin-audit-search/);
	});
});
