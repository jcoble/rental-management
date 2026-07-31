import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { propertyUpdateFields } from './property-update-payload.ts';

describe('propertyUpdateFields', () => {
	it('omits structural and ownership-selection fields from an ordinary property PATCH payload', () => {
		const payload = propertyUpdateFields({
			name: 'Rimview',
			type: 'SingleFamily',
			rentalStructure: 'SingleRental',
			ownerEntityId: 42,
			status: 'Active'
		});

		assert.deepEqual(payload, {
			name: 'Rimview',
			type: 'SingleFamily',
			status: 'Active'
		});
		assert.equal('rentalStructure' in payload, false);
		assert.equal('ownerEntityId' in payload, false);
	});

	it('keeps explicit optional text clears in the PATCH payload', () => {
		const payload = propertyUpdateFields({
			name: 'Rimview',
			type: 'SingleFamily',
			rentalStructure: 'SingleRental',
			ownerEntityId: 42,
			addressLine2: null
		});

		assert.deepEqual(payload, {
			name: 'Rimview',
			type: 'SingleFamily',
			addressLine2: ''
		});
	});

	it('preserves non-addressLine2 null values', () => {
		const payload = propertyUpdateFields({
			name: 'Rimview',
			type: 'SingleFamily',
			rentalStructure: 'SingleRental',
			ownerEntityId: 42,
			addressLine2: null,
			notes: null
		});

		assert.deepEqual(payload, {
			name: 'Rimview',
			type: 'SingleFamily',
			addressLine2: '',
			notes: null
		});
	});
});
