import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { isMismatchedUnitSelection } from './unit-membership-guard.ts';

describe('isMismatchedUnitSelection', () => {
	it('does not clear before a record or expected unit is loaded', () => {
		assert.equal(isMismatchedUnitSelection(undefined, 12), false);
		assert.equal(isMismatchedUnitSelection({ unitId: 12 }, undefined), false);
		assert.equal(isMismatchedUnitSelection({ unitId: 12 }, 0), false);
	});

	it('keeps records that belong to the expected unit', () => {
		assert.equal(isMismatchedUnitSelection({ unitId: 12 }, 12), false);
	});

	it('clears records from another unit or no unit', () => {
		assert.equal(isMismatchedUnitSelection({ unitId: 13 }, 12), true);
		assert.equal(isMismatchedUnitSelection({ unitId: null }, 12), true);
		assert.equal(isMismatchedUnitSelection({}, 12), true);
	});
});
