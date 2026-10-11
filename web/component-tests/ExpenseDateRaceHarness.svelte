<script lang="ts">
	import { QueryClient, QueryClientProvider } from '@tanstack/svelte-query';
	import type { UnitDashboard } from '$lib/types';
	import ExpensesTab from '$lib/components/unit/tabs/ExpensesTab.svelte';

	let {
		businessDate = null,
		businessDatePending = false
	}: {
		businessDate?: string | null;
		businessDatePending?: boolean;
	} = $props();

	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
	const dashboard = {
		unit: {
			id: 8,
			propertyId: 7,
			unitNumber: '1A',
			bedrooms: 1,
			bathrooms: 1,
			marketRent: 1950,
			status: 'Occupied',
			notes: '',
			createdAt: '2026-01-01T00:00:00Z',
			updatedAt: '2026-01-01T00:00:00Z'
		},
		propertyName: 'Example House',
		tenantAccountId: 20,
		overview: { recentPayments: [], openWorkOrders: [], pendingDocs: [], upcomingAppointments: [] }
	} as unknown as UnitDashboard;
</script>

<QueryClientProvider client={queryClient}>
	<ExpensesTab
		{dashboard}
		{businessDate}
		{businessDatePending}
		onScan={() => undefined}
	/>
</QueryClientProvider>
