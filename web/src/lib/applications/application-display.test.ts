import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
	canRunApplicationScreening,
	formatApplicationAddress,
	formatRequestedProperty,
	formatRequestedUnit,
} from './application-display.ts';

describe('formatApplicationAddress', () => {
	it('does not duplicate structured parts when scan put a full address in line 1', () => {
		assert.equal(formatApplicationAddress({
			currentAddressLine1: '44 Cedar Bend Apt 3, Columbus, OH 43215',
			currentAddressLine2: 'Apt 3',
			currentCity: 'Columbus',
			currentState: 'OH',
			currentPostalCode: '43215',
		}), '44 Cedar Bend Apt 3, Columbus, OH 43215');
	});

	it('composes normal structured address parts', () => {
		assert.equal(formatApplicationAddress({
			currentAddressLine1: '44 Cedar Bend',
			currentAddressLine2: 'Apt 3',
			currentCity: 'Columbus',
			currentState: 'OH',
			currentPostalCode: '43215',
		}), '44 Cedar Bend, Apt 3, Columbus, OH 43215');
	});
});

describe('canRunApplicationScreening', () => {
	it('allows screening only for open applications with consent', () => {
		assert.equal(canRunApplicationScreening('Submitted', true), true);
		assert.equal(canRunApplicationScreening('UnderReview', true), true);
		assert.equal(canRunApplicationScreening('Submitted', false), false);
		assert.equal(canRunApplicationScreening('Declined', true), false);
		assert.equal(canRunApplicationScreening('Withdrawn', true), false);
		assert.equal(canRunApplicationScreening('Approved', true), false);
	});
});

describe('requested home labels', () => {
	it('prefers display names over raw ids', () => {
		assert.equal(
			formatRequestedProperty({ propertyId: 1, propertyName: 'Maple Grove Duplex' }),
			'Maple Grove Duplex',
		);
		assert.equal(formatRequestedUnit({ unitId: 2, unitNumber: 'B' }), 'B');
	});
});
