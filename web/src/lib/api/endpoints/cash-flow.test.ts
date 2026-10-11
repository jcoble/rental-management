import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = readFileSync(new URL('./cash-flow.ts', import.meta.url), 'utf8');

describe('cash flow API contract', () => {
	it('constructs the true cash-flow route with date and property filters', () => {
		assert.match(source, /return `\/accounting\/cash-flow/);
		assert.match(source, /from: params\.from/);
		assert.match(source, /to: params\.to/);
		assert.match(source, /propertyId: params\.propertyId/);
		assert.match(source, /propertyIds: params\.propertyIds/);
		assert.match(source, /api\.get<CashFlowSummaryResponse>\(buildCashFlowPath\(params\)\)/);
	});

	it('binds server cash-flow rows and portfolio totals without recomputing them', () => {
		for (const field of [
			'from',
			'to',
			'properties',
			'income',
			'operatingExpenses',
			'noi',
			'debtService',
			'cashFlow',
			'totalIncome',
			'totalOperatingExpenses',
			'totalNoi',
			'totalDebtService',
			'totalCashFlow'
		]) {
			assert.match(source, new RegExp(`\\b${field}\\b`));
		}
		assert.doesNotMatch(source, /totalCashFlow\s*=|totalNoi\s*=/);
	});
});
