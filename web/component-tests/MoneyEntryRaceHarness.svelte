<script lang="ts">
	import { QueryClient, QueryClientProvider } from '@tanstack/svelte-query';
	import type { RecurringTenantChargeRow } from '$lib/api/endpoints/tenant-ledgers';
	import * as Tooltip from '$lib/components/ui/tooltip';
	import RecordPaymentSheet from '$lib/components/accounting/RecordPaymentSheet.svelte';
	import RecurringChargeSheet from '$lib/components/accounting/RecurringChargeSheet.svelte';

	let {
		kind = 'payment',
		open = true,
		businessDate = null,
		schedule = null
	}: {
		kind?: 'payment' | 'recurring';
		open?: boolean;
		businessDate?: string | null;
		schedule?: RecurringTenantChargeRow | null;
	} = $props();

	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
</script>

<Tooltip.Provider>
	<QueryClientProvider client={queryClient}>
		{#if kind === 'payment'}
			<RecordPaymentSheet
				{open}
				tenantAccountId={20}
				defaultPayerName="Ada Lovelace"
				businessDate={businessDate}
				onclose={() => undefined}
				onsaved={() => undefined}
			/>
		{:else}
			<RecurringChargeSheet
				{open}
				tenantAccountId={20}
				schedule={schedule}
				businessDate={businessDate}
				onclose={() => undefined}
				onsaved={() => undefined}
			/>
		{/if}
	</QueryClientProvider>
</Tooltip.Provider>
