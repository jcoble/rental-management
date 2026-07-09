<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { leases } from '$lib/api/endpoints/leases';
	import { properties } from '$lib/api/endpoints/properties';
	import { tenants } from '$lib/api/endpoints/tenants';
	import type { Lease } from '$lib/types';
	import { recordHref } from '$lib/navigation/record-href';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { leaseRentTrackingErrors, leaseSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { debounced } from '$lib/utils/debounce.svelte';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import * as Dialog from '$lib/components/ui/dialog';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import FormStepper, { type FormStepperStep } from '$lib/components/shared/FormStepper.svelte';
	import StepperNextButton from '$lib/components/shared/StepperNextButton.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import LeaseTermFields from '$lib/components/forms/LeaseTermFields.svelte';
	import TenantMultiSelect from '$lib/components/forms/TenantMultiSelect.svelte';
	import { clearFieldError } from '$lib/forms/form-errors';
	import {
		LEASE_STATUSES,
		getLeaseStatusOptions,
		getLeasesEmptyStateCopy,
	} from '$lib/leases/lease-list-state';
	import { defaultLeaseNumber } from '$lib/leases/lease-number';
	import { readLeaseCreatePrefill } from '$lib/leases/lease-create-prefill';
	import { Plus, FileText } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import * as Select from '$lib/components/ui/select';
	import { page } from '$app/state';
	import { readGridParam, syncGridUrl } from '$lib/utils/grid-url-state.svelte';
	import PageHeader from '$lib/components/m3/PageHeader.svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const PAGE_SIZE = 20;
	const leaseStatusOptions = getLeaseStatusOptions();

	// Search / status / sort / page persisted in the URL so they survive navigating away and back. The
	// grid is server-side: these values drive the API query instead of fetching a broad capped list and
	// filtering/sorting/paging in Svelte.
	const initialParams = page.url.searchParams;
	let search = $state(readGridParam(initialParams, 'q'));
	let statusFilter = $state(readGridParam(initialParams, 'status'));
	let gridSort = $state(readGridParam(initialParams, 'sort'));
	let gridPage = $state(readGridParam(initialParams, 'page', 1));
	const debouncedSearch = debounced(() => search, 300);

	// Reset to page 1 when a filter/search changes — but not on initial mount.
	let filterResetPrimed = false;
	$effect(() => {
		search;
		statusFilter;
		if (!filterResetPrimed) {
			filterResetPrimed = true;
			return;
		}
		gridPage = 1;
	});

	$effect(() => {
		syncGridUrl({ q: search, status: statusFilter, sort: gridSort, page: gridPage }, { page: 1 });
	});

	const leasesQuery = createQuery(() => ({
		queryKey: ['leases', portfolioId, 'page', debouncedSearch.value, statusFilter, gridSort, gridPage, PAGE_SIZE],
		queryFn: () => leases.listPage(portfolioId, {
			search: debouncedSearch.value,
			status: statusFilter || undefined,
			sort: gridSort || undefined,
			skip: (gridPage - 1) * PAGE_SIZE,
			take: PAGE_SIZE,
		}),
	}));

	const propertiesQuery = createQuery(() => ({
		queryKey: ['properties', portfolioId],
		queryFn: () => properties.list(portfolioId, { take: 200 }),
	}));
	const availableTenantsQuery = createQuery(() => ({
		queryKey: ['tenants', portfolioId, 'lease-create', 'available-for-lease'],
		queryFn: () => tenants.listPage(portfolioId, { take: 200, sort: 'name', availableForLease: true }),
	}));
	const editTenantsQuery = createQuery(() => ({
		queryKey: ['tenants', portfolioId, 'lease-edit'],
		queryFn: () => tenants.list(portfolioId, { take: 200 }),
	}));

	let formPropertyId = $state('');
	const unitsForPropertyQuery = createQuery(() => ({
		queryKey: ['units-for-lease', formPropertyId],
		enabled: !!formPropertyId,
		queryFn: () => properties.listUnits(Number(formPropertyId)),
	}));

	const empty = {
		leaseNumber: defaultLeaseNumber(), propertyId: '', unitId: '', tenantId: '', tenantIds: [] as string[], startDate: '', endDate: '',
		monthlyRent: '', securityDeposit: '', lateFeeAmount: '75', rentDueDay: '1',
		rentTrackingStartMode: 'ForwardOnly', rentTrackingStartDate: '',
		openingBalanceAmount: '', openingBalanceAsOfDate: '', openingBalanceNote: '',
		status: 'Active', notes: '',
	};
	let showForm = $state(false);
	let editingId = $state<number | null>(null);
	let form = $state({ ...empty });
	let formErrors = $state<Record<string, string>>({});
	let leaseStep = $state(0);
	let completedLeaseSteps = $state<number[]>([]);
	let deleteTarget = $state<Lease | null>(null);
	let appliedCreatePrefillKey = $state('');

	const leaseSteps: FormStepperStep[] = [
		{ id: 'location', label: 'Location', description: 'Property and unit' },
		{ id: 'tenants', label: 'Tenants', description: 'Lease parties' },
		{ id: 'identity', label: 'Lease #', description: 'Reference' },
		{ id: 'dates', label: 'Dates', description: 'Start and end' },
		{ id: 'rent', label: 'Rent', description: 'Rent and deposit' },
		{ id: 'fees', label: 'Fees', description: 'Late fee and due day' },
		{ id: 'status', label: 'Status', description: 'State and notes' },
		{ id: 'tracking', label: 'Tracking', description: 'Backfill options' },
	];
	const leaseStepFields = [
		['propertyId', 'unitId'],
		['tenantId'],
		['leaseNumber'],
		['startDate', 'endDate'],
		['monthlyRent', 'securityDeposit'],
		['lateFeeAmount', 'rentDueDay'],
		['status', 'notes'],
		['rentTrackingStartMode', 'rentTrackingStartDate', 'openingBalanceAmount', 'openingBalanceAsOfDate', 'openingBalanceNote'],
	] as const;

	function clearLeaseError(field: string) {
		const next = clearFieldError(formErrors, field);
		if (next !== formErrors) formErrors = next;
	}

	$effect(() => {
		if (form.propertyId) clearLeaseError('propertyId');
	});
	$effect(() => {
		if (form.unitId) clearLeaseError('unitId');
	});
	$effect(() => {
		if (form.tenantId || form.tenantIds.length > 0) clearLeaseError('tenantId');
	});
	$effect(() => {
		if (form.leaseNumber.trim()) clearLeaseError('leaseNumber');
	});
	$effect(() => {
		if (form.startDate) clearLeaseError('startDate');
	});
	$effect(() => {
		if (form.endDate) clearLeaseError('endDate');
	});
	$effect(() => {
		if (form.monthlyRent) clearLeaseError('monthlyRent');
	});
	$effect(() => {
		if (form.securityDeposit) clearLeaseError('securityDeposit');
	});
	$effect(() => {
		if (form.lateFeeAmount) clearLeaseError('lateFeeAmount');
	});
	$effect(() => {
		if (form.rentDueDay) clearLeaseError('rentDueDay');
	});
	$effect(() => {
		if (form.rentTrackingStartDate) clearLeaseError('rentTrackingStartDate');
	});
	$effect(() => {
		if (form.openingBalanceAmount) clearLeaseError('openingBalanceAmount');
	});
	$effect(() => {
		if (form.openingBalanceAsOfDate) clearLeaseError('openingBalanceAsOfDate');
	});
	$effect(() => {
		if (form.openingBalanceNote) clearLeaseError('openingBalanceNote');
	});
	$effect(() => {
		if (form.rentTrackingStartMode !== 'CustomCutoffDate' && form.rentTrackingStartDate) {
			form.rentTrackingStartDate = '';
		}
	});
	$effect(() => {
		if (form.rentTrackingStartMode !== 'OpeningBalanceOnly') {
			if (form.openingBalanceAmount) form.openingBalanceAmount = '';
			if (form.openingBalanceAsOfDate) form.openingBalanceAsOfDate = '';
			if (form.openingBalanceNote) form.openingBalanceNote = '';
		}
	});

	$effect(() => {
		if (form.propertyId !== formPropertyId) {
			formPropertyId = form.propertyId;
		}
	});

	function invalidate() {
		queryClient.invalidateQueries({ queryKey: ['leases', portfolioId] });
	}

	const saveMutation = createMutation(() => ({
		mutationFn: ({ id, data }: { id: number | null; data: Record<string, unknown> }) =>
			id == null ? leases.create(data) : leases.update(id, data),
		onSuccess: (_r, vars) => {
			showSuccess(vars.id == null ? 'Lease created.' : 'Lease updated.');
			closeForm();
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const statusMutation = createMutation(() => ({
		mutationFn: ({ id, status }: { id: number; status: string }) => leases.update(id, { status }),
		onSuccess: () => {
			showSuccess('Lease status updated.');
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const deleteMutation = createMutation(() => ({
		mutationFn: (id: number) => leases.delete(id),
		onSuccess: () => {
			showSuccess('Lease deleted.');
			deleteTarget = null;
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	$effect(() => {
		const prefill = readLeaseCreatePrefill(page.url.searchParams);
		if (!prefill) {
			appliedCreatePrefillKey = '';
			return;
		}

		const key = `create:${prefill.tenantId}`;
		if (appliedCreatePrefillKey === key) return;
		appliedCreatePrefillKey = key;
		openCreate({ tenantId: prefill.tenantId });
	});

	function openCreate(defaults: { tenantId?: string } = {}) {
		editingId = null;
		const tenantIds = defaults.tenantId ? [defaults.tenantId] : [];
		form = { ...empty, tenantId: defaults.tenantId ?? '', tenantIds };
		formPropertyId = '';
		formErrors = {};
		leaseStep = 0;
		completedLeaseSteps = [];
		showForm = true;
	}
	function openEdit(l: Lease) {
		editingId = l.id;
		const rentTrackingStartDate = l.rentTrackingStartDate?.slice(0, 10) ?? '';
		const tenantIds = (l.tenantIds?.length
			? l.tenantIds
			: l.tenants?.length
				? l.tenants.map((tenant) => tenant.id)
				: [l.tenantId]).map((id) => String(id));
		form = {
			leaseNumber: l.leaseNumber ?? '',
			propertyId: String(l.propertyId),
			unitId: String(l.unitId),
			tenantId: tenantIds[0] ?? String(l.tenantId),
			tenantIds,
			startDate: l.startDate?.slice(0, 10) ?? '',
			endDate: l.endDate?.slice(0, 10) ?? '',
			monthlyRent: String(l.monthlyRent),
			securityDeposit: String(l.securityDeposit),
			lateFeeAmount: String(l.lateFeeAmount),
			rentDueDay: String(l.rentDueDay),
			rentTrackingStartMode: rentTrackingStartDate
				? 'CustomCutoffDate'
				: l.status === 'Active'
					? 'BackfillFromLeaseStart'
					: 'ForwardOnly',
			rentTrackingStartDate,
			openingBalanceAmount: '',
			openingBalanceAsOfDate: '',
			openingBalanceNote: '',
			status: l.status,
			notes: l.notes ?? '',
		};
		formPropertyId = String(l.propertyId);
		formErrors = {};
		leaseStep = 0;
		completedLeaseSteps = [];
		showForm = true;
	}
	function closeForm() {
		showForm = false;
		editingId = null;
		formErrors = {};
		leaseStep = 0;
		completedLeaseSteps = [];
	}

	function selectedLeaseTenantIds() {
		return form.tenantIds
			.map((id) => Number(id))
			.filter((id) => Number.isInteger(id) && id > 0);
	}

	function leaseValidationState() {
		const selectedTenantIds = selectedLeaseTenantIds();
		const tenantId = String(selectedTenantIds[0] ?? '');
		const validationInput = { ...form, tenantId };
		const result = parseForm(leaseSchema, validationInput);
		const rentTrackingErrors = leaseRentTrackingErrors(validationInput);
		const selectedUnit = (unitsForPropertyQuery.data ?? []).find(
			(unit) => String(unit.id) === form.unitId,
		);
		const unitAvailabilityErrors: Record<string, string> =
			editingId === null && form.status === 'Active' && selectedUnit && selectedUnit.status !== 'Vacant'
				? { unitId: 'This unit already has an active lease. End it before creating another active lease.' }
				: {};
		const tenantErrors: Record<string, string> =
			selectedTenantIds.length === 0 ? { tenantId: 'Select at least one tenant' } : {};
		return {
			data: result.data,
			errors: { ...(result.errors ?? {}), ...rentTrackingErrors, ...unitAvailabilityErrors, ...tenantErrors },
			selectedTenantIds,
		};
	}

	function leaseStepErrorFields(step: number, errors: Record<string, string>) {
		const visibleFields = new Set<string>(leaseStepFields[step] ?? []);
		return Object.entries(errors).filter(([field]) => visibleFields.has(field));
	}

	function firstLeaseErrorStep(errors: Record<string, string>) {
		return leaseStepFields.findIndex((fields) => fields.some((field) => errors[field]));
	}

	function markLeaseStepInvalid(step: number) {
		completedLeaseSteps = completedLeaseSteps.filter((completedStep) => completedStep < step);
	}

	function validateLeaseStep(step: number) {
		const { errors } = leaseValidationState();
		const currentErrors = Object.fromEntries(leaseStepErrorFields(step, errors));
		const currentFields = new Set<string>(leaseStepFields[step] ?? []);
		const nextErrors = Object.fromEntries(Object.entries(formErrors).filter(([field]) => !currentFields.has(field)));
		formErrors = { ...nextErrors, ...currentErrors };
		const isValid = Object.keys(currentErrors).length === 0;
		if (!isValid) markLeaseStepInvalid(step);
		return isValid;
	}

	function nextLeaseStep() {
		if (!validateLeaseStep(leaseStep)) return;
		if (completedLeaseSteps.includes(leaseStep)) {
			leaseStep = Math.min(leaseStep + 1, leaseSteps.length - 1);
			return;
		}
		completedLeaseSteps = [...completedLeaseSteps, leaseStep];
		window.setTimeout(() => {
			leaseStep = Math.min(leaseStep + 1, leaseSteps.length - 1);
		}, 260);
	}

	function submit() {
		const { data, errors, selectedTenantIds } = leaseValidationState();
		if (Object.keys(errors).length > 0) {
			formErrors = errors;
			const firstErrorStep = firstLeaseErrorStep(errors);
			if (firstErrorStep >= 0) {
				leaseStep = firstErrorStep;
				markLeaseStepInvalid(firstErrorStep);
			}
			return;
		}
		if (!data) return;
		formErrors = {};
		saveMutation.mutate({
			id: editingId,
			data: { portfolioId, ...data, tenantId: selectedTenantIds[0], tenantIds: selectedTenantIds },
		});
	}

	const list = $derived(leasesQuery.data?.items ?? []);
	const totalCount = $derived(leasesQuery.data?.totalCount ?? 0);
	const hasActiveFilters = $derived(Boolean(search.trim() || statusFilter));
	const emptyCopy = $derived(getLeasesEmptyStateCopy({ hasActiveFilters }));
	const selectedStatusLabel = $derived(
		leaseStatusOptions.find((option) => option.value === statusFilter)?.label ?? ''
	);

	// Derived labels for select triggers
	const selectedPropertyLabel = $derived(
		propertiesQuery.data?.find((p) => String(p.id) === form.propertyId)?.name ?? ''
	);
	const selectedUnitLabel = $derived(
		unitsForPropertyQuery.data?.find((u) => String(u.id) === form.unitId)
			? `Unit ${unitsForPropertyQuery.data?.find((u) => String(u.id) === form.unitId)?.unitNumber} (${unitsForPropertyQuery.data?.find((u) => String(u.id) === form.unitId)?.status})`
			: ''
	);
	const leaseTenantOptions = $derived(
		editingId === null ? (availableTenantsQuery.data?.items ?? []) : (editTenantsQuery.data ?? [])
	);
	const leaseTenantOptionsLoading = $derived(
		editingId === null ? availableTenantsQuery.isLoading : editTenantsQuery.isLoading
	);
	// DataGrid column definitions
	const columns: ColumnDef<Lease>[] = [
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
			accessor: (l) => l.tenantName ?? '—',
		},
		{
			key: 'unitNumber',
			title: 'Unit',
			mobileRole: 'meta',
			accessor: (l) => l.unitNumber ?? '—',
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
			cell: statusCell,
		},
	];
</script>

{#snippet statusCell(lease: Lease)}
	<StatusBadge status={lease.status} />
{/snippet}

<svelte:head>
	<title>Leases - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="leases-page">
	<PageHeader
		class="mb-4"
		band
		art={5}
		tone="amber"
		eyebrow="Rentals"
		title="Leases"
		description="Lease lifecycle, rent terms, and status updates."
		data-testid="leases-header"
	/>

	<DataGrid
		data={list}
		{columns}
		loading={leasesQuery.isLoading || leasesQuery.isFetching}
		emptyMessage={emptyCopy.message}
		emptyDescription={emptyCopy.description}
		emptyIcon={FileText}
		emptyActionLabel={emptyCopy.actionLabel}
		emptyOnAction={openCreate}
		emptyTone="primary"
		onRowClick={(lease) => goto(recordHref('lease', lease))}
		getRowKey={(l) => l.id}
		data-testid="leases-list"
		pageSize={PAGE_SIZE}
		page={gridPage}
		totalCount={totalCount}
		serverSide
		onPageChange={(page) => (gridPage = page)}
		sort={gridSort}
		onSortChange={(s) => { gridSort = s ?? ''; gridPage = 1; }}
	>
		{#snippet toolbar()}
			<div class="flex flex-1 flex-wrap items-center gap-2">
				<div class="max-w-sm flex-1">
					<SearchInput bind:value={search} placeholder="Search leases…" testid="lease-search" />
				</div>
				<Select.Root type="single" bind:value={statusFilter}>
					<Select.Trigger class="w-[180px]" data-testid="lease-status-filter">
						{selectedStatusLabel || 'All statuses'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="" label="All statuses">All statuses</Select.Item>
						{#each leaseStatusOptions as status}
							<Select.Item value={status.value} label={status.label}>{status.label}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
			</div>
			<Button data-testid="lease-create-button" data-coach="add-lease" class="shrink-0 gap-2" onclick={openCreate}>
				<Plus class="h-4 w-4" />
				New Lease
			</Button>
		{/snippet}
	</DataGrid>
</div>

<Dialog.Root
	open={showForm}
	onOpenChange={(v) => { if (!v) closeForm(); }}
>
	<Dialog.Content
		class="max-h-[85vh] max-w-2xl overflow-y-auto"
		onInteractOutside={(event) => event.preventDefault()}
		onEscapeKeydown={(event) => event.preventDefault()}
	>
		<Dialog.Header>
			<Dialog.Title>{editingId == null ? 'New Lease' : 'Edit Lease'}</Dialog.Title>
		</Dialog.Header>
		<FormStepper steps={leaseSteps} bind:currentStep={leaseStep} completedSteps={completedLeaseSteps} testid="lease-stepper">
			<div class="space-y-4" data-testid="lease-form">
				{#if leaseStep === 0}
					<div class="grid gap-3 md:grid-cols-2">
						<div>
							<span class="mb-1 block text-xs font-medium text-muted-foreground">Property</span>
							<Select.Root type="single" bind:value={form.propertyId}>
								<Select.Trigger class="w-full" data-testid="lease-property-input">
									{selectedPropertyLabel ? selectedPropertyLabel : 'Select property'}
								</Select.Trigger>
								<Select.Content>
									<Select.Item value="" label="Select property">Select property</Select.Item>
									{#each propertiesQuery.data || [] as property}
										<Select.Item value={String(property.id)} label={property.name}>{property.name}</Select.Item>
									{/each}
								</Select.Content>
							</Select.Root>
							{#if formErrors.propertyId}<p class="mt-1 text-xs text-destructive" data-testid="lease-property-error">{formErrors.propertyId}</p>{/if}
						</div>
						<div>
							<span class="mb-1 block text-xs font-medium text-muted-foreground">Unit</span>
							<Select.Root type="single" bind:value={form.unitId} disabled={!form.propertyId}>
								<Select.Trigger class="w-full" data-testid="lease-unit-input" disabled={!form.propertyId}>
									{selectedUnitLabel ? selectedUnitLabel : 'Select unit'}
								</Select.Trigger>
								<Select.Content>
									<Select.Item value="" label="Select unit">Select unit</Select.Item>
									{#each unitsForPropertyQuery.data || [] as unit}
										<Select.Item value={String(unit.id)} label="Unit {unit.unitNumber} ({unit.status})">Unit {unit.unitNumber} ({unit.status})</Select.Item>
									{/each}
								</Select.Content>
							</Select.Root>
							{#if formErrors.unitId}<p class="mt-1 text-xs text-destructive" data-testid="lease-unit-error">{formErrors.unitId}</p>{/if}
						</div>
					</div>
				{:else if leaseStep === 1}
					<TenantMultiSelect
						label="Tenants"
						tenants={leaseTenantOptions}
						bind:selectedIds={form.tenantIds}
						error={formErrors.tenantId}
						disabled={leaseTenantOptionsLoading}
						testid="lease-tenants-input"
					/>
				{:else if leaseStep === 2}
					<LeaseTermFields bind:form errors={formErrors} statuses={LEASE_STATUSES} section="identity" />
				{:else if leaseStep === 3}
					<LeaseTermFields bind:form errors={formErrors} statuses={LEASE_STATUSES} section="dates" />
				{:else if leaseStep === 4}
					<LeaseTermFields bind:form errors={formErrors} statuses={LEASE_STATUSES} section="rent" />
				{:else if leaseStep === 5}
					<LeaseTermFields bind:form errors={formErrors} statuses={LEASE_STATUSES} section="fees" />
				{:else if leaseStep === 6}
					<LeaseTermFields bind:form errors={formErrors} statuses={LEASE_STATUSES} section="status" />
				{:else}
					<LeaseTermFields bind:form errors={formErrors} statuses={LEASE_STATUSES} section="tracking" />
				{/if}
			</div>
		</FormStepper>
		<Dialog.Footer>
			<Button data-testid="lease-form-cancel" variant="outline" onclick={closeForm}>Cancel</Button>
			{#if leaseStep > 0}
				<Button data-testid="lease-step-back" variant="outline" onclick={() => (leaseStep = Math.max(leaseStep - 1, 0))}>Back</Button>
				{/if}
				{#if leaseStep < leaseSteps.length - 1}
					<StepperNextButton
						testid="lease-step-next"
						onclick={nextLeaseStep}
						complete={completedLeaseSteps.includes(leaseStep)}
					/>
				{:else}
				<Button data-testid="lease-form-save" onclick={submit} disabled={saveMutation.isPending}>
					{saveMutation.isPending ? 'Saving…' : 'Save lease'}
				</Button>
			{/if}
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<ConfirmDialog
	open={deleteTarget !== null}
	title="Delete lease"
	message={deleteTarget ? `Delete lease ${deleteTarget.leaseNumber}?` : ''}
	busy={deleteMutation.isPending}
	testid="lease-delete"
	onconfirm={() => deleteTarget && deleteMutation.mutate(deleteTarget.id)}
	oncancel={() => (deleteTarget = null)}
/>
