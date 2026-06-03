<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/stores';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { ArrowLeft, Pencil, Save, Trash2, X } from '@lucide/svelte';
	import { workOrders } from '$lib/api/endpoints/workOrders';
	import { properties } from '$lib/api/endpoints/properties';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { workOrderSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';

	const queryClient = useQueryClient();
	const workOrderId = $derived(parseInt($page.params.id ?? '0', 10));
	const portfolioId = $derived(getCurrentPortfolioId());
	const PRIORITIES = ['Low', 'Normal', 'High', 'Emergency'];
	const STATUSES = ['New', 'Scheduled', 'InProgress', 'WaitingParts', 'Completed', 'Cancelled'];

	let editing = $state(false);
	let form = $state({
		propertyId: '',
		title: '',
		description: '',
		priority: 'Normal',
		status: 'New',
		category: ''
	});
	let formErrors = $state<Record<string, string>>({});

	const workOrderQuery = createQuery(() => ({
		queryKey: ['work-order', workOrderId],
		queryFn: () => workOrders.get(workOrderId),
		enabled: workOrderId > 0
	}));
	const propertiesQuery = createQuery(() => ({ queryKey: ['properties', portfolioId], queryFn: () => properties.list(portfolioId, { take: 200 }) }));

	const workOrder = $derived(workOrderQuery.data);
	const priorityOptions = $derived(PRIORITIES.map((value) => ({ value, label: value })));
	const statusOptions = $derived(STATUSES.map((value) => ({ value, label: value })));
	const propertyOptions = $derived([{ value: '', label: 'Select property' }, ...(propertiesQuery.data ?? []).map((p) => ({ value: String(p.id), label: p.name }))]);

	function startEditing() {
		if (!workOrder) return;
		form = {
			propertyId: String(workOrder.propertyId),
			title: workOrder.title,
			description: workOrder.description,
			priority: workOrder.priority,
			status: workOrder.status,
			category: workOrder.category
		};
		formErrors = {};
		editing = true;
	}

	function cancelEditing() {
		editing = false;
		formErrors = {};
	}

	const saveMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => workOrders.update(workOrderId, data),
		onSuccess: () => {
			showSuccess('Work order updated.');
			editing = false;
			queryClient.invalidateQueries({ queryKey: ['work-order', workOrderId] });
			queryClient.invalidateQueries({ queryKey: ['work-orders', portfolioId] });
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	function saveWorkOrder() {
		const result = parseForm(workOrderSchema, form);
		if (result.errors) {
			formErrors = result.errors;
			return;
		}
		formErrors = {};
		saveMutation.mutate({ portfolioId, ...result.data, status: form.status });
	}

	const deleteMutation = createMutation(() => ({
		mutationFn: () => workOrders.delete(workOrderId),
		onSuccess: () => {
			showSuccess('Work order deleted.');
			goto('/maintenance');
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));
</script>

<svelte:head>
	<title>{workOrder?.title ?? 'Work Order'} - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="work-order-detail-page">
	<div class="mb-5 flex flex-wrap items-start justify-between gap-3">
		<div class="min-w-0">
			<Button variant="ghost" href="/maintenance" class="mb-2 -ml-3"><ArrowLeft class="h-4 w-4" />Maintenance</Button>
			<h1 class="truncate text-2xl font-bold">{workOrder?.title ?? 'Work Order'}</h1>
			<p class="text-sm text-muted-foreground">{workOrder?.propertyName ?? ''}{#if workOrder?.status} · {workOrder.status}{/if}</p>
		</div>
		{#if workOrder}
			<div class="flex gap-2">
				{#if editing}
					<Button variant="outline" onclick={cancelEditing} disabled={saveMutation.isPending}><X class="h-4 w-4" />Cancel</Button>
					<Button onclick={saveWorkOrder} disabled={saveMutation.isPending}><Save class="h-4 w-4" />{saveMutation.isPending ? 'Saving...' : 'Save'}</Button>
				{:else}
					<Button variant="outline" onclick={startEditing}><Pencil class="h-4 w-4" />Edit</Button>
					<Button variant="destructive" onclick={() => deleteMutation.mutate()} disabled={deleteMutation.isPending}><Trash2 class="h-4 w-4" />Delete</Button>
				{/if}
			</div>
		{/if}
	</div>

	{#if workOrderQuery.isLoading}
		<div class="rounded-lg border border-border bg-card p-6 text-sm text-muted-foreground">Loading work order...</div>
	{:else if !workOrder}
		<div class="rounded-lg border border-border bg-card p-6 text-sm text-muted-foreground">Work order not found.</div>
	{:else}
		<Card.Root>
			<Card.Header>
				<Card.Title>Work Order Details</Card.Title>
				<Card.Description>Issue, property, status, and dispatch fields.</Card.Description>
			</Card.Header>
			<Card.Content>
				<div class="grid gap-4 md:grid-cols-3">
					<InlineField label="Issue title" bind:value={form.title} display={workOrder.title} {editing} error={formErrors.title} testid="work-order-detail-title" class="md:col-span-2" />
					<InlineField label="Status" bind:value={form.status} display={workOrder.status} {editing} type="select" options={statusOptions} testid="work-order-detail-status" />
					<InlineField label="Property" bind:value={form.propertyId} display={workOrder.propertyName} {editing} type="select" options={propertyOptions} error={formErrors.propertyId} testid="work-order-detail-property" />
					<InlineField label="Priority" bind:value={form.priority} display={workOrder.priority} {editing} type="select" options={priorityOptions} testid="work-order-detail-priority" />
					<InlineField label="Category" bind:value={form.category} display={workOrder.category} {editing} error={formErrors.category} testid="work-order-detail-category" />
					<InlineField label="Description" bind:value={form.description} display={workOrder.description} {editing} type="textarea" error={formErrors.description} testid="work-order-detail-description" class="md:col-span-3" />
				</div>
			</Card.Content>
		</Card.Root>
	{/if}
</div>
