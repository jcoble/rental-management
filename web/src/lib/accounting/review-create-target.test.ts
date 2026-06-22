import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { accountingReviewCreateTarget } from './review-create-target.ts';

describe('accountingReviewCreateTarget', () => {
	it('routes unmatched payments to tenant creation', () => {
		assert.deepEqual(
			accountingReviewCreateTarget({
				externalType: 'Payment',
				reason: 'No tenant mapping for this customer',
			}),
			{ href: '/tenants?create=1', label: 'Add tenant' },
		);
	});

	it('routes unmatched expenses to vendor creation', () => {
		assert.deepEqual(
			accountingReviewCreateTarget({
				externalType: 'Purchase',
				reason: 'No vendor / property / category mapping resolved',
			}),
			{ href: '/vendors?create=1', label: 'Add vendor' },
		);
		assert.deepEqual(accountingReviewCreateTarget({ externalType: 'Bill' }), {
			href: '/vendors?create=1',
			label: 'Add vendor',
		});
	});

	it('does not offer a fake create flow for non-cash skipped rows', () => {
		assert.equal(
			accountingReviewCreateTarget({
				externalType: 'Purchase',
				reason: 'Depreciation is non-cash; skipped on import',
			}),
			null,
		);
	});
});
