<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import { onMount, tick } from 'svelte';
	import {
		portal,
		type PortalTenantAccountHistoryItem,
		type PortalTenantAccountHistoryPeriod
	} from '$lib/api/endpoints/portal';
	import { ApiError } from '$lib/api/client';
	import { Button } from '$lib/components/ui/button';
	import * as Select from '$lib/components/ui/select';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import { apiErrorMessage, showError, showInfo, showSuccess } from '$lib/utils/toast';
	import { formatDateOnly } from '$lib/utils/date';
	import { CreditCard, Repeat } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const pageSize = 20;
	const requestedAccountId = Number(page.url.searchParams.get('account'));
	const requestedEntryId = Number(page.url.searchParams.get('entry'));
	const focusedEntryId =
		Number.isInteger(requestedEntryId) && requestedEntryId > 0 ? requestedEntryId : undefined;

	let selectedAccountId = $state<number | null>(
		Number.isInteger(requestedAccountId) && requestedAccountId > 0 ? requestedAccountId : null
	);
	let period = $state<PortalTenantAccountHistoryPeriod>(
		focusedEntryId == null ? 'currentMonth' : 'all'
	);
	let skip = $state(0);
	let paymentProviderUnavailable = $state(false);
	let payingId = $state<number | null>(null);

	const accountsQuery = createQuery(() => ({
		queryKey: ['portal-tenant-accounts', 'account-history'],
		queryFn: () => portal.tenantAccountsPage({ take: 200, sort: 'propertyName' })
	}));
	const selectedAccount = $derived(
		accountsQuery.data?.items.find((account) => account.tenantAccountId === selectedAccountId)
	);

	$effect(() => {
		const accounts = accountsQuery.data;
		if (selectedAccountId == null && accounts?.totalCount === 1 && accounts.items[0]) {
			selectedAccountId = accounts.items[0].tenantAccountId;
		}
	});

	const historyQuery = createQuery(() => ({
		queryKey: ['portal-tenant-account-history', selectedAccountId, period, skip, focusedEntryId],
		queryFn: () =>
			portal.tenantAccountHistory(selectedAccountId as number, {
				period,
				skip,
				take: pageSize,
				entry: focusedEntryId
			}),
		enabled: selectedAccountId != null
	}));

	let scrolledFocusedEntryId = $state<number | undefined>();
	$effect(() => {
		if (focusedEntryId == null || historyQuery.data == null || scrolledFocusedEntryId === focusedEntryId) return;
		if (!historyQuery.data.items.some((entry) => entry.tenantLedgerEntryId === focusedEntryId)) return;
		scrolledFocusedEntryId = focusedEntryId;
		void tick().then(() => {
			document
				.getElementById(`portal-ledger-entry-${focusedEntryId}`)
				?.scrollIntoView({ behavior: 'smooth', block: 'center' });
		});
	});

	const autopayQuery = createQuery(() => ({
		queryKey: ['portal-autopay', selectedAccountId],
		queryFn: () => portal.autopayStatus(selectedAccountId as number),
		enabled: selectedAccountId != null
	}));
	const onlinePaymentsUnavailable = $derived(
		autopayQuery.data?.onlinePaymentsAvailable === false || paymentProviderUnavailable
	);

	function updateRoute(accountId: number | null, keepEntry = false) {
		const url = new URL(page.url);
		if (accountId == null) url.searchParams.delete('account');
		else url.searchParams.set('account', String(accountId));
		if (!keepEntry) url.searchParams.delete('entry');
		goto(url.pathname + url.search, { replaceState: true, noScroll: true, keepFocus: true });
	}

	function selectAccount(value: string) {
		selectedAccountId = value ? Number(value) : null;
		skip = 0;
		paymentProviderUnavailable = false;
		updateRoute(selectedAccountId);
	}

	function selectPeriod(value: string | undefined) {
		period = (value || 'currentMonth') as PortalTenantAccountHistoryPeriod;
		skip = 0;
	}

	function returnUrls() {
		const origin = typeof window !== 'undefined' ? window.location.origin : '';
		return {
			successUrl: `${origin}/portal/payments?account=${selectedAccountId}&checkout=success`,
			cancelUrl: `${origin}/portal/payments?account=${selectedAccountId}&checkout=cancel`
		};
	}

	function isStripeOff(error: unknown) {
		return error instanceof ApiError && error.status === 503;
	}

	const payMutation = createMutation(() => ({
		mutationFn: (entry: PortalTenantAccountHistoryItem) =>
			portal.payCheckout(selectedAccountId as number, entry.tenantLedgerEntryId, returnUrls()),
		onSuccess: ({ checkoutUrl }) => (window.location.href = checkoutUrl),
		onError: (error) => {
			if (isStripeOff(error)) paymentProviderUnavailable = true;
			else showError(apiErrorMessage(error, "We couldn't start the payment. Please try again."));
		}
	}));

	function payNow(entry: PortalTenantAccountHistoryItem) {
		payingId = entry.tenantLedgerEntryId;
		payMutation.mutate(entry);
	}

	const enrollMutation = createMutation(() => ({
		mutationFn: () =>
			portal.autopayEnroll(selectedAccountId as number, {
				operationKey: crypto.randomUUID(),
				...returnUrls()
			}),
		onSuccess: ({ checkoutUrl }) => (window.location.href = checkoutUrl),
		onError: (error) => {
			if (isStripeOff(error)) paymentProviderUnavailable = true;
			else showError(apiErrorMessage(error, "We couldn't set up autopay. Please try again."));
		}
	}));

	const cancelMutation = createMutation(() => ({
		mutationFn: () => portal.autopayCancel(selectedAccountId as number),
		onSuccess: () => {
			queryClient.invalidateQueries({ queryKey: ['portal-autopay'] });
			showSuccess('Autopay is turned off.');
		},
		onError: (error) =>
			showError(apiErrorMessage(error, "We couldn't turn off autopay. Please try again."))
	}));

	onMount(() => {
		const result = page.url.searchParams.get('checkout');
		if (result === 'success') {
			showSuccess('Payment received — thanks!');
			queryClient.invalidateQueries({ queryKey: ['portal-tenant-account-history'] });
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
		return Math.abs(value).toLocaleString(undefined, { style: 'currency', currency });
	}

	function signedMoney(value: number, currency: string) {
		if (value < 0) return `−${money(value, currency)}`;
		return money(value, currency);
	}

	function balanceMoney(value: number, currency: string) {
		return value < 0 ? `(${money(value, currency)})` : money(value, currency);
	}

	function entryLabel(entry: PortalTenantAccountHistoryItem) {
		if (entry.reversesEntryId != null) return `${entry.description || 'Reversal'} · Reversal`;
		if (entry.reversedByEntryId != null) return `${entry.description} · Reversed`;
		return entry.description || entry.entryType.replace(/([a-z])([A-Z])/g, '$1 $2');
	}
</script>

<svelte:head><title>Account history - Rental Command</title></svelte:head>

<div class="h-full overflow-y-auto p-4 sm:p-6" data-testid="portal-payments-page">
	<header class="mx-auto mb-6 max-w-6xl">
		<div class="flex items-center gap-2">
			<CreditCard class="h-5 w-5 text-primary" />
			<h1 class="text-2xl font-semibold">Account history</h1>
		</div>
		<p class="mt-1 text-sm text-muted-foreground">Charges, payments, credits, refunds, and corrections in one place.</p>
	</header>

	<div class="mx-auto max-w-6xl">
		{#if accountsQuery.data && accountsQuery.data.totalCount > 1}
			<div class="mb-5 max-w-lg" data-testid="portal-account-selector">
				<label class="mb-1.5 block text-sm font-medium" for="portal-payment-rental">Rental</label>
				<Select.Root type="single" value={selectedAccountId == null ? '' : String(selectedAccountId)} onValueChange={(value) => selectAccount(value ?? '')}>
					<Select.Trigger id="portal-payment-rental" class="w-full">
						{selectedAccount ? `${selectedAccount.propertyName} · Unit ${selectedAccount.unitNumber}` : 'Choose a rental'}
					</Select.Trigger>
					<Select.Content>
						{#each accountsQuery.data.items as account (account.tenantAccountId)}
							<Select.Item value={String(account.tenantAccountId)} label={`${account.propertyName} · Unit ${account.unitNumber}`}>
								{account.propertyName} · Unit {account.unitNumber}
							</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
			</div>
		{/if}

		{#if accountsQuery.isLoading}
			<LoadingState label="Loading tenant accounts" variant="page" testid="portal-payment-accounts-loading" />
		{:else if accountsQuery.isError}
			<div class="border-y border-destructive/40 py-8 text-center">
				<p class="text-sm font-medium text-destructive">Couldn't load tenant accounts.</p>
				<Button type="button" variant="outline" size="sm" class="mt-3" onclick={() => accountsQuery.refetch()}>Try again</Button>
			</div>
		{:else if accountsQuery.data?.totalCount === 0}
			<p class="text-sm text-muted-foreground">No tenant account is available.</p>
		{:else if selectedAccountId == null}
			<p class="text-sm text-muted-foreground" data-testid="portal-account-required">Choose an account to view its history.</p>
		{:else if historyQuery.isLoading}
			<LoadingState label="Loading account history" variant="page" testid="portal-history-loading" />
		{:else if historyQuery.isError}
			<div class="border-y border-destructive/40 py-8 text-center">
				<p class="text-sm font-medium text-destructive">Couldn't load your account history.</p>
				<Button type="button" variant="outline" size="sm" class="mt-3" onclick={() => historyQuery.refetch()}>Try again</Button>
			</div>
		{:else if historyQuery.data}
			{@const history = historyQuery.data}
			<section class="border-b border-border pb-6" aria-labelledby="current-due-heading">
				<p id="current-due-heading" class="text-sm font-medium text-muted-foreground">
					{history.currentDue < 0 ? 'Account credit' : 'Current due'}
				</p>
				<p class="mt-1 text-4xl font-semibold tabular-nums" data-testid="portal-current-due">
					{money(history.currentDue, history.currency)}
				</p>
				<p class="mt-2 max-w-2xl text-sm text-muted-foreground">
					{history.currentDue < 0
						? 'This credit reduces what you owe next.'
						: 'Includes unpaid rent, fees, and deposit charges. Held security deposits are not included.'}
				</p>
			</section>

			<div class="grid gap-5 border-b border-border py-5 md:grid-cols-[minmax(0,1fr)_minmax(18rem,0.65fr)]">
				<div class="max-w-sm">
					<label class="mb-1.5 block text-sm font-medium" for="portal-history-period">Period</label>
					<Select.Root type="single" value={period} onValueChange={selectPeriod}>
						<Select.Trigger id="portal-history-period" class="w-full" data-testid="portal-history-period">
							{period === 'currentMonth' ? 'Current month' : period === 'previousMonth' ? 'Previous month' : period === 'last3Months' ? 'Last 3 months' : period === 'thisYear' ? 'This year' : 'All history'}
						</Select.Trigger>
						<Select.Content>
							<Select.Item value="currentMonth" label="Current month">Current month</Select.Item>
							<Select.Item value="previousMonth" label="Previous month">Previous month</Select.Item>
							<Select.Item value="last3Months" label="Last 3 months">Last 3 months</Select.Item>
							<Select.Item value="thisYear" label="This year">This year</Select.Item>
							<Select.Item value="all" label="All history">All history</Select.Item>
						</Select.Content>
					</Select.Root>
				</div>

				<div aria-labelledby="autopay-heading">
					<div class="flex items-center gap-2"><Repeat class="h-4 w-4 text-primary" /><h2 id="autopay-heading" class="font-semibold">Autopay</h2></div>
					{#if autopayQuery.isLoading}
						<LoadingState label="Loading autopay settings" variant="spinner" testid="portal-autopay-loading" />
					{:else if autopayQuery.isError}
						<div class="mt-2 flex flex-wrap items-center gap-3">
							<p class="text-sm text-destructive">Couldn't load autopay settings.</p>
							<Button type="button" variant="outline" size="sm" onclick={() => autopayQuery.refetch()}>Try again</Button>
						</div>
					{:else if autopayQuery.data?.active}
						<div class="mt-2 flex flex-wrap items-center gap-3">
							<p class="text-sm text-muted-foreground">Autopay is on.</p>
							<Button variant="outline" size="sm" disabled={cancelMutation.isPending} onclick={() => cancelMutation.mutate()} data-testid="portal-autopay-cancel">
								{cancelMutation.isPending ? 'Turning off…' : 'Turn off'}
							</Button>
						</div>
					{:else}
						<div class="mt-2 flex flex-wrap items-center gap-3">
							<p class="text-sm text-muted-foreground">{onlinePaymentsUnavailable ? 'Autopay is not available right now.' : 'Pay rent automatically each month.'}</p>
							<Button size="sm" disabled={onlinePaymentsUnavailable || enrollMutation.isPending} onclick={() => enrollMutation.mutate()} data-testid="portal-autopay-enroll">
								{enrollMutation.isPending ? 'Opening…' : 'Set up autopay'}
							</Button>
						</div>
					{/if}
				</div>
			</div>

			<div class="flex items-center justify-between border-b border-border py-4 font-semibold" data-testid="portal-beginning-balance">
				<span>Beginning balance</span>
				<span class="tabular-nums {history.beginningBalance < 0 ? 'text-emerald-700 dark:text-emerald-400' : ''}">
					{balanceMoney(history.beginningBalance, history.currency)}
				</span>
			</div>

			<div class="hidden grid-cols-[8rem_minmax(0,1fr)_9rem_9rem_7rem] gap-4 border-b border-border py-3 text-xs font-medium uppercase tracking-wide text-muted-foreground md:grid">
				<span>Date</span><span>Transaction</span><span class="text-right">Amount</span><span class="text-right">Balance</span><span></span>
			</div>

			<div data-testid="portal-account-history-list">
				{#each history.items as entry (entry.tenantLedgerEntryId)}
					<div
						id={`portal-ledger-entry-${entry.tenantLedgerEntryId}`}
						class="grid gap-2 border-b border-border py-4 md:grid-cols-[8rem_minmax(0,1fr)_9rem_9rem_7rem] md:items-center md:gap-4 {entry.isFocused ? 'bg-primary/5 outline outline-2 outline-primary/40' : ''}"
						data-focused={entry.isFocused}
						data-testid="portal-history-row"
					>
						<time class="font-medium" datetime={entry.effectiveOn}>{formatDateOnly(entry.effectiveOn) || entry.effectiveOn}</time>
						<div class="min-w-0">
							<p class="font-medium">{entryLabel(entry)}</p>
							<p class="mt-0.5 text-sm text-muted-foreground">{entry.displayType}</p>
						</div>
						<div class="flex justify-between gap-4 md:block md:text-right">
							<span class="text-sm text-muted-foreground md:hidden">Amount</span>
							<span class="font-medium tabular-nums {entry.signedAmount < 0 ? 'text-emerald-700 dark:text-emerald-400' : ''}">{signedMoney(entry.signedAmount, history.currency)}</span>
						</div>
						<div class="flex justify-between gap-4 md:block md:text-right">
							<span class="text-sm text-muted-foreground md:hidden">Balance</span>
							<span class="tabular-nums text-muted-foreground">{balanceMoney(entry.runningBalance, history.currency)}</span>
						</div>
						<div class="md:text-right">
							{#if entry.payable}
								<Button size="sm" disabled={onlinePaymentsUnavailable || (payMutation.isPending && payingId === entry.tenantLedgerEntryId)} onclick={() => payNow(entry)} data-testid="portal-payment-pay-now">
									{payMutation.isPending && payingId === entry.tenantLedgerEntryId ? 'Opening…' : 'Pay now'}
								</Button>
							{/if}
						</div>
					</div>
				{:else}
					<p class="border-b border-border py-8 text-center text-sm text-muted-foreground">No account activity in this period.</p>
				{/each}
			</div>

			<div class="flex items-center justify-between border-b border-border py-4 font-semibold" data-testid="portal-closing-balance">
				<span>Closing balance</span>
				<span class="tabular-nums {history.closingBalance < 0 ? 'text-emerald-700 dark:text-emerald-400' : ''}">
					{balanceMoney(history.closingBalance, history.currency)}
				</span>
			</div>

			{#if history.totalCount > pageSize}
				<nav class="mt-5 flex items-center justify-between" aria-label="Account history pages">
					<Button variant="outline" disabled={skip === 0} onclick={() => (skip = Math.max(0, skip - pageSize))}>Previous</Button>
					<span class="text-sm text-muted-foreground">{skip + 1}–{Math.min(skip + pageSize, history.totalCount)} of {history.totalCount}</span>
					<Button variant="outline" disabled={skip + pageSize >= history.totalCount} onclick={() => (skip += pageSize)}>Next</Button>
				</nav>
			{/if}
		{/if}
	</div>
</div>
