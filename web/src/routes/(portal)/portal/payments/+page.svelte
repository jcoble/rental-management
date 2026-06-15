<script lang="ts">
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { portal } from '$lib/api/endpoints/portal';
	import { ApiError } from '$lib/api/client';
	import { showSuccess, showInfo, showError, apiErrorMessage } from '$lib/utils/toast';
	import { formatDateOnly } from '$lib/utils/date';
	import { paymentTypeLabel } from '$lib/utils/payment-labels';
	import type { Payment } from '$lib/types';
	import { Button } from '$lib/components/ui/button';
	import { CreditCard, Repeat } from '@lucide/svelte';
	import HelpTooltip from '$lib/components/ui/HelpTooltip.svelte';
	import * as Tooltip from '$lib/components/ui/tooltip';
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import { onMount } from 'svelte';

	const queryClient = useQueryClient();
	const paymentsQuery = createQuery(() => ({ queryKey: ['portal-payments-page'], queryFn: () => portal.payments() }));

	// Autopay is enrollment-per-lease; the portal session's default lease is used when
	// we don't pass an explicit leaseId. We learn the lease from the first payment row.
	const autopayQuery = createQuery(() => ({ queryKey: ['portal-autopay'], queryFn: () => portal.autopayStatus() }));
	const leaseId = $derived(autopayQuery.data?.leaseId ?? paymentsQuery.data?.[0]?.leaseId ?? null);

	// When Stripe is off the API returns 503; we keep that gentle (not a red error toast).
	let onlinePaymentsUnavailable = $state(false);

	function isStripeOff(err: unknown): boolean {
		return err instanceof ApiError && err.status === 503;
	}

	// A payment the tenant can act on: still owed (not paid/waived/refunded).
	const PAYABLE: Payment['status'][] = ['Scheduled', 'Late', 'Partial', 'Failed'];
	function isPayable(p: Payment): boolean {
		return PAYABLE.includes(p.status);
	}

	function returnUrls() {
		const origin = typeof window !== 'undefined' ? window.location.origin : '';
		return {
			successUrl: `${origin}/portal/payments?checkout=success`,
			cancelUrl: `${origin}/portal/payments?checkout=cancel`
		};
	}

	const payMutation = createMutation(() => ({
		mutationFn: (paymentId: number) => portal.payCheckout(paymentId, returnUrls()),
		onSuccess: ({ checkoutUrl }) => {
			window.location.href = checkoutUrl;
		},
		onError: (err) => {
			if (isStripeOff(err)) {
				onlinePaymentsUnavailable = true;
			} else {
				showError(apiErrorMessage(err, "We couldn't start the payment. Please try again."));
			}
		}
	}));
	let payingId = $state<number | null>(null);
	function payNow(paymentId: number) {
		payingId = paymentId;
		payMutation.mutate(paymentId);
	}

	const enrollMutation = createMutation(() => ({
		mutationFn: () => portal.autopayEnroll({ leaseId: leaseId as number, ...returnUrls() }),
		onSuccess: ({ checkoutUrl }) => {
			window.location.href = checkoutUrl;
		},
		onError: (err) => {
			if (isStripeOff(err)) {
				onlinePaymentsUnavailable = true;
			} else {
				showError(apiErrorMessage(err, "We couldn't set up autopay. Please try again."));
			}
		}
	}));

	const cancelMutation = createMutation(() => ({
		mutationFn: () => portal.autopayCancel({ leaseId: leaseId as number }),
		onSuccess: () => {
			queryClient.invalidateQueries({ queryKey: ['portal-autopay'] });
			showSuccess('Autopay is turned off.');
		},
		onError: (err) => showError(apiErrorMessage(err, "We couldn't turn off autopay. Please try again."))
	}));

	// Handle Stripe's return (?checkout=success|cancel): toast once, then strip the
	// param so a refresh doesn't re-fire it. Refresh balances on success.
	onMount(() => {
		const result = page.url.searchParams.get('checkout');
		if (result === 'success') {
			showSuccess('Payment received — thanks!');
			queryClient.invalidateQueries({ queryKey: ['portal-payments-page'] });
			queryClient.invalidateQueries({ queryKey: ['portal-autopay'] });
		} else if (result === 'cancel') {
			showInfo('Payment canceled.');
		}
		if (result) {
			const url = new URL(page.url);
			url.searchParams.delete('checkout');
			goto(url.pathname + url.search, { replaceState: true, noScroll: true, keepFocus: true });
		}
	});

	function money(value: number | string | null | undefined) {
		return Number(value ?? 0).toLocaleString(undefined, { style: 'currency', currency: 'USD' });
	}
	function dueDate(value: string) {
		// Date-only field stored UTC-midnight; format UTC-pinned (see formatDateOnly).
		return formatDateOnly(value) || value;
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

<div class="h-full overflow-y-auto p-4 sm:p-6" data-testid="portal-payments-page">
	<div class="mb-5 flex items-center gap-2"><CreditCard class="h-5 w-5 text-primary" /><h1 class="text-2xl font-semibold">Payments</h1></div>

	{#if onlinePaymentsUnavailable}
		<div class="mb-4 rounded-lg border border-border bg-muted/40 p-4 text-sm text-muted-foreground" data-testid="portal-payments-unavailable">
			Online payments aren't set up yet. Please continue paying rent the way you do today — we'll let you know when this is ready.
		</div>
	{/if}

	<!-- Autopay -->
	<section class="mb-5 rounded-lg border border-border bg-card p-4" data-testid="portal-autopay-card">
		<div class="flex items-center gap-2">
			<Repeat class="h-4 w-4 text-primary" />
			<h2 class="font-semibold">Autopay</h2>
		</div>
		{#if autopayQuery.isLoading}
			<p class="mt-2 text-sm text-muted-foreground" data-testid="portal-autopay-loading">Loading…</p>
		{:else if autopayQuery.data?.active}
			<p class="mt-1.5 text-sm leading-snug text-muted-foreground" data-testid="portal-autopay-on">
				Autopay is on. We'll automatically charge your saved card or bank account when rent is due — you don't have to do anything.
			</p>
			<Button
				variant="outline"
				class="mt-3"
				disabled={cancelMutation.isPending || leaseId == null}
				onclick={() => cancelMutation.mutate()}
				data-testid="portal-autopay-cancel"
			>
				{cancelMutation.isPending ? 'Turning off…' : 'Turn off autopay'}
			</Button>
		{:else}
			<p class="mt-1.5 text-sm leading-snug text-muted-foreground">
				We'll automatically charge your saved card or bank account when rent is due, so you never miss a payment.
			</p>
			<Button
				class="mt-3"
				disabled={enrollMutation.isPending || leaseId == null}
				onclick={() => enrollMutation.mutate()}
				data-testid="portal-autopay-enroll"
			>
				{enrollMutation.isPending ? 'Opening…' : 'Set up autopay'}
			</Button>
		{/if}
	</section>

	<Tooltip.Provider delayDuration={150}>
		<div class="space-y-3">
			{#each paymentsQuery.data ?? [] as payment}
				<div class="rounded-lg border border-border bg-card p-4" data-testid="portal-payment-row">
					<div class="flex flex-wrap items-center justify-between gap-2">
						<div class="flex items-center gap-1.5">
							<p class="font-medium">{paymentTypeLabel(payment.paymentType)} · {money(payment.amount)}</p>
							<HelpTooltip text={explain(payment)} label="What is this charge?" />
						</div>
						{#if isPayable(payment)}
							<Button
								size="sm"
								disabled={payMutation.isPending && payingId === payment.id}
								onclick={() => payNow(payment.id)}
								data-testid="portal-payment-pay-now"
							>
								{payMutation.isPending && payingId === payment.id ? 'Opening…' : 'Pay now'}
							</Button>
						{/if}
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
