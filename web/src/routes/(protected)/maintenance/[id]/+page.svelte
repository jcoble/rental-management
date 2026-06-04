<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { page } from '$app/stores';
	import { workOrders } from '$lib/api/endpoints/workOrders';
	import { properties } from '$lib/api/endpoints/properties';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { workOrderSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import { Pencil, Save, Trash2, X } from '@lucide/svelte';
	import DocumentsPanel from '$lib/components/shared/DocumentsPanel.svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const id = $derived(Number($page.params.id));

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

	const priorityOptions = $derived(WO_PRIORITIES.map((value) => ({ value, label: value })));
	const propertyOptions = $derived(
		(propertiesQuery.data ?? []).map((p) => ({ value: String(p.id), label: p.name }))
	);

	// --- Inline edit ---
	let editing = $state(false);
	let form = $state({ propertyId: '', title: '', description: '', priority: 'Normal', category: 'General' });
	let formErrors = $state<Record<string, string>>({});
	let showDeleteConfirm = $state(false);

	function startEditing() {
		if (!wo) return;
		form = {
			propertyId: String(wo.propertyId),
			title: wo.title,
			description: wo.description,
			priority: wo.priority,
			category: wo.category,
		};
		formErrors = {};
		editing = true;
	}
	function cancelEditing() {
		editing = false;
		formErrors = {};
	}
	function save() {
		const result = parseForm(workOrderSchema, form);
		if (result.errors) {
			formErrors = result.errors;
			return;
		}
		formErrors = {};
		saveMutation.mutate({ portfolioId, ...result.data });
	}

	function invalidate() {
		queryClient.invalidateQueries({ queryKey: ['work-order', id] });
		queryClient.invalidateQueries({ queryKey: ['work-orders', portfolioId] });
	}

	const saveMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => workOrders.update(id, data),
		onSuccess: () => {
			showSuccess('Work order updated.');
			editing = false;
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

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="work-order-detail-page">
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
				{#if editing}
					<Button
						variant="outline"
						size="sm"
						data-testid="work-order-edit-cancel"
						onclick={cancelEditing}
						disabled={saveMutation.isPending}
					>
						<X class="h-4 w-4" />
						Cancel
					</Button>
					<Button
						size="sm"
						data-testid="work-order-edit-save"
						onclick={save}
						disabled={saveMutation.isPending}
					>
						<Save class="h-4 w-4" />
						{saveMutation.isPending ? 'Saving…' : 'Save'}
					</Button>
				{:else}
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
						onclick={startEditing}
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
				{/if}
			</div>
		</div>

		<!-- Info Card -->
		<Card.Root>
			<Card.Header>
				<Card.Title>Details</Card.Title>
			</Card.Header>
			<Card.Content>
				<div class="grid gap-x-8 gap-y-4 sm:grid-cols-2 lg:grid-cols-3">
					<InlineField label="Title" bind:value={form.title} display={wo.title} {editing} onedit={startEditing} error={formErrors.title} testid="work-order-detail-title-field" class="sm:col-span-2 lg:col-span-3" />
					<InlineField label="Description" bind:value={form.description} display={wo.description} {editing} onedit={startEditing} type="textarea" error={formErrors.description} testid="work-order-detail-description" class="sm:col-span-2 lg:col-span-3" />
					<InlineField label="Property" bind:value={form.propertyId} display={wo.propertyName} {editing} onedit={startEditing} type="select" options={propertyOptions} error={formErrors.propertyId} testid="work-order-detail-property-field" />
					<InlineField label="Priority" bind:value={form.priority} display={wo.priority} {editing} onedit={startEditing} type="select" options={priorityOptions} testid="work-order-detail-priority" />
					<InlineField label="Category" bind:value={form.category} display={wo.category} {editing} onedit={startEditing} error={formErrors.category} testid="work-order-detail-category" />
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
				</div>
			</Card.Content>
		</Card.Root>

		<!-- Documents section -->
		<div class="mt-6" data-testid="work-order-detail-documents">
			<DocumentsPanel entityType="WorkOrder" entityId={id} />
		</div>
	{/if}
</div>

<ConfirmDialog
	open={showDeleteConfirm}
	title="Delete work order"
	message={wo ? `Delete "${wo.title}"?` : ''}
	busy={deleteMutation.isPending}
	testid="work-order-delete"
	onconfirm={() => wo && deleteMutation.mutate(wo.id)}
	oncancel={() => (showDeleteConfirm = false)}
/>
