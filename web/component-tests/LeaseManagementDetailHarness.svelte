<script lang="ts">
	import { QueryClient, QueryClientProvider } from '@tanstack/svelte-query';
	import LeaseManagementDetail from '$lib/components/leases/LeaseManagementDetail.svelte';

	const queryClient = new QueryClient({
		defaultOptions: {
			queries: { retry: false },
			mutations: { retry: false }
		}
	});

	let { leaseManagementId = 19 }: { leaseManagementId?: number } = $props();
</script>

<QueryClientProvider client={queryClient}>
	<LeaseManagementDetail {leaseManagementId} />
</QueryClientProvider>
