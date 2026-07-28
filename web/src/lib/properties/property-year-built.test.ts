import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { businessYearFromDateOnly, propertyYearBuiltBusinessYearError } from './property-year-built.ts';

describe('property year built validation', () => {
	it('allows null and the current business year', () => {
		assert.equal(propertyYearBuiltBusinessYearError(null, '2027-07-28'), null);
		assert.equal(propertyYearBuiltBusinessYearError(2027, '2027-07-28'), null);
	});

	it('blocks years after the portfolio business year', () => {
		assert.equal(
			propertyYearBuiltBusinessYearError(2028, '2027-07-28'),
			'Year built cannot be later than the portfolio business year (2027)'
		);
	});

	it('does not invent a limit when the business date is unavailable', () => {
		assert.equal(businessYearFromDateOnly(null), null);
		assert.equal(propertyYearBuiltBusinessYearError(2028, null), null);
	});
});
