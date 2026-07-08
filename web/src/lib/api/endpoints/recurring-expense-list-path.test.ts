import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
	buildRecurringExpenseListPagePath,
	buildRecurringExpenseListPath
} from './recurring-expense-list-path.ts';

describe('buildRecurringExpenseListPath', () => {
	it('serializes server-side list controls for property recurring expenses', () => {
		assert.equal(
			buildRecurringExpenseListPagePath({
				propertyId: 8,
				skip: 20,
				take: 10,
				sort: 'nextRunDate',
				from: '2026-03-01',
				to: '2026-03-31'
			}),
			'/recurring-expenses/page?skip=20&take=10&sort=nextRunDate&from=2026-03-01&to=2026-03-31&propertyId=8'
		);
	});

	it('keeps the legacy list endpoint available', () => {
		assert.equal(
			buildRecurringExpenseListPath({ propertyId: 8, take: 20 }),
			'/recurring-expenses?take=20&propertyId=8'
		);
	});
});
