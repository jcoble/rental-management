<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { leases } from '$lib/api/endpoints/leases';
	import { properties } from '$lib/api/endpoints/properties';
	import { tenants } from '$lib/api/endpoints/tenants';
	import type { Lease } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { leaseSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { debounced } from '$lib/utils/debounce.svelte';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import * as Dialog from '$lib/components/ui/dialog';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import LeaseTermFields from '$lib/components/forms/LeaseTermFields.svelte';
	import {
		LEASE_STATUSES,
		getLeaseStatusOptions,
		getLeasesEmptyStateCopy,
	} from '$lib/leases/lease-list-state';
	import { readLeaseCreatePrefill } from '$lib/leases/lease-create-prefill';
	import { Plus, FileText } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import * as Select from '$lib/components/ui/select';
	import { page } from '$app/state';
	import { readGridParam, syncGridUrl } from '$lib/utils/grid-url-state.svelte';

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
	const tenantsQuery = createQuery(() => ({
		queryKey: ['tenants', portfolioId],
		queryFn: () => tenants.list(portfolioId, { take: 200 }),
	}));

	let formPropertyId = $state('');
	const unitsForPropertyQuery = createQuery(() => ({
		queryKey: ['units-for-lease', formPropertyId],
		enabled: !!formPropertyId,
		queryFn: () => properties.listUnits(Number(formPropertyId)),
	}));

	const empty = {
		leaseNumber: '', propertyId: '', unitId: '', tenantId: '', startDate: '', endDate: '',
		monthlyRent: '', securityDeposit: '', lateFeeAmount: '75', rentDueDay: '1', status: 'Draft', notes: '',
	};
	let showForm = $state(false);
	let editingId = $state<number | null>(null);
	let form = $state({ ...empty });
	let formErrors = $state<Record<string, string>>({});
	let deleteTarget = $state<Lease | null>(null);
	let appliedCreatePrefillKey = $state('');

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
		form = { ...empty, tenantId: defaults.tenantId ?? '' };
		formPropertyId = '';
		formErrors = {};
		showForm = true;
	}
	function openEdit(l: Lease) {
		editingId = l.id;
		form = {
			leaseNumber: l.leaseNumber ?? '',
			propertyId: String(l.propertyId),
			unitId: String(l.unitId),
			tenantId: String(l.tenantId),
			startDate: l.startDate?.slice(0, 10) ?? '',
			endDate: l.endDate?.slice(0, 10) ?? '',
			monthlyRent: String(l.monthlyRent),
			securityDeposit: String(l.securityDeposit),
			lateFeeAmount: String(l.lateFeeAmount),
			rentDueDay: String(l.rentDueDay),
			status: l.status,
			notes: l.notes ?? '',
		};
		formPropertyId = String(l.propertyId);
		formErrors = {};
		showForm = true;
	}
	function closeForm() {
		showForm = false;
		editingId = null;
		formErrors = {};
	}

	function submit() {
		// Validate AND send the FULL form. leaseSchema requires propertyId and the server's
		// CreateLeaseRequest has [Required][Range(1,..)] PropertyId. The old code destructured
		// propertyId OUT before parseForm, so validation always failed on the missing field —
		// Save silently no-op'd (no request, no surfaced error). Keep propertyId in the payload.
		const result = parseForm(leaseSchema, form);
		if (result.errors) {
			formErrors = result.errors;
			return;
		}
		formErrors = {};
		saveMutation.mutate({ id: editingId, data: { portfolioId, ...result.data } });
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
	const selectedTenantLabel = $derived(
		tenantsQuery.data?.find((t) => String(t.id) === form.tenantId)
			? (tenantsQuery.data?.find((t) => String(t.id) === form.tenantId)?.fullName ||
				`${tenantsQuery.data?.find((t) => String(t.id) === form.tenantId)?.firstName} ${tenantsQuery.data?.find((t) => String(t.id) === form.tenantId)?.lastName}`)
			: ''
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
	<div class="mb-4 flex items-center justify-between gap-3">
		<div>
			<h1 class="text-2xl font-bold">Leases</h1>
			<p class="text-sm text-muted-foreground">Lease lifecycle, rent terms, and status updates.</p>
		</div>
	</div>

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
		onRowClick={(lease) => goto('/leases/' + lease.id)}
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
	<Dialog.Content class="max-w-2xl">
		<Dialog.Header>
			<Dialog.Title>{editingId == null ? 'New Lease' : 'Edit Lease'}</Dialog.Title>
		</Dialog.Header>
		<div class="space-y-3" data-testid="lease-form">
			<!-- Property / unit / tenant pickers stay in the page; the term fields come from the shared component. -->
			<div class="grid gap-3 md:grid-cols-3">
				<div>
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
				<div>
					<Select.Root type="single" bind:value={form.tenantId}>
						<Select.Trigger class="w-full" data-testid="lease-tenant-input">
							{selectedTenantLabel ? selectedTenantLabel : 'Select tenant'}
						</Select.Trigger>
						<Select.Content>
							<Select.Item value="" label="Select tenant">Select tenant</Select.Item>
							{#each tenantsQuery.data || [] as tenant}
								<Select.Item value={String(tenant.id)} label={tenant.fullName || `${tenant.firstName} ${tenant.lastName}`}>{tenant.fullName || `${tenant.firstName} ${tenant.lastName}`}</Select.Item>
							{/each}
						</Select.Content>
					</Select.Root>
					{#if formErrors.tenantId}<p class="mt-1 text-xs text-destructive" data-testid="lease-tenant-error">{formErrors.tenantId}</p>{/if}
				</div>
			</div>
			<LeaseTermFields bind:form errors={formErrors} statuses={LEASE_STATUSES} />
		</div>
		<Dialog.Footer>
			<Button data-testid="lease-form-cancel" variant="outline" onclick={closeForm}>Cancel</Button>
			<Button data-testid="lease-form-save" onclick={submit} disabled={saveMutation.isPending}>
				{saveMutation.isPending ? 'Saving…' : 'Save lease'}
			</Button>
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
