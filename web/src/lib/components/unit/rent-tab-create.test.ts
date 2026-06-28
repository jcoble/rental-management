import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = readFileSync(
	new URL('./tabs/RentTab.svelte', import.meta.url),
	'utf8'
);

// TSK-457: RentTab lost its inline expand-to-edit payment UI; editing a payment's reference
// and notes now lives in the extracted PaymentDetail component, folded into the Rent tab on
// ?payment= select. The reference/notes-edit case below asserts against PaymentDetail.
const paymentDetailSource = readFileSync(
	new URL('../records/PaymentDetail.svelte', import.meta.url),
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

	it('shows and edits reference and notes from the folded payment detail', () => {
		// Re-homed (TSK-457): reference/notes editing moved from RentTab's inline expand into
		// PaymentDetail. Assert the same capability there: the edit form seeds both fields from
		// the payment, exposes editable inputs for each, and persists via the update mutation.
		assert.match(paymentDetailSource, /externalReference: payment\.externalReference \?\? ''/);
		assert.match(paymentDetailSource, /notes: payment\.notes \?\? ''/);
		assert.match(paymentDetailSource, /bind:value=\{form\.externalReference\}[^>]*testid="payment-detail-reference"/);
		assert.match(paymentDetailSource, /bind:value=\{form\.notes\}[^>]*testid="payment-detail-notes"/);
		assert.match(paymentDetailSource, /payments\.update\(paymentId, data\)/);
	});
});
