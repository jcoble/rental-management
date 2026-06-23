import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { LEASE_REVIEW_NEW_UNIT_DETAIL_FIELDS, seedLeaseUnitId, shouldSeedLeaseReviewState } from './lease-review-state.ts';

describe('shouldSeedLeaseReviewState', () => {
	it('waits until extraction produced reviewable fields', () => {
		assert.equal(shouldSeedLeaseReviewState('Failed', 0), false);
		assert.equal(shouldSeedLeaseReviewState('Pending', 0), false);
		assert.equal(shouldSeedLeaseReviewState('Processing', 0), false);
		assert.equal(shouldSeedLeaseReviewState('Reviewing', 0), false);
		assert.equal(shouldSeedLeaseReviewState('Reviewing', 8), true);
	});
});

describe('seedLeaseUnitId', () => {
	it('uses the linked proposal unit when extraction has only a unit number', () => {
		assert.equal(seedLeaseUnitId('', { action: 'link', existingId: 12 }, false), '12');
	});

	it('prefers the extracted unit id and never selects an existing unit while creating a property', () => {
		assert.equal(seedLeaseUnitId('9', { action: 'link', existingId: 12 }, false), '9');
		assert.equal(seedLeaseUnitId('', { action: 'link', existingId: 12 }, true), '');
	});
});

describe('LEASE_REVIEW_NEW_UNIT_DETAIL_FIELDS', () => {
	it('keeps the editable lease-recovery unit details aligned with backend override keys', () => {
		assert.deepEqual(
			LEASE_REVIEW_NEW_UNIT_DETAIL_FIELDS.map((field) => field.name),
			['unit_bedrooms', 'unit_bathrooms', 'unit_square_feet']
		);
	});
});
