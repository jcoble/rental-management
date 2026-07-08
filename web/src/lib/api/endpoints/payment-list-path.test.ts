import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { buildPaymentListPagePath, buildPaymentListPath } from './payment-list-path.ts';

describe('payment list paths', () => {
	it('serializes lease and payment date filters', () => {
		assert.equal(
			buildPaymentListPagePath(2, {
				leaseId: 7,
				take: 20,
				sort: '-dueDate',
				dueFrom: '2026-01-01',
				dueTo: '2026-01-31',
				paidFrom: '2026-01-05',
				paidTo: '2026-01-25'
			}),
			'/payments/page?take=20&sort=-dueDate&portfolioId=2&leaseId=7&dueFrom=2026-01-01&dueTo=2026-01-31&paidFrom=2026-01-05&paidTo=2026-01-25'
		);
	});

	it('omits optional filters when they are not set', () => {
		assert.equal(buildPaymentListPath(2, { take: 20 }), '/payments?take=20&portfolioId=2');
	});
});
