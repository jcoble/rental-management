import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = readFileSync(new URL('./money-position.ts', import.meta.url), 'utf8');

describe('money position API contract', () => {
	it('constructs the optional from/to query for the dedicated route', () => {
		assert.match(source, /`\/accounting\/money-position\$\{buildListQuery/);
		assert.match(source, /from: params\.from/);
		assert.match(source, /to: params\.to/);
		assert.match(source, /api\.get<MoneyPositionResponse>\(buildMoneyPositionPath\(params\)\)/);
	});

	it('binds every server-calculated position and movement field', () => {
		for (const field of [
			'asOfUtc',
			'fromUtc',
			'toUtc',
			'totalCashOnHand',
			'tenantDepositsHeld',
			'cashAfterTenantDeposits',
			'rentStillOwed',
			'loanBalance',
			'bookEquity',
			'cashReceived',
			'cashPaid',
			'netCashMovement',
			'profitOrLoss'
		]) {
			assert.match(source, new RegExp(`\\b${field}\\b`));
		}
		assert.doesNotMatch(source, /totalCashOnHand\s*[+-]/);
		assert.doesNotMatch(source, /tenantDepositsHeld\s*[+-]/);
	});
});
