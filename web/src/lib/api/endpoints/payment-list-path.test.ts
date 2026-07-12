import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { buildPaymentListPagePath, buildPaymentListPath } from './payment-list-path.ts';

describe('payment list paths', () => {
	it('serializes canonical account and receipt date filters', () => {
		assert.equal(
			buildPaymentListPagePath(2, {
				tenantAccountId: 7,
				leaseManagementId: 9,
				take: 20,
				sort: '-receivedOn',
				paidFrom: '2026-01-05',
				paidTo: '2026-01-25'
			}),
			'/payments/page?take=20&sort=-receivedOn&portfolioId=2&tenantAccountId=7&leaseManagementId=9&paidFrom=2026-01-05&paidTo=2026-01-25'
		);
	});

	it('omits optional filters when they are not set', () => {
		assert.equal(buildPaymentListPath(2, { take: 20 }), '/payments?take=20&portfolioId=2');
	});

});
