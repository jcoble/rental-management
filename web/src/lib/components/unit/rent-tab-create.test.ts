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

	it('renders the created payment immediately after the post succeeds', () => {
		assert.match(source, /onSuccess: \(payment: Payment\) =>/);
		assert.match(source, /paymentItems = \[payment, \.\.\.paymentItems\.filter\(\(item\) => item\.id !== payment\.id\)\]/);
	});

	it('shows and edits reference and notes from expanded payment rows', () => {
		assert.match(source, /externalReference: p\.externalReference \?\? ''/);
		assert.match(source, /testid="rent-edit-reference"/);
		assert.match(source, /testid="rent-edit-notes"/);
		assert.match(source, /<dt class="text-muted-foreground">Reference<\/dt>/);
		assert.match(source, /<dt class="text-muted-foreground">Notes<\/dt>/);
	});
});
