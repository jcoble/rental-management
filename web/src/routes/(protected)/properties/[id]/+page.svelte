<script lang="ts">
	import { page } from '$app/state';
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
	import RecordHistory from '$lib/components/shared/RecordHistory.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import HeroCard, { type HeroTone } from '$lib/components/shared/HeroCard.svelte';
	import StateSelect from '$lib/components/shared/StateSelect.svelte';
	import AddressAutocomplete from '$lib/components/shared/AddressAutocomplete.svelte';
	import * as Card from '$lib/components/ui/card';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { AlertCircle, Building2, Pencil, Plus, Save, Trash2, X, MapPin, Info } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const id = $derived(Number(page.params.id));

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

	// Hero occupancy + context tone: full occupancy reads green, vacancy is neutral.
	const occupiedUnits = $derived(property?.occupiedUnits ?? 0);
	const totalUnits = $derived(property?.unitCount ?? 0);
	const heroTone = $derived.by<HeroTone>(() => {
		if (property?.status === 'Inactive') return 'muted';
		if (property?.status === 'UnderMaintenance') return 'warning';
		if (totalUnits > 0 && occupiedUnits >= totalUnits) return 'success';
		return 'primary';
	});

	// ── Inline property edit ──────────────────────────────────────────────────
	const emptyProperty = { name: '', type: 'MultiFamily', addressLine1: '', addressLine2: '', city: '', state: '', postalCode: '', ownerEntityId: '' };
	let editingProperty = $state(false);
	let propertyForm = $state({ ...emptyProperty });
	let propertyFormErrors = $state<Record<string, string>>({});
	let showDeletePropertyConfirm = $state(false);

	const propertyTypes = ['SingleFamily', 'MultiFamily', 'Condo', 'Townhome', 'Commercial', 'MixedUse'];
	const propertyTypeOptions = $derived(propertyTypes.map((value) => ({ value, label: value })));
	const ownerOptions = $derived([
		{ value: '', label: 'No owner assigned' },
		...(ownersQuery.data ?? []).map((o) => ({ value: String(o.id), label: o.name })),
	]);

	function startEditingProperty() {
		if (!property) return;
		propertyForm = {
			name: property.name,
			type: property.type ?? 'MultiFamily',
			addressLine1: property.addressLine1,
			addressLine2: property.addressLine2 ?? '',
			city: property.city,
			state: property.state,
			postalCode: property.postalCode,
			ownerEntityId: property.ownerEntityId != null ? String(property.ownerEntityId) : '',
		};
		propertyFormErrors = {};
		editingProperty = true;
	}

	function cancelEditingProperty() {
		editingProperty = false;
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
			editingProperty = false;
			propertyFormErrors = {};
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

<!--
  Mirrors InlineField's display/edit wrapper (same testid family: -field/-value/
  -error) but lets us drop in a custom editing control (StateSelect /
  AddressAutocomplete) instead of a plain <input>. The `control` child renders
  only when editing.
-->
{#snippet inlineFieldWrap(
	testid: string,
	label: string,
	display: string,
	error: string | undefined,
	control: import('svelte').Snippet
)}
	<div data-testid={`${testid}-field`}>
		<label class="mb-1 block text-xs font-medium text-muted-foreground" for={`${testid}-input`}>{label}</label>
		{#if editingProperty}
			{@render control()}
			{#if error}
				<p class="mt-1 text-xs text-destructive" data-testid={`${testid}-error`}>{error}</p>
			{/if}
		{:else}
			<p
				class="min-h-10 rounded-md py-2 text-sm font-medium text-foreground"
				data-testid={`${testid}-value`}
			>
				{display === '' ? '-' : display}
			</p>
		{/if}
	</div>
{/snippet}

<!-- Editing controls for the Address card (hoisted to top level so they are not
     mistaken for slot props when referenced inside <DetailCard>). -->
{#snippet addressControl()}
	<AddressAutocomplete
		id="property-detail-address-input"
		testid="property-detail-address-input"
		bind:value={propertyForm.addressLine1}
		placeholder="Address"
		onresolved={(a) => {
			if (a.city) propertyForm.city = a.city;
			if (a.state) propertyForm.state = a.state;
			if (a.zip) propertyForm.postalCode = a.zip;
		}}
	/>
{/snippet}

{#snippet stateControl()}
	<StateSelect id="property-detail-state-input" testid="property-detail-state-input" bind:value={propertyForm.state} placeholder="State" />
{/snippet}

<svelte:head>
	<title>{property?.name ?? 'Property'} - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="property-detail-page">
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
				{#if editingProperty}
					<Button variant="outline" class="gap-2" onclick={cancelEditingProperty} disabled={savePropertyMutation.isPending} data-testid="property-detail-cancel">
						<X class="h-4 w-4" />
						Cancel
					</Button>
					<Button class="gap-2" onclick={submitProperty} disabled={savePropertyMutation.isPending} data-testid="property-detail-save">
						<Save class="h-4 w-4" />
						{savePropertyMutation.isPending ? 'Saving…' : 'Save'}
					</Button>
				{:else}
					<Button variant="outline" class="gap-2" onclick={startEditingProperty} data-testid="property-detail-edit">
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
				{/if}
			</div>
		</div>

		<!-- Hero: the property + its occupancy at a glance, washed by occupancy/status. -->
		<HeroCard tone={heroTone} testid="property-hero" contentClass="flex flex-wrap items-end justify-between gap-6" class="mb-6">
			<div class="min-w-0">
				<p class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Occupancy</p>
				<p class="mt-1 font-mono text-4xl font-bold tabular-nums tracking-tight" data-testid="property-hero-occupancy">
					{occupiedUnits}<span class="text-2xl text-muted-foreground">/{totalUnits}</span>
				</p>
				<div class="mt-3 flex flex-wrap items-center gap-x-2 gap-y-1 text-sm text-muted-foreground">
					<StatusBadge status={property.status} />
					<span aria-hidden="true">·</span>
					<span>{totalUnits === 1 ? '1 unit' : `${totalUnits} units`}{#if totalUnits > 0} · {Math.round((occupiedUnits / totalUnits) * 100)}% occupied{/if}</span>
				</div>
			</div>
		</HeroCard>

		<!-- Grouped detail cards -->
		<div class="mb-6 grid gap-6 lg:grid-cols-2">
			<DetailCard title="Identity" icon={Building2} accent="primary" testid="property-detail-card" contentClass="grid gap-x-6 gap-y-4 sm:grid-cols-2">
				<InlineField label="Name" bind:value={propertyForm.name} display={property.name} editing={editingProperty} error={propertyFormErrors.name} testid="property-detail-name-field" />
				<InlineField label="Type" bind:value={propertyForm.type} display={property.type} editing={editingProperty} type="select" options={propertyTypeOptions} error={propertyFormErrors.type} testid="property-detail-type" />
				<InlineField label="Owner" bind:value={propertyForm.ownerEntityId} display={property.ownerName ?? 'No owner assigned'} editing={editingProperty} type="select" options={ownerOptions} testid="property-detail-owner" class="sm:col-span-2" />
			</DetailCard>

			<DetailCard title="Address" icon={MapPin} accent="muted" testid="property-detail-address-card" contentClass="grid gap-x-6 gap-y-4 sm:grid-cols-2">
				<div class="sm:col-span-2">
					{@render inlineFieldWrap('property-detail-address', 'Address', `${property.addressLine1}${property.addressLine2 ? `, ${property.addressLine2}` : ''}`, propertyFormErrors.addressLine1, addressControl)}
				</div>
				<InlineField label="Apt / Suite / Unit #" bind:value={propertyForm.addressLine2} display={property.addressLine2 ?? '—'} editing={editingProperty} error={propertyFormErrors.addressLine2} testid="property-detail-address2" />
				<InlineField label="City" bind:value={propertyForm.city} display={property.city} editing={editingProperty} error={propertyFormErrors.city} testid="property-detail-city" />
				{@render inlineFieldWrap('property-detail-state', 'State', property.state ?? '', propertyFormErrors.state, stateControl)}
				<InlineField label="ZIP" bind:value={propertyForm.postalCode} display={property.postalCode} editing={editingProperty} error={propertyFormErrors.postalCode} testid="property-detail-zip" />
			</DetailCard>

			<DetailCard title="Details" icon={Info} accent="muted" testid="property-detail-meta-card" class="lg:col-span-2" contentClass="grid gap-x-6 gap-y-4 sm:grid-cols-2 lg:grid-cols-3">
				{#if !editingProperty && property.yearBuilt}
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Year Built</dt>
						<dd class="mt-1 text-sm text-foreground">{property.yearBuilt}</dd>
					</div>
				{/if}
				{#if !editingProperty && property.managementFeePercent != null}
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Management Fee</dt>
						<dd class="mt-1 text-sm text-foreground">{property.managementFeePercent}%</dd>
					</div>
				{/if}
				<div>
					<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Units</dt>
					<dd class="mt-1 text-sm font-semibold tabular-nums text-foreground">
						{property.unitCount ?? 0} total · {property.occupiedUnits ?? 0} occupied
					</dd>
				</div>
				{#if !editingProperty && property.notes}
					<div class="sm:col-span-2 lg:col-span-3">
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Notes</dt>
						<dd class="mt-1 text-sm text-foreground">{property.notes}</dd>
					</div>
				{/if}
			</DetailCard>
		</div>

		<!-- Units section -->
		<div class="mb-6" data-testid="property-detail-units">
			<h2 class="mb-3 text-lg font-semibold">Units</h2>
			<DataGrid
				data={unitsList}
				columns={unitColumns}
				loading={unitsQuery.isLoading}
				emptyMessage="No units on this property yet."
				getRowKey={(u) => u.id}
				onRowClick={(u) => openEditUnit(u)}
				pageSize={20}
				data-testid="property-units-grid"
			>
				{#snippet toolbar()}
					<div class="flex flex-1"></div>
					<Button class="gap-2 shrink-0" onclick={openAddUnit} data-testid="unit-add-button" data-coach="add-unit">
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

		<!-- Per-record audit history -->
		<div class="mt-6 rounded-lg border border-border bg-card p-4" data-testid="property-history-section">
			<h2 class="mb-1 text-base font-semibold">History</h2>
			<p class="mb-3 text-sm text-muted-foreground">Every recorded change to this property — who, what, and when.</p>
			<RecordHistory entityType="Property" entityId={id} />
		</div>
	{/if}
</div>

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
