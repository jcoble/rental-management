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

describe('unit rent canonical receipt and charge commands', () => {
	it('captures receipt metadata and posts with an idempotency key', () => {
		assert.match(source, /method: ''/);
		assert.match(source, /reference: ''/);
		assert.match(source, /operationKey \?\?= crypto\.randomUUID\(\)/);
		assert.match(source, /payments\.recordReceipt\(tenantAccountId, operationKey/);
	});

	it('offers a manual charge only from tenant-account context', () => {
		assert.match(source, /payments\.postCharge\(tenantAccountId, operationKey/);
		assert.match(source, /Use this only for a true one-off charge/);
	});

	it('renders immutable receipt detail without edit or delete actions', () => {
		assert.match(paymentDetailSource, /This receipt stays in your records/);
		assert.match(paymentDetailSource, /records a matching correction instead of rewriting it/);
		assert.doesNotMatch(paymentDetailSource, /payments\.(update|delete|markPaid)/);
	});
});
