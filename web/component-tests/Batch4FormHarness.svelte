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
		onsubmit = () => {},
		statefulForm = false,
		onformchange = () => {}
	}: {
		kind: 'tenant' | 'vendor' | 'appointment-details' | 'appointment-schedule';
		tenantForm?: TenantForm;
		vendorForm?: VendorForm;
		appointmentForm?: AppointmentForm;
		errors?: Record<string, string>;
		vendorStep?: number;
		onsubmit?: (event: SubmitEvent) => void;
		statefulForm?: boolean;
		onformchange?: (form: TenantForm | VendorForm) => void;
	} = $props();

	let tenantState = $state<TenantForm>(
		tenantForm ?? { firstName: '', lastName: '', email: '', phone: '', emergencyContact: '' }
	);
	let vendorState = $state<VendorForm>(
		vendorForm ?? {
			name: '',
			serviceType: '',
			addressLine1: '',
			city: '',
			state: '',
			postalCode: '',
			email: '',
			phone: '',
			website: '',
			is1099Eligible: false,
			w9OnFile: false,
			preferred: false
		}
	);

	$effect(() => {
		if (!statefulForm) return;
		if (kind === 'tenant') {
			void tenantState.phone;
			onformchange(tenantState);
		} else if (kind === 'vendor') {
			void vendorState.phone;
			void vendorState.postalCode;
			onformchange(vendorState);
		}
	});
</script>

<form data-testid="batch4-form" onsubmit={(event) => { event.preventDefault(); onsubmit(event); }}>
	{#if kind === 'tenant'}
		{#if statefulForm}
			<TenantFields bind:form={tenantState} bind:errors testidPrefix="tenant" />
		{:else}
			<TenantFields bind:form={tenantForm} bind:errors testidPrefix="tenant" />
		{/if}
	{:else if kind === 'vendor'}
		{#if statefulForm}
			<VendorFields bind:form={vendorState} bind:errors step={vendorStep} testidPrefix="vendor" />
		{:else}
			<VendorFields bind:form={vendorForm} bind:errors step={vendorStep} testidPrefix="vendor" />
		{/if}
	{:else if kind === 'appointment-details'}
		<AppointmentDetailsFields bind:form={appointmentForm} bind:errors testidPrefix="appointment" />
	{:else}
		<AppointmentScheduleFields bind:form={appointmentForm} bind:errors testidPrefix="appointment" />
	{/if}
	<button type="submit" data-testid="batch4-submit">Submit</button>
</form>
