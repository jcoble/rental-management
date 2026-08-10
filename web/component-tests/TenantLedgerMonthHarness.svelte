<script lang="ts">
	import type { TenantLedgerRow, TenantMonthSummary } from '$lib/api/endpoints/tenant-ledgers';
	import AccountingDetailMode from '$lib/components/accounting/AccountingDetailMode.svelte';
	import TenantLedgerMonth, { type TenantLedgerRowAction } from '$lib/components/accounting/TenantLedgerMonth.svelte';
	import * as Tooltip from '$lib/components/ui/tooltip';

	let {
		summary,
		rows,
		onaction
	}: {
		summary: TenantMonthSummary;
		rows: TenantLedgerRow[];
		onaction?: (row: TenantLedgerRow, action: TenantLedgerRowAction) => void;
	} = $props();
</script>

<Tooltip.Provider>
	<AccountingDetailMode testid="test-accounting-detail-mode">
		<TenantLedgerMonth {summary} {rows} {onaction} />
	</AccountingDetailMode>
</Tooltip.Provider>
