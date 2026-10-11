<script lang="ts">
	import { page } from '$app/state';
	import LeaseManagementDetail from '$lib/components/leases/LeaseManagementDetail.svelte';

	const leaseManagementId = $derived(Number(page.params.id));
</script>

<div class="m3-page-frame">
	<LeaseManagementDetail {leaseManagementId} />
</div>
