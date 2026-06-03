<script lang="ts">
	import { goto } from '$app/navigation';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { properties } from '$lib/api/endpoints/properties';
	import { owners } from '$lib/api/endpoints/owners';
	import type { Property, Unit } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { propertySchema, unitSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { debounced } from '$lib/utils/debounce.svelte';
	import * as Dialog from '$lib/components/ui/dialog';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import Pagination from '$lib/components/shared/Pagination.svelte';
	import { Plus, Home, Pencil, Trash2 } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	const PAGE_SIZE = 20;
	let search = $state('');
	let skip = $state(0);
	const debouncedSearch = debounced(() => search, 300);

	// Reset to the first page whenever the search term changes.
	$effect(() => {
		debouncedSearch.value;
		skip = 0;
	});

	const propertiesQuery = createQuery(() => ({
		queryKey: ['properties', portfolioId, debouncedSearch.value, skip],
		queryFn: () => properties.list(portfolioId, { search: debouncedSearch.value, skip, take: PAGE_SIZE }),
	}));

	const ownersQuery = createQuery(() => ({
		queryKey: ['owners', portfolioId],
		queryFn: () => owners.list(portfolioId, { take: 200 }),
	}));

	let selectedProperty = $state<number | null>(null);
	const unitsQuery = createQuery(() => ({
		queryKey: ['units', selectedProperty],
		enabled: selectedProperty !== null,
		queryFn: () => properties.listUnits(selectedProperty!),
	}));

	const emptyProperty = { name: '', type: 'MultiFamily', addressLine1: '', city: '', state: '', postalCode: '', ownerId: '' };
	let showForm = $state(false);
	let editingId = $state<number | null>(null);
	let form = $state({ ...emptyProperty });
	let formErrors = $state<Record<string, string>>({});

	let deleteTarget = $state<Property | null>(null);

	function invalidateList() {
		queryClient.invalidateQueries({ queryKey: ['properties', portfolioId] });
	}

	const savePropertyMutation = createMutation(() => ({
		mutationFn: ({ id, data }: { id: number | null; data: Record<string, unknown> }) =>
			id == null ? properties.create(data) : properties.update(id, data),
		onSuccess: (_res, vars) => {
			showSuccess(vars.id == null ? 'Property created.' : 'Property updated.');
			closeForm();
			invalidateList();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const deletePropertyMutation = createMutation(() => ({
		mutationFn: (id: number) => properties.delete(id),
		onSuccess: (_res, deletedId) => {
			showSuccess('Property deleted.');
			deleteTarget = null;
			if (selectedProperty === deletedId) selectedProperty = null;
			invalidateList();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function openCreate() {
		editingId = null;
		form = { ...emptyProperty };
		formErrors = {};
		showForm = true;
	}

	function openEdit(p: Property) {
		editingId = p.id;
		form = {
			name: p.name,
			type: p.propertyType ?? p.type ?? 'MultiFamily',
			addressLine1: p.addressLine1,
			city: p.city,
			state: p.state,
			postalCode: p.postalCode,
			ownerId: p.ownerEntityId != null ? String(p.ownerEntityId) : '',
		};
		formErrors = {};
		showForm = true;
	}

	function closeForm() {
		showForm = false;
		editingId = null;
		formErrors = {};
	}

	function submitProperty() {
		const result = parseForm(propertySchema, form);
		if (result.errors) {
			formErrors = result.errors;
			return;
		}
		formErrors = {};
		savePropertyMutation.mutate({
			id: editingId,
			data: {
				portfolioId,
				name: result.data.name,
				propertyType: result.data.type,
				ownerEntityId: result.data.ownerId,
				addressLine1: result.data.addressLine1,
				city: result.data.city,
				state: result.data.state,
				postalCode: result.data.postalCode
			},
		});
	}

	// --- Units (inline panel) ---
	const emptyUnit = { unitNumber: '', bedrooms: '1', bathrooms: '1', marketRent: '1200' };
	let unitForm = $state({ ...emptyUnit });
	let unitErrors = $state<Record<string, string>>({});

	const createUnitMutation = createMutation(() => ({
		mutationFn: ({ propertyId: pid, data }: { propertyId: number; data: Record<string, unknown> }) =>
			properties.createUnit(pid, data),
		onSuccess: () => {
			showSuccess('Unit added.');
			unitForm = { ...emptyUnit };
			queryClient.invalidateQueries({ queryKey: ['units', selectedProperty] });
			invalidateList();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const deleteUnitMutation = createMutation(() => ({
		mutationFn: (id: number) => properties.deleteUnit(id),
		onSuccess: () => {
			showSuccess('Unit removed.');
			queryClient.invalidateQueries({ queryKey: ['units', selectedProperty] });
			invalidateList();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function submitUnit() {
		if (!selectedProperty) return;
		const result = parseForm(unitSchema, unitForm);
		if (result.errors) {
			unitErrors = result.errors;
			return;
		}
		unitErrors = {};
		createUnitMutation.mutate({ propertyId: selectedProperty, data: result.data });
	}

	const list = $derived(propertiesQuery.data ?? []);
	const inputClass = 'h-10 rounded border border-border bg-background px-3 py-2 text-sm';
</script>

<svelte:head>
	<title>Properties - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6" data-testid="properties-page">
	<div class="mb-4 flex items-center justify-between gap-3">
		<div>
			<h1 class="text-2xl font-bold">Properties</h1>
			<p class="text-sm text-muted-foreground">Portfolio, units, and occupancy setup.</p>
		</div>
		<button
			data-testid="property-create-button"
			class="inline-flex items-center gap-2 rounded-md bg-primary px-3 py-2 text-sm text-white"
			onclick={openCreate}
		>
			<Plus class="h-4 w-4" />
			New Property
		</button>
	</div>

	<div class="mb-4 max-w-sm">
		<SearchInput bind:value={search} placeholder="Search properties…" testid="property-search" />
	</div>

	<div class="grid gap-5 lg:grid-cols-5">
		<div class="space-y-3 lg:col-span-3">
			{#if propertiesQuery.isLoading}
				<div class="rounded-lg border border-border bg-card p-6 text-center text-muted-foreground" data-testid="properties-loading">Loading…</div>
			{:else if list.length}
				<ul data-testid="properties-list" class="space-y-3">
					{#each list as property (property.id)}
						<li
							data-testid="property-row"
							data-property-id={property.id}
							class="rounded-lg border border-border bg-card p-4 transition-colors hover:border-border {selectedProperty === property.id ? 'ring-1 ring-ring' : ''}"
						>
							<div class="flex items-start justify-between gap-2">
								<button
									data-testid="property-select"
									class="min-w-0 flex-1 text-left"
									onclick={() => (selectedProperty = property.id)}
								>
									<p class="truncate font-medium" data-testid="property-name">{property.name}</p>
									<p class="truncate text-xs text-muted-foreground">{property.addressLine1}, {property.city}, {property.state} {property.postalCode}</p>
									<div class="mt-2 flex gap-4 text-xs text-muted-foreground">
										<span>{property.unitCount || 0} units</span>
										<span>{property.occupiedUnits || 0} occupied</span>
										<span>{property.propertyType ?? property.type}</span>
									</div>
								</button>
								<div class="flex shrink-0 items-center gap-1">
									<span class="rounded border border-border bg-background px-2 py-0.5 text-xs">{property.status}</span>
									<a
										href={`/properties/${property.id}`}
										data-testid="property-details"
										class="rounded border border-border px-2 py-1 text-xs text-primary hover:bg-secondary"
									>
										Details
									</a>
									<button
										data-testid="property-edit"
										aria-label="Edit property"
										class="rounded p-1.5 text-muted-foreground hover:bg-secondary hover:text-foreground"
										onclick={() => goto(`/properties/${property.id}`)}
									>
										<Pencil class="h-4 w-4" />
									</button>
									<button
										data-testid="property-delete"
										aria-label="Delete property"
										class="rounded p-1.5 text-muted-foreground hover:bg-secondary hover:text-destructive"
										onclick={() => (deleteTarget = property)}
									>
										<Trash2 class="h-4 w-4" />
									</button>
								</div>
							</div>
						</li>
					{/each}
				</ul>
			{:else}
				<div class="rounded-lg border border-border bg-card p-6 text-center text-muted-foreground" data-testid="properties-empty">No properties found.</div>
			{/if}

			<Pagination bind:skip take={PAGE_SIZE} count={list.length} testid="property-pagination" />
		</div>

		<div class="rounded-lg border border-border bg-card p-4 lg:col-span-2">
			<div class="mb-3 flex items-center gap-2"><Home class="h-4 w-4 text-primary" /><h2 class="font-semibold">Units</h2></div>
			{#if !selectedProperty}
				<p class="text-sm text-muted-foreground">Select a property to manage units.</p>
			{:else}
				<div class="mb-3 grid gap-2" data-testid="unit-form">
					<div>
						<label class="mb-1 block text-xs font-medium text-muted-foreground" for="unit-number-input">Unit number</label>
						<input id="unit-number-input" data-testid="unit-number-input" bind:value={unitForm.unitNumber} class="{inputClass} w-full" placeholder="Unit number" />
					</div>
					{#if unitErrors.unitNumber}<p class="text-xs text-destructive" data-testid="unit-number-error">{unitErrors.unitNumber}</p>{/if}
					<div class="grid grid-cols-3 gap-2">
						<div>
							<label class="mb-1 block text-xs font-medium text-muted-foreground" for="unit-bedrooms-input">Beds</label>
							<input id="unit-bedrooms-input" data-testid="unit-bedrooms-input" bind:value={unitForm.bedrooms} class="{inputClass} w-full" placeholder="Beds" />
						</div>
						<div>
							<label class="mb-1 block text-xs font-medium text-muted-foreground" for="unit-bathrooms-input">Baths</label>
							<input id="unit-bathrooms-input" data-testid="unit-bathrooms-input" bind:value={unitForm.bathrooms} class="{inputClass} w-full" placeholder="Baths" />
						</div>
						<div>
							<label class="mb-1 block text-xs font-medium text-muted-foreground" for="unit-rent-input">Market rent</label>
							<input id="unit-rent-input" data-testid="unit-rent-input" bind:value={unitForm.marketRent} class="{inputClass} w-full" placeholder="Rent" />
						</div>
					</div>
					<button data-testid="unit-save-button" onclick={submitUnit} class="rounded bg-primary px-3 py-2 text-sm text-white" disabled={createUnitMutation.isPending}>Add Unit</button>
				</div>
				<ul class="space-y-2" data-testid="units-list">
					{#each unitsQuery.data || [] as unit (unit.id)}
						<li class="rounded border border-border bg-background px-3 py-2 text-sm" data-testid="unit-row">
							<div class="flex items-center justify-between">
								<p class="font-medium">Unit {unit.unitNumber}</p>
								<div class="flex items-center gap-2">
									<span class="text-xs text-muted-foreground">{unit.status}</span>
									<button data-testid="unit-delete" aria-label="Remove unit" class="rounded p-1 text-muted-foreground hover:text-destructive" onclick={() => deleteUnitMutation.mutate(unit.id)}>
										<Trash2 class="h-3.5 w-3.5" />
									</button>
								</div>
							</div>
							<p class="text-xs text-muted-foreground">{unit.bedrooms}bd / {unit.bathrooms}ba · ${unit.marketRent}/mo</p>
						</li>
					{/each}
				</ul>
			{/if}
		</div>
	</div>
</div>

<Dialog.Root open={showForm} onOpenChange={(v) => { if (!v) closeForm(); }}>
	<Dialog.Content class="max-w-2xl">
		<Dialog.Header>
			<Dialog.Title>{editingId == null ? 'New Property' : 'Edit Property'}</Dialog.Title>
		</Dialog.Header>
		<div class="grid gap-3 md:grid-cols-2" data-testid="property-form">
			<div class="md:col-span-2">
				<label class="mb-1 block text-xs font-medium text-muted-foreground" for="property-name-input">Property name</label>
				<input id="property-name-input" data-testid="property-name-input" bind:value={form.name} class="{inputClass} w-full" placeholder="Property name" />
				{#if formErrors.name}<p class="mt-1 text-xs text-destructive" data-testid="property-name-error">{formErrors.name}</p>{/if}
			</div>
			<div>
				<label class="mb-1 block text-xs font-medium text-muted-foreground" for="property-type-input">Property type</label>
				<select id="property-type-input" data-testid="property-type-input" bind:value={form.type} class="{inputClass} w-full">
					<option>SingleFamily</option><option>MultiFamily</option><option>Condo</option><option>Townhome</option><option>Commercial</option><option>MixedUse</option>
				</select>
			</div>
			<div>
				<label class="mb-1 block text-xs font-medium text-muted-foreground" for="property-owner-input">Owner</label>
				<select id="property-owner-input" data-testid="property-owner-input" bind:value={form.ownerId} class="{inputClass} w-full">
					<option value="">No owner assigned</option>
					{#each ownersQuery.data || [] as owner}
						<option value={owner.id}>{owner.name}</option>
					{/each}
				</select>
			</div>
			<div class="md:col-span-2">
				<label class="mb-1 block text-xs font-medium text-muted-foreground" for="property-address-input">Address</label>
				<input id="property-address-input" data-testid="property-address-input" bind:value={form.addressLine1} class="{inputClass} w-full" placeholder="Address" />
				{#if formErrors.addressLine1}<p class="mt-1 text-xs text-destructive" data-testid="property-address-error">{formErrors.addressLine1}</p>{/if}
			</div>
			<div>
				<label class="mb-1 block text-xs font-medium text-muted-foreground" for="property-city-input">City</label>
				<input id="property-city-input" data-testid="property-city-input" bind:value={form.city} class="{inputClass} w-full" placeholder="City" />
				{#if formErrors.city}<p class="mt-1 text-xs text-destructive" data-testid="property-city-error">{formErrors.city}</p>{/if}
			</div>
			<div class="grid grid-cols-2 gap-2">
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="property-state-input">State</label>
					<input id="property-state-input" data-testid="property-state-input" bind:value={form.state} class="{inputClass} w-full" placeholder="State" />
					{#if formErrors.state}<p class="mt-1 text-xs text-destructive" data-testid="property-state-error">{formErrors.state}</p>{/if}
				</div>
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="property-zip-input">ZIP</label>
					<input id="property-zip-input" data-testid="property-zip-input" bind:value={form.postalCode} class="{inputClass} w-full" placeholder="ZIP" />
					{#if formErrors.postalCode}<p class="mt-1 text-xs text-destructive" data-testid="property-zip-error">{formErrors.postalCode}</p>{/if}
				</div>
			</div>
		</div>
		<div class="mt-4 flex justify-end gap-2">
			<button data-testid="property-form-cancel" class="rounded-md border border-border px-3 py-2 text-sm text-muted-foreground hover:bg-secondary" onclick={closeForm}>Cancel</button>
			<button data-testid="property-form-save" onclick={submitProperty} class="rounded bg-primary px-3 py-2 text-sm text-white" disabled={savePropertyMutation.isPending}>
				{savePropertyMutation.isPending ? 'Saving…' : 'Save Property'}
			</button>
		</div>
	</Dialog.Content>
</Dialog.Root>

<ConfirmDialog
	open={deleteTarget !== null}
	title="Delete property"
	message={deleteTarget ? `Delete “${deleteTarget.name}”? This also removes its units.` : ''}
	busy={deletePropertyMutation.isPending}
	testid="property-delete"
	onconfirm={() => deleteTarget && deletePropertyMutation.mutate(deleteTarget.id)}
	oncancel={() => (deleteTarget = null)}
/>
