<script lang="ts">
	import type { AllocationRef } from '$lib/api/endpoints/tenant-ledgers';
	import { formatAccountingCurrency, formatAccountingDate } from '$lib/accounting/accounting-display';

	let {
		allocations = [],
		currency = 'USD',
		testid = 'tenant-payment-allocations'
	}: {
		allocations?: AllocationRef[] | null;
		currency?: string;
		testid?: string;
	} = $props();

	const visibleAllocations = $derived(allocations ?? []);
</script>

{#if visibleAllocations.length > 0}
	<div class="mt-2 rounded-md border border-border/70 bg-muted/20 px-2.5 py-2" data-testid={testid}>
		<p class="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground" data-testid={`${testid}-label`}>Applied to</p>
		<ul class="mt-1 space-y-1" data-testid={`${testid}-list`}>
			{#each visibleAllocations as allocation (allocation.targetSourceId)}
				<li class="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-0.5 text-xs" data-testid={`tenant-ledger-payment-allocation-${allocation.targetSourceId}`}>
					<span class="min-w-0 truncate text-foreground" data-testid={`tenant-ledger-payment-allocation-label-${allocation.targetSourceId}`}>
						{allocation.targetDescription || 'Charge'}
					</span>
					<span class="shrink-0 font-mono tabular-nums text-muted-foreground" data-testid={`tenant-ledger-payment-allocation-fact-${allocation.targetSourceId}`}>
						{formatAccountingDate(allocation.effectiveOn)} · {formatAccountingCurrency(allocation.amount, currency)}
					</span>
				</li>
			{/each}
		</ul>
	</div>
{/if}
