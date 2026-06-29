/**
 * Client/server validation parity for property + unit forms.
 *
 * The server binds Create/Update{Property,Unit}Request with MaxLength / Range data
 * annotations (PropertyDtos.cs, UnitDtos.cs): property Name 200, AddressLine1/2 250,
 * City/State 100, PostalCode 20; unit UnitNumber 50, Bedrooms/Bathrooms Range(0, 99),
 * MarketRent Range(0, 99999999). Without matching upper bounds on the client schemas an
 * over-long/over-large value passed client validation and then 400'd with a raw .NET
 * message. These tests pin the client schemas to the same limits so the client catches it.
 *
 *   node --test --experimental-strip-types src/lib/schemas/property-unit-max-length.test.ts
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';

import { propertySchema, unitSchema, parseForm } from './index.ts';

const validProperty = {
	name: 'Maple Court',
	type: 'MultiFamily',
	status: 'Active',
	addressLine1: '100 Main St',
	addressLine2: '',
	city: 'Columbus',
	state: 'OH',
	postalCode: '43219',
	ownerEntityId: ''
};

const validUnit = {
	unitNumber: '101',
	bedrooms: '2',
	bathrooms: '1.5',
	marketRent: '1200'
};

test('propertySchema accepts a valid property and a name at the 200-char limit', () => {
	assert.equal(parseForm(propertySchema, validProperty).errors, null);
	const atLimit = parseForm(propertySchema, { ...validProperty, name: 'A'.repeat(200) });
	assert.equal(atLimit.errors, null, `unexpected errors: ${JSON.stringify(atLimit.errors)}`);
});

test('propertySchema rejects a name longer than 200 (server MaxLength(200))', () => {
	const result = parseForm(propertySchema, { ...validProperty, name: 'A'.repeat(201) });
	assert.ok(result.errors, 'expected a validation error for an over-long name');
	assert.ok(result.errors?.name, 'expected a name error');
});

test('propertySchema rejects an over-long address line 1 (server MaxLength(250))', () => {
	const result = parseForm(propertySchema, { ...validProperty, addressLine1: 'A'.repeat(251) });
	assert.ok(result.errors?.addressLine1, 'expected an addressLine1 error');
});

test('propertySchema rejects an over-long optional address line 2 (server MaxLength(250))', () => {
	const result = parseForm(propertySchema, { ...validProperty, addressLine2: 'A'.repeat(251) });
	assert.ok(result.errors?.addressLine2, 'expected an addressLine2 error');
});

test('propertySchema rejects an over-long ZIP (server MaxLength(20))', () => {
	const result = parseForm(propertySchema, { ...validProperty, postalCode: '1'.repeat(21) });
	assert.ok(result.errors?.postalCode, 'expected a postalCode error');
});

test('unitSchema accepts a valid unit, a 50-char unit number, and 99 beds/baths', () => {
	assert.equal(parseForm(unitSchema, validUnit).errors, null);
	const atLimits = parseForm(unitSchema, {
		unitNumber: 'U'.repeat(50),
		bedrooms: '99',
		bathrooms: '99',
		marketRent: '99999999'
	});
	assert.equal(atLimits.errors, null, `unexpected errors: ${JSON.stringify(atLimits.errors)}`);
});

test('unitSchema rejects a unit number longer than 50 (server MaxLength(50))', () => {
	const result = parseForm(unitSchema, { ...validUnit, unitNumber: 'U'.repeat(51) });
	assert.ok(result.errors?.unitNumber, 'expected a unitNumber error');
});

test('unitSchema rejects bedrooms/bathrooms above 99 (server Range(0, 99))', () => {
	assert.ok(parseForm(unitSchema, { ...validUnit, bedrooms: '100' }).errors?.bedrooms, 'expected a bedrooms error');
	assert.ok(parseForm(unitSchema, { ...validUnit, bathrooms: '100' }).errors?.bathrooms, 'expected a bathrooms error');
});

test('unitSchema rejects market rent above 99,999,999 (server Range(0, 99999999))', () => {
	const result = parseForm(unitSchema, { ...validUnit, marketRent: '100000000' });
	assert.ok(result.errors?.marketRent, 'expected a marketRent error');
});
