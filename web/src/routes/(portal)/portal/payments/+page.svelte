<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { portal } from '$lib/api/endpoints/portal';
	import { CreditCard } from '@lucide/svelte';

	const paymentsQuery = createQuery(() => ({ queryKey: ['portal-payments-page'], queryFn: () => portal.payments() }));
	function money(value: number | string | null | undefined) {
		return Number(value ?? 0).toLocaleString(undefined, { style: 'currency', currency: 'USD' });
	}
</script>

<svelte:head><title>Payments - Rental Command</title></svelte:head>

<div class="h-full overflow-y-auto p-6">
	<div class="mb-5 flex items-center gap-2"><CreditCard class="h-5 w-5 text-primary" /><h1 class="text-2xl font-semibold">Payments</h1></div>
	<div class="space-y-3">
		{#each paymentsQuery.data ?? [] as payment}
			<div class="rounded-lg border border-border bg-card p-4">
				<p class="font-medium">{payment.paymentType} · {money(payment.amount)}</p>
				<p class="mt-1 text-sm text-muted-foreground">Due {new Date(payment.dueDate).toLocaleDateString()} · {payment.status}</p>
			</div>
		{:else}
			<p class="text-sm text-muted-foreground">No payments found.</p>
		{/each}
	</div>
</div>
