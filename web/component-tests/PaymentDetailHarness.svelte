<script lang="ts">
	import { QueryClient, QueryClientProvider } from '@tanstack/svelte-query';
	import PaymentDetail from '$lib/components/records/PaymentDetail.svelte';

	let {
		tenantAccountId = 20,
		tenantLedgerEntryId = 52
	}: {
		tenantAccountId?: number;
		tenantLedgerEntryId?: number;
	} = $props();

	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false } }
	});
</script>

<QueryClientProvider client={queryClient}>
	<PaymentDetail {tenantAccountId} {tenantLedgerEntryId} />
</QueryClientProvider>
