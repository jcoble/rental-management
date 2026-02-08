<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { properties } from '$lib/api/endpoints/properties';
	import { owners } from '$lib/api/endpoints/owners';
	import type { Property } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { Plus, Home } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	const propertiesQuery = createQuery(() => ({
		queryKey: ['properties', portfolioId],
		queryFn: () => properties.list(portfolioId),
	}));

	const ownersQuery = createQuery(() => ({
		queryKey: ['owners', portfolioId],
		queryFn: () => owners.list(portfolioId),
	}));

	let selectedProperty = $state<number | null>(null);
	const unitsQuery = createQuery(() => ({
		queryKey: ['units', selectedProperty],
		enabled: selectedProperty !== null,
		queryFn: () => properties.listUnits(selectedProperty!),
	}));

	let showCreate = $state(false);
	let newProperty = $state({
		name: '',
		type: 'MultiFamily',
		addressLine1: '',
		city: '',
		state: '',
		postalCode: '',
		ownerId: '',
	});

	let newUnit = $state({
		unitNumber: '',
		bedrooms: '1',
		bathrooms: '1',
		marketRent: '1200',
	});

	const createPropertyMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => properties.create(data),
		onSuccess: () => {
			showCreate = false;
			newProperty = { name: '', type: 'MultiFamily', addressLine1: '', city: '', state: '', postalCode: '', ownerId: '' };
			queryClient.invalidateQueries({ queryKey: ['properties', portfolioId] });
		},
	}));

	const createUnitMutation = createMutation(() => ({
		mutationFn: ({ propertyId, data }: { propertyId: number; data: Record<string, unknown> }) => properties.createUnit(propertyId, data),
		onSuccess: () => {
			newUnit = { unitNumber: '', bedrooms: '1', bathrooms: '1', marketRent: '1200' };
			queryClient.invalidateQueries({ queryKey: ['units', selectedProperty] });
			queryClient.invalidateQueries({ queryKey: ['properties', portfolioId] });
		},
	}));

	function submitProperty() {
		if (!newProperty.name || !newProperty.addressLine1 || !newProperty.city || !newProperty.state || !newProperty.postalCode) return;
		createPropertyMutation.mutate({
			portfolioId,
			name: newProperty.name,
			type: newProperty.type,
			addressLine1: newProperty.addressLine1,
			city: newProperty.city,
			state: newProperty.state,
			postalCode: newProperty.postalCode,
			ownerId: newProperty.ownerId ? Number(newProperty.ownerId) : null,
		});
	}

	function submitUnit() {
		if (!selectedProperty || !newUnit.unitNumber) return;
		createUnitMutation.mutate({
			propertyId: selectedProperty,
			data: {
				unitNumber: newUnit.unitNumber,
				bedrooms: Number(newUnit.bedrooms),
				bathrooms: Number(newUnit.bathrooms),
				marketRent: Number(newUnit.marketRent),
			},
		});
	}
</script>

<svelte:head>
	<title>Properties - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6">
	<div class="mb-4 flex items-center justify-between">
		<div>
			<h1 class="text-2xl font-bold">Properties</h1>
			<p class="text-sm text-text-secondary">Portfolio, units, and occupancy setup.</p>
		</div>
		<button class="inline-flex items-center gap-2 rounded-md bg-accent px-3 py-2 text-sm text-white" onclick={() => (showCreate = !showCreate)}>
			<Plus class="h-4 w-4" />
			New Property
		</button>
	</div>

	{#if showCreate}
		<div class="mb-5 rounded-lg border border-border bg-surface p-4">
			<div class="grid gap-3 md:grid-cols-3">
				<input bind:value={newProperty.name} class="rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Property name" />
				<select bind:value={newProperty.type} class="rounded border border-border bg-bg px-3 py-2 text-sm">
					<option>SingleFamily</option><option>MultiFamily</option><option>Condo</option><option>Townhome</option><option>Commercial</option><option>MixedUse</option>
				</select>
				<select bind:value={newProperty.ownerId} class="rounded border border-border bg-bg px-3 py-2 text-sm">
					<option value="">No owner assigned</option>
					{#each ownersQuery.data || [] as owner}
						<option value={owner.id}>{owner.name}</option>
					{/each}
				</select>
				<input bind:value={newProperty.addressLine1} class="rounded border border-border bg-bg px-3 py-2 text-sm md:col-span-2" placeholder="Address" />
				<input bind:value={newProperty.city} class="rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="City" />
				<input bind:value={newProperty.state} class="rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="State" />
				<input bind:value={newProperty.postalCode} class="rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="ZIP" />
			</div>
			<div class="mt-3">
				<button onclick={submitProperty} class="rounded bg-accent px-3 py-2 text-sm text-white" disabled={createPropertyMutation.isPending}>Save Property</button>
			</div>
		</div>
	{/if}

	<div class="grid gap-5 lg:grid-cols-5">
		<div class="space-y-3 lg:col-span-3">
			{#if propertiesQuery.data?.length}
				{#each propertiesQuery.data as property}
					<button
						onclick={() => (selectedProperty = property.id)}
						class="w-full rounded-lg border border-border bg-surface p-4 text-left transition-colors hover:border-border-hover {selectedProperty === property.id ? 'ring-1 ring-accent' : ''}"
					>
						<div class="flex items-start justify-between">
							<div>
								<p class="font-medium">{property.name}</p>
								<p class="text-xs text-text-secondary">{property.addressLine1}, {property.city}, {property.state} {property.postalCode}</p>
							</div>
							<span class="rounded border border-border bg-bg px-2 py-0.5 text-xs">{property.status}</span>
						</div>
						<div class="mt-2 flex gap-4 text-xs text-text-secondary">
							<span>{property.unitCount || 0} units</span>
							<span>{property.occupiedUnits || 0} occupied</span>
							<span>{property.type}</span>
						</div>
					</button>
				{/each}
			{:else}
				<div class="rounded-lg border border-border bg-surface p-6 text-center text-text-secondary">No properties yet.</div>
			{/if}
		</div>

		<div class="rounded-lg border border-border bg-surface p-4 lg:col-span-2">
			<div class="mb-3 flex items-center gap-2"><Home class="h-4 w-4 text-accent" /><h2 class="font-semibold">Units</h2></div>
			{#if !selectedProperty}
				<p class="text-sm text-text-secondary">Select a property to manage units.</p>
			{:else}
				<div class="mb-3 grid gap-2">
					<input bind:value={newUnit.unitNumber} class="rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Unit number" />
					<div class="grid grid-cols-3 gap-2">
						<input bind:value={newUnit.bedrooms} class="rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Beds" />
						<input bind:value={newUnit.bathrooms} class="rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Baths" />
						<input bind:value={newUnit.marketRent} class="rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Rent" />
					</div>
					<button onclick={submitUnit} class="rounded bg-accent px-3 py-2 text-sm text-white" disabled={createUnitMutation.isPending}>Add Unit</button>
				</div>
				<div class="space-y-2">
					{#each unitsQuery.data || [] as unit}
						<div class="rounded border border-border bg-bg px-3 py-2 text-sm">
							<div class="flex items-center justify-between">
								<p class="font-medium">Unit {unit.unitNumber}</p>
								<span class="text-xs text-text-secondary">{unit.status}</span>
							</div>
							<p class="text-xs text-text-secondary">{unit.bedrooms}bd / {unit.bathrooms}ba · ${unit.marketRent}/mo</p>
						</div>
					{/each}
				</div>
			{/if}
		</div>
	</div>
</div>
