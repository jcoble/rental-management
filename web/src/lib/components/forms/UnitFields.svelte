<!--
  UnitFields — the single source-of-truth field group for a Unit create/edit form. Consumed by BOTH
  the manual Add-Unit modal (properties/[id]/+page.svelte) AND the guided scan flow's Step 2. Owns
  ONLY the field rows + inline errors + the optional "from your lease" badge (AC-3) — not the dialog
  chrome or submit button (each caller wraps it).
-->
<script lang="ts">
	import { Input } from '$lib/components/ui/input';
	import AutoFilledBadge from './AutoFilledBadge.svelte';
	import { STEP_FIELD_TO_EXTRACTION } from '$lib/scan/lease-prefill';

	let {
		form = $bindable(),
		errors = {},
		autoFilled,
		confidence,
		testidPrefix = 'unit'
	}: {
		form: {
			unitNumber: string;
			bedrooms: string;
			bathrooms: string;
			marketRent: string;
			floorPlan?: string;
			squareFeet?: string;
			notes?: string;
		};
		errors?: Record<string, string>;
		autoFilled?: Set<string>;
		confidence?: Record<string, number>;
		testidPrefix?: string;
	} = $props();

	const filled = (key: string) => !!autoFilled?.has(key);
	const conf = (key: string) => confidence?.[STEP_FIELD_TO_EXTRACTION[key] ?? ''];
</script>

<div class="grid gap-3" data-testid={`${testidPrefix}-fields`}>
	<div>
		<div class="mb-1 flex items-center gap-2">
			<span class="text-xs font-medium text-muted-foreground">Unit number</span>
			<AutoFilledBadge show={filled('unitNumber')} confidence={conf('unitNumber')} />
		</div>
		<Input data-testid={`${testidPrefix}-number-input`} bind:value={form.unitNumber} placeholder="Unit number" />
		{#if errors.unitNumber}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-number-error`}>{errors.unitNumber}</p>{/if}
	</div>
	<div class="grid grid-cols-3 gap-2">
		<div>
			<div class="mb-1 flex items-center gap-2">
				<span class="text-xs font-medium text-muted-foreground">Beds</span>
				<AutoFilledBadge show={filled('bedrooms')} confidence={conf('bedrooms')} />
			</div>
			<Input data-testid={`${testidPrefix}-bedrooms-input`} bind:value={form.bedrooms} placeholder="Beds" inputmode="numeric" mask="integer" />
			{#if errors.bedrooms}<p class="mt-1 text-xs text-destructive">{errors.bedrooms}</p>{/if}
		</div>
		<div>
			<div class="mb-1 flex items-center gap-2">
				<span class="text-xs font-medium text-muted-foreground">Baths</span>
				<AutoFilledBadge show={filled('bathrooms')} confidence={conf('bathrooms')} />
			</div>
			<Input data-testid={`${testidPrefix}-bathrooms-input`} bind:value={form.bathrooms} placeholder="Baths" inputmode="decimal" mask="decimal" />
			{#if errors.bathrooms}<p class="mt-1 text-xs text-destructive">{errors.bathrooms}</p>{/if}
		</div>
		<div>
			<span class="mb-1 block text-xs font-medium text-muted-foreground">Rent</span>
			<Input data-testid={`${testidPrefix}-rent-input`} bind:value={form.marketRent} placeholder="Rent" inputmode="decimal" mask="currency" />
			{#if errors.marketRent}<p class="mt-1 text-xs text-destructive">{errors.marketRent}</p>{/if}
		</div>
	</div>
	<div class="grid gap-2 sm:grid-cols-2">
		<div>
			<div class="mb-1 flex items-center gap-2">
				<span class="text-xs font-medium text-muted-foreground">Square feet</span>
				<AutoFilledBadge show={filled('squareFeet')} confidence={conf('squareFeet')} />
			</div>
			<Input data-testid={`${testidPrefix}-square-feet-input`} bind:value={form.squareFeet} placeholder="Square feet" inputmode="numeric" mask="integer" />
			{#if errors.squareFeet}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-square-feet-error`}>{errors.squareFeet}</p>{/if}
		</div>
		<div>
			<span class="mb-1 block text-xs font-medium text-muted-foreground">Floor plan</span>
			<Input data-testid={`${testidPrefix}-floor-plan-input`} bind:value={form.floorPlan} placeholder="e.g. Garden 2B" />
			{#if errors.floorPlan}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-floor-plan-error`}>{errors.floorPlan}</p>{/if}
		</div>
	</div>
	<div>
		<span class="mb-1 block text-xs font-medium text-muted-foreground">Notes</span>
		<textarea
			data-testid={`${testidPrefix}-notes-input`}
			bind:value={form.notes}
			rows="3"
			class="w-full rounded-md border border-input bg-background px-3 py-2 text-sm shadow-xs outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50"
			placeholder="Access, parking, appliances, or other unit details"
		></textarea>
		{#if errors.notes}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-notes-error`}>{errors.notes}</p>{/if}
	</div>
</div>
