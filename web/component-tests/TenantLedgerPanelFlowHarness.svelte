<script lang="ts">
	import { QueryClient, QueryClientProvider } from '@tanstack/svelte-query';
	import * as Tooltip from '$lib/components/ui/tooltip';
	import type { UnitDashboard } from '$lib/types';
	import TenantLedgerPanel from '$lib/components/accounting/TenantLedgerPanel.svelte';

	let { dashboard }: { dashboard: UnitDashboard } = $props();
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
	let viewedEntryId = $state<number | null>(null);

	function openPayment(entryId: number): void {
		viewedEntryId = entryId;
	}
</script>

<Tooltip.Provider>
	<QueryClientProvider client={queryClient}>
		<TenantLedgerPanel {dashboard} onScan={() => undefined} onopenpayment={openPayment} />
		{#if viewedEntryId != null}
			<div data-testid="panel-flow-view-destination">Tenant entry #{viewedEntryId}</div>
		{/if}
	</QueryClientProvider>
</Tooltip.Provider>
