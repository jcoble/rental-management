import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { calendarPlaceholderForStart, servicePeriodPreset, servicePeriodPresetState } from './range-date-picker-state.ts';

describe('service-period range picker behavior', () => {
	it('starts the end-selection calendar in the selected start month', () => {
		assert.equal(calendarPlaceholderForStart('2026-03-12'), '2026-03-01');
	});

	it('exposes selected-month presets and Custom for manual ranges', () => {
		assert.deepEqual(servicePeriodPreset(0, '2026-08-09'), { label: 'This month', start: '2026-08-01', end: '2026-08-31' });
		assert.deepEqual(servicePeriodPreset(-1, '2026-08-09'), { label: 'Last month', start: '2026-07-01', end: '2026-07-31' });
		assert.equal(servicePeriodPresetState('2026-08-02', '2026-08-09', '2026-08-09'), 'Custom');
	});
});
