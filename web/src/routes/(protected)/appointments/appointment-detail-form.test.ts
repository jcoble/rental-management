import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

import type { Appointment } from '$lib/types';

import {
	buildAppointmentDetailSavePayload,
	createAppointmentDetailEditForm,
} from './appointment-detail-form.ts';

function appointment(overrides: Partial<Appointment> = {}): Appointment {
	return {
		id: 1,
		portfolioId: 2,
		propertyId: 3,
		tenantId: 4,
		title: 'Service visit',
		type: 'MaintenanceVisit',
		status: 'Scheduled',
		scheduledStart: '2026-06-27T13:30:00Z',
		scheduledEnd: '2026-06-27T14:15:00Z',
		assignedTo: 'Jordan',
		createdAt: '2026-06-23T12:00:00Z',
		updatedAt: '2026-06-23T12:00:00Z',
		...overrides,
	};
}

describe('createAppointmentDetailEditForm', () => {
	it('seeds datetime-local fields from local wall-clock values, not raw UTC slices', () => {
		const form = createAppointmentDetailEditForm(appointment(), (utcIso) => {
			if (utcIso === '2026-06-27T13:30:00Z') return '2026-06-27T09:30:00';
			if (utcIso === '2026-06-27T14:15:00Z') return '2026-06-27T10:15:00';
			return '';
		});

		assert.equal(form.scheduledStart, '2026-06-27T09:30:00');
		assert.equal(form.scheduledEnd, '2026-06-27T10:15:00');
		assert.equal(form.propertyId, '3');
		assert.equal(form.tenantId, '4');
		assert.equal(form.type, 'MaintenanceVisit');
	});
});

describe('buildAppointmentDetailSavePayload', () => {
	it('converts local wall-clock datetime-local values back to UTC for the API', () => {
		const payload = buildAppointmentDetailSavePayload(
			{
				title: 'Service visit',
				type: 'MaintenanceVisit',
				status: 'Scheduled',
				scheduledStart: '2026-06-27T09:30:00',
				scheduledEnd: '2026-06-27T10:15:00',
				propertyId: 3,
				tenantId: 4,
				prospectName: null,
				prospectEmail: null,
				assignedTo: 'Jordan',
			},
			{
				title: 'Service visit',
				type: 'MaintenanceVisit',
				status: 'Scheduled',
				scheduledStart: '2026-06-27T09:30:00',
				scheduledEnd: '2026-06-27T10:15:00',
				propertyId: '3',
				tenantId: '4',
				prospectName: '',
				prospectEmail: '',
				assignedTo: 'Jordan',
			},
			2,
			(wallClock) => {
				if (wallClock === '2026-06-27T09:30:00') return '2026-06-27T13:30:00.000Z';
				if (wallClock === '2026-06-27T10:15:00') return '2026-06-27T14:15:00.000Z';
				return '';
			}
		);

		assert.equal(payload.portfolioId, 2);
		assert.equal(payload.propertyId, 3);
		assert.equal(payload.tenantId, 4);
		assert.equal(payload.scheduledStart, '2026-06-27T13:30:00.000Z');
		assert.equal(payload.scheduledEnd, '2026-06-27T14:15:00.000Z');
	});

	it('sends null, not an empty string, when optional end time is blank', () => {
		const payload = buildAppointmentDetailSavePayload(
			{
				title: 'Showing',
				type: 'Showing',
				status: 'Scheduled',
				scheduledStart: '2026-06-27T09:30:00',
				scheduledEnd: null,
				propertyId: null,
				tenantId: null,
				prospectName: null,
				prospectEmail: null,
				assignedTo: null,
			},
			{
				title: 'Showing',
				type: 'Showing',
				status: 'Scheduled',
				scheduledStart: '2026-06-27T09:30:00',
				scheduledEnd: '',
				propertyId: '',
				tenantId: '',
				prospectName: '',
				prospectEmail: '',
				assignedTo: '',
			},
			2,
			() => '2026-06-27T13:30:00.000Z'
		);

		assert.equal(payload.scheduledEnd, null);
	});
});

describe('appointment detail page wiring', () => {
	const source = readFileSync(new URL('./[id]/+page.svelte', import.meta.url), 'utf8');

	it('uses the shared detail form helpers instead of raw UTC string slicing', () => {
		assert.match(source, /createAppointmentDetailEditForm/);
		assert.match(source, /buildAppointmentDetailSavePayload/);
		assert.doesNotMatch(source, /scheduledStart:\s*appt\.scheduledStart\?\.slice\(0,\s*16\)/);
		assert.doesNotMatch(source, /scheduledEnd:\s*appt\.scheduledEnd\?\.slice\(0,\s*16\)/);
	});
});

describe('appointment create/edit modal wiring', () => {
	const source = readFileSync(new URL('./+page.svelte', import.meta.url), 'utf8');

	it('uses a guided stepper instead of a dense single-screen dialog form', () => {
		assert.match(source, /const appointmentSteps: FormStepperStep\[\] = \[/);
		assert.match(source, /testid="appointment-stepper"/);
		assert.match(source, /testid="appointment-step-next"/);
		assert.doesNotMatch(source, /grid gap-3 md:grid-cols-3/);
	});

	it('keeps Save on the final step and jumps validation back to the first problem step', () => {
		assert.match(source, /appointmentStep < appointmentSteps\.length - 1/);
		assert.match(source, /data-testid="appointment-form-save"/);
		assert.match(source, /appointmentStep = firstErrorStep/);
	});
});
