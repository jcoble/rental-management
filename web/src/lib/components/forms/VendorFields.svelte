<!--
  VendorFields — the field group used by the manual vendor form. Keeping the inputs and their
  submit-time error-clearing rules together makes the rendered validation contract testable without
  mounting the data-grid route shell.
-->
<script lang="ts">
	import { Input } from '$lib/components/ui/input';
	import AddressAutocomplete from '$lib/components/shared/AddressAutocomplete.svelte';
	import StateSelect from '$lib/components/shared/StateSelect.svelte';
	import { isOptionalEmailValid, vendorSchema } from '$lib/schemas';
	import { clearFieldErrorWhen } from '$lib/forms/form-errors';

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

	let {
		form = $bindable(),
		errors = $bindable({}),
		step = 0,
		testidPrefix = 'vendor'
	}: {
		form: VendorForm;
		errors?: Record<string, string>;
		step?: number;
		testidPrefix?: string;
	} = $props();

	function isBlankOrValidUrl(value: string) {
		if (!value.trim()) return true;
		try {
			new URL(value);
			return true;
		} catch {
			return false;
		}
	}

	function clearVendorError(field: string) {
		const shouldClear =
			field === 'name'
				? vendorSchema.shape.name.safeParse(form.name).success
				: field === 'serviceType'
					? vendorSchema.shape.serviceType.safeParse(form.serviceType).success
					: field === 'email'
						? isOptionalEmailValid(form.email)
						: field === 'website'
							? vendorSchema.shape.website.safeParse(form.website).success && isBlankOrValidUrl(form.website)
							: false;
		const next = clearFieldErrorWhen(errors, field, shouldClear);
		if (next !== errors) errors = next;
	}

	$effect(() => clearVendorError('name'));
	$effect(() => clearVendorError('serviceType'));
	$effect(() => clearVendorError('email'));
	$effect(() => clearVendorError('website'));
</script>

<div class="space-y-3" data-testid={`${testidPrefix}-fields`}>
	{#if step === 0}
		<div data-testid={`${testidPrefix}-basics-fields`}>
			<span class="mb-1 block text-xs font-medium text-muted-foreground">Vendor name</span>
			<Input data-testid={`${testidPrefix}-name-input`} bind:value={form.name} placeholder="Vendor name" maxlength={200} />
			{#if errors.name}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-name-error`}>{errors.name}</p>{/if}
		</div>
		<div data-testid={`${testidPrefix}-service-fields`}>
			<span class="mb-1 block text-xs font-medium text-muted-foreground">Service type</span>
			<Input data-testid={`${testidPrefix}-service-input`} bind:value={form.serviceType} placeholder="Service type" maxlength={120} />
			{#if errors.serviceType}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-service-error`}>{errors.serviceType}</p>{/if}
		</div>
	{:else if step === 1}
		<div class="grid gap-3 md:grid-cols-2" data-testid={`${testidPrefix}-contact-fields`}>
			<div>
				<span class="mb-1 block text-xs font-medium text-muted-foreground">Email</span>
				<Input data-testid={`${testidPrefix}-email-input`} bind:value={form.email} placeholder="Vendor email" type="email" autocomplete="email" maxlength={200} />
				{#if errors.email}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-email-error`}>{errors.email}</p>{/if}
			</div>
			<div>
				<span class="mb-1 block text-xs font-medium text-muted-foreground">Phone</span>
				<!-- Length-only international contract: schema/DTO/maxlength all allow 50 characters. -->
				<Input data-testid={`${testidPrefix}-phone-input`} bind:value={form.phone} placeholder="Vendor phone" type="tel" autocomplete="tel" inputmode="tel" maxlength={50} />
			</div>
			<div class="md:col-span-2">
				<span class="mb-1 block text-xs font-medium text-muted-foreground">Website</span>
				<Input data-testid={`${testidPrefix}-website-input`} bind:value={form.website} placeholder="https://example.com" type="url" autocomplete="url" inputmode="url" maxlength={500} />
				{#if errors.website}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-website-error`}>{errors.website}</p>{/if}
			</div>
		</div>
	{:else if step === 2}
		<div class="space-y-2" data-testid={`${testidPrefix}-address-fields`}>
			<AddressAutocomplete
				testid={`${testidPrefix}-address-input`}
				bind:value={form.addressLine1}
				placeholder="Vendor address"
				onresolved={(address) => {
					if (address.city) form.city = address.city;
					if (address.state) form.state = address.state;
					if (address.zip) form.postalCode = address.zip;
				}}
			/>
			{#if errors.addressLine1}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-address-error`}>{errors.addressLine1}</p>{/if}
			<div class="grid grid-cols-1 gap-2 sm:grid-cols-[1fr_8rem_9rem]">
				<Input data-testid={`${testidPrefix}-city-input`} bind:value={form.city} placeholder="City" maxlength={120} />
				<StateSelect testid={`${testidPrefix}-state-input`} bind:value={form.state} placeholder="State" />
				<!-- Length-only international contract: schema/DTO/maxlength all allow 20 characters. -->
				<Input data-testid={`${testidPrefix}-zip-input`} bind:value={form.postalCode} placeholder="ZIP or postal code" inputmode="text" autocomplete="postal-code" maxlength={20} />
			</div>
			{#if errors.postalCode}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-zip-error`}>{errors.postalCode}</p>{/if}
		</div>
	{:else}
		<div class="flex flex-wrap gap-4 text-sm" data-testid={`${testidPrefix}-compliance-fields`}>
			<label class="flex items-center gap-1.5" data-testid={`${testidPrefix}-1099-label`}><input data-testid={`${testidPrefix}-1099-input`} type="checkbox" bind:checked={form.is1099Eligible} /> 1099 eligible</label>
			<label class="flex items-center gap-1.5" data-testid={`${testidPrefix}-w9-label`}><input data-testid={`${testidPrefix}-w9-input`} type="checkbox" bind:checked={form.w9OnFile} /> W-9 on file</label>
			<label class="flex items-center gap-1.5" data-testid={`${testidPrefix}-preferred-label`}><input data-testid={`${testidPrefix}-preferred-input`} type="checkbox" bind:checked={form.preferred} /> Preferred</label>
		</div>
	{/if}
</div>
