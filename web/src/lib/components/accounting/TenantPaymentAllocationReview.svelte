<script lang="ts">
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import { formatAccountingCurrency, formatAccountingDate } from '$lib/accounting/accounting-display';
	import { normalizeTenantLedgerDescription } from '$lib/accounting/tenant-ledger-display';
	import type { AllocationRef } from '$lib/api/endpoints/tenant-ledgers';

	let {
		open = $bindable(false),
		entryId,
		allocations,
		currency = 'USD',
		onclose
	}: {
		open?: boolean;
		entryId: number;
		allocations: AllocationRef[];
		currency?: string;
		onclose: () => void;
	} = $props();
</script>

<Dialog.Root bind:open onOpenChange={(next) => { if (!next) onclose(); }}>
	<Dialog.Content class="max-w-lg" data-testid="tenant-payment-allocation-review">
		<Dialog.Header>
			<Dialog.Title>Review payment allocation</Dialog.Title>
			<Dialog.Description data-testid="tenant-payment-allocation-entry-id">
				Payment entry #{entryId}
			</Dialog.Description>
		</Dialog.Header>
		{#if allocations.length === 0}
			<p class="rounded-md border border-dashed border-border px-3 py-4 text-sm text-muted-foreground" data-testid="tenant-payment-allocation-empty">
				This payment is not allocated to a charge yet.
			</p>
		{:else}
			<ul class="space-y-2" data-testid="tenant-payment-allocation-list">
				{#each allocations as allocation (allocation.allocationId)}
					<li class="flex items-start justify-between gap-4 rounded-md border border-border px-3 py-2 text-sm">
						<span class="min-w-0">
							<span class="block truncate font-medium">{normalizeTenantLedgerDescription(allocation.targetDescription)}</span>
							<span class="block text-xs text-muted-foreground">{formatAccountingDate(allocation.effectiveOn)} · allocation #{allocation.allocationId}</span>
						</span>
						<span class="shrink-0 font-mono font-medium tabular-nums">{formatAccountingCurrency(allocation.amount, currency)}</span>
					</li>
				{/each}
			</ul>
		{/if}
		<Dialog.Footer>
			<Button variant="outline" onclick={onclose}>Close</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
