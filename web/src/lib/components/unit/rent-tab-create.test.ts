import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = readFileSync(
	new URL('./tabs/RentTab.svelte', import.meta.url),
	'utf8'
);

describe('unit rent quick payment create form', () => {
	it('captures method, reference, and notes before posting a payment', () => {
		assert.match(source, /method: ''/);
		assert.match(source, /externalReference: ''/);
		assert.match(source, /notes: ''/);
		assert.match(source, /data-testid="rent-method-input"/);
		assert.match(source, /data-testid="rent-reference-input"/);
		assert.match(source, /data-testid="rent-notes-input"/);
	});
});
