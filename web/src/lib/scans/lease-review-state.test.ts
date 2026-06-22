import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { shouldSeedLeaseReviewState } from './lease-review-state.ts';

describe('shouldSeedLeaseReviewState', () => {
	it('waits until extraction produced reviewable fields', () => {
		assert.equal(shouldSeedLeaseReviewState('Failed', 0), false);
		assert.equal(shouldSeedLeaseReviewState('Pending', 0), false);
		assert.equal(shouldSeedLeaseReviewState('Processing', 0), false);
		assert.equal(shouldSeedLeaseReviewState('Reviewing', 0), false);
		assert.equal(shouldSeedLeaseReviewState('Reviewing', 8), true);
	});
});
