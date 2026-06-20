<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import type { Expense } from '$lib/types';
	import { expenses as expensesApi } from '$lib/api/endpoints/expenses';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Receipt, Plus } from '@lucide/svelte';
	import type { UnitDrawerAction } from '../drawer-actions';

	let {
		unitId,
		onAction,
	}: {
		unitId: number;
		onAction: (action: UnitDrawerAction) => void;
	} = $props();

	const portfolioId = $derived(getCurrentPortfolioId());

	// Unit-relevant expenses: tied to the unit OR to one of its work orders (DB-side correlated filter).
	const expensesQuery = createQuery(() => ({
		queryKey: ['unit-expenses', portfolioId, unitId],
		enabled: portfolioId > 0 && unitId > 0,
		queryFn: () => expensesApi.list(portfolioId, { unitId, take: 500 }),
	}));

	const list = $derived(expensesQuery.data ?? []);

	const columns: ColumnDef<Expense>[] = [
		{ key: 'incurredAt', title: 'Date', format: 'date', sortable: true, mobileRole: 'subtitle' },
		{ key: 'description', title: 'Description', sortable: true, mobileRole: 'title' },
		{ key: 'category', title: 'Category', mobileRole: 'meta' },
		{ key: 'status', title: 'Status', mobileRole: 'badge', cell: statusCell },
		{ key: 'amount', title: 'Amount', format: 'currency', sortable: true, mobileRole: 'metric' },
	];
</script>

{#snippet statusCell(e: Expense)}
	<StatusBadge status={e.status} />
{/snippet}

<div class="space-y-4" data-testid="unit-expenses-tab">
	<div class="flex flex-wrap justify-end gap-2">
		<Button class="gap-2" onclick={() => onAction('add-expense')} data-testid="expenses-create">
			<Plus class="h-4 w-4" /> Add expense
		</Button>
	</div>

	<DataGrid
		data={list}
		{columns}
		loading={expensesQuery.isLoading}
		emptyMessage="No expenses yet"
		emptyDescription="Add a unit cost (appliance, permit, repair) or snap a receipt to track spend on this unit."
		emptyIcon={Receipt}
		getRowKey={(e) => e.id}
		data-testid="expenses-list"
	/>
</div>
