<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { workOrders } from '$lib/api/endpoints/workOrders';
	import { inspections } from '$lib/api/endpoints/inspections';
	import { properties } from '$lib/api/endpoints/properties';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { vendors } from '$lib/api/endpoints/vendors';
	import type { WorkOrder } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { workOrderSchema, inspectionSchema, parseForm } from '$lib/schemas';
	import { localInputToOffsetIso } from '$lib/utils/date';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { debounced } from '$lib/utils/debounce.svelte';
	import * as Dialog from '$lib/components/ui/dialog';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import { Plus, ShieldCheck, RefreshCw } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import * as Card from '$lib/components/ui/card';
	import { page } from '$app/state';
	import { readGridParam, syncGridUrl } from '$lib/utils/grid-url-state.svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const WO_STATUSES = ['New', 'Scheduled', 'InProgress', 'WaitingParts', 'Completed', 'Cancelled'];
	const WO_PRIORITIES = ['Low', 'Normal', 'High', 'Emergency'];

	// Work-order search / status / priority / sort / page persisted in the URL so they survive navigating
	// away and back. Sort/page seed the client-side DataGrid (initialSort / page) and are mirrored back
	// via onSortChange / bind:page.
	const initialParams = page.url.searchParams;
	let woSearch = $state(readGridParam(initialParams, 'q'));
	let woStatusFilter = $state(readGridParam(initialParams, 'status'));
	let woPriorityFilter = $state(readGridParam(initialParams, 'priority'));
	let gridSort = $state(readGridParam(initialParams, 'sort'));
	let gridPage = $state(readGridParam(initialParams, 'page', 1));
	const debouncedWoSearch = debounced(() => woSearch, 300);

	// Reset to page 1 when a filter/search changes — but not on initial mount.
	let filterResetPrimed = false;
	$effect(() => {
		woSearch;
		woStatusFilter;
		woPriorityFilter;
		if (!filterResetPrimed) {
			filterResetPrimed = true;
			return;
		}
		gridPage = 1;
	});

	$effect(() => {
		syncGridUrl(
			{ q: woSearch, status: woStatusFilter, priority: woPriorityFilter, sort: gridSort, page: gridPage },
			{ page: 1 }
		);
	});

	const workOrdersQuery = createQuery(() => ({
		queryKey: ['work-orders', portfolioId, debouncedWoSearch.value],
		queryFn: () => workOrders.list(portfolioId, { search: debouncedWoSearch.value, take: 100 }),
	}));
	const inspectionsQuery = createQuery(() => ({ queryKey: ['inspections', portfolioId], queryFn: () => inspections.list(portfolioId) }));
	const propertiesQuery = createQuery(() => ({ queryKey: ['properties', portfolioId], queryFn: () => properties.list(portfolioId, { take: 200 }) }));

	// --- Work order form/dialog ---
	const emptyWo = {
		propertyId: '',
		title: '',
		description: '',
		priority: 'Normal',
		category: 'General',
		unitId: '',
		tenantId: '',
		vendorId: '',
		scheduledFor: '',
		scheduledWindowEnd: '',
		estimatedCost: '',
	};
	let showWoForm = $state(false);
	let editingWoId = $state<number | null>(null);
	let woForm = $state({ ...emptyWo });
	let woErrors = $state<Record<string, string>>({});
	let woDeleteTarget = $state<WorkOrder | null>(null);

	// Optional work-order context. Tenants/vendors load only while the form is open; units are
	// fetched per selected property so the Unit dropdown only offers units of that property.
	const tenantsQuery = createQuery(() => ({ queryKey: ['tenants', portfolioId], queryFn: () => tenants.list(portfolioId, { take: 200 }), enabled: showWoForm }));
	const vendorsQuery = createQuery(() => ({ queryKey: ['vendors', portfolioId], queryFn: () => vendors.list(portfolioId, { take: 200 }), enabled: showWoForm }));
	const woPropertyId = $derived(woForm.propertyId ? Number(woForm.propertyId) : null);
	const woUnitsQuery = createQuery(() => ({
		queryKey: ['units', woPropertyId],
		queryFn: () => properties.listUnits(woPropertyId as number),
		enabled: showWoForm && woPropertyId != null,
	}));

	function invalidateWo() {
		queryClient.invalidateQueries({ queryKey: ['work-orders', portfolioId] });
	}

	const saveWoMutation = createMutation(() => ({
		mutationFn: ({ id, data }: { id: number | null; data: Record<string, unknown> }) =>
			id == null ? workOrders.create(data) : workOrders.update(id, data),
		onSuccess: (_r, vars) => {
			showSuccess(vars.id == null ? 'Work order created.' : 'Work order updated.');
			closeWoForm();
			invalidateWo();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const deleteWoMutation = createMutation(() => ({
		mutationFn: (id: number) => workOrders.delete(id),
		onSuccess: () => {
			showSuccess('Work order deleted.');
			woDeleteTarget = null;
			invalidateWo();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function openCreateWo() {
		editingWoId = null;
		woForm = { ...emptyWo };
		woErrors = {};
		showWoForm = true;
	}
	function openEditWo(wo: WorkOrder) {
		editingWoId = wo.id;
		woForm = {
			propertyId: String(wo.propertyId),
			title: wo.title,
			description: wo.description,
			priority: wo.priority,
			category: wo.category,
			unitId: wo.unitId != null ? String(wo.unitId) : '',
			tenantId: wo.tenantId != null ? String(wo.tenantId) : '',
			vendorId: wo.vendorId != null ? String(wo.vendorId) : '',
			// `datetime-local` wants `yyyy-MM-ddTHH:mm`; slice the ISO timestamp (same as appointments).
			scheduledFor: wo.scheduledFor?.slice(0, 16) ?? '',
			scheduledWindowEnd: wo.scheduledWindowEnd?.slice(0, 16) ?? '',
			estimatedCost: wo.estimatedCost != null ? String(wo.estimatedCost) : '',
		};
		woErrors = {};
		showWoForm = true;
	}
	function closeWoForm() {
		showWoForm = false;
		editingWoId = null;
		woErrors = {};
	}
	function submitWo() {
		const result = parseForm(workOrderSchema, woForm);
		if (result.errors) {
			woErrors = result.errors;
			return;
		}
		// Client guard: an arrival window can't end at or before it starts. Compare the raw
		// wall-clock values (both local) so the error surfaces inline before we hit the server.
		if (woForm.scheduledFor && woForm.scheduledWindowEnd) {
			const start = new Date(woForm.scheduledFor).getTime();
			const end = new Date(woForm.scheduledWindowEnd).getTime();
			if (!isNaN(start) && !isNaN(end) && end <= start) {
				woErrors = { scheduledWindowEnd: 'Window end must be after the start time.' };
				return;
			}
		}
		woErrors = {};
		// FROZEN contract: send the schedule as ISO-8601 with the browser's local offset so the
		// server stores the true instant (and the tenant SMS shows the landlord's wall-clock time),
		// rather than the un-zoned datetime-local string the API would mislabel as UTC.
		const data: Record<string, unknown> = { portfolioId, ...result.data };
		data.scheduledFor = localInputToOffsetIso(woForm.scheduledFor);
		data.scheduledWindowEnd = localInputToOffsetIso(woForm.scheduledWindowEnd);
		saveWoMutation.mutate({ id: editingWoId, data });
	}

	// --- Inspection form/dialog ---
	const emptyInspection = { propertyId: '', type: 'Routine', scheduledFor: '', templateId: '', inspector: '' };
	let showInspectionForm = $state(false);
	let inspectionForm = $state({ ...emptyInspection });
	let inspectionErrors = $state<Record<string, string>>({});

	// Smart-checklist templates (built-ins have negative ids). Loaded only when the form is open.
	const templatesQuery = createQuery(() => ({
		queryKey: ['inspection-templates'],
		queryFn: () => inspections.templates(),
		enabled: showInspectionForm,
	}));
	const templateOptions = $derived(templatesQuery.data ?? []);
	const selectedTemplateName = $derived(
		templateOptions.find((t) => String(t.id) === inspectionForm.templateId)?.name ?? 'No checklist (blank)'
	);

	const createInspectionMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => inspections.create(data),
		onSuccess: (created) => {
			showSuccess('Inspection scheduled.');
			showInspectionForm = false;
			inspectionForm = { ...emptyInspection };
			queryClient.invalidateQueries({ queryKey: ['inspections', portfolioId] });
			// Jump straight into the checklist so Maria can start ticking items.
			goto('/maintenance/inspections/' + created.id);
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function submitInspection() {
		const result = parseForm(inspectionSchema, inspectionForm);
		if (result.errors) {
			inspectionErrors = result.errors;
			return;
		}
		inspectionErrors = {};
		createInspectionMutation.mutate({ portfolioId, ...result.data });
	}

	// Work orders with client-side status + priority filter
	const woList = $derived.by(() => {
		const all = workOrdersQuery.data ?? [];
		return all.filter((w) => {
			if (woStatusFilter && w.status !== woStatusFilter) return false;
			if (woPriorityFilter && w.priority !== woPriorityFilter) return false;
			return true;
		});
	});

	// DataGrid column definitions — cell snippets referenced below in template
	const woColumns: ColumnDef<WorkOrder>[] = [
		{
			key: 'title',
			title: 'Title',
			sortable: true,
			mobileRole: 'title',
			cell: titleCellSnippet,
		},
		{
			key: 'propertyName',
			title: 'Property',
			sortable: true,
			mobileRole: 'subtitle',
			accessor: (wo) => wo.propertyName ?? '—',
		},
		{
			key: 'priority',
			title: 'Priority',
			mobileRole: 'badge',
			cell: priorityCellSnippet,
		},
		{
			key: 'status',
			title: 'Status',
			mobileRole: 'badge',
			cell: statusCellSnippet,
		},
		{
			key: 'requestedAt',
			title: 'Requested',
			format: 'date',
			sortable: true,
			mobileRole: 'meta',
		},
	];
</script>

{#snippet titleCellSnippet(wo: WorkOrder)}
	<span data-testid="work-order-title">{wo.title}</span>
{/snippet}

{#snippet priorityCellSnippet(wo: WorkOrder)}
	<StatusBadge status={wo.priority} />
{/snippet}

{#snippet statusCellSnippet(wo: WorkOrder)}
	<StatusBadge status={wo.status} />
{/snippet}

<svelte:head>
	<title>Work Orders - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="maintenance-page">
	<div class="mb-6 flex items-center justify-between gap-3">
		<div>
			<h1 class="text-2xl font-bold">Work Orders</h1>
			<p class="text-sm text-muted-foreground">Track resident requests, vendor execution, and compliance checks.</p>
		</div>
		<div class="flex gap-2">
			<Button data-testid="work-order-create-button" onclick={openCreateWo}><Plus class="h-4 w-4" /> New Work Order</Button>
			<Button data-testid="inspection-create-button" variant="outline" onclick={() => (showInspectionForm = true)}><ShieldCheck class="h-4 w-4" /> Inspection</Button>
			<Button data-testid="recurring-maintenance-link" variant="outline" onclick={() => goto('/maintenance/recurring')}><RefreshCw class="h-4 w-4" /> Recurring</Button>
		</div>
	</div>

	<!-- Work Orders DataGrid -->
	<DataGrid
		data={woList}
		columns={woColumns}
		loading={workOrdersQuery.isLoading}
		emptyMessage="No work orders found."
		onRowClick={(wo) => goto('/maintenance/' + wo.id)}
		getRowKey={(wo) => wo.id}
		data-testid="work-orders-list"
		initialSort={gridSort}
		bind:page={gridPage}
		onSortChange={(s) => (gridSort = s ?? '')}
	>
		{#snippet toolbar()}
			<div class="flex flex-1 items-center gap-2 min-w-0">
				<SearchInput bind:value={woSearch} placeholder="Search work orders…" testid="work-order-search" />
			</div>
			<Select.Root type="single" bind:value={woStatusFilter}>
				<Select.Trigger class="w-40 shrink-0" data-testid="work-order-status-filter">
					{woStatusFilter ? woStatusFilter : 'All statuses'}
				</Select.Trigger>
				<Select.Content>
					<Select.Item value="" label="All statuses">All statuses</Select.Item>
					{#each WO_STATUSES as s}
						<Select.Item value={s} label={s}>{s}</Select.Item>
					{/each}
				</Select.Content>
			</Select.Root>
			<Select.Root type="single" bind:value={woPriorityFilter}>
				<Select.Trigger class="w-36 shrink-0" data-testid="work-order-priority-filter">
					{woPriorityFilter ? woPriorityFilter : 'All priorities'}
				</Select.Trigger>
				<Select.Content>
					<Select.Item value="" label="All priorities">All priorities</Select.Item>
					{#each WO_PRIORITIES as p}
						<Select.Item value={p} label={p}>{p}</Select.Item>
					{/each}
				</Select.Content>
			</Select.Root>
			<Button data-testid="work-order-create-button-toolbar" class="shrink-0" onclick={openCreateWo}>
				<Plus class="h-4 w-4" /> New work order
			</Button>
		{/snippet}
	</DataGrid>

	<!-- Inspections section (unchanged) -->
	<div class="mt-6">
		<Card.Root class="gap-0 py-0">
			<Card.Header class="border-b border-border px-4 py-3">
				<Card.Title class="text-base font-semibold">Inspections</Card.Title>
			</Card.Header>
			<Card.Content class="max-h-[55vh] space-y-2 overflow-y-auto p-3" data-testid="inspections-list">
				{#if (inspectionsQuery.data || []).length === 0}
					<p class="py-6 text-center text-sm text-muted-foreground" data-testid="inspections-empty">No inspections scheduled yet.</p>
				{/if}
				{#each inspectionsQuery.data || [] as inspection (inspection.id)}
					<button
						type="button"
						class="block w-full rounded border border-border bg-background p-3 text-left text-sm transition-colors hover:bg-accent focus:outline-none focus-visible:ring-2 focus-visible:ring-ring"
						data-testid="inspection-row"
						onclick={() => goto('/maintenance/inspections/' + inspection.id)}
					>
						<div class="flex items-center justify-between gap-2">
							<p class="font-medium">{inspection.type} · {inspection.propertyName}</p>
							<StatusBadge status={inspection.status} />
						</div>
						<p class="font-mono tabular-nums text-xs text-muted-foreground">{new Date(inspection.scheduledFor).toLocaleString()}</p>
					</button>
				{/each}
			</Card.Content>
		</Card.Root>
	</div>
</div>

<!-- Work order create/edit dialog -->
<Dialog.Root
	open={showWoForm}
	onOpenChange={(v) => { if (!v) closeWoForm(); }}
>
	<Dialog.Content class="max-w-lg">
		<Dialog.Header>
			<Dialog.Title>{editingWoId == null ? 'New Work Order' : 'Edit Work Order'}</Dialog.Title>
		</Dialog.Header>
		<div class="space-y-2" data-testid="work-order-form">
			<div>
				<Select.Root
					type="single"
					value={woForm.propertyId}
					onValueChange={(v) => {
						woForm.propertyId = v;
						// A unit belongs to one property — clear a stale selection when the property changes.
						woForm.unitId = '';
					}}
				>
					<Select.Trigger class="w-full" data-testid="work-order-property-input">
						{woForm.propertyId
							? ((propertiesQuery.data || []).find((p) => String(p.id) === woForm.propertyId)?.name ?? 'Select property')
							: 'Select property'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="" label="Select property">Select property</Select.Item>
						{#each propertiesQuery.data || [] as property}
							<Select.Item value={String(property.id)} label={property.name}>{property.name}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
				{#if woErrors.propertyId}<p class="mt-1 text-xs text-destructive" data-testid="work-order-property-error">{woErrors.propertyId}</p>{/if}
			</div>
			<div>
				<Input data-testid="work-order-title-input" bind:value={woForm.title} placeholder="Issue title" />
				{#if woErrors.title}<p class="mt-1 text-xs text-destructive" data-testid="work-order-title-error">{woErrors.title}</p>{/if}
			</div>
			<div>
				<textarea data-testid="work-order-description-input" bind:value={woForm.description} rows={3} class="rounded border border-border bg-background px-3 py-2 text-sm w-full" placeholder="Description"></textarea>
				{#if woErrors.description}<p class="mt-1 text-xs text-destructive" data-testid="work-order-description-error">{woErrors.description}</p>{/if}
			</div>
			<div class="grid grid-cols-2 gap-2">
				<Select.Root type="single" bind:value={woForm.priority}>
					<Select.Trigger class="w-full" data-testid="work-order-priority-input">
						{woForm.priority || 'Priority'}
					</Select.Trigger>
					<Select.Content>
						{#each WO_PRIORITIES as p}
							<Select.Item value={p} label={p}>{p}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
				<Input data-testid="work-order-category-input" bind:value={woForm.category} placeholder="Category" />
			</div>

			<!-- Unit (filtered to the selected property) -->
			<div>
				<label for="work-order-unit" class="mb-1 block text-xs font-medium text-muted-foreground">Unit (optional)</label>
				<Select.Root type="single" bind:value={woForm.unitId} disabled={woPropertyId == null}>
					<Select.Trigger id="work-order-unit" class="w-full" data-testid="work-order-unit-input">
						{woForm.unitId
							? ((woUnitsQuery.data || []).find((u) => String(u.id) === woForm.unitId)?.unitNumber ?? 'Select unit')
							: woPropertyId == null
								? 'Select a property first'
								: 'No specific unit'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="" label="No specific unit">No specific unit</Select.Item>
						{#each woUnitsQuery.data || [] as unit (unit.id)}
							<Select.Item value={String(unit.id)} label={unit.unitNumber}>Unit {unit.unitNumber}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
			</div>

			<!-- Scheduled visit + arrival window -->
			<div class="grid grid-cols-2 gap-2">
				<div>
					<label for="work-order-scheduled" class="mb-1 block text-xs font-medium text-muted-foreground">Scheduled start</label>
					<Input id="work-order-scheduled" data-testid="work-order-scheduled-input" type="datetime-local" bind:value={woForm.scheduledFor} />
					{#if woErrors.scheduledFor}<p class="mt-1 text-xs text-destructive" data-testid="work-order-scheduled-error">{woErrors.scheduledFor}</p>{/if}
				</div>
				<div>
					<label for="work-order-window-end" class="mb-1 block text-xs font-medium text-muted-foreground">Arrival window end</label>
					<Input id="work-order-window-end" data-testid="work-order-window-end-input" type="datetime-local" bind:value={woForm.scheduledWindowEnd} />
					{#if woErrors.scheduledWindowEnd}<p class="mt-1 text-xs text-destructive" data-testid="work-order-window-end-error">{woErrors.scheduledWindowEnd}</p>{/if}
				</div>
			</div>

			<!-- Tenant + vendor (optional) -->
			<div class="grid grid-cols-2 gap-2">
				<div>
					<label for="work-order-tenant" class="mb-1 block text-xs font-medium text-muted-foreground">Tenant (optional)</label>
					<Select.Root type="single" bind:value={woForm.tenantId}>
						<Select.Trigger id="work-order-tenant" class="w-full" data-testid="work-order-tenant-input">
							{woForm.tenantId
								? ((tenantsQuery.data || []).find((t) => String(t.id) === woForm.tenantId)?.fullName
									?? (() => {
										const t = (tenantsQuery.data || []).find((t) => String(t.id) === woForm.tenantId);
										return t ? `${t.firstName} ${t.lastName}`.trim() : 'No tenant';
									})())
								: 'No tenant'}
						</Select.Trigger>
						<Select.Content>
							<Select.Item value="" label="No tenant">No tenant</Select.Item>
							{#each tenantsQuery.data || [] as tenant (tenant.id)}
								<Select.Item value={String(tenant.id)} label={tenant.fullName ?? `${tenant.firstName} ${tenant.lastName}`.trim()}>
									{tenant.fullName ?? `${tenant.firstName} ${tenant.lastName}`.trim()}
								</Select.Item>
							{/each}
						</Select.Content>
					</Select.Root>
				</div>
				<div>
					<label for="work-order-vendor" class="mb-1 block text-xs font-medium text-muted-foreground">Vendor (optional)</label>
					<Select.Root type="single" bind:value={woForm.vendorId}>
						<Select.Trigger id="work-order-vendor" class="w-full" data-testid="work-order-vendor-input">
							{woForm.vendorId
								? ((vendorsQuery.data || []).find((v) => String(v.id) === woForm.vendorId)?.name ?? 'No vendor')
								: 'No vendor'}
						</Select.Trigger>
						<Select.Content>
							<Select.Item value="" label="No vendor">No vendor</Select.Item>
							{#each vendorsQuery.data || [] as vendor (vendor.id)}
								<Select.Item value={String(vendor.id)} label={vendor.name}>{vendor.name}</Select.Item>
							{/each}
						</Select.Content>
					</Select.Root>
				</div>
			</div>

			<!-- Estimated cost (optional) -->
			<div>
				<label for="work-order-est-cost" class="mb-1 block text-xs font-medium text-muted-foreground">Estimated cost (optional)</label>
				<Input id="work-order-est-cost" data-testid="work-order-estimated-cost-input" type="number" min="0" step="0.01" bind:value={woForm.estimatedCost} placeholder="0.00" />
				{#if woErrors.estimatedCost}<p class="mt-1 text-xs text-destructive" data-testid="work-order-estimated-cost-error">{woErrors.estimatedCost}</p>{/if}
			</div>
		</div>
		<Dialog.Footer>
			<Button data-testid="work-order-form-cancel" variant="outline" onclick={closeWoForm}>Cancel</Button>
			<Button data-testid="work-order-form-save" onclick={submitWo} disabled={saveWoMutation.isPending}>{saveWoMutation.isPending ? 'Saving…' : 'Save work order'}</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<!-- Inspection create dialog -->
<Dialog.Root
	open={showInspectionForm}
	onOpenChange={(v) => { if (!v) showInspectionForm = false; }}
>
	<Dialog.Content class="max-w-md">
		<Dialog.Header>
			<Dialog.Title>Schedule Inspection</Dialog.Title>
		</Dialog.Header>
		<div class="space-y-2" data-testid="inspection-form">
			<div>
				<Select.Root type="single" bind:value={inspectionForm.propertyId}>
					<Select.Trigger class="w-full" data-testid="inspection-property-input">
						{inspectionForm.propertyId
							? ((propertiesQuery.data || []).find((p) => String(p.id) === inspectionForm.propertyId)?.name ?? 'Select property')
							: 'Select property'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="" label="Select property">Select property</Select.Item>
						{#each propertiesQuery.data || [] as property}
							<Select.Item value={String(property.id)} label={property.name}>{property.name}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
				{#if inspectionErrors.propertyId}<p class="mt-1 text-xs text-destructive" data-testid="inspection-property-error">{inspectionErrors.propertyId}</p>{/if}
			</div>
			<Select.Root type="single" bind:value={inspectionForm.type}>
				<Select.Trigger class="w-full" data-testid="inspection-type-input">
					{inspectionForm.type || 'Select type'}
				</Select.Trigger>
				<Select.Content>
					<Select.Item value="Routine" label="Routine">Routine</Select.Item>
					<Select.Item value="MoveIn" label="MoveIn">MoveIn</Select.Item>
					<Select.Item value="MoveOut" label="MoveOut">MoveOut</Select.Item>
					<Select.Item value="AnnualSafety" label="AnnualSafety">AnnualSafety</Select.Item>
				</Select.Content>
			</Select.Root>
			<div>
				<label for="inspection-template" class="mb-1 block text-xs font-medium text-muted-foreground">Checklist (optional)</label>
				<Select.Root
					type="single"
					value={inspectionForm.templateId}
					onValueChange={(v) => {
						inspectionForm.templateId = v;
						// Match the inspection type to the chosen checklist for a sensible default.
						const tpl = templateOptions.find((t) => String(t.id) === v);
						if (tpl) inspectionForm.type = tpl.inspectionType;
					}}
				>
					<Select.Trigger id="inspection-template" class="w-full" data-testid="inspection-template-input">
						{selectedTemplateName}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="" label="No checklist (blank)">No checklist (blank)</Select.Item>
						{#each templateOptions as tpl (tpl.id)}
							<Select.Item value={String(tpl.id)} label={tpl.name}>
								{tpl.name}{tpl.isBuiltIn ? ' · built-in' : ''}
							</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
				<p class="mt-1 text-xs text-muted-foreground">Pick a checklist to load the items for this inspection.</p>
			</div>
			<div>
				<label for="inspection-inspector" class="mb-1 block text-xs font-medium text-muted-foreground">Inspector (optional)</label>
				<Input id="inspection-inspector" data-testid="inspection-inspector-input" bind:value={inspectionForm.inspector} placeholder="Who's doing this inspection?" />
			</div>
			<div>
				<label for="inspection-scheduled" class="mb-1 block text-xs font-medium text-muted-foreground">Scheduled for</label>
				<Input id="inspection-scheduled" data-testid="inspection-scheduled-input" type="datetime-local" bind:value={inspectionForm.scheduledFor} />
				{#if inspectionErrors.scheduledFor}<p class="mt-1 text-xs text-destructive" data-testid="inspection-scheduled-error">{inspectionErrors.scheduledFor}</p>{/if}
			</div>
		</div>
		<Dialog.Footer>
			<Button data-testid="inspection-form-cancel" variant="outline" onclick={() => (showInspectionForm = false)}>Cancel</Button>
			<Button data-testid="inspection-form-save" onclick={submitInspection} disabled={createInspectionMutation.isPending}>{createInspectionMutation.isPending ? 'Scheduling…' : 'Schedule'}</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<ConfirmDialog
	open={woDeleteTarget !== null}
	title="Delete work order"
	message={woDeleteTarget ? `Delete "${woDeleteTarget.title}"?` : ''}
	busy={deleteWoMutation.isPending}
	testid="work-order-delete"
	onconfirm={() => woDeleteTarget && deleteWoMutation.mutate(woDeleteTarget.id)}
	oncancel={() => (woDeleteTarget = null)}
/>
