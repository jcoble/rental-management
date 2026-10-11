import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const expenseDetail = readFileSync(new URL('./ExpenseDetail.svelte', import.meta.url), 'utf8');

describe('expense accounting impact contract', () => {
	it('uses the source identity for each expense posting state below the detail fields', () => {
		assert.match(
			expenseDetail,
			/import AccountingImpactCard from '\$lib\/components\/accounting\/AccountingImpactCard\.svelte';/
		);
		assert.match(
			expenseDetail,
			/<AccountingImpactCard sourceType="CapitalPurchase" sourceId=\{expense\.capitalizedAssetId\} \/>/
		);
		assert.match(
			expenseDetail,
			/<AccountingImpactCard sourceType="ExpensePayment" sourceId=\{expense\.id\} \/>/
		);
		assert.match(
			expenseDetail,
			/<AccountingImpactCard sourceType="BillIncurred" sourceId=\{expense\.id\} \/>/
		);
		const impactIndex = expenseDetail.indexOf('<AccountingImpactCard sourceType="CapitalPurchase"');
		assert.ok(impactIndex > expenseDetail.indexOf('data-testid="expense-detail-receipt-data-toggle"'));
		assert.ok(impactIndex < expenseDetail.indexOf('data-testid="expense-history-section"'));
	});
});
