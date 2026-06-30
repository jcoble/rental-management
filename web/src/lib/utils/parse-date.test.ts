import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
	addCalendarYear,
	formatIsoToUsInput,
	maskDateInput,
	parseCompleteLooseDate,
	parseLooseDate
} from './parse-date.ts';

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

describe('maskDateInput', () => {
	it('formats typed digits as a bounded MM/DD/YYYY mask', () => {
		assert.equal(maskDateInput('1'), '1');
		assert.equal(maskDateInput('12'), '12');
		assert.equal(maskDateInput('123'), '12/3');
		assert.equal(maskDateInput('1231'), '12/31');
		assert.equal(maskDateInput('12/345'), '12/34/5');
		assert.equal(maskDateInput('12312026'), '12/31/2026');
		assert.equal(maskDateInput('12/31/2026123'), '12/31/2026');
	});

	it('strips non-digits before applying the mask', () => {
		assert.equal(maskDateInput('a1b2/3c1-2026'), '12/31/2026');
	});
});

describe('addCalendarYear', () => {
	it('returns the same month and day in the next calendar year', () => {
		assert.equal(addCalendarYear('2026-07-01'), '2027-07-01');
	});

	it('clamps leap day to February 28 when the next year is not leap', () => {
		assert.equal(addCalendarYear('2024-02-29'), '2025-02-28');
	});

	it('returns null for invalid ISO dates', () => {
		assert.equal(addCalendarYear('2026-13-01'), null);
	});
});
