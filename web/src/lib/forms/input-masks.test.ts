import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
	maskCardLast4Input,
	maskCurrencyInput,
	maskIntegerInput,
	maskInputValue,
	maskPercentageInput,
	maskPhoneInput,
	maskZipInput,
} from './input-masks.ts';
import { workOrderDetailSchema } from '../schemas/index.ts';

describe('input masks', () => {
	it('keeps currency input numeric and limited to cents', () => {
		assert.equal(maskCurrencyInput('$1,234.567'), '1234.56');
		assert.equal(maskCurrencyInput('.5'), '0.5');
		assert.equal(maskCurrencyInput('12.'), '12.');
		assert.equal(maskCurrencyInput('-50'), '-50');
	});

	it('rejects a negative actual cost', () => {
		const result = workOrderDetailSchema.safeParse({ actualCost: '-50' });
		assert.equal(result.error?.issues.some((issue) => issue.message === 'Actual cost must be a non-negative number'), true);
	});

	it('keeps percentage input numeric without appending a percent sign', () => {
		assert.equal(maskPercentageInput('7.125%'), '7.125');
		assert.equal(maskPercentageInput('100.12345'), '100.1234');
		assert.equal(maskPercentageInput('6.9375%'), '6.9375');
		assert.equal(maskInputValue('0.0725', 'percentage'), '0.0725');
		assert.equal(maskPercentageInput('12.3456', { maxDecimals: 2 }), '12.34');
	});

	it('formats US phone numbers while preserving extensions', () => {
		assert.equal(maskPhoneInput('6145550199'), '614-555-0199');
		assert.equal(maskPhoneInput('16145550199'), '1-614-555-0199');
		assert.equal(maskPhoneInput('(614) 555-0199 ext. 42'), '614-555-0199 x42');
	});

	it('formats ZIP and ZIP+4 values', () => {
		assert.equal(maskZipInput('432156789'), '43215-6789');
		assert.equal(maskZipInput('43215-6789 extra'), '43215-6789');
	});

	it('keeps card and integer fields digit-only', () => {
		assert.equal(maskCardLast4Input('12a34 56'), '1234');
		assert.equal(maskIntegerInput('day 31.9', 2), '31');
	});
});
