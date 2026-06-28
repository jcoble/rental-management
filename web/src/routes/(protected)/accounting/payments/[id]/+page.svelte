<script lang="ts">
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import PaymentDetail from '$lib/components/records/PaymentDetail.svelte';
</script>

<PaymentDetail paymentId={Number(page.params.id)} onDeleted={() => goto('/accounting')} />
