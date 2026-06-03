<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/stores';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { ArrowLeft, Pencil, Save, Trash2, X } from '@lucide/svelte';
	import { properties } from '$lib/api/endpoints/properties';
	import { owners } from '$lib/api/endpoints/owners';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { propertySchema, unitSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';

	const queryClient = useQueryClient();
	const propertyId = $derived(parseInt($page.params.id ?? '0', 10));
	const portfolioId = $derived(getCurrentPortfolioId());

	const PROPERTY_TYPES = ['SingleFamily', 'MultiFamily', 'Condo', 'Townhome', 'Commercial', 'MixedUse'];

	let editing = $state(false);
	let form = $state({
		name: '',
		type: 'MultiFamily',
		ownerId: '',
		addressLine1: '',
		city: '',
		state: '',
		postalCode: ''
	});
	let formErrors = $state<Record<string, string>>({});
	let unitForm = $state({ unitNumber: '', bedrooms: '', bathrooms: '', marketRent: '' });
	let unitErrors = $state<Record<string, string>>({});

	const propertyQuery = createQuery(() => ({
		queryKey: ['property', propertyId],
		queryFn: () => properties.get(propertyId),
		enabled: propertyId > 0
	}));
	const unitsQuery = createQuery(() => ({
		queryKey: ['units', propertyId],
		queryFn: () => properties.listUnits(propertyId),
		enabled: propertyId > 0
	}));
	const ownersQuery = createQuery(() => ({
		queryKey: ['owners', portfolioId],
		queryFn: () => owners.list(portfolioId, { take: 200 })
	}));

	const property = $derived(propertyQuery.data);
	const ownerOptions = $derived([
		{ value: '', label: 'No owner assigned' },
		...(ownersQuery.data ?? []).map((owner) => ({ value: String(owner.id), label: owner.name }))
	]);
	const propertyTypeOptions = $derived(PROPERTY_TYPES.map((type) => ({ value: type, label: type })));
	const selectedOwnerName = $derived(
		ownersQuery.data?.find((owner) => owner.id === property?.ownerEntityId)?.name ??
			property?.ownerName ??
			'No owner assigned'
	);

	function startEditing() {
		if (!property) return;
		form = {
			name: property.name,
			type: property.propertyType ?? property.type ?? 'MultiFamily',
			ownerId: property.ownerEntityId != null ? String(property.ownerEntityId) : '',
			addressLine1: property.addressLine1,
			city: property.city,
			state: property.state,
			postalCode: property.postalCode
		};
		formErrors = {};
		editing = true;
	}

	function cancelEditing() {
		editing = false;
		formErrors = {};
	}

	const saveMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => properties.update(propertyId, data),
		onSuccess: () => {
			showSuccess('Property updated.');
			editing = false;
			queryClient.invalidateQueries({ queryKey: ['property', propertyId] });
			queryClient.invalidateQueries({ queryKey: ['properties', portfolioId] });
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	function saveProperty() {
		const result = parseForm(propertySchema, form);
		if (result.errors) {
			formErrors = result.errors;
			return;
		}
		formErrors = {};
		saveMutation.mutate({
			portfolioId,
			name: result.data.name,
			propertyType: result.data.type,
			ownerEntityId: result.data.ownerId,
			addressLine1: result.data.addressLine1,
			city: result.data.city,
			state: result.data.state,
			postalCode: result.data.postalCode
		});
	}

	const createUnitMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => properties.createUnit(propertyId, data),
		onSuccess: () => {
			showSuccess('Unit added.');
			unitForm = { unitNumber: '', bedrooms: '', bathrooms: '', marketRent: '' };
			unitErrors = {};
			queryClient.invalidateQueries({ queryKey: ['units', propertyId] });
			queryClient.invalidateQueries({ queryKey: ['property', propertyId] });
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	function addUnit() {
		const result = parseForm(unitSchema, unitForm);
		if (result.errors) {
			unitErrors = result.errors;
			return;
		}
		createUnitMutation.mutate(result.data);
	}

	const deletePropertyMutation = createMutation(() => ({
		mutationFn: () => properties.delete(propertyId),
		onSuccess: () => {
			showSuccess('Property deleted.');
			goto('/properties');
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	const inputClass = 'h-10 rounded border border-border bg-background px-3 py-2 text-sm';
</script>

<svelte:head>
	<title>{property?.name ?? 'Property'} - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6" data-testid="property-detail-page">
	<div class="mb-5 flex flex-wrap items-start justify-between gap-3">
		<div class="min-w-0">
			<Button variant="ghost" href="/properties" class="mb-2 -ml-3">
				<ArrowLeft class="h-4 w-4" />
				Properties
			</Button>
			<h1 class="truncate text-2xl font-bold">{property?.name ?? 'Property'}</h1>
			<p class="text-sm text-muted-foreground">{property?.addressLine1 ?? ''}{#if property?.city}, {property.city}, {property.state} {property.postalCode}{/if}</p>
		</div>
		{#if property}
			<div class="flex gap-2">
				{#if editing}
					<Button variant="outline" onclick={cancelEditing} disabled={saveMutation.isPending}>
						<X class="h-4 w-4" />
						Cancel
					</Button>
					<Button onclick={saveProperty} disabled={saveMutation.isPending}>
						<Save class="h-4 w-4" />
						{saveMutation.isPending ? 'Saving...' : 'Save'}
					</Button>
				{:else}
					<Button variant="outline" onclick={startEditing}>
						<Pencil class="h-4 w-4" />
						Edit
					</Button>
					<Button variant="destructive" onclick={() => deletePropertyMutation.mutate()} disabled={deletePropertyMutation.isPending}>
						<Trash2 class="h-4 w-4" />
						Delete
					</Button>
				{/if}
			</div>
		{/if}
	</div>

	{#if propertyQuery.isLoading}
		<div class="rounded-lg border border-border bg-card p-6 text-sm text-muted-foreground">Loading property...</div>
	{:else if !property}
		<div class="rounded-lg border border-border bg-card p-6 text-sm text-muted-foreground">Property not found.</div>
	{:else}
		<div class="grid gap-5 xl:grid-cols-[minmax(0,1.35fr)_minmax(360px,0.65fr)]">
			<Card.Root>
				<Card.Header>
					<Card.Title>Property Details</Card.Title>
					<Card.Description>Primary portfolio, address, and ownership fields.</Card.Description>
				</Card.Header>
				<Card.Content>
					<div class="grid gap-4 md:grid-cols-2">
						<InlineField label="Property name" bind:value={form.name} display={property.name} {editing} error={formErrors.name} testid="property-detail-name" class="md:col-span-2" />
						<InlineField label="Property type" bind:value={form.type} display={property.propertyType ?? property.type} {editing} type="select" options={propertyTypeOptions} testid="property-detail-type" />
						<InlineField label="Owner" bind:value={form.ownerId} display={selectedOwnerName} {editing} type="select" options={ownerOptions} testid="property-detail-owner" />
						<InlineField label="Address" bind:value={form.addressLine1} display={property.addressLine1} {editing} error={formErrors.addressLine1} testid="property-detail-address" class="md:col-span-2" />
						<InlineField label="City" bind:value={form.city} display={property.city} {editing} error={formErrors.city} testid="property-detail-city" />
						<InlineField label="State" bind:value={form.state} display={property.state} {editing} error={formErrors.state} testid="property-detail-state" />
						<InlineField label="ZIP" bind:value={form.postalCode} display={property.postalCode} {editing} error={formErrors.postalCode} testid="property-detail-zip" />
						<InlineField label="Status" display={property.status} testid="property-detail-status" />
					</div>
				</Card.Content>
			</Card.Root>

			<Card.Root>
				<Card.Header>
					<Card.Title>Units</Card.Title>
					<Card.Description>Add units without leaving the property record.</Card.Description>
				</Card.Header>
				<Card.Content>
					<div class="mb-4 grid gap-3" data-testid="property-detail-unit-form">
						<div>
							<label class="mb-1 block text-xs font-medium text-muted-foreground" for="property-detail-unit-number-input">Unit number</label>
							<input id="property-detail-unit-number-input" data-testid="property-detail-unit-number-input" bind:value={unitForm.unitNumber} class="{inputClass} w-full" />
							{#if unitErrors.unitNumber}<p class="mt-1 text-xs text-destructive" data-testid="property-detail-unit-number-error">{unitErrors.unitNumber}</p>{/if}
						</div>
						<div class="grid grid-cols-3 gap-2">
							<div>
								<label class="mb-1 block text-xs font-medium text-muted-foreground" for="property-detail-unit-bedrooms-input">Beds</label>
								<input id="property-detail-unit-bedrooms-input" data-testid="property-detail-unit-bedrooms-input" bind:value={unitForm.bedrooms} class="{inputClass} w-full" />
							</div>
							<div>
								<label class="mb-1 block text-xs font-medium text-muted-foreground" for="property-detail-unit-bathrooms-input">Baths</label>
								<input id="property-detail-unit-bathrooms-input" data-testid="property-detail-unit-bathrooms-input" bind:value={unitForm.bathrooms} class="{inputClass} w-full" />
							</div>
							<div>
								<label class="mb-1 block text-xs font-medium text-muted-foreground" for="property-detail-unit-rent-input">Rent</label>
								<input id="property-detail-unit-rent-input" data-testid="property-detail-unit-rent-input" bind:value={unitForm.marketRent} class="{inputClass} w-full" />
							</div>
						</div>
						<Button onclick={addUnit} disabled={createUnitMutation.isPending} data-testid="property-detail-unit-save">
							{createUnitMutation.isPending ? 'Adding...' : 'Add Unit'}
						</Button>
					</div>
					<div class="space-y-2" data-testid="property-detail-units-list">
						{#if unitsQuery.isLoading}
							<p class="text-sm text-muted-foreground">Loading units...</p>
						{:else if !(unitsQuery.data?.length)}
							<p class="rounded border border-border bg-background p-3 text-sm text-muted-foreground">No units yet.</p>
						{:else}
							{#each unitsQuery.data as unit}
								<div class="rounded border border-border bg-background p-3 text-sm" data-testid="property-detail-unit-row">
									<div class="flex items-center justify-between">
										<p class="font-medium">Unit {unit.unitNumber}</p>
										<span class="text-xs text-muted-foreground">{unit.status}</span>
									</div>
									<p class="text-xs text-muted-foreground">{unit.bedrooms}bd / {unit.bathrooms}ba · ${unit.marketRent}/mo</p>
								</div>
							{/each}
						{/if}
					</div>
				</Card.Content>
			</Card.Root>
		</div>
	{/if}
</div>
