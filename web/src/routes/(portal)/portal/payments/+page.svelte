<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { portal } from '$lib/api/endpoints/portal';
	import type { Payment } from '$lib/types';
	import { CreditCard } from '@lucide/svelte';
	import HelpTooltip from '$lib/components/ui/HelpTooltip.svelte';
	import * as Tooltip from '$lib/components/ui/tooltip';

	const paymentsQuery = createQuery(() => ({ queryKey: ['portal-payments-page'], queryFn: () => portal.payments() }));
	function money(value: number | string | null | undefined) {
		return Number(value ?? 0).toLocaleString(undefined, { style: 'currency', currency: 'USD' });
	}
	function dueDate(value: string) {
		const d = new Date(value);
		return isNaN(d.getTime()) ? value : d.toLocaleDateString();
	}

	// Friendly, read-only "what is this charge" sentence so the tenant never has to guess —
	// transparency kills payment disputes. (Portal payments have no server ledger text yet.)
	const TYPE_LABEL: Record<string, string> = {
		Rent: 'monthly rent',
		SecurityDeposit: 'security deposit',
		LateFee: 'late fee',
		Utility: 'utility charge',
		Other: 'charge'
	};
	function explain(p: Payment): string {
		const label = TYPE_LABEL[p.paymentType] ?? 'charge';
		const amount = money(p.amount);
		const due = dueDate(p.dueDate);
		switch (p.status) {
			case 'Paid':
				return `Your ${amount} ${label} due ${due} has been paid in full. Nothing else is owed on this one.`;
			case 'Partial':
				return `This ${amount} ${label} was due ${due} and has been partly paid. A balance is still outstanding.`;
			case 'Late':
				return `This ${amount} ${label} was due ${due} and is now past due. Please pay it as soon as you can.`;
			case 'Waived':
				return `This ${amount} ${label} was waived — you do not owe it.`;
			case 'Failed':
				return `A payment attempt for this ${amount} ${label} (due ${due}) did not go through, so it is still owed.`;
			case 'Refunded':
				return `This ${amount} ${label} was refunded back to you.`;
			default:
				return `This is your ${amount} ${label}, due ${due}.`;
		}
	}
</script>

<svelte:head><title>Payments - Rental Command</title></svelte:head>

<div class="h-full overflow-y-auto p-6">
	<div class="mb-5 flex items-center gap-2"><CreditCard class="h-5 w-5 text-primary" /><h1 class="text-2xl font-semibold">Payments</h1></div>
	<Tooltip.Provider delayDuration={150}>
		<div class="space-y-3">
			{#each paymentsQuery.data ?? [] as payment}
				<div class="rounded-lg border border-border bg-card p-4" data-testid="portal-payment-row">
					<div class="flex items-center gap-1.5">
						<p class="font-medium">{payment.paymentType} · {money(payment.amount)}</p>
						<HelpTooltip text={explain(payment)} label="What is this charge?" />
					</div>
					<p class="mt-1 text-sm text-muted-foreground">Due {dueDate(payment.dueDate)} · {payment.status}</p>
					<p class="mt-1.5 text-sm leading-snug text-muted-foreground" data-testid="portal-payment-explanation">{explain(payment)}</p>
				</div>
			{:else}
				<p class="text-sm text-muted-foreground">No payments found.</p>
			{/each}
		</div>
	</Tooltip.Provider>
</div>
