<script lang="ts">
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import WorkOrderDetail from '$lib/components/records/WorkOrderDetail.svelte';
</script>

<WorkOrderDetail workOrderId={Number(page.params.id)} onDeleted={() => goto('/maintenance')} />
