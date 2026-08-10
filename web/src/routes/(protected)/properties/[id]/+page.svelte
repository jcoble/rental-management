<script lang="ts">
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import { tick } from 'svelte';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { properties } from '$lib/api/endpoints/properties';
	import { owners } from '$lib/api/endpoints/owners';
	import { leaseManagements } from '$lib/api/endpoints/lease-managements';
	import { workOrders } from '$lib/api/endpoints/workOrders';
	import { expenses } from '$lib/api/endpoints/expenses';
	import type { Expense, LeaseManagementSummary, Property, Unit, UnitHealth, WorkOrder } from '$lib/types';
	import { recordHref } from '$lib/navigation/record-href';
	import { getPropertyDeleteState } from '$lib/properties/property-delete-state';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { propertySchema, propertyBasisSchema, propertyOperationsSchema, unitSchema, parseForm } from '$lib/schemas';
	import PropertyCapitalAssetsSection from '$lib/components/property/PropertyCapitalAssetsSection.svelte';
	import PropertyDispositionsSection from '$lib/components/property/PropertyDispositionsSection.svelte';
	import PropertyLoansSection from '$lib/components/property/PropertyLoansSection.svelte';
	import PropertyRecurringExpensesSection from '$lib/components/property/PropertyRecurringExpensesSection.svelte';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import RecordHistory from '$lib/components/shared/RecordHistory.svelte';
	import DocumentsPanel from '$lib/components/shared/DocumentsPanel.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import RemoteRecordSelect from '$lib/components/shared/RemoteRecordSelect.svelte';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import HeroCard, { type HeroTone } from '$lib/components/shared/HeroCard.svelte';
	import StateSelect from '$lib/components/shared/StateSelect.svelte';
	import AddressAutocomplete from '$lib/components/shared/AddressAutocomplete.svelte';
	import UnitFields from '$lib/components/forms/UnitFields.svelte';
	import {
		formatPropertyStatus,
		formatPropertyType,
		formatRentalStructure,
		propertyStatusOptions,
		propertyTypeOptions,
		rentalStructureOptions
	} from '$lib/properties/property-labels';
	import { propertyUpdateFields } from '$lib/properties/property-update-payload';
	import { propertyYearBuiltBusinessYearError } from '$lib/properties/property-year-built';
	import * as Card from '$lib/components/ui/card';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { AlertCircle, Building2, Pencil, Plus, Save, Trash2, X, MapPin, Info, ArrowRight } from '@lucide/svelte';
	import type { WorkspaceExperience } from '$lib/types/user';
	import { getAuthState } from '$lib/stores/auth.svelte';
	import { CAPABILITY } from '$lib/auth/experience-policy';
	import {
		propertyWorkspaceSections,
		readPropertyWorkspaceSection,
		type PropertyWorkspaceSection
	} from '$lib/components/property/property-workspace';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const id = $derived(Number(page.params.id));
	const authState = getAuthState();
	const currentAccess = $derived(page.data.access ?? authState.accessEnvelope ?? null);
	const activeExperience = $derived(authState.activeExperience ?? currentAccess?.selectedContext.activeExperience ?? null);
	const activeCapabilities = $derived<Set<string>>(new Set<string>(
		currentAccess?.navigation.find((entry: { experience: WorkspaceExperience; capabilityKeys: string[] }) =>
			entry.experience === activeExperience)?.capabilityKeys ?? []
	));
	const canManageRentals = $derived(
		activeExperience === 'Management' && activeCapabilities.has(CAPABILITY.rentalsManage)
	);
	const canManageMoneyExpenses = $derived(
		activeExperience === 'Management' && activeCapabilities.has(CAPABILITY.moneyExpensesManage)
	);
	const PAGE_SIZE = 20;
	const activeArea = $derived<PropertyWorkspaceSection>(
		readPropertyWorkspaceSection(page.url.searchParams.get('area'))
	);
	let rentalsPage = $state(1);
	let workPage = $state(1);
	let financePage = $state(1);
	let leasePage = $state(1);

	function openArea(area: PropertyWorkspaceSection) {
		void goto(`/properties/${id}?area=${area}`, { keepFocus: true, noScroll: true });
	}

	// ── Queries ────────────────────────────────────────────────────────────────
	const propertyQuery = createQuery(() => ({
		queryKey: ['property', id],
		queryFn: () => properties.get(id),
		enabled: !isNaN(id) && id > 0,
	}));

	const unitsQuery = createQuery(() => ({
		queryKey: ['property-workspace', id, 'rentals', rentalsPage, PAGE_SIZE],
		queryFn: () => properties.listWorkspaceUnitsPage(id, {
			skip: (rentalsPage - 1) * PAGE_SIZE,
			take: PAGE_SIZE,
			sort: 'unitNumber'
		}),
		enabled: !isNaN(id) && id > 0 && activeArea === 'rentals',
	}));

	const leasesQuery = createQuery(() => ({
		queryKey: ['property-workspace', id, 'leases', leasePage, PAGE_SIZE],
		queryFn: () => leaseManagements.listPage({
			propertyId: id,
			skip: (leasePage - 1) * PAGE_SIZE,
			take: PAGE_SIZE,
			sort: '-updatedAtUtc'
		}),
		enabled: !isNaN(id) && id > 0 && portfolioId > 0 && activeArea === 'rentals',
	}));

	const workOrdersQuery = createQuery(() => ({
		queryKey: ['property-workspace', id, 'work', workPage, PAGE_SIZE],
		queryFn: () => workOrders.listPage(portfolioId, {
			propertyId: id,
			skip: (workPage - 1) * PAGE_SIZE,
			take: PAGE_SIZE,
			sort: '-updatedAt'
		}),
		enabled: !isNaN(id) && id > 0 && portfolioId > 0 && activeArea === 'property-work',
	}));

	const businessDateQuery = createQuery(() => ({
		queryKey: ['portfolio-business-date', portfolioId],
		queryFn: () => leaseManagements.prepareMoveInContext(),
		enabled: canManageRentals && portfolioId > 0,
	}));

	const expensesQuery = createQuery(() => ({
		queryKey: ['property-workspace', id, 'finances', financePage, PAGE_SIZE],
		queryFn: () => expenses.listPage(portfolioId, {
			propertyId: id,
			skip: (financePage - 1) * PAGE_SIZE,
			take: PAGE_SIZE,
			sort: '-incurredAt'
		}),
		enabled: !isNaN(id) && id > 0 && portfolioId > 0 && activeArea === 'property-finances',
	}));

	async function loadOwnerOptions(params: { search?: string; skip: number; take: number }) {
		const result = await owners.listPage(portfolioId, { ...params, sort: 'name' });
		return {
			...result,
			items: result.items.map((owner) => ({
				id: owner.id,
				label: owner.name,
				description: owner.ownerEntityType
			}))
		};
	}

	const property = $derived(propertyQuery.data);
	const propertyDeleteState = $derived(property ? getPropertyDeleteState(property) : null);
	const unitsList = $derived(unitsQuery.data?.items ?? []);
	const leasesList = $derived(leasesQuery.data?.items ?? []);

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
	const emptyProperty = { name: '', type: 'MultiFamily', rentalStructure: '', status: 'Active', addressLine1: '', addressLine2: '', city: '', state: '', postalCode: '', ownerEntityId: '', yearBuilt: '', managementFeePercent: '', notes: '', purchasePrice: '', landValue: '', inServiceDate: '', manualAnnualDepreciation: '' };
	let editingProperty = $state(false);
	let propertyForm = $state({ ...emptyProperty });
	let propertyFormErrors = $state<Record<string, string>>({});
	let showDeletePropertyConfirm = $state(false);
	let selectedOwnerLabel = $state<string | null>(null);
	let initialOwnerEntityId = $state('');

	function fmtMoney(value: number): string {
		return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(value || 0);
	}
	function fmtDateOnly(value: string): string {
		const d = new Date(value);
		return isNaN(d.getTime()) ? '' : d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric', timeZone: 'UTC' });
	}
	function ownershipLabel(ownerships: Property['ownerships']): string {
		if (ownerships.length === 0) return 'No owner assigned';
		return ownerships
			.map((ownership) =>
				ownerships.length === 1 && ownership.ownershipSharePercent === 100
					? ownership.ownerName
					: `${ownership.ownerName} (${ownership.ownershipSharePercent}%)`
			)
			.join(', ');
	}

	// TSK-599 — field-level container transform. Wrap a view⇄edit state change in a View
	// Transition and tag <html> so each InlineField's read-only box morphs into its input
	// in place (see .rc-vt-fieldmorph in app.css), instead of the whole card flipping.
	// Falls back to an instant toggle where View Transitions are unsupported or the user
	// prefers reduced motion.
	function morphEdit(apply: () => void) {
		const startVT = (
			document as unknown as {
				startViewTransition?: (cb: () => Promise<void> | void) => { finished: Promise<unknown> };
			}
		).startViewTransition?.bind(document);
		const reduce = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
		if (!startVT || reduce) {
			apply();
			return;
		}
		document.documentElement.classList.add('rc-vt-fieldmorph');
		startVT(async () => {
			apply();
			await tick();
		}).finished.finally(() => document.documentElement.classList.remove('rc-vt-fieldmorph'));
	}

	function startEditingProperty() {
		if (!canManageRentals) return;
		if (!property) return;
		if (activeArea !== 'ownership-management') openArea('ownership-management');
		propertyForm = {
			name: property.name,
			type: property.type ?? 'MultiFamily',
			rentalStructure: property.rentalStructure,
			status: property.status ?? 'Active',
			addressLine1: property.addressLine1,
			addressLine2: property.addressLine2 ?? '',
			city: property.city,
			state: property.state,
			postalCode: property.postalCode,
			ownerEntityId: property.ownerships.length === 1 ? String(property.ownerships[0].ownerEntityId) : '',
			yearBuilt: property.yearBuilt != null ? String(property.yearBuilt) : '',
			managementFeePercent: property.managementFeePercent != null ? String(property.managementFeePercent) : '',
			notes: property.notes ?? '',
			purchasePrice: property.purchasePrice != null ? String(property.purchasePrice) : '',
			landValue: property.landValue != null ? String(property.landValue) : '',
			inServiceDate: property.inServiceDate ? property.inServiceDate.slice(0, 10) : '',
			manualAnnualDepreciation: property.manualAnnualDepreciation != null ? String(property.manualAnnualDepreciation) : '',
		};
		initialOwnerEntityId = propertyForm.ownerEntityId;
		selectedOwnerLabel = property.ownerships.length === 1 ? property.ownerships[0].ownerName : null;
		propertyFormErrors = {};
		morphEdit(() => {
			editingProperty = true;
		});
	}

	function cancelEditingProperty() {
		morphEdit(() => {
			editingProperty = false;
			propertyFormErrors = {};
		});
	}

	function submitProperty() {
		if (!canManageRentals) return;
		const result = parseForm(propertySchema, propertyForm);
		const operations = parseForm(propertyOperationsSchema, propertyForm);
		const basis = parseForm(propertyBasisSchema, propertyForm);
		if (result.errors || operations.errors || basis.errors) {
			propertyFormErrors = { ...(result.errors ?? {}), ...(operations.errors ?? {}), ...(basis.errors ?? {}) };
			return;
		}
		const yearBuiltError = propertyYearBuiltBusinessYearError(
			operations.data.yearBuilt,
			businessDateQuery.data?.businessDate
		);
		if (yearBuiltError) {
			propertyFormErrors = { yearBuilt: yearBuiltError };
			return;
		}
		propertyFormErrors = {};
		const mutableProperty = propertyUpdateFields(result.data);
		const ownerEntityId = result.data.ownerEntityId;
		const ownershipChange = String(ownerEntityId ?? '') === initialOwnerEntityId
			? {}
			: ownerEntityId == null
				? { ownerships: [], clearOwnership: true }
				: {
						ownerships: [{ ownerEntityId, ownershipSharePercent: 100 }],
						clearOwnership: false
					};
		savePropertyMutation.mutate({
			id,
			data: {
				portfolioId,
				...mutableProperty,
				...operations.data,
				...basis.data,
				...ownershipChange,
			},
		});
	}

	const savePropertyMutation = createMutation(() => ({
		mutationFn: ({ id: pid, data }: { id: number; data: Record<string, unknown> }) =>
			properties.update(pid, data),
		onSuccess: () => {
			showSuccess('Property updated.');
			morphEdit(() => {
				editingProperty = false;
			});
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
	const emptyUnit = { unitNumber: '', floorPlan: '', bedrooms: '1', bathrooms: '1', squareFeet: '', marketRent: '1200', notes: '' };
	let showUnitForm = $state(false);
	let editingUnitId = $state<number | null>(null);
	let unitForm = $state({ ...emptyUnit });
	let unitFormErrors = $state<Record<string, string>>({});

	function openAddUnit() {
		if (!canManageRentals) return;
		editingUnitId = null;
		unitForm = { ...emptyUnit };
		unitFormErrors = {};
		showUnitForm = true;
	}

	function openEditUnit(u: Unit) {
		if (!canManageRentals) return;
		editingUnitId = u.id;
		unitForm = {
			unitNumber: u.unitNumber,
			floorPlan: u.floorPlan ?? '',
			bedrooms: String(u.bedrooms),
			bathrooms: String(u.bathrooms),
			squareFeet: u.squareFeet == null ? '' : String(u.squareFeet),
			marketRent: String(u.marketRent),
			notes: u.notes ?? '',
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
		queryClient.invalidateQueries({ queryKey: ['property-workspace', id, 'rentals'] });
		queryClient.invalidateQueries({ queryKey: ['property', id] });
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

	let deleteUnitTarget = $state<UnitHealth | null>(null);

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
	const unitColumns: ColumnDef<UnitHealth>[] = [
		{
			key: 'actions',
			title: '',
			align: 'right',
			mobileRole: 'hidden',
			isAction: true,
			cell: unitActionsCell,
		},
		{
			key: 'unitNumber',
			title: 'Unit #',
			sortable: true,
			mobileRole: 'title',
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
			key: 'openWorkOrderCount',
			title: 'Open work',
			format: 'number',
			mobileRole: 'meta',
		},
	];

	const workOrderColumns: ColumnDef<WorkOrder>[] = [
		{ key: 'title', title: 'Work order', sortable: true, mobileRole: 'title' },
		{ key: 'unitNumber', title: 'Unit', mobileRole: 'subtitle', accessor: (row) => row.unitNumber ?? 'Property-wide' },
		{ key: 'priority', title: 'Priority', sortable: true, mobileRole: 'meta' },
		{ key: 'status', title: 'Status', sortable: true, mobileRole: 'badge' },
		{ key: 'updatedAt', title: 'Updated', sortable: true, format: 'date', mobileRole: 'meta' }
	];

	const expenseColumns: ColumnDef<Expense>[] = [
		{ key: 'description', title: 'Expense', sortable: true, mobileRole: 'title' },
		{ key: 'category', title: 'Category', sortable: true, mobileRole: 'subtitle' },
		{ key: 'amount', title: 'Amount', sortable: true, format: 'currency', mobileRole: 'metric' },
		{ key: 'status', title: 'Status', sortable: true, mobileRole: 'badge' },
		{ key: 'incurredAt', title: 'Incurred', sortable: true, format: 'date', mobileRole: 'meta' }
	];

	// ── Lease columns ──────────────────────────────────────────────────────────
	const leaseColumns: ColumnDef<LeaseManagementSummary>[] = [
		{
			key: 'agreementNumber',
			title: 'Agreement',
			accessor: (relationship) => relationship.agreementNumber ?? 'No lease in effect',
			sortable: true,
			mobileRole: 'title',
		},
		{
			key: 'primaryTenantName',
			title: 'Tenant',
			sortable: true,
			mobileRole: 'subtitle',
			accessor: (relationship) => relationship.primaryTenantName ?? '–',
		},
		{
			key: 'baseRentAmount',
			title: 'Rent',
			format: 'currency',
			sortable: true,
			mobileRole: 'metric',
		},
		{
			key: 'termStartOn',
			title: 'Start',
			format: 'date',
			sortable: true,
			mobileRole: 'meta',
		},
		{
			key: 'termEndOn',
			title: 'End',
			format: 'date',
			sortable: true,
			mobileRole: 'meta',
		},
		{
			key: 'lifecycle',
			title: 'Relationship',
			mobileRole: 'badge',
			cell: leaseStatusCell,
		},
	];
</script>

{#snippet unitStatusCell(u: UnitHealth)}
	<StatusBadge status={u.status} />
{/snippet}

{#snippet unitActionsCell(unit: UnitHealth)}
	{#if canManageRentals}
		<Button
			variant="ghost"
			size="icon"
			data-testid="unit-delete-{unit.id}"
			aria-label={`Remove unit ${unit.unitNumber}`}
			onclick={(event) => {
				event.stopPropagation();
				deleteUnitTarget = unit;
			}}
		>
			<Trash2 class="h-4 w-4 text-destructive" />
		</Button>
	{/if}
{/snippet}

{#snippet leaseStatusCell(relationship: LeaseManagementSummary)}
	<StatusBadge status={relationship.lifecycle} />
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
	control: import('svelte').Snippet,
	editing: boolean,
	morphName: string,
	readonlySurface: boolean = false
)}
	<div data-testid={`${testid}-field`} style:view-transition-name={morphName || null}>
		{#if editing}
			<label class="mb-1 block text-xs font-medium text-muted-foreground" for={`${testid}-input`}>{label}</label>
			{@render control()}
			{#if error}
				<p class="mt-1 text-xs text-destructive" data-testid={`${testid}-error`}>{error}</p>
			{/if}
		{:else if readonlySurface}
			<div class="m3-readonly-field flex flex-col justify-center" data-testid={`${testid}-value`}>
				<span class="m3-readonly-field__label">{label}</span>
				<span class="m3-readonly-field__value mt-1">{display === '' ? '-' : display}</span>
			</div>
		{:else}
			<label class="mb-1 block text-xs font-medium text-muted-foreground" for={`${testid}-input`}>{label}</label>
			<p
				class="min-h-10 rounded-md py-2 text-sm font-medium text-foreground"
				data-testid={`${testid}-value`}
			>
				{display === '' ? '-' : display}
			</p>
		{/if}
	</div>
{/snippet}

{#snippet propertyInServiceDateControl()}
	<DatePicker
		id="property-detail-in-service-date-input"
		testid="property-detail-in-service-date-input"
		bind:value={propertyForm.inServiceDate}
	/>
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
		<div class="rc-hero mb-6 flex flex-wrap items-start justify-between gap-3">
			<div>
				<h1 class="text-2xl font-bold" data-testid="property-detail-name">{property.name}</h1>
				<p class="mt-1 text-sm text-muted-foreground">
					{property.addressLine1}{property.addressLine2 ? `, ${property.addressLine2}` : ''}, {property.city}, {property.state} {property.postalCode}
				</p>
				<div class="mt-2">
					<StatusBadge status={property.status} />
				</div>
			</div>
			{#if canManageRentals}
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
			{/if}
		</div>

		<nav class="mb-6 overflow-x-auto border-b border-border" aria-label="Property workspace" data-testid="property-workspace-sections">
			<div class="flex min-w-max gap-1">
				{#each propertyWorkspaceSections as section}
					<button
						type="button"
						class="border-b-2 px-4 py-3 text-sm font-medium transition-colors {activeArea === section.id ? 'border-primary text-foreground' : 'border-transparent text-muted-foreground hover:text-foreground'}"
						aria-current={activeArea === section.id ? 'page' : undefined}
						data-testid={`property-area-${section.id}`}
						onclick={() => openArea(section.id)}
					>
						{section.label}
					</button>
				{/each}
			</div>
		</nav>

		{#if activeArea === 'summary'}
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
			<DetailCard
				title="Property summary"
				icon={Info}
				accent="muted"
				testid="property-detail-meta-card"
				contentClass="grid gap-x-6 gap-y-4 sm:grid-cols-2 lg:grid-cols-4"
			>
				<div><dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Rental count</dt><dd class="mt-1 text-sm font-semibold">{formatRentalStructure(property.rentalStructure)}</dd></div>
				<div><dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Owner</dt><dd class="mt-1 text-sm font-semibold">{ownershipLabel(property.ownerships)}</dd></div>
				<div><dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Year built</dt><dd class="mt-1 text-sm font-semibold">{property.yearBuilt ?? '—'}</dd></div>
				<div><dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Management fee</dt><dd class="mt-1 text-sm font-semibold">{property.managementFeePercent != null ? `${property.managementFeePercent}%` : '—'}</dd></div>
				<div class="sm:col-span-2"><dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Notes</dt><dd class="mt-1 text-sm font-semibold">{property.notes ?? '—'}</dd></div>
			</DetailCard>
		{:else if activeArea === 'ownership-management'}
		<div class="mb-6 grid gap-6 lg:grid-cols-2" data-testid="property-area-ownership-management-content">
			<!-- TSK-599: field-level container transform. Each InlineField carries a morphName,
			     so toggling Edit (via morphEdit → startViewTransition) morphs each read-only box
			     into its input in place; the card itself stays put. -->
			<DetailCard title="Identity" icon={Building2} accent="primary" testid="property-detail-card" contentClass="grid gap-x-6 gap-y-4 sm:grid-cols-2">
				<InlineField label="Name" bind:value={propertyForm.name} display={property.name} editing={editingProperty} error={propertyFormErrors.name} testid="property-detail-name-field" morphName="vt-prop-name" />
				<InlineField label="Type" bind:value={propertyForm.type} display={formatPropertyType(property.type)} editing={editingProperty} type="select" options={propertyTypeOptions} error={propertyFormErrors.type} testid="property-detail-type" morphName="vt-prop-type" />
				<InlineField label="Rental count" bind:value={propertyForm.rentalStructure} display={formatRentalStructure(property.rentalStructure)} editing={false} type="select" options={rentalStructureOptions} error={propertyFormErrors.rentalStructure} testid="property-detail-rental-structure" morphName="vt-prop-rental-structure" />
				<InlineField label="Status" bind:value={propertyForm.status} display={formatPropertyStatus(property.status)} editing={editingProperty} type="select" options={propertyStatusOptions} error={propertyFormErrors.status} testid="property-detail-status" morphName="vt-prop-status" />
				{#if editingProperty}
					<div class="sm:col-span-2">
						<RemoteRecordSelect
							queryKey={['property-detail-owner', portfolioId]}
							label="Owner"
							bind:value={propertyForm.ownerEntityId}
							selectedLabel={selectedOwnerLabel}
							placeholder="No owner assigned"
							clearLabel="No owner assigned"
							searchPlaceholder="Search owners…"
							emptyLabel="No matching owners"
							loadPage={loadOwnerOptions}
							onValueChange={(_value, option) => (selectedOwnerLabel = option?.label ?? null)}
							testid="property-detail-owner"
						/>
						{#if propertyFormErrors.ownerEntityId}<p class="mt-1 text-xs text-destructive">{propertyFormErrors.ownerEntityId}</p>{/if}
					</div>
				{:else}
					<InlineField label="Owner" bind:value={propertyForm.ownerEntityId} display={ownershipLabel(property.ownerships)} editing={false} testid="property-detail-owner" class="sm:col-span-2" morphName="vt-prop-owner" />
				{/if}
			</DetailCard>

			<DetailCard title="Address" icon={MapPin} accent="muted" testid="property-detail-address-card" contentClass="grid gap-x-6 gap-y-4 sm:grid-cols-2">
				<div class="sm:col-span-2">
					{@render inlineFieldWrap('property-detail-address', 'Address', `${property.addressLine1}${property.addressLine2 ? `, ${property.addressLine2}` : ''}`, propertyFormErrors.addressLine1, addressControl, editingProperty, 'vt-prop-address')}
				</div>
				<InlineField label="Apt / Suite / Unit #" bind:value={propertyForm.addressLine2} display={property.addressLine2 ?? '—'} editing={editingProperty} error={propertyFormErrors.addressLine2} testid="property-detail-address2" morphName="vt-prop-address2" />
				<InlineField label="City" bind:value={propertyForm.city} display={property.city} editing={editingProperty} error={propertyFormErrors.city} testid="property-detail-city" morphName="vt-prop-city" />
				{@render inlineFieldWrap('property-detail-state', 'State', property.state ?? '', propertyFormErrors.state, stateControl, editingProperty, 'vt-prop-state')}
				<InlineField label="ZIP" bind:value={propertyForm.postalCode} display={property.postalCode} editing={editingProperty} error={propertyFormErrors.postalCode} testid="property-detail-zip" morphName="vt-prop-zip" />
			</DetailCard>

			<DetailCard title="Operations" icon={Info} accent="muted" testid="property-detail-operations-card" contentClass="grid gap-x-6 gap-y-4 sm:grid-cols-2">
				<InlineField label="Year built" bind:value={propertyForm.yearBuilt} display={property.yearBuilt != null ? String(property.yearBuilt) : '—'} editing={editingProperty} type="number" error={propertyFormErrors.yearBuilt} testid="property-detail-year-built" morphName="vt-prop-year-built" />
				<InlineField label="Management fee %" bind:value={propertyForm.managementFeePercent} display={property.managementFeePercent != null ? `${property.managementFeePercent}%` : '—'} editing={editingProperty} type="number" error={propertyFormErrors.managementFeePercent} testid="property-detail-management-fee" morphName="vt-prop-management-fee" />
				<InlineField label="Notes" bind:value={propertyForm.notes} display={property.notes ?? '—'} editing={editingProperty} type="textarea" error={propertyFormErrors.notes} testid="property-detail-notes" class="sm:col-span-2" morphName="vt-prop-notes" />
			</DetailCard>

			<DetailCard title="Tax basis" icon={Info} accent="muted" testid="property-detail-tax-basis-card" contentClass="grid gap-x-6 gap-y-4 sm:grid-cols-2">
				<InlineField label="Purchase price" bind:value={propertyForm.purchasePrice} display={property.purchasePrice != null ? fmtMoney(property.purchasePrice) : '—'} editing={editingProperty} type="number" error={propertyFormErrors.purchasePrice} testid="property-detail-purchase-price" morphName="vt-prop-purchase-price" />
				<InlineField label="Land value" bind:value={propertyForm.landValue} display={property.landValue != null ? fmtMoney(property.landValue) : '—'} editing={editingProperty} type="number" error={propertyFormErrors.landValue} testid="property-detail-land-value" morphName="vt-prop-land-value" />
				{@render inlineFieldWrap('property-detail-in-service-date', 'In-service date', property.inServiceDate ? fmtDateOnly(property.inServiceDate) : '—', propertyFormErrors.inServiceDate, propertyInServiceDateControl, editingProperty, 'vt-prop-in-service-date', true)}
				<InlineField label="Manual annual depreciation" bind:value={propertyForm.manualAnnualDepreciation} display={property.manualAnnualDepreciation != null ? fmtMoney(property.manualAnnualDepreciation) : '—'} editing={editingProperty} type="number" error={propertyFormErrors.manualAnnualDepreciation} testid="property-detail-manual-depreciation" morphName="vt-prop-manual-depreciation" />
				<InlineField label="Accumulated depreciation" bind:value={propertyForm.manualAnnualDepreciation} display={fmtMoney(property.accumulatedDepreciation ?? 0)} editing={false} type="number" testid="property-detail-accumulated-depreciation" class="sm:col-span-2" morphName="vt-prop-accumulated-depreciation" />
			</DetailCard>

		</div>
		{:else if activeArea === 'rentals'}
		<div class="mb-6" data-testid="property-detail-units">
			<h2 class="mb-3 text-lg font-semibold">Units</h2>
			<DataGrid
				data={unitsList}
				columns={unitColumns}
				loading={unitsQuery.isLoading}
				emptyMessage="No units on this property yet."
				getRowKey={(u) => u.id}
				onRowClick={(u) => goto(`/units/${u.id}`)}
				pageSize={PAGE_SIZE}
				page={rentalsPage}
				totalCount={unitsQuery.data?.totalCount ?? 0}
				serverSide
				onPageChange={(next) => (rentalsPage = next)}
				data-testid="property-units-grid"
			>
				{#snippet mobileActions(unit)}
					{#if canManageRentals}
						<Button
							variant="ghost"
							size="icon"
							data-testid="unit-delete-mobile-{unit.id}"
							aria-label={`Remove unit ${unit.unitNumber}`}
							onclick={(event) => {
								event.stopPropagation();
								deleteUnitTarget = unit;
							}}
						>
							<Trash2 class="h-4 w-4 text-destructive" />
						</Button>
					{/if}
				{/snippet}
				{#snippet toolbar()}
					<div class="flex flex-1"></div>
					{#if canManageRentals}
						<Button class="gap-2 shrink-0" onclick={openAddUnit} data-testid="unit-add-button" data-coach="add-unit">
							<Plus class="h-4 w-4" />
							Add Unit
						</Button>
					{/if}
				{/snippet}
			</DataGrid>
		</div>
		<div data-testid="property-detail-leases">
			<h2 class="mb-3 text-lg font-semibold">Leases</h2>
			<DataGrid
				data={leasesList}
				columns={leaseColumns}
				loading={leasesQuery.isLoading}
				emptyMessage="No leases for this property."
				onRowClick={(relationship) => goto(recordHref('leaseManagement', { id: relationship.leaseManagementId, unitId: relationship.unitId }))}
				getRowKey={(relationship) => relationship.leaseManagementId}
				pageSize={PAGE_SIZE}
				page={leasePage}
				totalCount={leasesQuery.data?.totalCount ?? 0}
				serverSide
				onPageChange={(next) => (leasePage = next)}
				data-testid="property-leases-grid"
			/>
		</div>
		{:else if activeArea === 'property-work'}
			<DataGrid
				data={workOrdersQuery.data?.items ?? []}
				columns={workOrderColumns}
				loading={workOrdersQuery.isLoading || workOrdersQuery.isFetching}
				emptyMessage="No property-wide or Unit work orders."
				onRowClick={(row) => goto(recordHref('workOrder', row))}
				getRowKey={(row) => row.id}
				pageSize={PAGE_SIZE}
				page={workPage}
				totalCount={workOrdersQuery.data?.totalCount ?? 0}
				serverSide
				onPageChange={(next) => (workPage = next)}
				data-testid="property-work-grid"
			/>
		{:else if activeArea === 'property-finances'}
			<div class="space-y-6" data-testid="property-finances-content">
				<DataGrid
					data={expensesQuery.data?.items ?? []}
					columns={expenseColumns}
					loading={expensesQuery.isLoading || expensesQuery.isFetching}
					emptyMessage="No property expenses."
					onRowClick={(row) => goto(recordHref('expense', row))}
					getRowKey={(row) => row.id}
					pageSize={PAGE_SIZE}
					page={financePage}
					totalCount={expensesQuery.data?.totalCount ?? 0}
					serverSide
					onPageChange={(next) => (financePage = next)}
					data-testid="property-expenses-grid"
				/>
				<PropertyLoansSection propertyId={id} canManage={canManageMoneyExpenses} />
				<PropertyCapitalAssetsSection propertyId={id} canManage={canManageMoneyExpenses} />
				<PropertyDispositionsSection propertyId={id} canManage={canManageRentals} />
				<PropertyRecurringExpensesSection propertyId={id} canManage={canManageMoneyExpenses} />
				<DetailCard
					title="Cost basis (depreciation)"
					icon={Info}
					accent="muted"
					testid="property-detail-basis-card"
					contentClass="grid gap-x-6 gap-y-4 sm:grid-cols-2 lg:grid-cols-4"
				>
					<InlineField label="Purchase price" bind:value={propertyForm.purchasePrice} display={property.purchasePrice != null ? fmtMoney(property.purchasePrice) : '—'} editing={false} type="number" testid="property-basis-purchase-price" />
					<InlineField label="Land value" bind:value={propertyForm.landValue} display={property.landValue != null ? fmtMoney(property.landValue) : '—'} editing={false} type="number" testid="property-basis-land-value" />
					<InlineField label="In-service date" bind:value={propertyForm.inServiceDate} display={property.inServiceDate ? fmtDateOnly(property.inServiceDate) : '—'} editing={false} testid="property-basis-in-service" />
					<InlineField label="Accumulated depreciation" bind:value={propertyForm.manualAnnualDepreciation} display={fmtMoney(property.accumulatedDepreciation ?? 0)} editing={false} type="number" testid="property-basis-accumulated" />
				</DetailCard>
			</div>
		{:else if activeArea === 'documents-history'}
		<div class="space-y-6" data-testid="property-documents-history-surface">
			<section data-testid="property-documents-section">
				<DocumentsPanel title="Property documents" entityType="Property" entityId={id} />
			</section>
			<section class="rounded-lg border border-border bg-card p-4" data-testid="property-history-section">
				<h2 class="mb-1 text-base font-semibold">History</h2>
				<p class="mb-3 text-sm text-muted-foreground">Every permitted change to this property.</p>
				<RecordHistory entityType="Property" entityId={id} />
			</section>
		</div>
		{/if}
	{/if}
</div>

<!-- Unit add/edit dialog -->
<Dialog.Root open={canManageRentals && showUnitForm} onOpenChange={(v) => { if (!v) closeUnitForm(); }}>
	<Dialog.Content class="max-w-sm">
		<Dialog.Header>
			<Dialog.Title>{editingUnitId == null ? 'Add Unit' : 'Edit Unit'}</Dialog.Title>
		</Dialog.Header>
		<div data-testid="unit-form">
			<UnitFields bind:form={unitForm} errors={unitFormErrors} />
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
	open={canManageRentals && showDeletePropertyConfirm}
	title="Delete property"
	message={propertyDeleteState?.message ?? ''}
	busy={deletePropertyMutation.isPending}
	confirmDisabled={propertyDeleteState?.confirmDisabled ?? false}
	testid="property-detail-delete-confirm"
	onconfirm={() => {
		if (!property || propertyDeleteState?.confirmDisabled) return;
		deletePropertyMutation.mutate(property.id);
	}}
	oncancel={() => (showDeletePropertyConfirm = false)}
/>

<!-- Delete unit confirm -->
<ConfirmDialog
	open={canManageRentals && deleteUnitTarget !== null}
	title="Remove unit"
	message={deleteUnitTarget ? `Remove unit "${deleteUnitTarget.unitNumber}"?` : ''}
	busy={deleteUnitMutation.isPending}
	testid="unit-delete-confirm"
	onconfirm={() => deleteUnitTarget && deleteUnitMutation.mutate(deleteUnitTarget.id)}
	oncancel={() => (deleteUnitTarget = null)}
/>
