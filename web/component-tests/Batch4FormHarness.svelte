<script lang="ts">
	import TenantFields from '$lib/components/forms/TenantFields.svelte';
	import VendorFields from '$lib/components/forms/VendorFields.svelte';
	import AppointmentDetailsFields from '$lib/components/forms/AppointmentDetailsFields.svelte';
	import AppointmentScheduleFields from '$lib/components/forms/AppointmentScheduleFields.svelte';

	type TenantForm = {
		firstName: string;
		lastName: string;
		email: string;
		phone: string;
		emergencyContact: string;
	};

	type VendorForm = {
		name: string;
		serviceType: string;
		addressLine1: string;
		city: string;
		state: string;
		postalCode: string;
		email: string;
		phone: string;
		website: string;
		is1099Eligible: boolean;
		w9OnFile: boolean;
		preferred: boolean;
	};

	type AppointmentForm = {
		title: string;
		type: string;
		status: string;
		scheduledStart: string;
		scheduledEnd: string;
		[key: string]: string;
	};

	let {
		kind,
		tenantForm = $bindable(),
		vendorForm = $bindable(),
		appointmentForm = $bindable(),
		errors = $bindable({}),
		vendorStep = 0,
		onsubmit = () => {}
	}: {
		kind: 'tenant' | 'vendor' | 'appointment-details' | 'appointment-schedule';
		tenantForm?: TenantForm;
		vendorForm?: VendorForm;
		appointmentForm?: AppointmentForm;
		errors?: Record<string, string>;
		vendorStep?: number;
		onsubmit?: (event: SubmitEvent) => void;
	} = $props();
</script>

<form data-testid="batch4-form" onsubmit={(event) => { event.preventDefault(); onsubmit(event); }}>
	{#if kind === 'tenant'}
		<TenantFields bind:form={tenantForm} bind:errors testidPrefix="tenant" />
	{:else if kind === 'vendor'}
		<VendorFields bind:form={vendorForm} bind:errors step={vendorStep} testidPrefix="vendor" />
	{:else if kind === 'appointment-details'}
		<AppointmentDetailsFields bind:form={appointmentForm} bind:errors testidPrefix="appointment" />
	{:else}
		<AppointmentScheduleFields bind:form={appointmentForm} bind:errors testidPrefix="appointment" />
	{/if}
	<button type="submit" data-testid="batch4-submit">Submit</button>
</form>
