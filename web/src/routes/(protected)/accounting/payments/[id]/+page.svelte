<script lang="ts">
	import { page } from '$app/state';
	import PaymentDetail from '$lib/components/records/PaymentDetail.svelte';
</script>

<PaymentDetail paymentId={Number(page.params.id)} />
