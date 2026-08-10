<script lang="ts">
	import { QueryClient, QueryClientProvider } from '@tanstack/svelte-query';
	import { Button } from '$lib/components/ui/button';
	import RecurringMaintenanceFormDialog from '$lib/components/maintenance/RecurringMaintenanceFormDialog.svelte';
	import type { RecurringMaintenanceTask } from '$lib/api/endpoints/recurring-maintenance';

	let {
		propertyId = 42,
		propertyLabel = 'QA Property',
		unitId = 314,
		unitLabel = 'Unit 2B',
		onsaved = () => {},
		onrefreshed = () => {}
	}: {
		propertyId?: number;
		propertyLabel?: string;
		unitId?: number;
		unitLabel?: string;
		onsaved?: (task: RecurringMaintenanceTask) => void;
		onrefreshed?: () => void;
	} = $props();

	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
	let open = $state(false);

	function handleSaved(task: RecurringMaintenanceTask) {
		open = false;
		onsaved(task);
		onrefreshed();
	}
</script>

<QueryClientProvider client={queryClient}>
	<Button data-testid="unit-recurring-create-button" onclick={() => (open = true)}>New recurring task</Button>
	<RecurringMaintenanceFormDialog
		open={open}
		propertyId={propertyId}
		propertyLabel={propertyLabel}
		unitId={unitId}
		unitLabel={unitLabel}
		lockProperty
		lockUnit
		onclose={() => (open = false)}
		onsaved={handleSaved}
	/>
</QueryClientProvider>
