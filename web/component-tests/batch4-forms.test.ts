import { cleanup, fireEvent, render, waitFor } from '@testing-library/svelte';
import { afterEach, describe, expect, it, vi } from 'vitest';
import TenantFields from '$lib/components/forms/TenantFields.svelte';
import VendorFields from '$lib/components/forms/VendorFields.svelte';
import Batch4FormHarness from './Batch4FormHarness.svelte';
import { appointmentSchema, parseForm } from '$lib/schemas';
import { validationErrorsToFormErrors } from '$lib/forms/form-errors';

afterEach(() => cleanup());

function tenantForm(overrides: Partial<Record<string, string>> = {}) {
	return {
		firstName: 'Ada',
		lastName: 'Lovelace',
		email: '',
		phone: '',
		emergencyContact: '',
		...overrides,
	};
}

function vendorForm(overrides: Partial<Record<string, string | boolean>> = {}) {
	return {
		name: 'North Star Plumbing',
		serviceType: 'Plumbing',
		addressLine1: '',
		city: '',
		state: '',
		postalCode: '',
		email: '',
		phone: '',
		website: '',
		is1099Eligible: true,
		w9OnFile: false,
		preferred: false,
		...overrides,
	};
}

function appointmentForm(overrides: Partial<Record<string, string>> = {}) {
	return {
		title: 'Unit showing',
		type: 'Showing',
		status: 'Scheduled',
		scheduledStart: '2027-01-01T10:00:00',
		scheduledEnd: '',
		propertyId: '',
		unitId: '',
		tenantId: '',
		workOrderId: '',
		prospectName: '',
		prospectEmail: '',
		assignedTo: '',
		...overrides,
	};
}

describe('batch 4 rendered form validation', () => {
	it('keeps a mapped tenant server error visible at the invalid boundary, then clears after a valid edit', async () => {
		const form = tenantForm();
		const errors = validationErrorsToFormErrors({ FirstName: ['First name is too long'] });
		const onsubmit = vi.fn();
		const view = render(Batch4FormHarness, {
			props: { kind: 'tenant', tenantForm: form, errors: {}, onsubmit },
		});
		const { getByTestId, queryByTestId } = view;
		await fireEvent.input(getByTestId('tenant-first-name-input'), { target: { value: 'X'.repeat(101) } });
		await fireEvent.submit(getByTestId('batch4-form'));
		expect(onsubmit).toHaveBeenCalledOnce();
		await view.rerender({ kind: 'tenant', tenantForm: tenantForm({ firstName: 'X'.repeat(101) }), errors, onsubmit });

		expect(getByTestId('tenant-first-name-error').textContent).toContain('First name is too long');
		await fireEvent.input(getByTestId('tenant-first-name-input'), {
			target: { value: 'Grace' },
		});
		await waitFor(() => expect(queryByTestId('tenant-first-name-error')).toBeNull());
		expect((getByTestId('tenant-first-name-input') as HTMLInputElement).value).toBe('Grace');
	});

	it('keeps a mapped vendor server error visible at the invalid boundary, then clears after a valid edit', async () => {
		const form = vendorForm();
		const errors = validationErrorsToFormErrors({ Name: ['Name is too long'] });
		const onsubmit = vi.fn();
		const view = render(Batch4FormHarness, {
			props: { kind: 'vendor', vendorForm: form, errors: {}, vendorStep: 0, onsubmit },
		});
		const { getByTestId, queryByTestId } = view;
		await fireEvent.input(getByTestId('vendor-name-input'), { target: { value: 'X'.repeat(201) } });
		await fireEvent.submit(getByTestId('batch4-form'));
		expect(onsubmit).toHaveBeenCalledOnce();
		await view.rerender({ kind: 'vendor', vendorForm: vendorForm({ name: 'X'.repeat(201) }), errors, vendorStep: 0, onsubmit });

		expect(getByTestId('vendor-name-error').textContent).toContain('Name is too long');
		await fireEvent.input(getByTestId('vendor-name-input'), {
			target: { value: 'North Star' },
		});
		await waitFor(() => expect(queryByTestId('vendor-name-error')).toBeNull());
		expect((getByTestId('vendor-name-input') as HTMLInputElement).value).toBe('North Star');
	});

	it('keeps a mapped appointment title error visible at the invalid boundary, then clears after a valid edit', async () => {
		const form = appointmentForm();
		const errors = validationErrorsToFormErrors({ Title: ['Title is too long'] });
		const onsubmit = vi.fn();
		const view = render(Batch4FormHarness, {
			props: { kind: 'appointment-details', appointmentForm: form, errors: {}, onsubmit },
		});
		const { getByTestId, queryByTestId } = view;
		await fireEvent.input(getByTestId('appointment-title-input'), { target: { value: 'X'.repeat(201) } });
		await fireEvent.submit(getByTestId('batch4-form'));
		expect(onsubmit).toHaveBeenCalledOnce();
		await view.rerender({ kind: 'appointment-details', appointmentForm: appointmentForm({ title: 'X'.repeat(201) }), errors, onsubmit });

		expect(getByTestId('appointment-title-error').textContent).toContain('Title is too long');
		await fireEvent.input(getByTestId('appointment-title-input'), {
			target: { value: 'Inspection' },
		});
		await waitFor(() => expect(queryByTestId('appointment-title-error')).toBeNull());
		expect((getByTestId('appointment-title-input') as HTMLInputElement).value).toBe('Inspection');
	});

	it('clears the rendered appointment range error through reversed, blank, equal, and later end values', async () => {
		const form = appointmentForm({ scheduledEnd: '2027-01-01T09:00:00' });
		const parsed = parseForm(appointmentSchema, form);
		const errors = validationErrorsToFormErrors({
			ScheduledEnd: [parsed.errors?.scheduledEnd ?? 'End time must be at or after start time'],
		});
		const { getByTestId, queryByTestId } = render(Batch4FormHarness, {
			props: { kind: 'appointment-schedule', appointmentForm: form, errors },
		});

		expect(getByTestId('appointment-end-error')).toBeTruthy();

		await fireEvent.input(getByTestId('appointment-end-input-date'), { target: { value: '' } });
		await waitFor(() => expect(queryByTestId('appointment-end-error')).toBeNull());
		expect((getByTestId('appointment-end-input-date') as HTMLInputElement).value).toBe('');

		await fireEvent.input(getByTestId('appointment-end-input-date'), { target: { value: '01/01/2027' } });
		await fireEvent.input(getByTestId('appointment-end-input-time'), { target: { value: '10:00' } });
		await waitFor(() => expect(queryByTestId('appointment-end-error')).toBeNull());
		expect((getByTestId('appointment-end-input-time') as HTMLInputElement).value).toBe('10:00');

		await fireEvent.input(getByTestId('appointment-end-input-time'), { target: { value: '11:00' } });
		await waitFor(() => expect(queryByTestId('appointment-end-error')).toBeNull());
		expect((getByTestId('appointment-end-input-time') as HTMLInputElement).value).toBe('11:00');
	});

	it('enforces the international length-only phone and postal boundaries through rendered tenant and vendor inputs', async () => {
		const tenant = tenantForm();
		const tenantView = render(TenantFields, {
			props: { form: tenant, errors: {}, testidPrefix: 'tenant' },
		});
		const tenantPhone = tenantView.getByTestId('tenant-phone-input') as HTMLInputElement;
		const exactPhone = `+${'١'.repeat(49)}`;
		await fireEvent.input(tenantPhone, { target: { value: exactPhone } });
		expect(tenantPhone.value).toBe(exactPhone);
		await fireEvent.input(tenantPhone, { target: { value: `${exactPhone}9` } });
		expect(tenantPhone.value).toHaveLength(50);
		cleanup();

		const vendor = vendorForm();
		const vendorContactView = render(VendorFields, {
			props: { form: vendor, errors: {}, step: 1, testidPrefix: 'vendor' },
		});
		const vendorPhone = vendorContactView.getByTestId('vendor-phone-input') as HTMLInputElement;
		await fireEvent.input(vendorPhone, { target: { value: exactPhone } });
		expect(vendorPhone.value).toBe(exactPhone);
		await fireEvent.input(vendorPhone, { target: { value: `${exactPhone}9` } });
		expect(vendorPhone.value).toHaveLength(50);
		cleanup();

		const vendorAddressView = render(VendorFields, {
			props: { form: vendor, errors: {}, step: 2, testidPrefix: 'vendor' },
		});
		const postal = vendorAddressView.getByTestId('vendor-zip-input') as HTMLInputElement;
		const exactPostal = 'SW1A 1AA 東京';
		await fireEvent.input(postal, { target: { value: exactPostal } });
		expect(postal.value).toBe(exactPostal);
		const overPostal = `${'國'.repeat(21)}`;
		await fireEvent.input(postal, { target: { value: overPostal } });
		expect(postal.value).toHaveLength(20);
	});
});
