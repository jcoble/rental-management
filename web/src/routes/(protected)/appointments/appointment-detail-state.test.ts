import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import {
	appointmentDetailStatusActions,
	appointmentDetailStatusLabel,
	appointmentStatusOptions,
} from './appointment-detail-state.ts';

describe('appointment detail status display', () => {
	it('formats NoShow as a user-facing label in detail fields and selects', () => {
		assert.equal(appointmentDetailStatusLabel('NoShow'), 'No show');
		assert.equal(appointmentStatusOptions().find((option) => option.value === 'NoShow')?.label, 'No show');
	});

	it('treats NoShow as a terminal status for detail transition actions', () => {
		assert.deepEqual(appointmentDetailStatusActions('NoShow'), {
			canConfirm: false,
			canComplete: false,
			canCancel: false,
			canNoShow: false,
		});
	});

	it('keeps active transition actions available for scheduled and confirmed appointments', () => {
		assert.deepEqual(appointmentDetailStatusActions('Scheduled'), {
			canConfirm: true,
			canComplete: true,
			canCancel: true,
			canNoShow: true,
		});
		assert.deepEqual(appointmentDetailStatusActions('Confirmed'), {
			canConfirm: false,
			canComplete: true,
			canCancel: true,
			canNoShow: true,
		});
	});
});
