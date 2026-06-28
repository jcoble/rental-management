import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';
import { buildExpenseReceiptDataForSave } from './expense-receipt-data.ts';

describe('buildExpenseReceiptDataForSave', () => {
	it('rebuilds receiptData lineItems from edited typed rows while preserving other scan fields', () => {
		const result = buildExpenseReceiptDataForSave({
			rawReceiptData:
				'{"receiptNumber":"R-42","paymentMethod":"Visa","lineItems":[{"description":"Old","amount":1}]}',
			lineItems: [
				{ description: 'Washer hose', quantity: '2', unitPrice: '6.50', amount: '12.99' },
				{ description: 'Pipe tape', quantity: '', unitPrice: '', amount: '3.00' },
			],
		});

		assert.equal(result.clearReceiptData, false);
		assert.ok(result.receiptData);
		const parsed = JSON.parse(result.receiptData!);
		assert.equal(parsed.receiptNumber, 'R-42');
		assert.equal(parsed.paymentMethod, 'Visa');
		assert.deepEqual(parsed.lineItems, [
			{ description: 'Washer hose', quantity: 2, unitPrice: 6.5, amount: 12.99 },
			{ description: 'Pipe tape', amount: 3 },
		]);
	});

	it('sends an explicit clear flag when the raw receipt JSON is emptied', () => {
		const result = buildExpenseReceiptDataForSave({
			rawReceiptData: '   ',
			lineItems: [{ description: 'Deleted row', quantity: '', unitPrice: '', amount: '' }],
		});

		assert.deepEqual(result, { receiptData: null, clearReceiptData: true });
	});

	it('is wired into the expense detail save payload', () => {
		const pageSource = readFileSync(
			new URL('../components/records/ExpenseDetail.svelte', import.meta.url),
			'utf8'
		);

		assert.match(pageSource, /import \{ buildExpenseReceiptDataForSave \}/);
		assert.match(pageSource, /const receiptPayload = buildExpenseReceiptDataForSave\(/);
		assert.match(pageSource, /saveMutation\.mutate\(\{ portfolioId, \.\.\.result\.data, \.\.\.receiptPayload, lineItems \}\)/);
	});
});
