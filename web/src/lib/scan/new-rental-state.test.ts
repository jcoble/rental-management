import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { findNewRentalExistingUnitId, seedNewRentalLateFeeAmount } from './new-rental-state.ts';

describe('findNewRentalExistingUnitId', () => {
	it('uses a linked proposal when that unit belongs to the selected property', () => {
		assert.equal(
			findNewRentalExistingUnitId('1A', { action: 'link', existingId: 14 }, [
				{ id: 14, unitNumber: '1A' }
			]),
			'14'
		);
	});

	it('falls back to an exact unit-number match for camera-derived leases', () => {
		assert.equal(
			findNewRentalExistingUnitId(' 1a ', null, [
				{ id: 14, unitNumber: '1A' },
				{ id: 15, unitNumber: '2B' }
			]),
			'14'
		);
	});

	it('does not guess when the proposal is outside the loaded units or the unit number is ambiguous', () => {
		assert.equal(findNewRentalExistingUnitId('1A', { action: 'link', existingId: 99 }, [
			{ id: 14, unitNumber: '1A' },
			{ id: 15, unitNumber: '1A' }
		]), '');
	});
});

describe('seedNewRentalLateFeeAmount', () => {
	it('keeps extracted late fees and defaults missing values to zero for schema validation', () => {
		assert.equal(seedNewRentalLateFeeAmount('35.00'), '35.00');
		assert.equal(seedNewRentalLateFeeAmount('  '), '0');
		assert.equal(seedNewRentalLateFeeAmount(null), '0');
	});
});
