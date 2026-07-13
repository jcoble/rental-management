<script lang="ts">
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { portal, type PortalTenantCharge } from '$lib/api/endpoints/portal';
	import { ApiError } from '$lib/api/client';
	import { showSuccess, showInfo, showError, apiErrorMessage } from '$lib/utils/toast';
	import { formatDateOnly } from '$lib/utils/date';
	import { Button } from '$lib/components/ui/button';
	import { CreditCard, Repeat } from '@lucide/svelte';
	import HelpTooltip from '$lib/components/ui/HelpTooltip.svelte';
	import * as Tooltip from '$lib/components/ui/tooltip';
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import { onMount } from 'svelte';

	const queryClient = useQueryClient();
	const pageSize = 20;
	let chargeSkip = $state(0);
	const requestedAccountId = Number(page.url.searchParams.get('account'));
	let selectedAccountId = $state<number | null>(
		Number.isInteger(requestedAccountId) && requestedAccountId > 0 ? requestedAccountId : null
	);

	const accountsQuery = createQuery(() => ({
		queryKey: ['portal-tenant-accounts', 'payments'],
		queryFn: () => portal.tenantAccountsPage({ take: 200, sort: 'propertyName' })
	}));

	$effect(() => {
		const accounts = accountsQuery.data;
		if (selectedAccountId == null && accounts?.totalCount === 1 && accounts.items[0]) {
			selectedAccountId = accounts.items[0].tenantAccountId;
		}
	});

	const chargesQuery = createQuery(() => ({
		queryKey: ['portal-tenant-account-charges', selectedAccountId, chargeSkip, pageSize],
		queryFn: () =>
			portal.tenantAccountChargesPage(selectedAccountId as number, {
				skip: chargeSkip,
				take: pageSize,
				sort: 'dueOn'
			}),
		enabled: selectedAccountId != null
	}));

	const autopayQuery = createQuery(() => ({
		queryKey: ['portal-autopay', selectedAccountId],
		queryFn: () => portal.autopayStatus(selectedAccountId as number),
		enabled: selectedAccountId != null
	}));

	let paymentProviderUnavailable = $state(false);
	const onlinePaymentsUnavailable = $derived(
		autopayQuery.data?.onlinePaymentsAvailable === false || paymentProviderUnavailable
	);

	function selectAccount(value: string) {
		selectedAccountId = value ? Number(value) : null;
		chargeSkip = 0;
		paymentProviderUnavailable = false;
		const url = new URL(page.url);
		if (selectedAccountId == null) url.searchParams.delete('account');
		else url.searchParams.set('account', String(selectedAccountId));
		goto(url.pathname + url.search, { replaceState: true, noScroll: true, keepFocus: true });
	}

	function isStripeOff(err: unknown): boolean {
		return err instanceof ApiError && err.status === 503;
	}

	function returnUrls() {
		const origin = typeof window !== 'undefined' ? window.location.origin : '';
		return {
			successUrl: `${origin}/portal/payments?account=${selectedAccountId}&checkout=success`,
			cancelUrl: `${origin}/portal/payments?account=${selectedAccountId}&checkout=cancel`
		};
	}

	const payMutation = createMutation(() => ({
		mutationFn: (charge: PortalTenantCharge) =>
			portal.payCheckout(charge.tenantAccountId, charge.tenantLedgerEntryId, returnUrls()),
		onSuccess: ({ checkoutUrl }) => {
			window.location.href = checkoutUrl;
		},
		onError: (err) => {
			if (isStripeOff(err)) paymentProviderUnavailable = true;
			else showError(apiErrorMessage(err, "We couldn't start the payment. Please try again."));
		}
	}));
	let payingId = $state<number | null>(null);
	function payNow(charge: PortalTenantCharge) {
		payingId = charge.tenantLedgerEntryId;
		payMutation.mutate(charge);
	}

	const enrollMutation = createMutation(() => ({
		mutationFn: () =>
			portal.autopayEnroll(selectedAccountId as number, {
				operationKey: crypto.randomUUID(),
				...returnUrls()
			}),
		onSuccess: ({ checkoutUrl }) => {
			window.location.href = checkoutUrl;
		},
		onError: (err) => {
			if (isStripeOff(err)) paymentProviderUnavailable = true;
			else showError(apiErrorMessage(err, "We couldn't set up autopay. Please try again."));
		}
	}));

	const cancelMutation = createMutation(() => ({
		mutationFn: () => portal.autopayCancel(selectedAccountId as number),
		onSuccess: () => {
			queryClient.invalidateQueries({ queryKey: ['portal-autopay'] });
			showSuccess('Autopay is turned off.');
		},
		onError: (err) => showError(apiErrorMessage(err, "We couldn't turn off autopay. Please try again."))
	}));

	onMount(() => {
		const result = page.url.searchParams.get('checkout');
		if (result === 'success') {
			showSuccess('Payment received — thanks!');
			queryClient.invalidateQueries({ queryKey: ['portal-tenant-account-charges'] });
			queryClient.invalidateQueries({ queryKey: ['portal-tenant-accounts'] });
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

	function money(value: number, currency = 'USD') {
		return Number(value).toLocaleString(undefined, { style: 'currency', currency });
	}

	function dueDate(value: string | null | undefined) {
		return value ? formatDateOnly(value) || value : 'No due date';
	}

	function explain(charge: PortalTenantCharge): string {
		if (charge.openAmount <= 0) return `${charge.description} has been paid in full.`;
		if (charge.isPastDue) {
			return `${money(charge.openAmount, charge.currency)} remains due for ${charge.description.toLowerCase()}.`;
		}
		return `${money(charge.openAmount, charge.currency)} is open and due ${dueDate(charge.dueOn)}.`;
	}
</script>

<svelte:head><title>Payments - Rental Command</title></svelte:head>

<div class="h-full overflow-y-auto p-4 sm:p-6" data-testid="portal-payments-page">
	<div class="mb-5 flex items-center gap-2"><CreditCard class="h-5 w-5 text-primary" /><h1 class="text-2xl font-semibold">Payments</h1></div>

	{#if accountsQuery.data && accountsQuery.data.totalCount > 1}
		<label class="mb-5 block max-w-lg text-sm font-medium" data-testid="portal-account-selector">
			Account
			<select
				class="mt-1 block w-full rounded-md border border-border bg-background px-3 py-2"
				value={selectedAccountId ?? ''}
				onchange={(event) => selectAccount(event.currentTarget.value)}
			>
				<option value="">Choose an account</option>
				{#each accountsQuery.data.items as account (account.tenantAccountId)}
					<option value={account.tenantAccountId}>{account.propertyName} · Unit {account.unitNumber} · {account.accountNumber}</option>
				{/each}
			</select>
		</label>
	{/if}

	{#if accountsQuery.isLoading}
		<p class="text-sm text-muted-foreground">Loading accounts…</p>
	{:else if accountsQuery.isError}
		<p class="text-sm text-destructive">Couldn't load tenant accounts.</p>
	{:else if accountsQuery.data?.totalCount === 0}
		<p class="text-sm text-muted-foreground">No tenant account is available.</p>
	{:else if selectedAccountId == null}
		<p class="text-sm text-muted-foreground" data-testid="portal-account-required">Choose an account to view its charges and autopay settings.</p>
	{:else}
		{#if onlinePaymentsUnavailable}
			<div class="mb-4 rounded-lg border border-border bg-muted/40 p-4 text-sm text-muted-foreground" data-testid="portal-payments-unavailable">
				Online payments aren't set up yet. Please continue paying rent the way you do today — we'll let you know when this is ready.
			</div>
		{/if}

		<section class="mb-5 rounded-lg border border-border bg-card p-4" data-testid="portal-autopay-card">
			<div class="flex items-center gap-2"><Repeat class="h-4 w-4 text-primary" /><h2 class="font-semibold">Autopay</h2></div>
			{#if autopayQuery.isLoading}
				<p class="mt-2 text-sm text-muted-foreground">Loading…</p>
			{:else if autopayQuery.data?.active}
				<p class="mt-1.5 text-sm text-muted-foreground">Autopay is on for this account.</p>
				<Button variant="outline" class="mt-3" disabled={cancelMutation.isPending} onclick={() => cancelMutation.mutate()} data-testid="portal-autopay-cancel">
					{cancelMutation.isPending ? 'Turning off…' : 'Turn off autopay'}
				</Button>
			{:else if onlinePaymentsUnavailable}
				<p class="mt-1.5 text-sm text-muted-foreground" data-testid="portal-autopay-unavailable">Online payments aren't set up yet, so autopay is not available right now.</p>
				<Button class="mt-3" disabled data-testid="portal-autopay-enroll">Set up autopay</Button>
			{:else}
				<p class="mt-1.5 text-sm text-muted-foreground">Set up automatic payments for this account.</p>
				<Button class="mt-3" disabled={enrollMutation.isPending} onclick={() => enrollMutation.mutate()} data-testid="portal-autopay-enroll">
					{enrollMutation.isPending ? 'Opening…' : 'Set up autopay'}
				</Button>
			{/if}
		</section>

		{#if chargesQuery.isLoading}
			<p class="text-sm text-muted-foreground">Loading charges…</p>
		{:else if chargesQuery.isError}
			<p class="text-sm text-destructive">Couldn't load charges for this account.</p>
		{:else}
			<Tooltip.Provider delayDuration={150}>
				<div class="space-y-3">
					{#each chargesQuery.data?.items ?? [] as charge (charge.tenantLedgerEntryId)}
					<div class="rounded-lg border border-border bg-card p-4" data-testid="portal-payment-row">
						<div class="flex flex-wrap items-center justify-between gap-2">
							<div class="flex items-center gap-1.5">
								<p class="font-medium">{charge.description} · {money(charge.openAmount, charge.currency)}</p>
								<HelpTooltip text={explain(charge)} label="What is this charge?" />
							</div>
							{#if charge.openAmount > 0}
								<Button size="sm" disabled={onlinePaymentsUnavailable || (payMutation.isPending && payingId === charge.tenantLedgerEntryId)} onclick={() => payNow(charge)} data-testid="portal-payment-pay-now">
									{onlinePaymentsUnavailable ? 'Pay unavailable' : payMutation.isPending && payingId === charge.tenantLedgerEntryId ? 'Opening…' : 'Pay now'}
								</Button>
							{/if}
						</div>
						<p class="mt-1 text-sm text-muted-foreground">Due {dueDate(charge.dueOn)} · {charge.isPastDue ? 'Past due' : charge.openAmount > 0 ? 'Open' : 'Paid'}</p>
						<p class="mt-1.5 text-sm text-muted-foreground" data-testid="portal-payment-explanation">{explain(charge)}</p>
					</div>
					{:else}
						<p class="text-sm text-muted-foreground">No charges found for this account.</p>
					{/each}
				</div>
			</Tooltip.Provider>
		{/if}

		{#if chargesQuery.data && chargesQuery.data.totalCount > pageSize}
			<div class="mt-4 flex items-center justify-between">
				<Button variant="outline" disabled={chargeSkip === 0} onclick={() => (chargeSkip = Math.max(0, chargeSkip - pageSize))}>Previous</Button>
				<span class="text-sm text-muted-foreground">{chargeSkip + 1}–{Math.min(chargeSkip + pageSize, chargesQuery.data.totalCount)} of {chargesQuery.data.totalCount}</span>
				<Button variant="outline" disabled={chargeSkip + pageSize >= chargesQuery.data.totalCount} onclick={() => (chargeSkip += pageSize)}>Next</Button>
			</div>
		{/if}
	{/if}
</div>
