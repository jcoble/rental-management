<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { workOrders } from '$lib/api/endpoints/workOrders';
	import { inspections } from '$lib/api/endpoints/inspections';
	import { properties } from '$lib/api/endpoints/properties';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { Plus, ShieldCheck } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	const workOrdersQuery = createQuery(() => ({
		queryKey: ['work-orders', portfolioId],
		queryFn: () => workOrders.list(portfolioId),
	}));

	const inspectionsQuery = createQuery(() => ({
		queryKey: ['inspections', portfolioId],
		queryFn: () => inspections.list(portfolioId),
	}));

	const propertiesQuery = createQuery(() => ({
		queryKey: ['properties', portfolioId],
		queryFn: () => properties.list(portfolioId),
	}));

	let showWorkOrderForm = $state(false);
	let woForm = $state({ propertyId: '', title: '', description: '', priority: 'Normal', category: 'General' });

	let showInspectionForm = $state(false);
	let inspectionForm = $state({ propertyId: '', type: 'Routine', scheduledFor: '' });

	const createWorkOrderMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => workOrders.create(data),
		onSuccess: () => {
			showWorkOrderForm = false;
			woForm = { propertyId: '', title: '', description: '', priority: 'Normal', category: 'General' };
			queryClient.invalidateQueries({ queryKey: ['work-orders', portfolioId] });
		},
	}));

	const createInspectionMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => inspections.create(data),
		onSuccess: () => {
			showInspectionForm = false;
			inspectionForm = { propertyId: '', type: 'Routine', scheduledFor: '' };
			queryClient.invalidateQueries({ queryKey: ['inspections', portfolioId] });
		},
	}));

	const statusMutation = createMutation(() => ({
		mutationFn: ({ id, status }: { id: number; status: string }) => workOrders.updateStatus(id, status),
		onSuccess: () => queryClient.invalidateQueries({ queryKey: ['work-orders', portfolioId] }),
	}));

	function submitWorkOrder() {
		if (!woForm.propertyId || !woForm.title || !woForm.description) return;
		createWorkOrderMutation.mutate({
			portfolioId,
			propertyId: Number(woForm.propertyId),
			title: woForm.title,
			description: woForm.description,
			priority: woForm.priority,
			category: woForm.category,
		});
	}

	function submitInspection() {
		if (!inspectionForm.propertyId || !inspectionForm.scheduledFor) return;
		createInspectionMutation.mutate({
			portfolioId,
			propertyId: Number(inspectionForm.propertyId),
			type: inspectionForm.type,
			scheduledFor: inspectionForm.scheduledFor,
		});
	}
</script>

<svelte:head>
	<title>Maintenance - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6">
	<div class="mb-4">
		<h1 class="text-2xl font-bold">Maintenance & Inspections</h1>
		<p class="text-sm text-text-secondary">Track resident requests, vendor execution, and compliance checks.</p>
	</div>

	<div class="mb-5 grid gap-4 lg:grid-cols-2">
		<div class="rounded-lg border border-border bg-surface p-4">
			<div class="mb-3 flex items-center justify-between">
				<h2 class="font-semibold">New Work Order</h2>
				<button class="rounded border border-border px-2 py-1 text-xs" onclick={() => (showWorkOrderForm = !showWorkOrderForm)}>
					<Plus class="inline h-3 w-3" /> Add
				</button>
			</div>
			{#if showWorkOrderForm}
				<div class="space-y-2">
					<select bind:value={woForm.propertyId} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm">
						<option value="">Select property</option>
						{#each propertiesQuery.data || [] as property}
							<option value={property.id}>{property.name}</option>
						{/each}
					</select>
					<input bind:value={woForm.title} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Issue title" />
					<textarea bind:value={woForm.description} rows={3} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Description"></textarea>
					<div class="grid grid-cols-2 gap-2">
						<select bind:value={woForm.priority} class="rounded border border-border bg-bg px-3 py-2 text-sm"><option>Low</option><option>Normal</option><option>High</option><option>Emergency</option></select>
						<input bind:value={woForm.category} class="rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Category" />
					</div>
					<button onclick={submitWorkOrder} class="rounded bg-accent px-3 py-2 text-sm text-white" disabled={createWorkOrderMutation.isPending}>Save Work Order</button>
				</div>
			{/if}
		</div>

		<div class="rounded-lg border border-border bg-surface p-4">
			<div class="mb-3 flex items-center justify-between">
				<h2 class="font-semibold">Schedule Inspection</h2>
				<button class="rounded border border-border px-2 py-1 text-xs" onclick={() => (showInspectionForm = !showInspectionForm)}>
					<ShieldCheck class="inline h-3 w-3" /> Add
				</button>
			</div>
			{#if showInspectionForm}
				<div class="space-y-2">
					<select bind:value={inspectionForm.propertyId} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm">
						<option value="">Select property</option>
						{#each propertiesQuery.data || [] as property}
							<option value={property.id}>{property.name}</option>
						{/each}
					</select>
					<select bind:value={inspectionForm.type} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm"><option>Routine</option><option>MoveIn</option><option>MoveOut</option><option>AnnualSafety</option></select>
					<input type="datetime-local" bind:value={inspectionForm.scheduledFor} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm" />
					<button onclick={submitInspection} class="rounded bg-accent px-3 py-2 text-sm text-white" disabled={createInspectionMutation.isPending}>Schedule</button>
				</div>
			{/if}
		</div>
	</div>

	<div class="grid gap-4 lg:grid-cols-2">
		<div class="rounded-lg border border-border bg-surface">
			<div class="border-b border-border px-4 py-3 font-semibold">Work Orders</div>
			<div class="max-h-[50vh] overflow-y-auto p-3 space-y-2">
				{#each workOrdersQuery.data || [] as wo}
					<div class="rounded border border-border bg-bg p-3 text-sm">
						<div class="flex items-center justify-between gap-2">
							<p class="font-medium">{wo.title}</p>
							<span class="text-xs">{wo.priority}</span>
						</div>
						<p class="text-xs text-text-secondary">{wo.propertyName} · {wo.category} · {wo.status}</p>
						<div class="mt-2 flex gap-2">
							<button class="rounded border border-border px-2 py-1 text-xs" onclick={() => statusMutation.mutate({ id: wo.id, status: 'InProgress' })}>In Progress</button>
							<button class="rounded border border-border px-2 py-1 text-xs" onclick={() => statusMutation.mutate({ id: wo.id, status: 'Completed' })}>Complete</button>
						</div>
					</div>
				{/each}
			</div>
		</div>

		<div class="rounded-lg border border-border bg-surface">
			<div class="border-b border-border px-4 py-3 font-semibold">Inspections</div>
			<div class="max-h-[50vh] overflow-y-auto p-3 space-y-2">
				{#each inspectionsQuery.data || [] as inspection}
					<div class="rounded border border-border bg-bg p-3 text-sm">
						<div class="flex items-center justify-between">
							<p class="font-medium">{inspection.type} · {inspection.propertyName}</p>
							<span class="text-xs">{inspection.status}</span>
						</div>
						<p class="text-xs text-text-secondary">{new Date(inspection.scheduledFor).toLocaleString()}</p>
					</div>
				{/each}
			</div>
		</div>
	</div>
</div>
