import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { formatIsoToUsInput, parseCompleteLooseDate, parseLooseDate } from './parse-date.ts';

describe('parseLooseDate', () => {
	it('parses supported typed date formats to canonical ISO dates', () => {
		assert.equal(parseLooseDate('07/31/2027'), '2027-07-31');
		assert.equal(parseLooseDate('7/31/27'), '2027-07-31');
		assert.equal(parseLooseDate('2027-07-31'), '2027-07-31');
	});

	it('rejects impossible dates', () => {
		assert.equal(parseLooseDate('02/30/2027'), null);
		assert.equal(parseLooseDate('13/01/2027'), null);
	});
});

describe('parseCompleteLooseDate', () => {
	it('auto-commits complete user-typed dates while the field is still focused', () => {
		assert.equal(parseCompleteLooseDate('07/31/2027'), '2027-07-31');
		assert.equal(parseCompleteLooseDate('7/31/27'), '2027-07-31');
		assert.equal(parseCompleteLooseDate('2027-07-31'), '2027-07-31');
	});

	it('does not auto-commit partial year-first dates before the user finishes the day', () => {
		assert.equal(parseCompleteLooseDate('2027-07-3'), null);
		assert.equal(parseCompleteLooseDate('2027-7-31'), null);
	});

	it('does not auto-commit partial month-first years', () => {
		assert.equal(parseCompleteLooseDate('07/31/2'), null);
		assert.equal(parseCompleteLooseDate('07/31/202'), null);
	});
});

describe('formatIsoToUsInput', () => {
	it('formats canonical ISO dates for the visible text input', () => {
		assert.equal(formatIsoToUsInput('2027-07-31'), '07/31/2027');
	});
});
