<!--
  PropertyFields — the single source-of-truth field group for a Property create/edit form. Consumed
  by BOTH the manual New-Property modal (properties/+page.svelte) AND the guided scan flow's Step 1.
  Owns ONLY the field rows + inline errors + the optional "from your lease" badge — NOT the dialog
  chrome or submit button (each caller wraps it). Address uses the shared Google-Places AddressAutocomplete.
-->
<script lang="ts">
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import AddressAutocomplete from '$lib/components/shared/AddressAutocomplete.svelte';
	import StateSelect from '$lib/components/shared/StateSelect.svelte';
	import AutoFilledBadge from './AutoFilledBadge.svelte';
	import { STEP_FIELD_TO_EXTRACTION } from '$lib/scan/lease-prefill';

	let {
		form = $bindable(),
		errors = {},
		autoFilled,
		confidence,
		testidPrefix = 'property'
	}: {
		form: { name: string; type: string; addressLine1: string; addressLine2: string; city: string; state: string; postalCode: string; ownerEntityId: string };
		errors?: Record<string, string>;
		autoFilled?: Set<string>;
		confidence?: Record<string, number>;
		testidPrefix?: string;
	} = $props();

	const PROPERTY_TYPES = ['SingleFamily', 'MultiFamily', 'Condo', 'Townhouse', 'Commercial', 'Other'];
	const filled = (key: string) => !!autoFilled?.has(key);
	const conf = (key: string) => confidence?.[STEP_FIELD_TO_EXTRACTION[key] ?? ''];
</script>

<div class="grid gap-3 md:grid-cols-2" data-testid={`${testidPrefix}-fields`}>
	<div class="md:col-span-2">
		<div class="mb-1 flex items-center gap-2">
			<span class="text-xs font-medium text-muted-foreground">Name</span>
			<AutoFilledBadge show={filled('name')} confidence={conf('name')} />
		</div>
		<Input data-testid={`${testidPrefix}-name-input`} bind:value={form.name} placeholder="Property name" />
		{#if errors.name}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-name-error`}>{errors.name}</p>{/if}
	</div>

	<div class="md:col-span-2">
		<div class="mb-1 flex items-center gap-2">
			<span class="text-xs font-medium text-muted-foreground">Address</span>
			<AutoFilledBadge show={filled('addressLine1')} confidence={conf('addressLine1')} />
		</div>
		<AddressAutocomplete
			testid={`${testidPrefix}-address-input`}
			bind:value={form.addressLine1}
			placeholder="Street address"
			onresolved={(a) => {
				if (a.city) form.city = a.city;
				if (a.state) form.state = a.state;
				if (a.zip) form.postalCode = a.zip;
			}}
		/>
		{#if errors.addressLine1}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-address-error`}>{errors.addressLine1}</p>{/if}
	</div>

	<div>
		<div class="mb-1 flex items-center gap-2">
			<span class="text-xs font-medium text-muted-foreground">City</span>
			<AutoFilledBadge show={filled('city')} confidence={conf('city')} />
		</div>
		<Input data-testid={`${testidPrefix}-city-input`} bind:value={form.city} placeholder="City" />
		{#if errors.city}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-city-error`}>{errors.city}</p>{/if}
	</div>

	<div class="grid grid-cols-2 gap-2">
		<div>
			<div class="mb-1 flex items-center gap-2">
				<span class="text-xs font-medium text-muted-foreground">State</span>
				<AutoFilledBadge show={filled('state')} confidence={conf('state')} />
			</div>
			<StateSelect bind:value={form.state} testid={`${testidPrefix}-state-input`} />
			{#if errors.state}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-state-error`}>{errors.state}</p>{/if}
		</div>
		<div>
			<div class="mb-1 flex items-center gap-2">
				<span class="text-xs font-medium text-muted-foreground">ZIP</span>
				<AutoFilledBadge show={filled('postalCode')} confidence={conf('postalCode')} />
			</div>
			<Input data-testid={`${testidPrefix}-postal-input`} bind:value={form.postalCode} placeholder="ZIP" />
			{#if errors.postalCode}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-postal-error`}>{errors.postalCode}</p>{/if}
		</div>
	</div>

	<div class="md:col-span-2">
		<span class="mb-1 block text-xs font-medium text-muted-foreground">Type</span>
		<Select.Root type="single" bind:value={form.type}>
			<Select.Trigger class="w-full" data-testid={`${testidPrefix}-type-input`}>{form.type || 'Select type'}</Select.Trigger>
			<Select.Content>
				{#each PROPERTY_TYPES as t}
					<Select.Item value={t} label={t}>{t}</Select.Item>
				{/each}
			</Select.Content>
		</Select.Root>
	</div>
</div>
