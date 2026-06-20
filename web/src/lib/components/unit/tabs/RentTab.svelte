<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import type { UnitDashboard, Payment } from '$lib/types';
	import { payments as paymentsApi } from '$lib/api/endpoints/payments';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { money } from '../money';
	import { formatDateOnly } from '$lib/utils/date';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { Button } from '$lib/components/ui/button';
	import { DollarSign, Send, ScanLine } from '@lucide/svelte';
	import type { UnitDrawerAction } from '../drawer-actions';

	let {
		dashboard,
		onAction,
	}: {
		dashboard: UnitDashboard;
		onAction: (action: UnitDrawerAction) => void;
	} = $props();

	const portfolioId = $derived(getCurrentPortfolioId());
	const leaseId = $derived(dashboard.currentLease?.id);

	// Payments for the unit's current lease (the dominant case). The filter is DB-side (?leaseId=).
	const paymentsQuery = createQuery(() => ({
		queryKey: ['payments', portfolioId, { leaseId }],
		enabled: !!leaseId && portfolioId > 0,
		queryFn: () => paymentsApi.list(portfolioId, { leaseId, take: 500 }),
	}));

	const list = $derived(paymentsQuery.data ?? []);

	const columns: ColumnDef<Payment>[] = [
		{ key: 'dueDate', title: 'Due', format: 'date', sortable: true, mobileRole: 'subtitle' },
		{ key: 'paymentType', title: 'Type', mobileRole: 'meta' },
		{ key: 'amount', title: 'Amount', format: 'currency', sortable: true, mobileRole: 'metric' },
		{ key: 'status', title: 'Status', mobileRole: 'badge', cell: statusCell },
		{ key: 'paidDate', title: 'Paid', format: 'date', mobileRole: 'meta', accessor: (p) => (p.paidDate ? formatDateOnly(p.paidDate) : '—') },
	];
</script>

{#snippet statusCell(p: Payment)}
	<StatusBadge status={p.status} />
{/snippet}

<div class="space-y-4" data-testid="unit-rent-tab">
	<div class="flex flex-wrap items-center justify-between gap-3 rounded-xl border bg-card p-4">
		<div>
			<p class="text-sm text-muted-foreground">Outstanding balance</p>
			<p class="text-2xl font-bold">{money(dashboard.header.outstandingRentBalance)}</p>
		</div>
		<div class="flex flex-wrap gap-2">
			<Button class="gap-2" onclick={() => onAction('post-payment')} data-testid="rent-post-payment">
				<DollarSign class="h-4 w-4" /> Post payment
			</Button>
			<Button variant="outline" class="gap-2" onclick={() => onAction('scan')} data-testid="rent-scan">
				<ScanLine class="h-4 w-4" /> Scan receipt
			</Button>
		</div>
	</div>

	{#if !leaseId}
		<p class="rounded-xl border bg-card p-6 text-center text-sm text-muted-foreground">
			No lease on this unit yet — rent activity appears once a lease exists.
		</p>
	{:else}
		<DataGrid
			data={list}
			{columns}
			loading={paymentsQuery.isLoading}
			emptyMessage="No payments yet"
			emptyDescription="Post a payment or scan a rent check to start the ledger."
			emptyIcon={DollarSign}
			getRowKey={(p) => p.id}
			data-testid="rent-payments"
		/>
	{/if}
</div>
