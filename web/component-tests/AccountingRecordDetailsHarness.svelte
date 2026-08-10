<script lang="ts">
	import { QueryClient, QueryClientProvider } from '@tanstack/svelte-query';
	import * as Tooltip from '$lib/components/ui/tooltip';
	import AccountingDetailMode from '$lib/components/accounting/AccountingDetailMode.svelte';
	import AccountingImpactCard from '$lib/components/accounting/AccountingImpactCard.svelte';
	import JournalDetailDrawer from '$lib/components/accounting/JournalDetailDrawer.svelte';

	let {
		sourceId,
		journalPublicId = 'journal-1'
	}: {
		sourceId: number;
		journalPublicId?: string | null;
	} = $props();

	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false } }
	});
</script>

<Tooltip.Provider>
	<QueryClientProvider client={queryClient}>
		<AccountingDetailMode testid="test-accounting-detail-mode">
			<AccountingImpactCard sourceType="TenantCharge" {sourceId} />
			<JournalDetailDrawer journalPublicId={journalPublicId} />
		</AccountingDetailMode>
	</QueryClientProvider>
</Tooltip.Provider>
