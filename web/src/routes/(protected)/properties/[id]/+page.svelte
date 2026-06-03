<script lang="ts">
	import { page } from '$app/stores';
	import { goto } from '$app/navigation';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { properties } from '$lib/api/endpoints/properties';
	import { owners } from '$lib/api/endpoints/owners';
	import { leases } from '$lib/api/endpoints/leases';
	import type { Lease, Property, Unit } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { propertySchema, unitSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import * as Card from '$lib/components/ui/card';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { AlertCircle, Building2, Pencil, Plus, Trash2 } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const id = $derived(Number($page.params.id));

	// ── Queries ────────────────────────────────────────────────────────────────
	const propertyQuery = createQuery(() => ({
		queryKey: ['property', id],
		queryFn: () => properties.get(id),
		enabled: !isNaN(id) && id > 0,
	}));

	const unitsQuery = createQuery(() => ({
		queryKey: ['units', id],
		queryFn: () => properties.listUnits(id),
		enabled: !isNaN(id) && id > 0,
	}));

	const leasesQuery = createQuery(() => ({
		queryKey: ['leases', portfolioId, { propertyId: id }],
		queryFn: () => leases.list(portfolioId, { propertyId: id, take: 100 }),
		enabled: !isNaN(id) && id > 0 && portfolioId > 0,
	}));

	const ownersQuery = createQuery(() => ({
		queryKey: ['owners', portfolioId],
		queryFn: () => owners.list(portfolioId, { take: 200 }),
		enabled: portfolioId > 0,
	}));

	const property = $derived(propertyQuery.data);
	const unitsList = $derived(unitsQuery.data ?? []);
	const leasesList = $derived(leasesQuery.data ?? []);

	// ── Edit property dialog ──────────────────────────────────────────────────
	const emptyProperty = { name: '', type: 'MultiFamily', addressLine1: '', city: '', state: '', postalCode: '', ownerId: '' };
	let showPropertyForm = $state(false);
	let propertyForm = $state({ ...emptyProperty });
	let propertyFormErrors = $state<Record<string, string>>({});
	let showDeletePropertyConfirm = $state(false);

	const propertyTypes = ['SingleFamily', 'MultiFamily', 'Condo', 'Townhome', 'Commercial', 'MixedUse'];

	function openEditProperty() {
		if (!property) return;
		propertyForm = {
			name: property.name,
			type: property.type ?? 'MultiFamily',
			addressLine1: property.addressLine1,
			city: property.city,
			state: property.state,
			postalCode: property.postalCode,
			ownerId: property.ownerId != null ? String(property.ownerId) : '',
		};
		propertyFormErrors = {};
		showPropertyForm = true;
	}

	function closePropertyForm() {
		showPropertyForm = false;
		propertyFormErrors = {};
	}

	function submitProperty() {
		const result = parseForm(propertySchema, propertyForm);
		if (result.errors) {
			propertyFormErrors = result.errors;
			return;
		}
		propertyFormErrors = {};
		savePropertyMutation.mutate({ id, data: { portfolioId, ...result.data } });
	}

	const savePropertyMutation = createMutation(() => ({
		mutationFn: ({ id: pid, data }: { id: number; data: Record<string, unknown> }) =>
			properties.update(pid, data),
		onSuccess: () => {
			showSuccess('Property updated.');
			closePropertyForm();
			queryClient.invalidateQueries({ queryKey: ['property', id] });
			queryClient.invalidateQueries({ queryKey: ['properties', portfolioId] });
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const deletePropertyMutation = createMutation(() => ({
		mutationFn: (pid: number) => properties.delete(pid),
		onSuccess: () => {
			showSuccess('Property deleted.');
			queryClient.invalidateQueries({ queryKey: ['properties', portfolioId] });
			goto('/properties');
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	// ── Unit form ──────────────────────────────────────────────────────────────
	const emptyUnit = { unitNumber: '', bedrooms: '1', bathrooms: '1', marketRent: '1200' };
	let showUnitForm = $state(false);
	let editingUnitId = $state<number | null>(null);
	let unitForm = $state({ ...emptyUnit });
	let unitFormErrors = $state<Record<string, string>>({});

	function openAddUnit() {
		editingUnitId = null;
		unitForm = { ...emptyUnit };
		unitFormErrors = {};
		showUnitForm = true;
	}

	function openEditUnit(u: Unit) {
		editingUnitId = u.id;
		unitForm = {
			unitNumber: u.unitNumber,
			bedrooms: String(u.bedrooms),
			bathrooms: String(u.bathrooms),
			marketRent: String(u.marketRent),
		};
		unitFormErrors = {};
		showUnitForm = true;
	}

	function closeUnitForm() {
		showUnitForm = false;
		editingUnitId = null;
		unitFormErrors = {};
	}

	function submitUnit() {
		const result = parseForm(unitSchema, unitForm);
		if (result.errors) {
			unitFormErrors = result.errors;
			return;
		}
		unitFormErrors = {};
		if (editingUnitId != null) {
			updateUnitMutation.mutate({ unitId: editingUnitId, data: result.data });
		} else {
			createUnitMutation.mutate({ propertyId: id, data: result.data });
		}
	}

	function invalidateUnits() {
		queryClient.invalidateQueries({ queryKey: ['units', id] });
		queryClient.invalidateQueries({ queryKey: ['properties', portfolioId] });
	}

	const createUnitMutation = createMutation(() => ({
		mutationFn: ({ propertyId: pid, data }: { propertyId: number; data: Record<string, unknown> }) =>
			properties.createUnit(pid, data),
		onSuccess: () => {
			showSuccess('Unit added.');
			closeUnitForm();
			invalidateUnits();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const updateUnitMutation = createMutation(() => ({
		mutationFn: ({ unitId, data }: { unitId: number; data: Record<string, unknown> }) =>
			properties.updateUnit(unitId, data),
		onSuccess: () => {
			showSuccess('Unit updated.');
			closeUnitForm();
			invalidateUnits();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	let deleteUnitTarget = $state<Unit | null>(null);

	const deleteUnitMutation = createMutation(() => ({
		mutationFn: (unitId: number) => properties.deleteUnit(unitId),
		onSuccess: () => {
			showSuccess('Unit removed.');
			deleteUnitTarget = null;
			invalidateUnits();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	// ── Unit columns ───────────────────────────────────────────────────────────
	const unitColumns: ColumnDef<Unit>[] = [
		{
			key: 'unitNumber',
			title: 'Unit #',
			sortable: true,
			mobileRole: 'title',
		},
		{
			key: 'bedrooms',
			title: 'Beds',
			format: 'number',
			sortable: true,
			mobileRole: 'meta',
		},
		{
			key: 'bathrooms',
			title: 'Baths',
			format: 'number',
			sortable: true,
			mobileRole: 'meta',
		},
		{
			key: 'marketRent',
			title: 'Market Rent',
			format: 'currency',
			sortable: true,
			mobileRole: 'metric',
		},
		{
			key: 'status',
			title: 'Status',
			mobileRole: 'badge',
			cell: unitStatusCell,
		},
		{
			key: 'actions',
			title: '',
			mobileRole: 'hidden',
			align: 'right',
			width: '6rem',
			cell: unitActionsCell,
		},
	];

	// ── Lease columns ──────────────────────────────────────────────────────────
	const leaseColumns: ColumnDef<Lease>[] = [
		{
			key: 'leaseNumber',
			title: 'Lease #',
			sortable: true,
			mobileRole: 'title',
		},
		{
			key: 'tenantName',
			title: 'Tenant',
			sortable: true,
			mobileRole: 'subtitle',
			accessor: (l) => l.tenantName ?? '–',
		},
		{
			key: 'monthlyRent',
			title: 'Rent',
			format: 'currency',
			sortable: true,
			mobileRole: 'metric',
		},
		{
			key: 'startDate',
			title: 'Start',
			format: 'date',
			sortable: true,
			mobileRole: 'meta',
		},
		{
			key: 'endDate',
			title: 'End',
			format: 'date',
			sortable: true,
			mobileRole: 'meta',
		},
		{
			key: 'status',
			title: 'Status',
			mobileRole: 'badge',
			cell: leaseStatusCell,
		},
	];
</script>

{#snippet unitStatusCell(u: Unit)}
	<StatusBadge status={u.status} />
{/snippet}

{#snippet unitActionsCell(u: Unit)}
	<div class="flex items-center justify-end gap-1" onclick={(e) => e.stopPropagation()} role="none">
		<button
			type="button"
			data-testid="unit-edit"
			class="inline-flex h-7 w-7 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground"
			aria-label="Edit unit"
			onclick={(e) => { e.stopPropagation(); openEditUnit(u); }}
		>
			<Pencil class="h-3.5 w-3.5" />
		</button>
		<button
			type="button"
			data-testid="unit-delete"
			class="inline-flex h-7 w-7 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-destructive/10 hover:text-destructive"
			aria-label="Delete unit"
			onclick={(e) => { e.stopPropagation(); deleteUnitTarget = u; }}
		>
			<Trash2 class="h-3.5 w-3.5" />
		</button>
	</div>
{/snippet}

{#snippet leaseStatusCell(l: Lease)}
	<StatusBadge status={l.status} />
{/snippet}

<svelte:head>
	<title>{property?.name ?? 'Property'} - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6" data-testid="property-detail-page">
	<!-- Breadcrumb -->
	<div class="mb-4">
		<PageBreadcrumb
			crumbs={[
				{ label: 'Properties', href: '/properties' },
				{ label: property?.name ?? '…' },
			]}
		/>
	</div>

	<!-- Loading -->
	{#if propertyQuery.isLoading}
		<div class="flex items-center justify-center py-20 text-muted-foreground" data-testid="property-detail-loading">
			<div class="flex flex-col items-center gap-2">
				<div class="h-6 w-6 animate-spin rounded-full border-2 border-current border-t-transparent"></div>
				<span class="text-sm">Loading…</span>
			</div>
		</div>

	<!-- Error -->
	{:else if propertyQuery.isError}
		<div class="rounded-lg border border-destructive/30 bg-destructive/10 p-6 text-center" data-testid="property-detail-error">
			<AlertCircle class="mx-auto mb-2 h-8 w-8 text-destructive" />
			<p class="font-medium text-destructive">Could not load property</p>
			<p class="mt-1 text-sm text-muted-foreground">{apiErrorMessage(propertyQuery.error)}</p>
			<Button variant="outline" class="mt-4" onclick={() => propertyQuery.refetch()}>Retry</Button>
		</div>

	<!-- Not found -->
	{:else if !property}
		<div class="rounded-lg border border-border p-6 text-center" data-testid="property-detail-not-found">
			<Building2 class="mx-auto mb-2 h-8 w-8 text-muted-foreground" />
			<p class="font-medium">Property not found</p>
			<Button variant="outline" class="mt-4" onclick={() => goto('/properties')}>Back to Properties</Button>
		</div>

	{:else}
		<!-- Header -->
		<div class="mb-6 flex flex-wrap items-start justify-between gap-3">
			<div>
				<h1 class="text-2xl font-bold" data-testid="property-detail-name">{property.name}</h1>
				<p class="mt-1 text-sm text-muted-foreground">
					{property.addressLine1}{property.addressLine2 ? `, ${property.addressLine2}` : ''}, {property.city}, {property.state} {property.postalCode}
				</p>
				<div class="mt-2">
					<StatusBadge status={property.status} />
				</div>
			</div>
			<div class="flex items-center gap-2">
				<Button variant="outline" class="gap-2" onclick={openEditProperty} data-testid="property-detail-edit">
					<Pencil class="h-4 w-4" />
					Edit
				</Button>
				<Button
					variant="destructive"
					class="gap-2"
					onclick={() => (showDeletePropertyConfirm = true)}
					data-testid="property-detail-delete"
				>
					<Trash2 class="h-4 w-4" />
					Delete
				</Button>
			</div>
		</div>

		<!-- Info card -->
		<Card.Root class="mb-6" data-testid="property-detail-card">
			<Card.Header>
				<Card.Title>Property Details</Card.Title>
			</Card.Header>
			<Card.Content>
				<dl class="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Type</dt>
						<dd class="mt-1 text-sm text-foreground">{property.type}</dd>
					</div>
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Address</dt>
						<dd class="mt-1 text-sm text-foreground">
							{property.addressLine1}{property.addressLine2 ? `, ${property.addressLine2}` : ''}
						</dd>
					</div>
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">City / State / ZIP</dt>
						<dd class="mt-1 text-sm text-foreground">{property.city}, {property.state} {property.postalCode}</dd>
					</div>
					{#if property.yearBuilt}
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Year Built</dt>
							<dd class="mt-1 text-sm text-foreground">{property.yearBuilt}</dd>
						</div>
					{/if}
					{#if property.managementFeePercent != null}
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Management Fee</dt>
							<dd class="mt-1 text-sm text-foreground">{property.managementFeePercent}%</dd>
						</div>
					{/if}
					{#if property.ownerName}
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Owner</dt>
							<dd class="mt-1 text-sm text-foreground">{property.ownerName}</dd>
						</div>
					{/if}
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Units</dt>
						<dd class="mt-1 text-sm font-semibold tabular-nums text-foreground">
							{property.unitCount ?? 0} total · {property.occupiedUnits ?? 0} occupied
						</dd>
					</div>
					{#if property.notes}
						<div class="sm:col-span-2 lg:col-span-3">
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Notes</dt>
							<dd class="mt-1 text-sm text-foreground">{property.notes}</dd>
						</div>
					{/if}
				</dl>
			</Card.Content>
		</Card.Root>

		<!-- Units section -->
		<div class="mb-6" data-testid="property-detail-units">
			<h2 class="mb-3 text-lg font-semibold">Units</h2>
			<DataGrid
				data={unitsList}
				columns={unitColumns}
				loading={unitsQuery.isLoading}
				emptyMessage="No units on this property yet."
				getRowKey={(u) => u.id}
				pageSize={20}
				data-testid="property-units-grid"
			>
				{#snippet toolbar()}
					<div class="flex flex-1"></div>
					<Button class="gap-2 shrink-0" onclick={openAddUnit} data-testid="unit-add-button">
						<Plus class="h-4 w-4" />
						Add Unit
					</Button>
				{/snippet}
			</DataGrid>
		</div>

		<!-- Leases section -->
		<div data-testid="property-detail-leases">
			<h2 class="mb-3 text-lg font-semibold">Leases</h2>
			<DataGrid
				data={leasesList}
				columns={leaseColumns}
				loading={leasesQuery.isLoading}
				emptyMessage="No leases for this property."
				onRowClick={(l) => goto(`/leases/${l.id}`)}
				getRowKey={(l) => l.id}
				pageSize={10}
				data-testid="property-leases-grid"
			/>
		</div>
	{/if}
</div>

<!-- Edit Property dialog -->
<Dialog.Root open={showPropertyForm} onOpenChange={(v) => { if (!v) closePropertyForm(); }}>
	<Dialog.Content class="max-w-2xl">
		<Dialog.Header>
			<Dialog.Title>Edit Property</Dialog.Title>
		</Dialog.Header>
		<div class="grid gap-3 md:grid-cols-2" data-testid="property-form">
			<div class="md:col-span-2">
				<Input data-testid="property-name-input" bind:value={propertyForm.name} placeholder="Property name" />
				{#if propertyFormErrors.name}<p class="mt-1 text-xs text-destructive" data-testid="property-name-error">{propertyFormErrors.name}</p>{/if}
			</div>
			<Select.Root type="single" bind:value={propertyForm.type}>
				<Select.Trigger class="w-full" data-testid="property-type-input">
					{propertyForm.type ? propertyForm.type : 'Select type'}
				</Select.Trigger>
				<Select.Content>
					{#each propertyTypes as pt}
						<Select.Item value={pt} label={pt}>{pt}</Select.Item>
					{/each}
				</Select.Content>
			</Select.Root>
			<Select.Root type="single" bind:value={propertyForm.ownerId}>
				<Select.Trigger class="w-full" data-testid="property-owner-input">
					{propertyForm.ownerId ? ((ownersQuery.data || []).find(o => String(o.id) === propertyForm.ownerId)?.name ?? 'No owner assigned') : 'No owner assigned'}
				</Select.Trigger>
				<Select.Content>
					<Select.Item value="" label="No owner assigned">No owner assigned</Select.Item>
					{#each ownersQuery.data || [] as owner}
						<Select.Item value={String(owner.id)} label={owner.name}>{owner.name}</Select.Item>
					{/each}
				</Select.Content>
			</Select.Root>
			<div class="md:col-span-2">
				<Input data-testid="property-address-input" bind:value={propertyForm.addressLine1} placeholder="Address" />
				{#if propertyFormErrors.addressLine1}<p class="mt-1 text-xs text-destructive" data-testid="property-address-error">{propertyFormErrors.addressLine1}</p>{/if}
			</div>
			<div>
				<Input data-testid="property-city-input" bind:value={propertyForm.city} placeholder="City" />
				{#if propertyFormErrors.city}<p class="mt-1 text-xs text-destructive" data-testid="property-city-error">{propertyFormErrors.city}</p>{/if}
			</div>
			<div class="grid grid-cols-2 gap-2">
				<div>
					<Input data-testid="property-state-input" bind:value={propertyForm.state} placeholder="State" />
					{#if propertyFormErrors.state}<p class="mt-1 text-xs text-destructive" data-testid="property-state-error">{propertyFormErrors.state}</p>{/if}
				</div>
				<div>
					<Input data-testid="property-zip-input" bind:value={propertyForm.postalCode} placeholder="ZIP" />
					{#if propertyFormErrors.postalCode}<p class="mt-1 text-xs text-destructive" data-testid="property-zip-error">{propertyFormErrors.postalCode}</p>{/if}
				</div>
			</div>
		</div>
		<div class="mt-4 flex justify-end gap-2">
			<Button data-testid="property-form-cancel" variant="outline" onclick={closePropertyForm}>Cancel</Button>
			<Button data-testid="property-form-save" onclick={submitProperty} disabled={savePropertyMutation.isPending}>
				{savePropertyMutation.isPending ? 'Saving…' : 'Save Property'}
			</Button>
		</div>
	</Dialog.Content>
</Dialog.Root>

<!-- Unit add/edit dialog -->
<Dialog.Root open={showUnitForm} onOpenChange={(v) => { if (!v) closeUnitForm(); }}>
	<Dialog.Content class="max-w-sm">
		<Dialog.Header>
			<Dialog.Title>{editingUnitId == null ? 'Add Unit' : 'Edit Unit'}</Dialog.Title>
		</Dialog.Header>
		<div class="grid gap-3" data-testid="unit-form">
			<div>
				<Input data-testid="unit-number-input" bind:value={unitForm.unitNumber} placeholder="Unit number" />
				{#if unitFormErrors.unitNumber}<p class="mt-1 text-xs text-destructive" data-testid="unit-number-error">{unitFormErrors.unitNumber}</p>{/if}
			</div>
			<div class="grid grid-cols-3 gap-2">
				<div>
					<Input data-testid="unit-bedrooms-input" bind:value={unitForm.bedrooms} placeholder="Beds" />
					{#if unitFormErrors.bedrooms}<p class="mt-1 text-xs text-destructive">{unitFormErrors.bedrooms}</p>{/if}
				</div>
				<div>
					<Input data-testid="unit-bathrooms-input" bind:value={unitForm.bathrooms} placeholder="Baths" />
					{#if unitFormErrors.bathrooms}<p class="mt-1 text-xs text-destructive">{unitFormErrors.bathrooms}</p>{/if}
				</div>
				<div>
					<Input data-testid="unit-rent-input" bind:value={unitForm.marketRent} placeholder="Rent" />
					{#if unitFormErrors.marketRent}<p class="mt-1 text-xs text-destructive">{unitFormErrors.marketRent}</p>{/if}
				</div>
			</div>
		</div>
		<div class="mt-4 flex justify-end gap-2">
			<Button variant="outline" onclick={closeUnitForm}>Cancel</Button>
			<Button
				data-testid="unit-save-button"
				onclick={submitUnit}
				disabled={createUnitMutation.isPending || updateUnitMutation.isPending}
			>
				{(createUnitMutation.isPending || updateUnitMutation.isPending) ? 'Saving…' : editingUnitId == null ? 'Add Unit' : 'Save Unit'}
			</Button>
		</div>
	</Dialog.Content>
</Dialog.Root>

<!-- Delete property confirm -->
<ConfirmDialog
	open={showDeletePropertyConfirm}
	title="Delete property"
	message={property ? `Delete "${property.name}"? This also removes its units.` : ''}
	busy={deletePropertyMutation.isPending}
	testid="property-detail-delete-confirm"
	onconfirm={() => property && deletePropertyMutation.mutate(property.id)}
	oncancel={() => (showDeletePropertyConfirm = false)}
/>

<!-- Delete unit confirm -->
<ConfirmDialog
	open={deleteUnitTarget !== null}
	title="Remove unit"
	message={deleteUnitTarget ? `Remove unit "${deleteUnitTarget.unitNumber}"?` : ''}
	busy={deleteUnitMutation.isPending}
	testid="unit-delete-confirm"
	onconfirm={() => deleteUnitTarget && deleteUnitMutation.mutate(deleteUnitTarget.id)}
	oncancel={() => (deleteUnitTarget = null)}
/>
