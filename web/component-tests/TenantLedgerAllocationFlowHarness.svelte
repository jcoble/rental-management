<script lang="ts">
	import type { TenantLedgerRow, TenantMonthSummary } from '$lib/api/endpoints/tenant-ledgers';
	import TenantLedgerMonth, { type TenantLedgerRowAction } from '$lib/components/accounting/TenantLedgerMonth.svelte';
	import TenantPaymentAllocationReview from '$lib/components/accounting/TenantPaymentAllocationReview.svelte';

	let {
		summary,
		row
	}: {
		summary: TenantMonthSummary;
		row: TenantLedgerRow;
	} = $props();

	let selected = $state<TenantLedgerRow | null>(null);

	function onaction(nextRow: TenantLedgerRow, action: TenantLedgerRowAction): void {
		if (action === 'fix-payment') selected = nextRow;
	}
</script>

<TenantLedgerMonth {summary} rows={[row]} {onaction} />
{#if selected}
	<TenantPaymentAllocationReview
		open={true}
		entryId={selected.tenantLedgerEntryId}
		allocations={selected.allocations}
		currency={selected.currency}
		onclose={() => selected = null}
	/>
{/if}
