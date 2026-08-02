<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import { onMount, tick } from 'svelte';
	import {
		portal,
		type PortalLeaseRelationship,
		type PortalTenantAccountHistoryItem,
		type PortalTenantAccountHistoryPeriod
	} from '$lib/api/endpoints/portal';
	import { ApiError } from '$lib/api/client';
	import { formatAccountingCurrency, formatAccountingDate, accountingAmountClass } from '$lib/accounting/accounting-display';
	import { tenantLedgerLabel } from '$lib/portal/tenant-ledger';
	import { Button } from '$lib/components/ui/button';
	import * as Select from '$lib/components/ui/select';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import { apiErrorMessage, showError, showInfo, showSuccess } from '$lib/utils/toast';
	import { CreditCard, Printer, Repeat } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const pageSize = 20;
	const requestedAccountId = Number(page.url.searchParams.get('account'));
	const requestedEntryId = Number(page.url.searchParams.get('entry'));
	const focusedEntryId =
		Number.isInteger(requestedEntryId) && requestedEntryId > 0 ? requestedEntryId : undefined;

	let selectedAccountId = $state<number | null>(
		Number.isInteger(requestedAccountId) && requestedAccountId > 0 ? requestedAccountId : null
	);
	let period = $state<PortalTenantAccountHistoryPeriod>('all');
	let skip = $state(0);
	let paymentProviderUnavailable = $state(false);
	let payingId = $state<number | null>(null);

	const accountsQuery = createQuery(() => ({
		queryKey: ['portal-tenant-accounts', 'account-history'],
		queryFn: () => portal.tenantAccountsPage({ take: 200, sort: 'propertyName' })
	}));
	const leasesQuery = createQuery(() => ({
		queryKey: ['portal-tenant-leases', 'account-history'],
		queryFn: () => portal.leases()
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

	const selectedLease = $derived(
		leasesQuery.data?.find(
			(lease: PortalLeaseRelationship) =>
				lease.tenantAccountId === selectedAccountId ||
				lease.leaseManagementId === selectedAccount?.leaseManagementId
		)
	);

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
		period = (value || 'all') as PortalTenantAccountHistoryPeriod;
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

	function printStatement() {
		window.print();
	}

	onMount(() => {
		const result = page.url.searchParams.get('checkout');
		if (result === 'success') {
			showSuccess('Payment received — thank you');
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
</script>

<svelte:head><title>Account history - Rental Command</title></svelte:head>

<div class="h-full overflow-y-auto p-4 sm:p-6" data-testid="portal-payments-page">
	<header class="mx-auto mb-6 flex max-w-6xl items-start justify-between gap-4">
		<div>
			<div class="flex items-center gap-2">
				<CreditCard class="h-5 w-5 text-primary" />
				<h1 class="text-2xl font-semibold">Your account</h1>
			</div>
			<p class="mt-1 text-sm text-muted-foreground">See your balance and account history.</p>
		</div>
	</header>

	<div class="mx-auto max-w-6xl">
		{#if accountsQuery.data && accountsQuery.data.totalCount > 1}
			<div class="mb-5 max-w-lg" data-testid="portal-account-selector">
				<label class="mb-1.5 block text-sm font-medium" for="portal-payment-rental">Rental</label>
				<Select.Root
					type="single"
					value={selectedAccountId == null ? '' : String(selectedAccountId)}
					onValueChange={(value) => selectAccount(value ?? '')}
				>
					<Select.Trigger id="portal-payment-rental" class="w-full">
						{selectedAccount ? `${selectedAccount.propertyName} · Unit ${selectedAccount.unitNumber}` : 'Choose an account'}
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
			<div class="border-y border-destructive/40 py-8 text-center" data-testid="portal-account-error">
				<p class="text-sm font-medium text-destructive">Couldn't load your account.</p>
				<Button type="button" variant="outline" size="sm" class="mt-3" onclick={() => accountsQuery.refetch()}>Try again</Button>
			</div>
		{:else if accountsQuery.data?.totalCount === 0}
			<div class="border-y border-border py-8 text-center" data-testid="portal-account-empty">
				<p class="font-medium">No account history yet.</p>
				<p class="mt-1 text-sm text-muted-foreground">Your account will appear here when your rental relationship is ready.</p>
			</div>
		{:else if selectedAccountId == null}
			<p class="border-y border-border py-8 text-center text-sm text-muted-foreground" data-testid="portal-account-required">Choose an account to view its history.</p>
		{:else if historyQuery.isLoading}
			<LoadingState label="Loading account history" variant="page" testid="portal-history-loading" />
		{:else if historyQuery.isError}
			<div class="border-y border-destructive/40 py-8 text-center" data-testid="portal-history-error">
				<p class="text-sm font-medium text-destructive">Couldn't load your account history.</p>
				<Button type="button" variant="outline" size="sm" class="mt-3" onclick={() => historyQuery.refetch()}>Try again</Button>
			</div>
		{:else if historyQuery.data && selectedAccount}
			{@const history = historyQuery.data}
			<section class="screen-ledger" aria-labelledby="account-summary-heading">
				<div class="mb-5 flex flex-wrap items-center justify-between gap-3">
					<div>
						<p class="text-sm font-medium text-muted-foreground">{selectedAccount.propertyName} · Unit {selectedAccount.unitNumber}</p>
						<h2 id="account-summary-heading" class="mt-1 text-xl font-semibold">Account summary</h2>
					</div>
					<Button type="button" variant="outline" class="gap-2" onclick={printStatement} data-testid="portal-print-statement">
						<Printer class="h-4 w-4" />
						Print statement
					</Button>
				</div>

				<div class="grid gap-3 sm:grid-cols-2 xl:grid-cols-4" data-testid="portal-balance-header">
					<div class="rounded-xl border border-border bg-card p-4" data-testid="portal-current-balance">
						<p class="text-sm text-muted-foreground">Current balance</p>
						<p class="mt-2 text-2xl font-semibold tabular-nums {accountingAmountClass(selectedAccount.receivableBalance)}">
							{formatAccountingCurrency(selectedAccount.receivableBalance, selectedAccount.currency)}
						</p>
					</div>
					<div class="rounded-xl border border-border bg-card p-4" data-testid="portal-past-due">
						<p class="text-sm text-muted-foreground">Past due</p>
						<p class="mt-2 text-2xl font-semibold tabular-nums {accountingAmountClass(selectedAccount.pastDueAmount)}">
							{formatAccountingCurrency(selectedAccount.pastDueAmount, selectedAccount.currency)}
						</p>
					</div>
					<div class="rounded-xl border border-border bg-card p-4" data-testid="portal-next-due">
						<p class="text-sm text-muted-foreground">Next due</p>
						{#if selectedAccount.nextDueOn}
							<p class="mt-2 font-semibold">{formatAccountingDate(selectedAccount.nextDueOn)}</p>
							<p class="mt-0.5 text-2xl font-semibold tabular-nums {accountingAmountClass(selectedAccount.nextDueAmount)}">
								{formatAccountingCurrency(selectedAccount.nextDueAmount, selectedAccount.currency)}
							</p>
						{:else}
							<p class="mt-2 font-semibold">No upcoming charge</p>
						{/if}
					</div>
					<div class="rounded-xl border border-border bg-card p-4" data-testid="portal-deposit-held">
						<p class="text-sm text-muted-foreground">Deposit held</p>
						{#if selectedAccount.deposit}
							<p class="mt-2 text-2xl font-semibold tabular-nums {accountingAmountClass(selectedAccount.deposit.heldBalance)}">
								{formatAccountingCurrency(selectedAccount.deposit.heldBalance, selectedAccount.deposit.currency)}
							</p>
						{:else}
							<p class="mt-2 font-semibold">No deposit held</p>
						{/if}
					</div>
				</div>

				<div class="mt-6 grid gap-5 border-b border-border py-5 md:grid-cols-[minmax(0,1fr)_minmax(18rem,0.65fr)]">
					<div class="max-w-sm">
						<label class="mb-1.5 block text-sm font-medium" for="portal-history-period">Statement period</label>
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

				<h2 class="mt-7 text-lg font-semibold" id="account-history-heading">Account history</h2>
				<div class="mt-3 flex items-center justify-between border-y border-border py-4 font-semibold" data-testid="portal-beginning-balance">
					<span>Beginning balance</span>
					<span class="tabular-nums {accountingAmountClass(history.beginningBalance)}">
						{formatAccountingCurrency(history.beginningBalance, history.currency)}
					</span>
				</div>

				<div class="hidden grid-cols-[8rem_minmax(0,1fr)_9rem_9rem_7rem] gap-4 border-b border-border py-3 text-xs font-medium uppercase tracking-wide text-muted-foreground md:grid">
					<span>Date</span><span>What happened</span><span class="text-right">Amount</span><span class="text-right">Balance</span><span></span>
				</div>

				<div data-testid="portal-account-history-list">
					{#each history.items as entry (entry.tenantLedgerEntryId)}
						<div
							id={`portal-ledger-entry-${entry.tenantLedgerEntryId}`}
							class="grid gap-2 border-b border-border py-4 md:grid-cols-[8rem_minmax(0,1fr)_9rem_9rem_7rem] md:items-center md:gap-4 {entry.isFocused ? 'bg-primary/5 outline outline-2 outline-primary/40' : ''}"
							data-focused={entry.isFocused}
							data-testid="portal-history-row"
						>
							<time class="font-medium" datetime={entry.effectiveOn}>{formatAccountingDate(entry.effectiveOn)}</time>
							<!-- The server's entry.displayType stays private; tenantLedgerLabel owns visible vocabulary. -->
							<div class="min-w-0">
								<p class="font-medium">{tenantLedgerLabel(entry)}</p>
							</div>
							<div class="flex justify-between gap-4 md:block md:text-right">
								<span class="text-sm text-muted-foreground md:hidden">Amount</span>
								<span class="font-medium tabular-nums {accountingAmountClass(entry.signedAmount)}">{formatAccountingCurrency(entry.signedAmount, history.currency)}</span>
							</div>
							<div class="flex justify-between gap-4 md:block md:text-right">
								<span class="text-sm text-muted-foreground md:hidden">Balance</span>
								<span class="tabular-nums {accountingAmountClass(entry.runningBalance)}">{formatAccountingCurrency(entry.runningBalance, history.currency)}</span>
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
					<span class="tabular-nums {accountingAmountClass(history.closingBalance)}">
						{formatAccountingCurrency(history.closingBalance, history.currency)}
					</span>
				</div>

				{#if history.totalCount > pageSize}
					<nav class="mt-5 flex items-center justify-between" aria-label="Account history pages">
						<Button variant="outline" disabled={skip === 0} onclick={() => (skip = Math.max(0, skip - pageSize))}>Previous</Button>
						<span class="text-sm text-muted-foreground">{skip + 1}–{Math.min(skip + pageSize, history.totalCount)} of {history.totalCount}</span>
						<Button variant="outline" disabled={skip + pageSize >= history.totalCount} onclick={() => (skip += pageSize)}>Next</Button>
					</nav>
				{/if}
			</section>

			<section class="print-statement" aria-labelledby="print-statement-heading">
				<h1 id="print-statement-heading">Account statement</h1>
				<p class="print-property">{selectedAccount.propertyName} · Unit {selectedAccount.unitNumber}</p>
				<p>{selectedLease?.tenantName || 'Tenant account'}</p>
				<p class="print-date-range">
					{formatAccountingDate(history.periodFrom ?? selectedAccount.openedAtUtc)} – {formatAccountingDate(history.periodTo)}
				</p>
				<p class="print-closing-balance">Closing balance: {formatAccountingCurrency(history.closingBalance, history.currency)}</p>

				<table>
					<thead>
						<tr><th scope="col">Date</th><th scope="col">What happened</th><th scope="col">Amount</th><th scope="col">Balance</th></tr>
					</thead>
					<tbody>
						{#each history.items as entry (entry.tenantLedgerEntryId)}
							<tr>
								<td>{formatAccountingDate(entry.effectiveOn)}</td>
								<td>{tenantLedgerLabel(entry)}</td>
								<td>{formatAccountingCurrency(entry.signedAmount, history.currency)}</td>
								<td>{formatAccountingCurrency(entry.runningBalance, history.currency)}</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</section>
		{/if}
	</div>
</div>

<style>
	.print-statement {
		display: none;
	}

	@media print {
		@page {
			margin: 0.7in;
		}

		:global(body) {
			background: #fff !important;
		}

		:global(body > div) {
			visibility: hidden !important;
		}

		.screen-ledger {
			display: none !important;
		}

		.print-statement {
			position: absolute;
			inset: 0;
			display: block !important;
			visibility: visible !important;
			color: #111;
			font-family: ui-sans-serif, system-ui, sans-serif;
			font-size: 11pt;
		}

		.print-statement * {
			visibility: visible !important;
		}

		.print-statement h1 {
			margin: 0 0 0.15in;
			font-size: 20pt;
		}

		.print-statement p {
			margin: 0.04in 0;
		}

		.print-property,
		.print-date-range {
			color: #444;
		}

		.print-closing-balance {
			margin-top: 0.25in !important;
			font-weight: 700;
		}

		.print-statement table {
			width: 100%;
			margin-top: 0.3in;
			border-collapse: collapse;
		}

		.print-statement th,
		.print-statement td {
			border-bottom: 1px solid #ccc;
			padding: 0.1in 0.06in;
			text-align: left;
		}

		.print-statement th:nth-child(n + 3),
		.print-statement td:nth-child(n + 3) {
			text-align: right;
		}
	}
</style>
