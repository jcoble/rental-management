<script lang="ts">
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
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import * as Card from '$lib/components/ui/card';

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
			type: p.type,
			addressLine1: p.addressLine1,
			city: p.city,
			state: p.state,
			postalCode: p.postalCode,
			ownerId: p.ownerId != null ? String(p.ownerId) : '',
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
			data: { portfolioId, ...result.data },
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

	// Label helpers for Select triggers
	const propertyTypes = ['SingleFamily', 'MultiFamily', 'Condo', 'Townhome', 'Commercial', 'MixedUse'];
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
		<Button
			data-testid="property-create-button"
			onclick={openCreate}
		>
			<Plus class="h-4 w-4" />
			New Property
		</Button>
	</div>

	<div class="mb-4 max-w-sm">
		<SearchInput bind:value={search} placeholder="Search properties…" testid="property-search" />
	</div>

	<div class="grid gap-5 lg:grid-cols-5">
		<div class="space-y-3 lg:col-span-3">
			{#if propertiesQuery.isLoading}
				<Card.Root class="gap-0 py-0" data-testid="properties-loading">
					<Card.Content class="p-6 text-center text-muted-foreground">Loading…</Card.Content>
				</Card.Root>
			{:else if list.length}
				<ul data-testid="properties-list" class="space-y-3">
					{#each list as property (property.id)}
						<li
							data-testid="property-row"
							data-property-id={property.id}
							class="rounded-lg border border-border bg-card p-4 transition-colors hover:border-border {selectedProperty === property.id ? 'ring-1 ring-ring' : ''}"
						>
							<div class="flex items-start justify-between gap-2">
								<Button
									data-testid="property-select"
									variant="ghost"
									class="min-w-0 flex-1 justify-start text-left h-auto py-0 px-0 hover:bg-transparent"
									onclick={() => (selectedProperty = property.id)}
								>
									<div>
										<p class="truncate font-medium" data-testid="property-name">{property.name}</p>
										<p class="truncate text-xs text-muted-foreground">{property.addressLine1}, {property.city}, {property.state} {property.postalCode}</p>
										<div class="mt-2 flex gap-4 text-xs text-muted-foreground">
											<span>{property.unitCount || 0} units</span>
											<span>{property.occupiedUnits || 0} occupied</span>
											<span>{property.type}</span>
										</div>
									</div>
								</Button>
								<div class="flex shrink-0 items-center gap-1">
									<span class="rounded border border-border bg-background px-2 py-0.5 text-xs">{property.status}</span>
									<Button
										data-testid="property-edit"
										aria-label="Edit property"
										variant="ghost"
										size="icon"
										onclick={() => openEdit(property)}
									>
										<Pencil class="h-4 w-4" />
									</Button>
									<Button
										data-testid="property-delete"
										aria-label="Delete property"
										variant="ghost"
										size="icon"
										onclick={() => (deleteTarget = property)}
									>
										<Trash2 class="h-4 w-4" />
									</Button>
								</div>
							</div>
						</li>
					{/each}
				</ul>
			{:else}
				<Card.Root class="gap-0 py-0" data-testid="properties-empty">
					<Card.Content class="p-6 text-center text-muted-foreground">No properties found.</Card.Content>
				</Card.Root>
			{/if}

			<Pagination bind:skip take={PAGE_SIZE} count={list.length} testid="property-pagination" />
		</div>

		<Card.Root class="gap-0 py-0 lg:col-span-2">
			<Card.Content class="p-4">
				<div class="mb-3 flex items-center gap-2"><Home class="h-4 w-4 text-primary" /><h2 class="font-semibold">Units</h2></div>
				{#if !selectedProperty}
					<p class="text-sm text-muted-foreground">Select a property to manage units.</p>
				{:else}
					<div class="mb-3 grid gap-2" data-testid="unit-form">
						<Input data-testid="unit-number-input" bind:value={unitForm.unitNumber} placeholder="Unit number" />
						{#if unitErrors.unitNumber}<p class="text-xs text-destructive" data-testid="unit-number-error">{unitErrors.unitNumber}</p>{/if}
						<div class="grid grid-cols-3 gap-2">
							<Input data-testid="unit-bedrooms-input" bind:value={unitForm.bedrooms} placeholder="Beds" />
							<Input data-testid="unit-bathrooms-input" bind:value={unitForm.bathrooms} placeholder="Baths" />
							<Input data-testid="unit-rent-input" bind:value={unitForm.marketRent} placeholder="Rent" />
						</div>
						<Button data-testid="unit-save-button" onclick={submitUnit} disabled={createUnitMutation.isPending}>Add Unit</Button>
					</div>
					<ul class="space-y-2" data-testid="units-list">
						{#each unitsQuery.data || [] as unit (unit.id)}
							<li class="rounded border border-border bg-background px-3 py-2 text-sm" data-testid="unit-row">
								<div class="flex items-center justify-between">
									<p class="font-medium">Unit {unit.unitNumber}</p>
									<div class="flex items-center gap-2">
										<span class="text-xs text-muted-foreground">{unit.status}</span>
										<Button data-testid="unit-delete" aria-label="Remove unit" variant="ghost" size="icon" class="h-6 w-6" onclick={() => deleteUnitMutation.mutate(unit.id)}>
											<Trash2 class="h-3.5 w-3.5" />
										</Button>
									</div>
								</div>
								<p class="text-xs text-muted-foreground">{unit.bedrooms}bd / {unit.bathrooms}ba · ${unit.marketRent}/mo</p>
							</li>
						{/each}
					</ul>
				{/if}
			</Card.Content>
		</Card.Root>
	</div>
</div>

<Dialog.Root open={showForm} onOpenChange={(v) => { if (!v) closeForm(); }}>
	<Dialog.Content class="max-w-2xl">
		<Dialog.Header>
			<Dialog.Title>{editingId == null ? 'New Property' : 'Edit Property'}</Dialog.Title>
		</Dialog.Header>
		<div class="grid gap-3 md:grid-cols-2" data-testid="property-form">
			<div class="md:col-span-2">
				<Input data-testid="property-name-input" bind:value={form.name} placeholder="Property name" />
				{#if formErrors.name}<p class="mt-1 text-xs text-destructive" data-testid="property-name-error">{formErrors.name}</p>{/if}
			</div>
			<Select.Root type="single" bind:value={form.type}>
				<Select.Trigger class="w-full" data-testid="property-type-input">
					{form.type ? form.type : 'Select type'}
				</Select.Trigger>
				<Select.Content>
					{#each propertyTypes as pt}
						<Select.Item value={pt} label={pt}>{pt}</Select.Item>
					{/each}
				</Select.Content>
			</Select.Root>
			<Select.Root type="single" bind:value={form.ownerId}>
				<Select.Trigger class="w-full" data-testid="property-owner-input">
					{form.ownerId ? ((ownersQuery.data || []).find(o => String(o.id) === form.ownerId)?.name ?? 'No owner assigned') : 'No owner assigned'}
				</Select.Trigger>
				<Select.Content>
					<Select.Item value="" label="No owner assigned">No owner assigned</Select.Item>
					{#each ownersQuery.data || [] as owner}
						<Select.Item value={String(owner.id)} label={owner.name}>{owner.name}</Select.Item>
					{/each}
				</Select.Content>
			</Select.Root>
			<div class="md:col-span-2">
				<Input data-testid="property-address-input" bind:value={form.addressLine1} placeholder="Address" />
				{#if formErrors.addressLine1}<p class="mt-1 text-xs text-destructive" data-testid="property-address-error">{formErrors.addressLine1}</p>{/if}
			</div>
			<div>
				<Input data-testid="property-city-input" bind:value={form.city} placeholder="City" />
				{#if formErrors.city}<p class="mt-1 text-xs text-destructive" data-testid="property-city-error">{formErrors.city}</p>{/if}
			</div>
			<div class="grid grid-cols-2 gap-2">
				<div>
					<Input data-testid="property-state-input" bind:value={form.state} placeholder="State" />
					{#if formErrors.state}<p class="mt-1 text-xs text-destructive" data-testid="property-state-error">{formErrors.state}</p>{/if}
				</div>
				<div>
					<Input data-testid="property-zip-input" bind:value={form.postalCode} placeholder="ZIP" />
					{#if formErrors.postalCode}<p class="mt-1 text-xs text-destructive" data-testid="property-zip-error">{formErrors.postalCode}</p>{/if}
				</div>
			</div>
		</div>
		<div class="mt-4 flex justify-end gap-2">
			<Button data-testid="property-form-cancel" variant="outline" onclick={closeForm}>Cancel</Button>
			<Button data-testid="property-form-save" onclick={submitProperty} disabled={savePropertyMutation.isPending}>
				{savePropertyMutation.isPending ? 'Saving…' : 'Save Property'}
			</Button>
		</div>
	</Dialog.Content>
</Dialog.Root>

<ConfirmDialog
	open={deleteTarget !== null}
	title="Delete property"
	message={deleteTarget ? `Delete "${deleteTarget.name}"? This also removes its units.` : ''}
	busy={deletePropertyMutation.isPending}
	testid="property-delete"
	onconfirm={() => deleteTarget && deletePropertyMutation.mutate(deleteTarget.id)}
	oncancel={() => (deleteTarget = null)}
/>
