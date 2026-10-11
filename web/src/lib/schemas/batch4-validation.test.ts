import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
	appointmentSchema,
	isOptionalEmailValid,
	parseForm,
	tenantSchema,
	vendorSchema
} from './index.ts';

test('S26-BUG-1 uses schema-valid email state instead of an @ substring check', () => {
	assert.equal(isOptionalEmailValid(''), true);
	assert.equal(isOptionalEmailValid('person@example.com'), true);
	assert.equal(isOptionalEmailValid('person@'), false);
});

test('S26-BUG-2 mirrors the tenant name maximum on the client', () => {
	const base = { firstName: 'A', lastName: 'B', email: '', phone: '', emergencyContact: '' };
	assert.equal(parseForm(tenantSchema, { ...base, firstName: 'A'.repeat(100) }).errors, null);
	assert.ok(parseForm(tenantSchema, { ...base, firstName: 'A'.repeat(101) }).errors?.firstName);
});

test('S26-POT-4 mirrors vendor field maximums on the client', () => {
	const base = {
		name: 'Vendor', serviceType: 'Plumbing', addressLine1: '', city: '', state: '', postalCode: '',
		email: '', phone: '', website: '', is1099Eligible: false, w9OnFile: false, preferred: false
	};
	assert.equal(parseForm(vendorSchema, { ...base, name: 'N'.repeat(200) }).errors, null);
	assert.ok(parseForm(vendorSchema, { ...base, name: 'N'.repeat(201) }).errors?.name);
	assert.ok(parseForm(vendorSchema, { ...base, serviceType: 'S'.repeat(121) }).errors?.serviceType);
	assert.ok(parseForm(vendorSchema, { ...base, addressLine1: 'A'.repeat(251) }).errors?.addressLine1);
	assert.ok(parseForm(vendorSchema, { ...base, postalCode: 'P'.repeat(21) }).errors?.postalCode);
});

test('S26-POT-4 mirrors appointment field maximums on the client', () => {
	const base = {
		title: 'Showing', type: 'Showing', status: 'Scheduled', scheduledStart: '2027-01-01T09:00', scheduledEnd: '',
		propertyId: '', unitId: '', tenantId: '', workOrderId: '', prospectName: '', prospectEmail: '', assignedTo: ''
	};
	assert.ok(parseForm(appointmentSchema, { ...base, title: 'T'.repeat(201) }).errors?.title);
	assert.ok(parseForm(appointmentSchema, { ...base, prospectName: 'P'.repeat(201) }).errors?.prospectName);
	assert.ok(parseForm(appointmentSchema, { ...base, assignedTo: 'A'.repeat(121) }).errors?.assignedTo);
	assert.ok(parseForm(appointmentSchema, { ...base, prospectEmail: 'e'.repeat(201) }).errors?.prospectEmail);
});

test('S26-POT-1 rejects an appointment ending at or before its start', () => {
	const base = {
		title: 'Showing', type: 'Showing', status: 'Scheduled', scheduledStart: '2027-01-01T09:00', scheduledEnd: '2027-01-01T09:00',
		propertyId: '', unitId: '', tenantId: '', workOrderId: '', prospectName: '', prospectEmail: '', assignedTo: ''
	};
	assert.equal(parseForm(appointmentSchema, base).errors?.scheduledEnd, 'The end time must be after the start time');
	assert.ok(parseForm(appointmentSchema, { ...base, scheduledEnd: '2027-01-01T08:59' }).errors?.scheduledEnd);
	assert.equal(parseForm(appointmentSchema, { ...base, scheduledEnd: '2027-01-01T09:01' }).errors, null);
});
