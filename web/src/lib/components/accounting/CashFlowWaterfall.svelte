<script lang="ts">
	import { formatAccountingCurrency } from '$lib/accounting/accounting-display';

	type WaterfallRow = {
		key: 'income' | 'operatingExpenses' | 'noi' | 'debtService' | 'cashFlow';
		label: string;
		amount: number;
		outflow: boolean;
		strong: boolean;
	};

	let {
		income,
		operatingExpenses,
		noi,
		debtService,
		cashFlow,
		currency = 'USD',
		advanced = false,
		loading = false
	}: {
		income: number;
		operatingExpenses: number;
		noi: number;
		debtService: number;
		cashFlow: number;
		currency?: string;
		advanced?: boolean;
		loading?: boolean;
	} = $props();

	const rows = $derived<WaterfallRow[]>([
		{
			key: 'income',
			label: advanced ? 'Rent and other operating cash in' : 'Money received from rent and other activity',
			amount: income,
			outflow: false,
			strong: false
		},
		{
			key: 'operatingExpenses',
			label: advanced ? 'Operating expenses' : 'Operating costs',
			amount: operatingExpenses,
			outflow: true,
			strong: false
		},
		{
			key: 'noi',
			label: advanced ? 'Net operating income' : 'Money left after operating costs',
			amount: noi,
			outflow: false,
			strong: true
		},
		{
			key: 'debtService',
			label: advanced ? 'Debt service' : 'Loan payments',
			amount: debtService,
			outflow: true,
			strong: false
		},
		{
			key: 'cashFlow',
			label: advanced ? 'Net cash flow' : 'Cash flow after expenses and loan payments',
			amount: cashFlow,
			outflow: false,
			strong: true
		}
	]);

	function formatOutflow(value: number | null | undefined): string {
		const formatted = formatAccountingCurrency(value, currency);
		if (formatted === '—' || value == null || value < 0) return formatted;
		return `−${formatted}`;
	}
</script>

<section class="rounded-2xl border border-border bg-card" data-testid="cash-flow-waterfall" aria-labelledby="cash-flow-waterfall-heading">
	<header class="border-b border-border px-4 py-4 sm:px-5">
		<p class="text-xs font-semibold uppercase tracking-[0.16em] text-muted-foreground">{advanced ? 'Cash-flow waterfall' : 'How the cash moved'}</p>
		<h2 id="cash-flow-waterfall-heading" class="mt-1 text-base font-semibold">{advanced ? 'Operating cash flow' : 'Cash in, costs, and loan payments'}</h2>
	</header>

	{#if loading}
		<div class="space-y-4 px-4 py-5 sm:px-5" role="status" aria-label="Loading cash-flow waterfall">
			{#each Array(5) as _, index}
				<div class="flex items-center justify-between gap-4" data-testid={`cash-flow-waterfall-loading-${index}`}>
					<div class="h-4 {index === 2 || index === 4 ? 'w-2/3' : 'w-3/5'} animate-pulse rounded bg-muted"></div>
					<div class="h-4 w-28 animate-pulse rounded bg-muted"></div>
				</div>
			{/each}
		</div>
	{:else}
		<div class="divide-y divide-border">
			{#each rows as row (row.key)}
				<div class={`flex items-center justify-between gap-4 px-4 py-4 sm:px-5 ${row.strong ? 'bg-muted/25 font-semibold' : ''}`}>
					<div class="flex min-w-0 items-center gap-3">
						<span class={`h-2.5 w-2.5 shrink-0 rounded-full ${row.outflow ? 'bg-amber-500' : row.strong ? 'bg-primary' : 'bg-emerald-500'}`} aria-hidden="true"></span>
						<span class="min-w-0 text-sm">{row.label}</span>
					</div>
					<span class={`shrink-0 font-mono text-sm tabular-nums ${row.outflow ? 'text-destructive' : ''}`}>
						{row.outflow ? formatOutflow(row.amount) : formatAccountingCurrency(row.amount, currency)}
					</span>
				</div>
			{/each}
		</div>
	{/if}
</section>
