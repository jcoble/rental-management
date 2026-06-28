<script lang="ts">
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import ApplicationDetail from '$lib/components/records/ApplicationDetail.svelte';
</script>

<ApplicationDetail applicationId={Number(page.params.id)} onDeleted={() => goto('/applications')} />
