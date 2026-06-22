import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { formatPropertyStatus, formatPropertyType, propertyTypeOptions } from './property-labels.ts';

describe('property labels', () => {
	it('formats property type enum values as landlord-facing labels', () => {
		assert.equal(formatPropertyType('SingleFamily'), 'Single-family');
		assert.equal(formatPropertyType('MultiFamily'), 'Multi-family');
		assert.equal(formatPropertyType('MixedUse'), 'Mixed-use');
	});

	it('falls back safely for unknown property type values', () => {
		assert.equal(formatPropertyType('ShortTermRental'), 'Short Term Rental');
	});

	it('formats property status enum values as readable labels', () => {
		assert.equal(formatPropertyStatus('UnderMaintenance'), 'Under maintenance');
	});

	it('keeps option values as API enum values while showing readable labels', () => {
		assert.deepEqual(propertyTypeOptions.find((o) => o.value === 'MultiFamily'), {
			value: 'MultiFamily',
			label: 'Multi-family',
		});
	});
});
