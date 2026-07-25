<script lang="ts">
	import { page } from '$app/state';
	import PaymentDetail from '$lib/components/records/PaymentDetail.svelte';

	const tenantAccountId = $derived(Number(page.params.tenantAccountId));
	const tenantLedgerEntryId = $derived(Number(page.params.tenantLedgerEntryId));
</script>

<PaymentDetail {tenantAccountId} {tenantLedgerEntryId} />
