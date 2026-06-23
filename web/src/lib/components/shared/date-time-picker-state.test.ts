import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { applyDateTimePartChange, combineDateTimeParts, splitDateTimeValue } from './date-time-picker-state.ts';

describe('splitDateTimeValue', () => {
	it('splits a local ISO datetime into date and HH:mm parts', () => {
		assert.deepEqual(splitDateTimeValue('2026-06-25T10:00:00'), {
			datePart: '2026-06-25',
			timePart: '10:00'
		});
	});
});

describe('combineDateTimeParts', () => {
	it('emits a stable local ISO datetime only when date and time are both present', () => {
		assert.equal(combineDateTimeParts('2026-06-26', '11:15'), '2026-06-26T11:15:00');
		assert.equal(combineDateTimeParts('2026-06-26', ''), '');
		assert.equal(combineDateTimeParts('', '11:15'), '');
	});
});

describe('applyDateTimePartChange', () => {
	it('keeps a typed replacement date when the user edits the time afterward', () => {
		const initial = splitDateTimeValue('2026-06-25T10:00:00');

		const afterDate = applyDateTimePartChange(initial, { datePart: '2026-06-26' });
		assert.deepEqual(afterDate, {
			datePart: '2026-06-26',
			timePart: '10:00',
			value: '2026-06-26T10:00:00'
		});

		const afterTime = applyDateTimePartChange(afterDate, { timePart: '11:15' });
		assert.deepEqual(afterTime, {
			datePart: '2026-06-26',
			timePart: '11:15',
			value: '2026-06-26T11:15:00'
		});
	});
});
