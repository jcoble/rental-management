<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { workOrders } from '$lib/api/endpoints/workOrders';
	import { inspections } from '$lib/api/endpoints/inspections';
	import { properties } from '$lib/api/endpoints/properties';
	import type { WorkOrder } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { workOrderSchema, inspectionSchema, parseForm } from '$lib/schemas';
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

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const WO_STATUSES = ['New', 'Scheduled', 'InProgress', 'WaitingParts', 'Completed', 'Cancelled'];
	const WO_PRIORITIES = ['Low', 'Normal', 'High', 'Emergency'];

	let woSearch = $state('');
	let woStatusFilter = $state('');
	let woPriorityFilter = $state('');
	const debouncedWoSearch = debounced(() => woSearch, 300);

	const workOrdersQuery = createQuery(() => ({
		queryKey: ['work-orders', portfolioId, debouncedWoSearch.value],
		queryFn: () => workOrders.list(portfolioId, { search: debouncedWoSearch.value, take: 100 }),
	}));
	const inspectionsQuery = createQuery(() => ({ queryKey: ['inspections', portfolioId], queryFn: () => inspections.list(portfolioId) }));
	const propertiesQuery = createQuery(() => ({ queryKey: ['properties', portfolioId], queryFn: () => properties.list(portfolioId, { take: 200 }) }));

	// --- Work order form/dialog ---
	const emptyWo = { propertyId: '', title: '', description: '', priority: 'Normal', category: 'General' };
	let showWoForm = $state(false);
	let editingWoId = $state<number | null>(null);
	let woForm = $state({ ...emptyWo });
	let woErrors = $state<Record<string, string>>({});
	let woDeleteTarget = $state<WorkOrder | null>(null);

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
		woForm = { propertyId: String(wo.propertyId), title: wo.title, description: wo.description, priority: wo.priority, category: wo.category };
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
		woErrors = {};
		saveWoMutation.mutate({ id: editingWoId, data: { portfolioId, ...result.data } });
	}

	// --- Inspection form/dialog ---
	const emptyInspection = { propertyId: '', type: 'Routine', scheduledFor: '' };
	let showInspectionForm = $state(false);
	let inspectionForm = $state({ ...emptyInspection });
	let inspectionErrors = $state<Record<string, string>>({});

	const createInspectionMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => inspections.create(data),
		onSuccess: () => {
			showSuccess('Inspection scheduled.');
			showInspectionForm = false;
			inspectionForm = { ...emptyInspection };
			queryClient.invalidateQueries({ queryKey: ['inspections', portfolioId] });
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
	<title>Maintenance - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="maintenance-page">
	<div class="mb-6 flex items-center justify-between gap-3">
		<div>
			<h1 class="text-2xl font-bold">Maintenance & Inspections</h1>
			<p class="text-sm text-muted-foreground">Track resident requests, vendor execution, and compliance checks.</p>
		</div>
		<div class="flex gap-2">
			<Button data-testid="work-order-create-button" onclick={openCreateWo}><Plus class="h-4 w-4" /> Work Order</Button>
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
				{#each inspectionsQuery.data || [] as inspection (inspection.id)}
					<div class="rounded border border-border bg-background p-3 text-sm" data-testid="inspection-row">
						<div class="flex items-center justify-between">
							<p class="font-medium">{inspection.type} · {inspection.propertyName}</p>
							<StatusBadge status={inspection.status} />
						</div>
						<p class="font-mono tabular-nums text-xs text-muted-foreground">{new Date(inspection.scheduledFor).toLocaleString()}</p>
					</div>
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
				<Select.Root type="single" bind:value={woForm.propertyId}>
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
		</div>
		<Dialog.Footer>
			<Button data-testid="work-order-form-cancel" variant="outline" onclick={closeWoForm}>Cancel</Button>
			<Button data-testid="work-order-form-save" onclick={submitWo} disabled={saveWoMutation.isPending}>{saveWoMutation.isPending ? 'Saving…' : 'Save Work Order'}</Button>
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
				<Input data-testid="inspection-scheduled-input" type="datetime-local" bind:value={inspectionForm.scheduledFor} />
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
