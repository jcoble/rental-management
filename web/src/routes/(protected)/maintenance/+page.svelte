<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
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
	import { Plus, ShieldCheck, Pencil, Trash2 } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const WO_STATUSES = ['New', 'Scheduled', 'InProgress', 'WaitingParts', 'Completed', 'Cancelled'];
	const WO_PRIORITIES = ['Low', 'Normal', 'High', 'Emergency'];

	let woSearch = $state('');
	let woStatusFilter = $state('');
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

	const woStatusMutation = createMutation(() => ({
		mutationFn: ({ id, status }: { id: number; status: string }) => workOrders.updateStatus(id, status),
		onSuccess: () => {
			showSuccess('Work order status updated.');
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

	const woList = $derived((workOrdersQuery.data ?? []).filter((w) => !woStatusFilter || w.status === woStatusFilter));
	const inputClass = 'rounded border border-border bg-background px-3 py-2 text-sm';
</script>

<svelte:head>
	<title>Maintenance - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6" data-testid="maintenance-page">
	<div class="mb-4 flex items-center justify-between gap-3">
		<div>
			<h1 class="text-2xl font-bold">Maintenance & Inspections</h1>
			<p class="text-sm text-muted-foreground">Track resident requests, vendor execution, and compliance checks.</p>
		</div>
		<div class="flex gap-2">
			<button data-testid="work-order-create-button" class="inline-flex items-center gap-2 rounded-md bg-primary px-3 py-2 text-sm text-white" onclick={openCreateWo}><Plus class="h-4 w-4" /> Work Order</button>
			<button data-testid="inspection-create-button" class="inline-flex items-center gap-2 rounded-md border border-border px-3 py-2 text-sm" onclick={() => (showInspectionForm = true)}><ShieldCheck class="h-4 w-4" /> Inspection</button>
		</div>
	</div>

	<div class="mb-4 flex flex-wrap items-center gap-3">
		<div class="max-w-sm flex-1"><SearchInput bind:value={woSearch} placeholder="Search work orders…" testid="work-order-search" /></div>
		<select data-testid="work-order-status-filter" bind:value={woStatusFilter} class="{inputClass} h-9">
			<option value="">All statuses</option>
			{#each WO_STATUSES as s}<option value={s}>{s}</option>{/each}
		</select>
	</div>

	<div class="grid gap-4 lg:grid-cols-2">
		<div class="rounded-lg border border-border bg-card">
			<div class="border-b border-border px-4 py-3 font-semibold">Work Orders</div>
			<div class="max-h-[55vh] space-y-2 overflow-y-auto p-3" data-testid="work-orders-list">
				{#if workOrdersQuery.isLoading}
					<p class="py-4 text-center text-sm text-muted-foreground" data-testid="work-orders-loading">Loading…</p>
				{:else if woList.length === 0}
					<p class="py-4 text-center text-sm text-muted-foreground" data-testid="work-orders-empty">No work orders found.</p>
				{:else}
					{#each woList as wo (wo.id)}
						<div class="rounded border border-border bg-background p-3 text-sm" data-testid="work-order-row" data-work-order-id={wo.id}>
							<div class="flex items-center justify-between gap-2">
								<p class="font-medium" data-testid="work-order-title">{wo.title}</p>
								<span class="text-xs">{wo.priority}</span>
							</div>
							<p class="text-xs text-muted-foreground">{wo.propertyName} · {wo.category} · {wo.status}</p>
							<div class="mt-2 flex flex-wrap gap-2">
								<button data-testid="work-order-set-progress" class="rounded border border-border px-2 py-1 text-xs" onclick={() => woStatusMutation.mutate({ id: wo.id, status: 'InProgress' })}>In Progress</button>
								<button data-testid="work-order-set-complete" class="rounded border border-border px-2 py-1 text-xs" onclick={() => woStatusMutation.mutate({ id: wo.id, status: 'Completed' })}>Complete</button>
								<button data-testid="work-order-edit" aria-label="Edit work order" class="rounded border border-border px-2 py-1 text-xs" onclick={() => openEditWo(wo)}><Pencil class="h-3.5 w-3.5" /></button>
								<button data-testid="work-order-delete" aria-label="Delete work order" class="rounded border border-border px-2 py-1 text-xs hover:text-destructive" onclick={() => (woDeleteTarget = wo)}><Trash2 class="h-3.5 w-3.5" /></button>
							</div>
						</div>
					{/each}
				{/if}
			</div>
		</div>

		<div class="rounded-lg border border-border bg-card">
			<div class="border-b border-border px-4 py-3 font-semibold">Inspections</div>
			<div class="max-h-[55vh] space-y-2 overflow-y-auto p-3" data-testid="inspections-list">
				{#each inspectionsQuery.data || [] as inspection (inspection.id)}
					<div class="rounded border border-border bg-background p-3 text-sm" data-testid="inspection-row">
						<div class="flex items-center justify-between">
							<p class="font-medium">{inspection.type} · {inspection.propertyName}</p>
							<span class="text-xs">{inspection.status}</span>
						</div>
						<p class="text-xs text-muted-foreground">{new Date(inspection.scheduledFor).toLocaleString()}</p>
					</div>
				{/each}
			</div>
		</div>
	</div>
</div>

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
				<select data-testid="work-order-property-input" bind:value={woForm.propertyId} class="{inputClass} w-full">
					<option value="">Select property</option>
					{#each propertiesQuery.data || [] as property}<option value={property.id}>{property.name}</option>{/each}
				</select>
				{#if woErrors.propertyId}<p class="mt-1 text-xs text-destructive" data-testid="work-order-property-error">{woErrors.propertyId}</p>{/if}
			</div>
			<div>
				<input data-testid="work-order-title-input" bind:value={woForm.title} class="{inputClass} w-full" placeholder="Issue title" />
				{#if woErrors.title}<p class="mt-1 text-xs text-destructive" data-testid="work-order-title-error">{woErrors.title}</p>{/if}
			</div>
			<div>
				<textarea data-testid="work-order-description-input" bind:value={woForm.description} rows={3} class="{inputClass} w-full" placeholder="Description"></textarea>
				{#if woErrors.description}<p class="mt-1 text-xs text-destructive" data-testid="work-order-description-error">{woErrors.description}</p>{/if}
			</div>
			<div class="grid grid-cols-2 gap-2">
				<select data-testid="work-order-priority-input" bind:value={woForm.priority} class={inputClass}>{#each WO_PRIORITIES as p}<option value={p}>{p}</option>{/each}</select>
				<input data-testid="work-order-category-input" bind:value={woForm.category} class={inputClass} placeholder="Category" />
			</div>
		</div>
		<Dialog.Footer>
			<button data-testid="work-order-form-cancel" class="rounded-md border border-border px-3 py-2 text-sm text-muted-foreground hover:bg-secondary" onclick={closeWoForm}>Cancel</button>
			<button data-testid="work-order-form-save" onclick={submitWo} class="rounded bg-primary px-3 py-2 text-sm text-white" disabled={saveWoMutation.isPending}>{saveWoMutation.isPending ? 'Saving…' : 'Save Work Order'}</button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

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
				<select data-testid="inspection-property-input" bind:value={inspectionForm.propertyId} class="{inputClass} w-full">
					<option value="">Select property</option>
					{#each propertiesQuery.data || [] as property}<option value={property.id}>{property.name}</option>{/each}
				</select>
				{#if inspectionErrors.propertyId}<p class="mt-1 text-xs text-destructive" data-testid="inspection-property-error">{inspectionErrors.propertyId}</p>{/if}
			</div>
			<select data-testid="inspection-type-input" bind:value={inspectionForm.type} class="{inputClass} w-full"><option>Routine</option><option>MoveIn</option><option>MoveOut</option><option>AnnualSafety</option></select>
			<div>
				<input data-testid="inspection-scheduled-input" type="datetime-local" bind:value={inspectionForm.scheduledFor} class="{inputClass} w-full" />
				{#if inspectionErrors.scheduledFor}<p class="mt-1 text-xs text-destructive" data-testid="inspection-scheduled-error">{inspectionErrors.scheduledFor}</p>{/if}
			</div>
		</div>
		<Dialog.Footer>
			<button data-testid="inspection-form-cancel" class="rounded-md border border-border px-3 py-2 text-sm text-muted-foreground hover:bg-secondary" onclick={() => (showInspectionForm = false)}>Cancel</button>
			<button data-testid="inspection-form-save" onclick={submitInspection} class="rounded bg-primary px-3 py-2 text-sm text-white" disabled={createInspectionMutation.isPending}>{createInspectionMutation.isPending ? 'Scheduling…' : 'Schedule'}</button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<ConfirmDialog
	open={woDeleteTarget !== null}
	title="Delete work order"
	message={woDeleteTarget ? `Delete “${woDeleteTarget.title}”?` : ''}
	busy={deleteWoMutation.isPending}
	testid="work-order-delete"
	onconfirm={() => woDeleteTarget && deleteWoMutation.mutate(woDeleteTarget.id)}
	oncancel={() => (woDeleteTarget = null)}
/>
