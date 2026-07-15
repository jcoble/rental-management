import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { propertyUpdateFields } from './property-update-payload.ts';

describe('propertyUpdateFields', () => {
	it('omits RentalStructure from an ordinary property PATCH payload', () => {
		const payload = propertyUpdateFields({
			name: 'Rimview',
			type: 'SingleFamily',
			rentalStructure: 'SingleRental',
			status: 'Active'
		});

		assert.deepEqual(payload, {
			name: 'Rimview',
			type: 'SingleFamily',
			status: 'Active'
		});
		assert.equal('rentalStructure' in payload, false);
	});
});
