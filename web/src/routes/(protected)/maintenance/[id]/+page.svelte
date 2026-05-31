<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { page } from '$app/stores';
	import { workOrders } from '$lib/api/endpoints/workOrders';
	import { properties } from '$lib/api/endpoints/properties';
	import type { WorkOrder } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { workOrderSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import { Pencil, Trash2 } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const id = $derived(Number($page.params.id));

	const WO_STATUSES = ['New', 'Scheduled', 'InProgress', 'WaitingParts', 'Completed', 'Cancelled'];
	const WO_PRIORITIES = ['Low', 'Normal', 'High', 'Emergency'];

	// Status transitions: map current status → allowed next statuses
	const STATUS_TRANSITIONS: Record<string, string[]> = {
		New: ['Scheduled', 'InProgress', 'Cancelled'],
		Scheduled: ['InProgress', 'WaitingParts', 'Cancelled'],
		InProgress: ['WaitingParts', 'Completed', 'Cancelled'],
		WaitingParts: ['InProgress', 'Completed', 'Cancelled'],
		Completed: [],
		Cancelled: ['New'],
	};

	const workOrderQuery = createQuery(() => ({
		queryKey: ['work-order', id],
		queryFn: () => workOrders.get(id),
		enabled: !isNaN(id) && id > 0,
	}));

	const propertiesQuery = createQuery(() => ({
		queryKey: ['properties', portfolioId],
		queryFn: () => properties.list(portfolioId, { take: 200 }),
	}));

	const wo = $derived(workOrderQuery.data);

	// --- Edit form/dialog ---
	const emptyWo = { propertyId: '', title: '', description: '', priority: 'Normal', category: 'General' };
	let showEditForm = $state(false);
	let woForm = $state({ ...emptyWo });
	let woErrors = $state<Record<string, string>>({});
	let showDeleteConfirm = $state(false);

	function openEdit() {
		if (!wo) return;
		woForm = {
			propertyId: String(wo.propertyId),
			title: wo.title,
			description: wo.description,
			priority: wo.priority,
			category: wo.category,
		};
		woErrors = {};
		showEditForm = true;
	}
	function closeEdit() {
		showEditForm = false;
		woErrors = {};
	}
	function submitEdit() {
		const result = parseForm(workOrderSchema, woForm);
		if (result.errors) {
			woErrors = result.errors;
			return;
		}
		woErrors = {};
		saveMutation.mutate({ id, data: { portfolioId, ...result.data } });
	}

	function invalidate() {
		queryClient.invalidateQueries({ queryKey: ['work-order', id] });
		queryClient.invalidateQueries({ queryKey: ['work-orders', portfolioId] });
	}

	const saveMutation = createMutation(() => ({
		mutationFn: ({ id: woId, data }: { id: number; data: Record<string, unknown> }) =>
			workOrders.update(woId, data),
		onSuccess: () => {
			showSuccess('Work order updated.');
			closeEdit();
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const statusMutation = createMutation(() => ({
		mutationFn: ({ id: woId, status }: { id: number; status: string }) =>
			workOrders.updateStatus(woId, status),
		onSuccess: () => {
			showSuccess('Status updated.');
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const deleteMutation = createMutation(() => ({
		mutationFn: (woId: number) => workOrders.delete(woId),
		onSuccess: () => {
			showSuccess('Work order deleted.');
			goto('/maintenance');
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const availableTransitions = $derived(
		wo ? (STATUS_TRANSITIONS[wo.status] ?? []) : []
	);

	function formatDate(val: string | undefined | null): string {
		if (!val) return '—';
		const d = new Date(val);
		return isNaN(d.getTime()) ? val : d.toLocaleDateString();
	}

	function formatCurrency(val: number | undefined | null): string {
		if (val == null) return '—';
		return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(val);
	}
</script>

<svelte:head>
	<title>{wo?.title ?? 'Work Order'} - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6" data-testid="work-order-detail-page">
	<div class="mb-4">
		<PageBreadcrumb
			crumbs={[
				{ label: 'Maintenance', href: '/maintenance' },
				{ label: wo?.title ?? 'Work Order' },
			]}
		/>
	</div>

	{#if workOrderQuery.isLoading}
		<p class="py-8 text-center text-sm text-muted-foreground" data-testid="work-order-detail-loading">Loading…</p>
	{:else if workOrderQuery.isError}
		<p class="py-8 text-center text-sm text-destructive" data-testid="work-order-detail-error">Failed to load work order.</p>
	{:else if !wo}
		<p class="py-8 text-center text-sm text-muted-foreground" data-testid="work-order-detail-not-found">Work order not found.</p>
	{:else}
		<!-- Header -->
		<div class="mb-6 flex flex-wrap items-start justify-between gap-4">
			<div class="space-y-2">
				<div class="flex flex-wrap items-center gap-2">
					<h1 class="text-2xl font-bold" data-testid="work-order-detail-title">{wo.title}</h1>
					<StatusBadge status={wo.status} />
					<StatusBadge status={wo.priority} />
				</div>
				<div class="flex flex-wrap items-center gap-x-4 gap-y-1 text-sm text-muted-foreground">
					{#if wo.propertyName}
						<span data-testid="work-order-detail-property">{wo.propertyName}</span>
					{/if}
					{#if wo.unitNumber}
						<span data-testid="work-order-detail-unit">Unit {wo.unitNumber}</span>
					{/if}
					{#if wo.vendorName}
						<span data-testid="work-order-detail-vendor">{wo.vendorName}</span>
					{/if}
				</div>
			</div>
			<div class="flex flex-wrap items-center gap-2">
				<!-- Status transition buttons -->
				{#each availableTransitions as nextStatus}
					<Button
						variant="outline"
						size="sm"
						disabled={statusMutation.isPending}
						data-testid="work-order-set-{nextStatus.toLowerCase()}"
						onclick={() => statusMutation.mutate({ id: wo.id, status: nextStatus })}
					>
						{nextStatus === 'InProgress' ? 'In Progress' : nextStatus}
					</Button>
				{/each}
				<Button
					variant="outline"
					size="sm"
					data-testid="work-order-edit"
					onclick={openEdit}
				>
					<Pencil class="h-4 w-4" />
					Edit
				</Button>
				<Button
					variant="outline"
					size="sm"
					class="hover:text-destructive"
					data-testid="work-order-delete"
					onclick={() => (showDeleteConfirm = true)}
				>
					<Trash2 class="h-4 w-4" />
					Delete
				</Button>
			</div>
		</div>

		<!-- Info Card -->
		<Card.Root>
			<Card.Header>
				<Card.Title>Details</Card.Title>
			</Card.Header>
			<Card.Content>
				<dl class="grid gap-x-8 gap-y-4 sm:grid-cols-2 lg:grid-cols-3">
					{#if wo.description}
						<div class="sm:col-span-2 lg:col-span-3">
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Description</dt>
							<dd class="mt-1 text-sm" data-testid="work-order-detail-description">{wo.description}</dd>
						</div>
					{/if}
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Category</dt>
						<dd class="mt-1 text-sm" data-testid="work-order-detail-category">{wo.category}</dd>
					</div>
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Requested</dt>
						<dd class="mt-1 font-mono text-sm tabular-nums" data-testid="work-order-detail-requested">{formatDate(wo.requestedAt)}</dd>
					</div>
					{#if wo.scheduledFor}
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Scheduled For</dt>
							<dd class="mt-1 font-mono text-sm tabular-nums" data-testid="work-order-detail-scheduled">{formatDate(wo.scheduledFor)}</dd>
						</div>
					{/if}
					{#if wo.completedAt}
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Completed</dt>
							<dd class="mt-1 font-mono text-sm tabular-nums" data-testid="work-order-detail-completed">{formatDate(wo.completedAt)}</dd>
						</div>
					{/if}
					{#if wo.estimatedCost != null}
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Estimated Cost</dt>
							<dd class="mt-1 font-mono text-sm tabular-nums" data-testid="work-order-detail-estimated-cost">{formatCurrency(wo.estimatedCost)}</dd>
						</div>
					{/if}
					{#if wo.actualCost != null}
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Actual Cost</dt>
							<dd class="mt-1 font-mono text-sm tabular-nums" data-testid="work-order-detail-actual-cost">{formatCurrency(wo.actualCost)}</dd>
						</div>
					{/if}
				</dl>
			</Card.Content>
		</Card.Root>
	{/if}
</div>

<!-- Edit dialog -->
{#if wo}
	<Dialog.Root
		open={showEditForm}
		onOpenChange={(v) => { if (!v) closeEdit(); }}
	>
		<Dialog.Content class="max-w-lg">
			<Dialog.Header>
				<Dialog.Title>Edit Work Order</Dialog.Title>
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
					<textarea data-testid="work-order-description-input" bind:value={woForm.description} rows={3} class="w-full rounded border border-border bg-background px-3 py-2 text-sm" placeholder="Description"></textarea>
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
				<Button data-testid="work-order-form-cancel" variant="outline" onclick={closeEdit}>Cancel</Button>
				<Button data-testid="work-order-form-save" onclick={submitEdit} disabled={saveMutation.isPending}>
					{saveMutation.isPending ? 'Saving…' : 'Save Work Order'}
				</Button>
			</Dialog.Footer>
		</Dialog.Content>
	</Dialog.Root>
{/if}

<ConfirmDialog
	open={showDeleteConfirm}
	title="Delete work order"
	message={wo ? `Delete "${wo.title}"?` : ''}
	busy={deleteMutation.isPending}
	testid="work-order-delete"
	onconfirm={() => wo && deleteMutation.mutate(wo.id)}
	oncancel={() => (showDeleteConfirm = false)}
/>
