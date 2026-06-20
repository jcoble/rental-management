<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import type { WorkOrder, Expense } from '$lib/types';
	import { workOrders as workOrdersApi } from '$lib/api/endpoints/workOrders';
	import { expenses as expensesApi } from '$lib/api/endpoints/expenses';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { money } from '../money';
	import { formatDateOnly } from '$lib/utils/date';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Wrench, Receipt, Plus } from '@lucide/svelte';
	import type { UnitDrawerAction } from '../drawer-actions';

	let {
		unitId,
		onAction,
	}: {
		unitId: number;
		onAction: (action: UnitDrawerAction) => void;
	} = $props();

	const portfolioId = $derived(getCurrentPortfolioId());

	// Work orders for this unit (DB-side ?unitId= filter).
	const workOrdersQuery = createQuery(() => ({
		queryKey: ['unit-work-orders', portfolioId, unitId],
		enabled: portfolioId > 0 && unitId > 0,
		queryFn: () => workOrdersApi.list(portfolioId, { unitId, take: 500 }),
	}));

	// Expenses tied to this unit OR its work orders (DB-side correlated filter). We surface the
	// work-order-linked ones here as "receipts on these jobs"; the Expenses tab shows the full set.
	const expensesQuery = createQuery(() => ({
		queryKey: ['unit-expenses', portfolioId, unitId],
		enabled: portfolioId > 0 && unitId > 0,
		queryFn: () => expensesApi.list(portfolioId, { unitId, take: 500 }),
	}));

	const workOrderList = $derived(workOrdersQuery.data ?? []);
	const workOrderReceipts = $derived((expensesQuery.data ?? []).filter((e) => e.workOrderId != null));

	const columns: ColumnDef<WorkOrder>[] = [
		{ key: 'title', title: 'Work order', sortable: true, mobileRole: 'title' },
		{ key: 'priority', title: 'Priority', mobileRole: 'meta', cell: priorityCell },
		{ key: 'status', title: 'Status', mobileRole: 'badge', cell: statusCell },
		{ key: 'requestedAt', title: 'Requested', format: 'date', sortable: true, mobileRole: 'meta' },
		{ key: 'actualCost', title: 'Cost', format: 'currency', mobileRole: 'metric', accessor: (w) => w.actualCost ?? w.estimatedCost ?? 0 },
	];
</script>

{#snippet statusCell(w: WorkOrder)}
	<StatusBadge status={w.status} />
{/snippet}
{#snippet priorityCell(w: WorkOrder)}
	<StatusBadge status={w.priority} />
{/snippet}

<div class="space-y-4" data-testid="unit-maintenance-tab">
	<div class="flex flex-wrap justify-end gap-2">
		<Button class="gap-2" onclick={() => onAction('create-work-order')} data-testid="maintenance-create">
			<Plus class="h-4 w-4" /> New work order
		</Button>
	</div>

	<DataGrid
		data={workOrderList}
		{columns}
		loading={workOrdersQuery.isLoading}
		emptyMessage="No work orders"
		emptyDescription="Create a ticket when something needs fixing, or scan a vendor invoice."
		emptyIcon={Wrench}
		onRowClick={(w) => goto('/work-orders/' + w.id)}
		getRowKey={(w) => w.id}
		data-testid="maintenance-work-orders"
	/>

	<DetailCard title="Receipts on these jobs" icon={Receipt} accent="muted" testid="maintenance-receipts">
		{#if workOrderReceipts.length === 0}
			<p class="text-sm text-muted-foreground">No work-order receipts yet. Snap a receipt to link it to a job.</p>
		{:else}
			<ul class="divide-y text-sm">
				{#each workOrderReceipts as e (e.id)}
					<li class="flex items-center justify-between py-1.5">
						<span class="truncate">{formatDateOnly(e.incurredAt)} · {e.description}</span>
						<span class="flex shrink-0 items-center gap-2"><StatusBadge status={e.status} />{money(e.amount)}</span>
					</li>
				{/each}
			</ul>
		{/if}
	</DetailCard>
</div>
