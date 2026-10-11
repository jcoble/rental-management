import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';
import { receiptGrandTotal } from './expense-receipt-display.ts';

describe('expense receipt display totals', () => {
	it('formats the expense amount as the receipt grand total', () => {
		assert.equal(
			receiptGrandTotal({ amount: 325.05, subtotal: 304.5, taxAmount: 20.55 }),
			325.05
		);
	});

	it('falls back to subtotal plus tax if a legacy payload has no valid amount', () => {
		assert.equal(
			receiptGrandTotal({ amount: Number.NaN, subtotal: 304.5, taxAmount: 20.55 }),
			325.05
		);
	});

	it('wires receipt amount and grand total displays into the expense detail page', () => {
		const pageSource = readFileSync(
			new URL('../components/records/ExpenseDetail.svelte', import.meta.url),
			'utf8'
		);

		assert.match(pageSource, /expense-detail-receipt-amount/);
		assert.match(pageSource, /expense-detail-line-items-grand-total/);
		assert.match(pageSource, /Grand total/);
		assert.match(pageSource, /Technical receipt data/);
		assert.match(pageSource, /\/docs\/recording-expenses/);
		assert.doesNotMatch(pageSource, /Receipt details \(raw\)/);
	});
});
