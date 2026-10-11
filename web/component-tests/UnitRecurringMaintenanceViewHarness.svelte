<script lang="ts">
	import { QueryClient, QueryClientProvider } from '@tanstack/svelte-query';
	import UnitRecurringMaintenanceView from '$lib/components/maintenance/UnitRecurringMaintenanceView.svelte';

	let {
		propertyId = 42,
		propertyLabel = 'QA Property',
		unitId = 314,
		unitLabel = 'Unit 2B',
		emptyMessage = 'No recurring tasks yet. Select the New recurring task button to add work that repeats on a schedule.',
	}: {
		propertyId?: number;
		propertyLabel?: string;
		unitId?: number;
		unitLabel?: string;
		emptyMessage?: string;
	} = $props();

	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
</script>

<QueryClientProvider client={queryClient}>
	<UnitRecurringMaintenanceView
		propertyId={propertyId}
		propertyLabel={propertyLabel}
		unitId={unitId}
		unitLabel={unitLabel}
		emptyMessage={emptyMessage}
	/>
</QueryClientProvider>
