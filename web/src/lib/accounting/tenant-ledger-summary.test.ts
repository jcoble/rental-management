import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
	tenantMonthClosingBalance,
	tenantMonthSummaryReconciles
} from './tenant-ledger-summary.ts';

const summary = {
	year: 2027,
	month: 1,
	currency: 'USD',
	openingBalance: 2235.48,
	chargeAmount: 1051,
	paymentAmount: 1,
	creditAmount: 1,
	closingBalance: 3284.48
};

test('S18-BUG-3 includes credits in the tenant month closing reconciliation', () => {
	assert.equal(tenantMonthClosingBalance(summary), 3284.48);
	assert.equal(tenantMonthSummaryReconciles(summary), true);
	assert.equal(
		tenantMonthSummaryReconciles({ ...summary, closingBalance: 3285.48 }),
		false
	);
});
