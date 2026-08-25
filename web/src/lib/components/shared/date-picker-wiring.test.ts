import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
	applyRangeDatePickerValue,
	commitDatePickerText,
	datePickerYearOptions,
	datePickerLayoutForWidth,
	datePickerSubmitDisabled
} from './date-picker-state.ts';
import { SERVICE_PERIOD_SECTION_CLASS } from '../accounting/one-time-charge-layout.ts';
import {
	activeScanDateFieldNames,
	hasVisibleScanDateInvalid
} from '../../scan/scan-date-guard.ts';

describe('DatePicker behavior', () => {
	it('marks an invalid typed date and disables submit, then enables submit after correction', () => {
		const invalid = commitDatePickerText('02/30/2026', '2026-01-01');
		assert.equal(invalid.invalid, true);
		assert.equal(invalid.value, '2026-01-01');
		assert.equal(datePickerSubmitDisabled(invalid.invalid), true);

		const corrected = commitDatePickerText('02/28/2026', invalid.value);
		assert.equal(corrected.invalid, false);
		assert.equal(corrected.value, '2026-02-28');
		assert.equal(datePickerSubmitDisabled(corrected.invalid), false);
	});

	it('keeps the formatted date visible while dropping the Today label in narrow fields', () => {
		const normal = datePickerLayoutForWidth(284);
		assert.equal(normal.showTodayLabel, true);
		assert.equal(normal.calendarButtonSizePx, 36);

		const compact = datePickerLayoutForWidth(124);
		assert.equal(compact.showTodayLabel, false);
		assert.ok(compact.inputPaddingRightPx < normal.inputPaddingRightPx);
		assert.ok(compact.calendarButtonSizePx <= 32);
		assert.equal(commitDatePickerText('08/01/2026', '').text, '08/01/2026');
	});

	it('shares a reachable year window between single and range captions', () => {
		assert.deepEqual(datePickerYearOptions(undefined, undefined, 2026).slice(0, 2), [1900, 1901]);
		assert.equal(datePickerYearOptions(undefined, undefined, 2026).at(-1), 2036);
		assert.deepEqual(datePickerYearOptions(2028, 2030, 2026), [2028, 2029, 2030]);
		assert.deepEqual(datePickerYearOptions(2040, 2030, 2026), [2030]);
	});
});

describe('RangeDatePicker behavior', () => {
	it('clears a bound invalid state when a valid range or preset is applied', () => {
		const current = { start: '2026-01-01', end: '2026-01-31', invalid: true };
		const next = applyRangeDatePickerValue(current, { start: '2026-02-01', end: '2026-02-28' });
		assert.equal(next.invalid, false);
		assert.deepEqual({ start: next.start, end: next.end }, { start: '2026-02-01', end: '2026-02-28' });

		const sameRange = applyRangeDatePickerValue(
			{ start: next.start, end: next.end, invalid: true },
			{ start: next.start, end: next.end }
		);
		assert.equal(sameRange.invalid, false);
		assert.equal(sameRange.changed, false);

		const preset = applyRangeDatePickerValue(
			{ start: next.start, end: next.end, invalid: true },
			{ start: '2026-01-01', end: '2026-12-31' }
		);
		assert.equal(preset.invalid, false);
	});

	it('clears both range endpoints through the normal change contract', () => {
		const cleared = applyRangeDatePickerValue(
			{ start: '2026-01-01', end: '2026-01-31', invalid: true },
			{ start: '', end: '' }
		);
		assert.deepEqual({ start: cleared.start, end: cleared.end }, { start: '', end: '' });
		assert.equal(cleared.invalid, false);
		assert.equal(cleared.changed, true);
	});
});

describe('scan review date guard behavior', () => {
	it('ignores invalid fields hidden by the active loan review mode', () => {
		const invalidByField = { statement_effective_date: true, start_date: false };
		const createFields = activeScanDateFieldNames({
			targetEntityType: 'Loan',
			loanReviewMode: 'create',
			isTerminal: false
		});
		assert.equal(hasVisibleScanDateInvalid(invalidByField, createFields), false);

		const matchFields = activeScanDateFieldNames({
			targetEntityType: 'Loan',
			loanReviewMode: 'match',
			isTerminal: false
		});
		assert.equal(hasVisibleScanDateInvalid(invalidByField, matchFields), true);
	});

	it('does not keep a hidden generic date invalid after the field is no longer applicable', () => {
		const activeFields = activeScanDateFieldNames({
			targetEntityType: 'Payment',
			fields: [{ name: 'transaction_date' }, { name: 'due_date' }]
		});
		assert.equal(hasVisibleScanDateInvalid({ due_date: true }, activeFields), false);
		assert.equal(hasVisibleScanDateInvalid({ transaction_date: true }, activeFields), true);
	});
});

describe('OneTimeCharge layout behavior', () => {
	it('places the service-period pair across both dialog columns', () => {
		assert.equal(SERVICE_PERIOD_SECTION_CLASS.split(/\s+/).includes('sm:col-span-2'), true);
	});
});
