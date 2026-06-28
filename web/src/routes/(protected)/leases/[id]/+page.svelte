<script lang="ts">
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import LeaseDetail from '$lib/components/records/LeaseDetail.svelte';
</script>

<LeaseDetail
	leaseId={Number(page.params.id)}
	initialTab={page.url.searchParams.get('tab') ?? undefined}
	onDeleted={() => goto('/leases')}
/>
