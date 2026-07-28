<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import type { PrepareMoveInResponse } from '$lib/api/endpoints/lease-managements';
	import PrepareMoveInDialog from '$lib/components/applications/PrepareMoveInDialog.svelte';
	import ApplicationDetail from '$lib/components/records/ApplicationDetail.svelte';
	import { readPrepareMoveInPrefill } from '$lib/leases/prepare-move-in-prefill';

	const applicationId = $derived(Number(page.params.id));
	const prepareMoveInBasePath = $derived(`/leasing/applications/${page.params.id}`);
	const prepareMoveInPrefill = $derived(readPrepareMoveInPrefill(page.url.searchParams));

	function closePrepareMoveIn() {
		const url = new URL(page.url);
		url.searchParams.delete('prepareMoveIn');
		url.searchParams.delete('applicationId');
		url.searchParams.delete('unitId');
		url.searchParams.delete('tenantId');
		void goto(`${url.pathname}${url.search}`, {
			replaceState: true,
			keepFocus: true,
			noScroll: true
		});
	}

	function finishPrepareMoveIn(result: PrepareMoveInResponse) {
		void goto(`/leasing/move-ins/${result.leaseManagementId}`);
	}
</script>

<ApplicationDetail
	{applicationId}
	onDeleted={() => goto('/leasing/pipeline')}
	{prepareMoveInBasePath}
/>

{#if prepareMoveInPrefill}
	<PrepareMoveInDialog
		prefill={prepareMoveInPrefill}
		onclose={closePrepareMoveIn}
		onprepared={finishPrepareMoveIn}
	/>
{/if}
