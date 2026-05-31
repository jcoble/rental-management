<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { properties } from '$lib/api/endpoints/properties';
	import { owners } from '$lib/api/endpoints/owners';
	import type { Property } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { propertySchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Select from '$lib/components/ui/select';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { Plus, Pencil, Trash2 } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	let search = $state('');
	let typeFilter = $state('');
	let statusFilter = $state('');

	const propertiesQuery = createQuery(() => ({
		queryKey: ['properties', portfolioId],
		queryFn: () => properties.list(portfolioId, { take: 500 }),
	}));

	const ownersQuery = createQuery(() => ({
		queryKey: ['owners', portfolioId],
		queryFn: () => owners.list(portfolioId, { take: 200 }),
	}));

	// Client-side filter
	const list = $derived.by(() => {
		const all = propertiesQuery.data ?? [];
		const q = search.trim().toLowerCase();
		return all.filter((p) => {
			if (typeFilter && p.type !== typeFilter) return false;
			if (statusFilter && p.status !== statusFilter) return false;
			if (!q) return true;
			return (
				p.name.toLowerCase().includes(q) ||
				p.addressLine1.toLowerCase().includes(q) ||
				p.city.toLowerCase().includes(q)
			);
		});
	});

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
		onSuccess: () => {
			showSuccess('Property deleted.');
			deleteTarget = null;
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

	const propertyTypes = ['SingleFamily', 'MultiFamily', 'Condo', 'Townhome', 'Commercial', 'MixedUse'];
	const propertyStatuses = ['Active', 'UnderMaintenance', 'Inactive'];

	// DataGrid column definitions
	const columns: ColumnDef<Property>[] = [
		{
			key: 'name',
			title: 'Name',
			sortable: true,
			mobileRole: 'title',
			cell: nameCellSnippet,
		},
		{
			key: 'address',
			title: 'Address',
			sortable: false,
			mobileRole: 'subtitle',
			accessor: (p) => `${p.addressLine1}, ${p.city}, ${p.state}`,
		},
		{
			key: 'type',
			title: 'Type',
			sortable: true,
			mobileRole: 'meta',
		},
		{
			key: 'status',
			title: 'Status',
			mobileRole: 'badge',
			cell: statusCellSnippet,
		},
		{
			key: 'unitCount',
			title: 'Units',
			format: 'number',
			sortable: true,
			mobileRole: 'metric',
			accessor: (p) => p.unitCount ?? 0,
		},
		{
			key: 'occupiedUnits',
			title: 'Occupied',
			format: 'number',
			sortable: true,
			mobileRole: 'meta',
			accessor: (p) => p.occupiedUnits ?? 0,
		},
		{
			key: 'actions',
			title: '',
			mobileRole: 'hidden',
			align: 'right',
			width: '6rem',
			cell: actionsCellSnippet,
		},
	];
</script>

{#snippet nameCellSnippet(p: Property)}
	<span data-testid="property-name">{p.name}</span>
{/snippet}

{#snippet statusCellSnippet(p: Property)}
	<StatusBadge status={p.status} />
{/snippet}

{#snippet actionsCellSnippet(p: Property)}
	<div class="flex items-center justify-end gap-1" onclick={(e) => e.stopPropagation()} role="none">
		<button
			type="button"
			data-testid="property-edit"
			class="inline-flex h-7 w-7 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground"
			aria-label="Edit property"
			onclick={(e) => { e.stopPropagation(); openEdit(p); }}
		>
			<Pencil class="h-3.5 w-3.5" />
		</button>
		<button
			type="button"
			data-testid="property-delete"
			class="inline-flex h-7 w-7 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-destructive/10 hover:text-destructive"
			aria-label="Delete property"
			onclick={(e) => { e.stopPropagation(); deleteTarget = p; }}
		>
			<Trash2 class="h-3.5 w-3.5" />
		</button>
	</div>
{/snippet}

<svelte:head>
	<title>Properties - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6" data-testid="properties-page">
	<div class="mb-4 flex items-center justify-between gap-3">
		<div>
			<h1 class="text-2xl font-bold">Properties</h1>
			<p class="text-sm text-muted-foreground">Portfolio, units, and occupancy setup.</p>
		</div>
	</div>

	<DataGrid
		data={list}
		{columns}
		loading={propertiesQuery.isLoading}
		emptyMessage="No properties found."
		onRowClick={(p) => goto(`/properties/${p.id}`)}
		getRowKey={(p) => p.id}
		getRowTestId={() => 'property-row'}
		data-testid="properties-list"
	>
		{#snippet toolbar()}
			<div class="flex flex-1 flex-wrap items-center gap-2">
				<SearchInput bind:value={search} placeholder="Search properties…" testid="property-search" />
				<Select.Root type="single" bind:value={typeFilter}>
					<Select.Trigger class="h-9 w-40 text-sm" data-testid="property-type-filter">
						{typeFilter || 'All types'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="" label="All types">All types</Select.Item>
						{#each propertyTypes as pt}
							<Select.Item value={pt} label={pt}>{pt}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
				<Select.Root type="single" bind:value={statusFilter}>
					<Select.Trigger class="h-9 w-44 text-sm" data-testid="property-status-filter">
						{statusFilter || 'All statuses'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="" label="All statuses">All statuses</Select.Item>
						{#each propertyStatuses as ps}
							<Select.Item value={ps} label={ps}>{ps}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
			</div>
			<Button data-testid="property-create-button" class="gap-2 shrink-0" onclick={openCreate}>
				<Plus class="h-4 w-4" />
				New Property
			</Button>
		{/snippet}
	</DataGrid>
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
