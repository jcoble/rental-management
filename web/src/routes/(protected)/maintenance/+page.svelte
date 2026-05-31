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
			<Button data-testid="work-order-create-button" onclick={openCreateWo}><Plus class="h-4 w-4" /> Work Order</Button>
			<Button data-testid="inspection-create-button" variant="outline" onclick={() => (showInspectionForm = true)}><ShieldCheck class="h-4 w-4" /> Inspection</Button>
		</div>
	</div>

	<div class="mb-4 flex flex-wrap items-center gap-3">
		<div class="max-w-sm flex-1"><SearchInput bind:value={woSearch} placeholder="Search work orders…" testid="work-order-search" /></div>
		<Select.Root type="single" bind:value={woStatusFilter}>
			<Select.Trigger class="w-40" data-testid="work-order-status-filter">
				{woStatusFilter ? woStatusFilter : 'All statuses'}
			</Select.Trigger>
			<Select.Content>
				<Select.Item value="" label="All statuses">All statuses</Select.Item>
				{#each WO_STATUSES as s}
					<Select.Item value={s} label={s}>{s}</Select.Item>
				{/each}
			</Select.Content>
		</Select.Root>
	</div>

	<div class="grid gap-4 lg:grid-cols-2">
		<Card.Root class="gap-0 py-0">
			<Card.Header class="border-b border-border px-4 py-3">
				<Card.Title class="text-base font-semibold">Work Orders</Card.Title>
			</Card.Header>
			<Card.Content class="max-h-[55vh] space-y-2 overflow-y-auto p-3" data-testid="work-orders-list">
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
								<Button data-testid="work-order-set-progress" variant="outline" size="sm" onclick={() => woStatusMutation.mutate({ id: wo.id, status: 'InProgress' })}>In Progress</Button>
								<Button data-testid="work-order-set-complete" variant="outline" size="sm" onclick={() => woStatusMutation.mutate({ id: wo.id, status: 'Completed' })}>Complete</Button>
								<Button data-testid="work-order-edit" variant="outline" size="icon" aria-label="Edit work order" onclick={() => openEditWo(wo)}><Pencil class="h-3.5 w-3.5" /></Button>
								<Button data-testid="work-order-delete" variant="outline" size="icon" aria-label="Delete work order" class="hover:text-destructive" onclick={() => (woDeleteTarget = wo)}><Trash2 class="h-3.5 w-3.5" /></Button>
							</div>
						</div>
					{/each}
				{/if}
			</Card.Content>
		</Card.Root>

		<Card.Root class="gap-0 py-0">
			<Card.Header class="border-b border-border px-4 py-3">
				<Card.Title class="text-base font-semibold">Inspections</Card.Title>
			</Card.Header>
			<Card.Content class="max-h-[55vh] space-y-2 overflow-y-auto p-3" data-testid="inspections-list">
				{#each inspectionsQuery.data || [] as inspection (inspection.id)}
					<div class="rounded border border-border bg-background p-3 text-sm" data-testid="inspection-row">
						<div class="flex items-center justify-between">
							<p class="font-medium">{inspection.type} · {inspection.propertyName}</p>
							<span class="text-xs">{inspection.status}</span>
						</div>
						<p class="text-xs text-muted-foreground">{new Date(inspection.scheduledFor).toLocaleString()}</p>
					</div>
				{/each}
			</Card.Content>
		</Card.Root>
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
