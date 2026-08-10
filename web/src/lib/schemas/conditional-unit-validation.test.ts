import assert from 'node:assert/strict';
import { test } from 'node:test';

import { isResidentialDwellingType, parseForm, unitSchemaForPropertyType } from './index.ts';

const baseUnit = {
	unitNumber: 'Storage A',
	bedrooms: '',
	bathrooms: '',
	marketRent: '0',
	};

test('residential dwelling units require beds and baths', () => {
	for (const propertyType of ['SingleFamily', 'MultiFamily', 'Condo', 'Townhome']) {
		const result = parseForm(unitSchemaForPropertyType(propertyType), baseUnit);
		assert.ok(result.errors?.bedrooms, `${propertyType} should require bedrooms`);
		assert.ok(result.errors?.bathrooms, `${propertyType} should require bathrooms`);
	}
});

test('storage, parking, and commercial units can omit beds and baths', () => {
	for (const propertyType of ['Storage', 'Parking', 'Commercial', 'MixedUse']) {
		const result = parseForm(unitSchemaForPropertyType(propertyType), baseUnit);
		assert.equal(result.errors, null, `${propertyType} should allow blank beds and baths`);
		assert.equal(result.data?.bedrooms, null);
		assert.equal(result.data?.bathrooms, null);
	}
});

test('an omitted property type stays residential by default', () => {
	assert.equal(isResidentialDwellingType(undefined), true);
	assert.ok(parseForm(unitSchemaForPropertyType(), baseUnit).errors?.bedrooms);
});
