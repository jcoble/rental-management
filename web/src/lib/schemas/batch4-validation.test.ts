import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
	appointmentSchema,
	isOptionalEmailValid,
	parseForm,
	tenantSchema,
	vendorSchema
} from './index.ts';
import { clearFieldErrorWhen, validationErrorsToFormErrors } from '../forms/form-errors.ts';
import { limitInputLength } from '../forms/input-masks.ts';

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

test('S26-POT-1 rejects an appointment ending before it starts but permits an equal end', () => {
	const base = {
		title: 'Showing', type: 'Showing', status: 'Scheduled', scheduledStart: '2027-01-01T09:00', scheduledEnd: '2027-01-01T09:00',
		propertyId: '', unitId: '', tenantId: '', workOrderId: '', prospectName: '', prospectEmail: '', assignedTo: ''
	};
	assert.equal(parseForm(appointmentSchema, base).errors, null);
	assert.ok(parseForm(appointmentSchema, { ...base, scheduledEnd: '2027-01-01T08:59' }).errors?.scheduledEnd);
});

test('S26-BUG-2 keeps mapped maximum-length errors until the matching schema field is valid', () => {
	let tenantErrors = validationErrorsToFormErrors({ FirstName: ['First name is too long.'] });
	tenantErrors = clearFieldErrorWhen(
		tenantErrors,
		'firstName',
		tenantSchema.shape.firstName.safeParse('A'.repeat(101)).success
	);
	assert.equal(tenantErrors.firstName, 'First name is too long.');
	tenantErrors = clearFieldErrorWhen(
		tenantErrors,
		'firstName',
		tenantSchema.shape.firstName.safeParse('A'.repeat(100)).success
	);
	assert.equal(tenantErrors.firstName, undefined);

	let vendorErrors = validationErrorsToFormErrors({
		Name: ['Name is too long.'],
		ServiceType: ['Service type is too long.']
	});
	vendorErrors = clearFieldErrorWhen(
		vendorErrors,
		'name',
		vendorSchema.shape.name.safeParse('N'.repeat(201)).success
	);
	vendorErrors = clearFieldErrorWhen(
		vendorErrors,
		'serviceType',
		vendorSchema.shape.serviceType.safeParse('S'.repeat(121)).success
	);
	assert.deepEqual(vendorErrors, {
		name: 'Name is too long.',
		serviceType: 'Service type is too long.'
	});
	vendorErrors = clearFieldErrorWhen(
		vendorErrors,
		'name',
		vendorSchema.shape.name.safeParse('N'.repeat(200)).success
	);
	vendorErrors = clearFieldErrorWhen(
		vendorErrors,
		'serviceType',
		vendorSchema.shape.serviceType.safeParse('S'.repeat(120)).success
	);
	assert.deepEqual(vendorErrors, {});

	let appointmentErrors = validationErrorsToFormErrors({ Title: ['Title is too long.'] });
	appointmentErrors = clearFieldErrorWhen(
		appointmentErrors,
		'title',
		appointmentSchema.shape.title.safeParse('T'.repeat(201)).success
	);
	assert.equal(appointmentErrors.title, 'Title is too long.');
	appointmentErrors = clearFieldErrorWhen(
		appointmentErrors,
		'title',
		appointmentSchema.shape.title.safeParse('T'.repeat(200)).success
	);
	assert.equal(appointmentErrors.title, undefined);
});

test('S26-POT-1 clears the appointment range error for blank, equal, and later end times', () => {
	const base = {
		title: 'Showing', type: 'Showing', status: 'Scheduled', scheduledStart: '2027-01-01T09:00',
		propertyId: '', unitId: '', tenantId: '', workOrderId: '', prospectName: '', prospectEmail: '', assignedTo: ''
	};
	const clearFor = (scheduledEnd: string, errors: Record<string, string>) =>
		clearFieldErrorWhen(
			errors,
			'scheduledEnd',
			!scheduledEnd || !parseForm(appointmentSchema, { ...base, scheduledEnd }).errors?.scheduledEnd
		);

	const reversed = clearFor('2027-01-01T08:59', { scheduledEnd: 'End time must be at or after start time' });
	assert.equal(reversed.scheduledEnd, 'End time must be at or after start time');
	assert.equal(clearFor('', reversed).scheduledEnd, undefined);
	assert.equal(clearFor('2027-01-01T09:00', { scheduledEnd: 'old range error' }).scheduledEnd, undefined);
	assert.equal(clearFor('2027-01-01T10:00', { scheduledEnd: 'old range error' }).scheduledEnd, undefined);
});

test('S26-POT-4 keeps length-only international vendor inputs within the rendered maxlength contract', () => {
	const postalCode = 'SW1A 1AA';
	const phone = '+44 20 7946 0958 ext 123';
	assert.equal(limitInputLength(postalCode, 20), postalCode);
	assert.equal(limitInputLength(phone, 50), phone);
	assert.equal(limitInputLength('P'.repeat(21), 20), 'P'.repeat(20));
	assert.equal(limitInputLength('1'.repeat(51), 50), '1'.repeat(50));

	const result = parseForm(vendorSchema, {
		name: 'Vendor', serviceType: 'Plumbing', addressLine1: '', city: '', state: '',
		postalCode, email: '', phone, website: '', is1099Eligible: false, w9OnFile: false, preferred: false
	});
	assert.equal(result.errors, null);
});
