import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { createUnitEditForm } from './unit-edit-form.ts';
import type { Unit } from '$lib/types';

const unit: Unit = {
	id: 1,
	propertyId: 2,
	unitNumber: '1A',
	bedrooms: 2,
	bathrooms: 1.5,
	marketRent: 1125,
	status: 'Occupied',
	createdAt: '2026-01-01T00:00:00Z',
	updatedAt: '2026-01-01T00:00:00Z',
};

describe('unit detail edit form', () => {
	it('seeds string-bound form fields from the loaded unit', () => {
		assert.deepEqual(createUnitEditForm(unit), {
			unitNumber: '1A',
			bedrooms: '2',
			bathrooms: '1.5',
			marketRent: '1125',
		});
	});
});
