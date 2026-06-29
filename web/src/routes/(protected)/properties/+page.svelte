<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { properties } from '$lib/api/endpoints/properties';
	import { owners } from '$lib/api/endpoints/owners';
	import type { Property } from '$lib/types';
	import { getPropertyDeleteState } from '$lib/properties/property-delete-state';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { propertySchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { debounced } from '$lib/utils/debounce.svelte';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Select from '$lib/components/ui/select';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import PropertyFields from '$lib/components/forms/PropertyFields.svelte';
	import { clearFieldError } from '$lib/forms/form-errors';
	import { Plus, Pencil, Trash2, Building } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import PageHeader from '$lib/components/m3/PageHeader.svelte';
	import { page } from '$app/state';
	import { readGridParam, syncGridUrl } from '$lib/utils/grid-url-state.svelte';
	import {
		formatPropertyStatus,
		formatPropertyType,
		propertyStatusOptions,
		propertyTypeOptions
	} from '$lib/properties/property-labels';
	import {
		createEmptyPropertyDraft,
		getPropertiesEmptyStateCopy
	} from '$lib/properties/property-list-state';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const PAGE_SIZE = 20;

	// Second coach hop for the "add a unit" checklist step. That step lands here (the property LIST)
	// and spotlights "open a property", but units are added on the property DETAIL page. When we arrive
	// via that coach, latch it so the row the user opens carries ?coach=add-unit onward — the detail
	// page's CoachTrigger then spotlights its "Add Unit" button, continuing the guidance.
	let forwardUnitCoach = $state(false);
	$effect(() => {
		if (page.url.searchParams.get('coach') === 'open-property-for-units') forwardUnitCoach = true;
	});
	function openProperty(p: Property) {
		goto(`/properties/${p.id}${forwardUnitCoach ? '?coach=add-unit' : ''}`);
	}

	// Filter/search/sort/page state persisted in the URL so it survives navigating away and back (and
	// browser Back/Forward, which remounts and re-seeds from these params). The DataGrid is server-side:
	// this state drives the API query instead of fetching a broad cap and sorting/paging in Svelte.
	const initialParams = page.url.searchParams;
	let search = $state(readGridParam(initialParams, 'q'));
	let typeFilter = $state(readGridParam(initialParams, 'type'));
	let statusFilter = $state(readGridParam(initialParams, 'status'));
	let gridSort = $state(readGridParam(initialParams, 'sort'));
	let gridPage = $state(readGridParam(initialParams, 'page', 1));
	const debouncedSearch = debounced(() => search, 300);

	// Reset to page 1 when a filter/search changes — but not on initial mount, so a deep-linked/restored
	// ?page= loads as-is.
	let filterResetPrimed = false;
	$effect(() => {
		search;
		typeFilter;
		statusFilter;
		if (!filterResetPrimed) {
			filterResetPrimed = true;
			return;
		}
		gridPage = 1;
	});

	$effect(() => {
		syncGridUrl(
			{ q: search, type: typeFilter, status: statusFilter, sort: gridSort, page: gridPage },
			{ page: 1 }
		);
	});

	const propertiesQuery = createQuery(() => ({
		queryKey: [
			'properties',
			portfolioId,
			'page',
			debouncedSearch.value,
			typeFilter,
			statusFilter,
			gridSort,
			gridPage,
			PAGE_SIZE
		],
		queryFn: () => properties.listPage(portfolioId, {
			search: debouncedSearch.value,
			type: typeFilter || undefined,
			status: statusFilter || undefined,
			sort: gridSort || undefined,
			skip: (gridPage - 1) * PAGE_SIZE,
			take: PAGE_SIZE,
		}),
	}));

	const ownersQuery = createQuery(() => ({
		queryKey: ['owners', portfolioId],
		queryFn: () => owners.list(portfolioId, { take: 200 }),
	}));

	const list = $derived(propertiesQuery.data?.items ?? []);
	const totalCount = $derived(propertiesQuery.data?.totalCount ?? 0);
	const hasActiveFilters = $derived(Boolean(search.trim() || typeFilter || statusFilter));
	const emptyStateCopy = $derived(getPropertiesEmptyStateCopy({ hasActiveFilters }));

	let showForm = $state(false);
	let editingId = $state<number | null>(null);
	let form = $state(createEmptyPropertyDraft());
	let formErrors = $state<Record<string, string>>({});
	let deleteTarget = $state<Property | null>(null);
	const deleteState = $derived(deleteTarget ? getPropertyDeleteState(deleteTarget) : null);

	function clearPropertyError(field: string) {
		const next = clearFieldError(formErrors, field);
		if (next !== formErrors) formErrors = next;
	}

	$effect(() => {
		if (form.name.trim()) clearPropertyError('name');
	});
	$effect(() => {
		if (form.addressLine1.trim()) clearPropertyError('addressLine1');
	});
	$effect(() => {
		if (form.city.trim()) clearPropertyError('city');
	});
	$effect(() => {
		if (form.state.trim()) clearPropertyError('state');
	});
	$effect(() => {
		if (form.postalCode.trim()) clearPropertyError('postalCode');
	});

	function invalidateList() {
		queryClient.invalidateQueries({ queryKey: ['properties'] });
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
		form = createEmptyPropertyDraft({ typeFilter, statusFilter });
		formErrors = {};
		showForm = true;
	}

	function openEdit(p: Property) {
		editingId = p.id;
		form = {
			name: p.name,
			type: p.type ?? 'MultiFamily',
			status: p.status ?? 'Active',
			addressLine1: p.addressLine1,
			addressLine2: p.addressLine2 ?? '',
			city: p.city,
			state: p.state,
			postalCode: p.postalCode,
			ownerEntityId: p.ownerEntityId != null ? String(p.ownerEntityId) : '',
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
			accessor: (p) => formatPropertyType(p.type),
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

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="properties-page">
	<PageHeader
		class="mb-4"
		band
		art={2}
		eyebrow="Portfolio"
		title="Properties"
		description="Portfolio, units, and occupancy setup."
		data-testid="properties-header"
	/>

	<!-- data-coach anchor: the getting-started "add a unit" step lands here and spotlights the list so
	     the user opens a property, then adds units on its detail page. -->
	<div data-coach="open-property-for-units">
	<DataGrid
		data={list}
		{columns}
		loading={propertiesQuery.isLoading || propertiesQuery.isFetching}
		emptyMessage={emptyStateCopy.message}
		emptyDescription={emptyStateCopy.description}
		emptyIcon={Building}
		emptyActionLabel={emptyStateCopy.actionLabel}
		emptyOnAction={openCreate}
		emptyTone="primary"
		onRowClick={openProperty}
		getRowKey={(p) => p.id}
		getRowTestId={() => 'property-row'}
		data-testid="properties-list"
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
				<SearchInput bind:value={search} placeholder="Search properties…" testid="property-search" />
				<Select.Root type="single" bind:value={typeFilter}>
					<Select.Trigger class="h-9 w-40 text-sm" data-testid="property-type-filter">
						{typeFilter ? formatPropertyType(typeFilter) : 'All types'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="" label="All types">All types</Select.Item>
						{#each propertyTypeOptions as option}
							<Select.Item value={option.value} label={option.label}>{option.label}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
				<Select.Root type="single" bind:value={statusFilter}>
					<Select.Trigger class="h-9 w-44 text-sm" data-testid="property-status-filter">
						{statusFilter ? formatPropertyStatus(statusFilter) : 'All statuses'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="" label="All statuses">All statuses</Select.Item>
						{#each propertyStatusOptions as option}
							<Select.Item value={option.value} label={option.label}>{option.label}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
			</div>
			<Button data-testid="property-create-button" data-coach="add-property" class="gap-2 shrink-0" onclick={openCreate}>
				<Plus class="h-4 w-4" />
				New Property
			</Button>
		{/snippet}
	</DataGrid>
	</div>
</div>

<Dialog.Root open={showForm} onOpenChange={(v) => { if (!v) closeForm(); }}>
	<Dialog.Content class="max-w-2xl">
		<Dialog.Header>
			<Dialog.Title>{editingId == null ? 'New Property' : 'Edit Property'}</Dialog.Title>
		</Dialog.Header>
		<div class="space-y-3" data-testid="property-form">
			<PropertyFields bind:form errors={formErrors} />
			<div>
				<span class="mb-1 block text-xs font-medium text-muted-foreground">Status</span>
				<Select.Root type="single" bind:value={form.status}>
					<Select.Trigger class="w-full" data-testid="property-status-input">{form.status ? formatPropertyStatus(form.status) : 'Select status'}</Select.Trigger>
					<Select.Content>
						{#each propertyStatusOptions as option}
							<Select.Item value={option.value} label={option.label}>{option.label}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
			</div>
			<div>
				<span class="mb-1 block text-xs font-medium text-muted-foreground">Apt / Suite / Unit #</span>
				<Input data-testid="property-address2-input" bind:value={form.addressLine2} placeholder="Apt / Suite / Unit # (optional)" />
			</div>
			<div>
				<span class="mb-1 block text-xs font-medium text-muted-foreground">Owner</span>
				<Select.Root type="single" bind:value={form.ownerEntityId}>
					<Select.Trigger class="w-full" data-testid="property-owner-input">
						{form.ownerEntityId ? ((ownersQuery.data || []).find(o => String(o.id) === form.ownerEntityId)?.name ?? 'No owner assigned') : 'No owner assigned'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="" label="No owner assigned">No owner assigned</Select.Item>
						{#each ownersQuery.data || [] as owner}
							<Select.Item value={String(owner.id)} label={owner.name}>{owner.name}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
			</div>
		</div>
		<div class="mt-4 flex justify-end gap-2">
			<Button data-testid="property-form-cancel" variant="outline" onclick={closeForm}>Cancel</Button>
			<Button data-testid="property-form-save" onclick={submitProperty} disabled={savePropertyMutation.isPending}>
				{savePropertyMutation.isPending ? 'Saving…' : 'Save property'}
			</Button>
		</div>
	</Dialog.Content>
</Dialog.Root>

<ConfirmDialog
	open={deleteTarget !== null}
	title="Delete property"
	message={deleteState?.message ?? ''}
	busy={deletePropertyMutation.isPending}
	confirmDisabled={deleteState?.confirmDisabled ?? false}
	testid="property-delete"
	onconfirm={() => {
		if (!deleteTarget || deleteState?.confirmDisabled) return;
		deletePropertyMutation.mutate(deleteTarget.id);
	}}
	oncancel={() => (deleteTarget = null)}
/>
