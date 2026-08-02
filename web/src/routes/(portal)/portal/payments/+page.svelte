<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import { onMount } from 'svelte';
	import {
		portal,
		type PortalTenantAccountHistoryItem,
		type PortalTenantLedgerRow,
		type PortalTenantMonthSummary
	} from '$lib/api/endpoints/portal';
	import { ApiError } from '$lib/api/client';
	import { Button } from '$lib/components/ui/button';
	import * as Select from '$lib/components/ui/select';
	import * as Dialog from '$lib/components/ui/dialog';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import MonthGroup from '$lib/components/ledger/MonthGroup.svelte';
	import LedgerAmount from '$lib/components/ledger/LedgerAmount.svelte';
	import LedgerTypeBadge from '$lib/components/ledger/LedgerTypeBadge.svelte';
	import { apiErrorMessage, showError, showInfo, showSuccess } from '$lib/utils/toast';
	import { formatDateOnly } from '$lib/utils/date';
	import { CreditCard, FileText, Repeat } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const ledgerTake = 200;
	const requestedAccountId = Number(page.url.searchParams.get('account'));
	type PeriodMonths = 3 | 6 | 9 | 12;

	let selectedAccountId = $state<number | null>(
		Number.isInteger(requestedAccountId) && requestedAccountId > 0 ? requestedAccountId : null
	);
	let periodMonths = $state<PeriodMonths>(3);
	let paymentProviderUnavailable = $state(false);
	let payingId = $state<number | null>(null);
	let selectedLedgerRow = $state<PortalTenantLedgerRow | null>(null);

	function periodRange(months: PeriodMonths) {
		const now = new Date();
		const from = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() - months + 1, 1));
		const to = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() + 1, 0));
		return { from: from.toISOString().slice(0, 10), to: to.toISOString().slice(0, 10) };
	}
	const range = $derived(periodRange(periodMonths));

	function belongsToMonth(row: PortalTenantLedgerRow, month: PortalTenantMonthSummary) {
		return Number(row.effectiveOn.slice(0, 4)) === month.year && Number(row.effectiveOn.slice(5, 7)) === month.month;
	}

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
		queryKey: ['portal-tenant-account-history', selectedAccountId, 'currentMonth'],
		queryFn: () => portal.tenantAccountHistory(selectedAccountId as number, { period: 'currentMonth', take: ledgerTake }),
		enabled: selectedAccountId != null
	}));
	const ledgerQuery = createQuery(() => ({
		queryKey: ['portal-tenant-ledger', selectedAccountId, periodMonths, range.from, range.to],
		queryFn: () => portal.tenantAccountLedger(selectedAccountId as number, { ...range, skip: 0, take: ledgerTake }),
		enabled: selectedAccountId != null
	}));
	const monthSummaryQuery = createQuery(() => ({
		queryKey: ['portal-tenant-month-summary', selectedAccountId, periodMonths, range.from, range.to],
		queryFn: () => portal.tenantAccountMonthSummary(selectedAccountId as number, range),
		enabled: selectedAccountId != null
	}));
	const detailQuery = createQuery(() => ({
		queryKey: ['portal-tenant-ledger-entry', selectedAccountId, selectedLedgerRow?.tenantLedgerEntryId],
		queryFn: () => portal.tenantAccountLedgerEntry(selectedAccountId as number, selectedLedgerRow!.tenantLedgerEntryId),
		enabled: selectedAccountId != null && selectedLedgerRow != null
	}));

	$effect(() => {
		void selectedAccountId;
		selectedLedgerRow = null;
	});

	function updateRoute(accountId: number | null) {
		const url = new URL(page.url);
		if (accountId == null) url.searchParams.delete('account');
		else url.searchParams.set('account', String(accountId));
		goto(url.pathname + url.search, { replaceState: true, noScroll: true, keepFocus: true });
	}

	function selectAccount(value: string) {
		selectedAccountId = value ? Number(value) : null;
		paymentProviderUnavailable = false;
		updateRoute(selectedAccountId);
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
		mutationFn: () => portal.autopayEnroll(selectedAccountId as number, { operationKey: crypto.randomUUID(), ...returnUrls() }),
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
		onError: (error) => showError(apiErrorMessage(error, "We couldn't turn off autopay. Please try again."))
	}));

	const autopayQuery = createQuery(() => ({
		queryKey: ['portal-autopay', selectedAccountId],
		queryFn: () => portal.autopayStatus(selectedAccountId as number),
		enabled: selectedAccountId != null
	}));
	const onlinePaymentsUnavailable = $derived(autopayQuery.data?.onlinePaymentsAvailable === false || paymentProviderUnavailable);

	onMount(() => {
		const result = page.url.searchParams.get('checkout');
		if (result === 'success') {
			showSuccess('Payment received — thanks!');
			queryClient.invalidateQueries({ queryKey: ['portal-tenant-account-history'] });
			queryClient.invalidateQueries({ queryKey: ['portal-tenant-ledger'] });
			queryClient.invalidateQueries({ queryKey: ['portal-tenant-month-summary'] });
			queryClient.invalidateQueries({ queryKey: ['portal-tenant-accounts'] });
			queryClient.invalidateQueries({ queryKey: ['portal-autopay'] });
		} else if (result === 'cancel') showInfo('Payment canceled.');
		if (result) {
			const url = new URL(page.url);
			url.searchParams.delete('checkout');
			goto(url.pathname + url.search, { replaceState: true, noScroll: true, keepFocus: true });
		}
	});
</script>

<svelte:head><title>Money - Rental Command</title></svelte:head>

<div class="h-full overflow-y-auto p-4 sm:p-6" data-testid="portal-payments-page">
	<header class="mx-auto mb-6 max-w-6xl">
		<div class="flex items-center gap-2"><CreditCard class="h-5 w-5 text-primary" /><h1 class="text-2xl font-semibold">Money</h1></div>
		<p class="mt-1 text-sm text-muted-foreground">Every charge, payment, and credit with the amount owed after each entry.</p>
	</header>

	<div class="mx-auto max-w-6xl">
		{#if accountsQuery.data && accountsQuery.data.totalCount > 1}
			<div class="mb-5 max-w-lg" data-testid="portal-account-selector">
				<label class="mb-1.5 block text-sm font-medium" for="portal-payment-rental">Rental</label>
				<Select.Root type="single" value={selectedAccountId == null ? '' : String(selectedAccountId)} onValueChange={(value) => selectAccount(value ?? '')}>
					<Select.Trigger id="portal-payment-rental" class="w-full">{selectedAccount ? `${selectedAccount.propertyName} · Unit ${selectedAccount.unitNumber}` : 'Choose a rental'}</Select.Trigger>
					<Select.Content>{#each accountsQuery.data.items as account (account.tenantAccountId)}<Select.Item value={String(account.tenantAccountId)} label={`${account.propertyName} · Unit ${account.unitNumber}`}>{account.propertyName} · Unit {account.unitNumber}</Select.Item>{/each}</Select.Content>
				</Select.Root>
			</div>
		{/if}

		{#if accountsQuery.isLoading}
			<LoadingState label="Loading tenant accounts" variant="page" testid="portal-payment-accounts-loading" />
		{:else if accountsQuery.isError}
			<div class="border-y border-destructive/40 py-8 text-center"><p class="text-sm font-medium text-destructive">Couldn't load tenant accounts.</p><Button type="button" variant="outline" size="sm" class="mt-3" onclick={() => accountsQuery.refetch()}>Try again</Button></div>
		{:else if accountsQuery.data?.totalCount === 0}
			<p class="text-sm text-muted-foreground">No tenant account is available.</p>
		{:else if selectedAccountId == null}
			<p class="text-sm text-muted-foreground" data-testid="portal-account-required">Choose an account to view its history.</p>
		{:else}
			<section class="border-b border-border pb-6" aria-labelledby="current-due-heading">
				<p id="current-due-heading" class="text-sm font-medium text-muted-foreground">{(historyQuery.data?.currentDue ?? selectedAccount?.receivableBalance ?? 0) < 0 ? 'Account credit' : 'Current due'}</p>
				<p class="mt-1 text-4xl font-semibold tabular-nums" data-testid="portal-current-due"><LedgerAmount amount={historyQuery.data?.currentDue ?? selectedAccount?.receivableBalance ?? 0} currency={selectedAccount?.currency ?? 'USD'} /></p>
				<p class="mt-2 max-w-2xl text-sm text-muted-foreground">{(historyQuery.data?.currentDue ?? selectedAccount?.receivableBalance ?? 0) < 0 ? 'This credit reduces what you owe next.' : 'Includes unpaid rent, fees, and deposit charges. Held security deposits are not included.'}</p>
			</section>

			<div class="grid gap-5 border-b border-border py-5 md:grid-cols-[minmax(0,1fr)_minmax(18rem,0.65fr)]">
				<div><div class="flex flex-wrap items-center justify-between gap-3"><div><h2 class="font-semibold">Account history</h2><p class="text-sm text-muted-foreground">Choose how much server-generated history to show.</p></div><Button variant="outline" size="sm" href={`/portal/statement?account=${selectedAccountId}&from=${range.from}&to=${range.to}`} data-testid="portal-statement-link"><FileText class="h-4 w-4" /> Statement</Button></div><div class="mt-3 inline-flex rounded-lg bg-muted p-1" role="group" aria-label="Ledger period" data-testid="portal-money-period-switch">{#each [3, 6, 9, 12] as months}<button type="button" class="rounded-md px-3 py-1.5 text-sm font-medium transition-colors {periodMonths === months ? 'bg-background text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground'}" aria-pressed={periodMonths === months} onclick={() => (periodMonths = months as PeriodMonths)} data-testid={`portal-money-period-${months}`}>{months} mo</button>{/each}</div></div>
				<div aria-labelledby="autopay-heading"><div class="flex items-center gap-2"><Repeat class="h-4 w-4 text-primary" /><h2 id="autopay-heading" class="font-semibold">Autopay</h2></div>{#if autopayQuery.isLoading}<LoadingState label="Loading autopay settings" variant="spinner" testid="portal-autopay-loading" />{:else if autopayQuery.isError}<div class="mt-2 flex flex-wrap items-center gap-3"><p class="text-sm text-destructive">Couldn't load autopay settings.</p><Button type="button" variant="outline" size="sm" onclick={() => autopayQuery.refetch()}>Try again</Button></div>{:else if autopayQuery.data?.active}<div class="mt-2 flex flex-wrap items-center gap-3"><p class="text-sm text-muted-foreground">Autopay is on.</p><Button variant="outline" size="sm" disabled={cancelMutation.isPending} onclick={() => cancelMutation.mutate()} data-testid="portal-autopay-cancel">{cancelMutation.isPending ? 'Turning off…' : 'Turn off'}</Button></div>{:else}<div class="mt-2 flex flex-wrap items-center gap-3"><p class="text-sm text-muted-foreground">{onlinePaymentsUnavailable ? 'Autopay is not available right now.' : 'Pay rent automatically each month.'}</p><Button size="sm" disabled={onlinePaymentsUnavailable || enrollMutation.isPending} onclick={() => enrollMutation.mutate()} data-testid="portal-autopay-enroll">{enrollMutation.isPending ? 'Opening…' : 'Set up autopay'}</Button></div>{/if}</div>
			</div>

			{#if ledgerQuery.isLoading || monthSummaryQuery.isLoading}
				<LoadingState label="Loading account history" variant="page" testid="portal-history-loading" />
			{:else if ledgerQuery.isError || monthSummaryQuery.isError}
				<div class="border-y border-destructive/40 py-8 text-center"><p class="text-sm font-medium text-destructive">Couldn't load your account history.</p><Button type="button" variant="outline" size="sm" class="mt-3" onclick={() => { ledgerQuery.refetch(); monthSummaryQuery.refetch(); }}>Try again</Button></div>
			{:else if (monthSummaryQuery.data?.length ?? 0) === 0}
				<p class="border-b border-border py-8 text-center text-sm text-muted-foreground">No account activity in this period.</p>
			{:else}
				<div class="space-y-4 py-5" data-testid="portal-account-history-list">
					{#each monthSummaryQuery.data ?? [] as month (`${month.year}-${month.month}`)}
						<MonthGroup {month}>
							<div class="hidden grid-cols-[7rem_minmax(0,1fr)_8rem_9rem_9rem] gap-3 border-b px-4 py-2 text-xs font-medium uppercase tracking-wide text-muted-foreground md:grid"><span>Date</span><span>What happened</span><span class="text-right">Charges</span><span class="text-right">Payments / credits</span><span class="text-right">Amount owed</span></div>
							{#each ledgerQuery.data?.items ?? [] as row (row.tenantLedgerEntryId)}
								{#if belongsToMonth(row, month)}
									<button type="button" class="grid w-full gap-2 border-b px-4 py-3 text-left transition-colors hover:bg-muted/40 md:grid-cols-[7rem_minmax(0,1fr)_8rem_9rem_9rem] md:items-center md:gap-3" onclick={() => (selectedLedgerRow = row)} data-testid={`portal-history-row-${row.tenantLedgerEntryId}`}>
										<div><time class="text-sm font-medium" datetime={row.effectiveOn}>{formatDateOnly(row.effectiveOn)}</time><p class="text-xs text-muted-foreground">Entered {formatDateOnly(row.postedAtUtc)}</p></div>
										<div class="min-w-0"><div class="flex flex-wrap items-center gap-2"><LedgerTypeBadge type={row.type} /><span class="font-medium">{row.description}</span></div>{#if row.sourceDocumentContext}<p class="mt-1 text-xs text-muted-foreground">{row.sourceDocumentContext}</p>{/if}</div>
										<div class="flex justify-between text-sm md:block md:text-right"><span class="text-muted-foreground md:hidden">Charges</span>{#if row.chargeAmount}<LedgerAmount amount={row.chargeAmount} currency={row.currency} />{:else}<span class="text-muted-foreground">—</span>{/if}</div>
										<div class="flex justify-between text-sm md:block md:text-right"><span class="text-muted-foreground md:hidden">Payments / credits</span>{#if row.paymentAmount}<LedgerAmount amount={row.paymentAmount} currency={row.currency} tone="payment" />{:else if row.creditAmount}<LedgerAmount amount={row.creditAmount} currency={row.currency} tone="credit" />{:else}<span class="text-muted-foreground">—</span>{/if}</div>
										<div class="flex items-center justify-between gap-2 text-sm md:justify-end"><span class="text-muted-foreground md:hidden">Amount owed</span><LedgerAmount amount={row.runningAmountOwed} currency={row.currency} />{#if historyQuery.data?.items.find((entry) => entry.tenantLedgerEntryId === row.tenantLedgerEntryId && entry.payable)}{@const payableEntry = historyQuery.data.items.find((entry) => entry.tenantLedgerEntryId === row.tenantLedgerEntryId && entry.payable)}{#if payableEntry}<Button size="sm" disabled={onlinePaymentsUnavailable || (payMutation.isPending && payingId === row.tenantLedgerEntryId)} onclick={(event) => { event.stopPropagation(); payNow(payableEntry); }} data-testid="portal-payment-pay-now">{payMutation.isPending && payingId === row.tenantLedgerEntryId ? 'Opening…' : 'Pay now'}</Button>{/if}{/if}</div>
									</button>
								{/if}
							{/each}
						</MonthGroup>
					{/each}
				</div>
				{#if ledgerQuery.data && ledgerQuery.data.totalCount > ledgerQuery.data.items.length}<p class="text-sm text-destructive" role="alert">This period contains more than {ledgerQuery.data.items.length} entries. Choose a shorter period to see every row.</p>{/if}
			{/if}
		{/if}
	</div>
</div>

{#if selectedLedgerRow}
	<Dialog.Root open onOpenChange={(open) => { if (!open) selectedLedgerRow = null; }}>
		<Dialog.Content class="portal-ledger-sheet" data-testid="portal-ledger-row-sheet">
			<Dialog.Header><LedgerTypeBadge type={selectedLedgerRow.type} /><Dialog.Title>{selectedLedgerRow.description}</Dialog.Title><Dialog.Description>Effective {formatDateOnly(selectedLedgerRow.effectiveOn)} · Entered {new Date(selectedLedgerRow.postedAtUtc).toLocaleString()}</Dialog.Description></Dialog.Header>
			{#if detailQuery.isLoading}<LoadingState label="Loading receipt details" variant="section" />
			{:else if detailQuery.isError}<div role="alert"><p class="text-sm text-destructive">Couldn't load these details.</p><Button class="mt-3" variant="outline" size="sm" onclick={() => detailQuery.refetch()}>Try again</Button></div>
			{:else if detailQuery.data}{@const detail = detailQuery.data}
				<section class="portal-detail-grid" aria-label="Receipt details"><div><span>Status</span><strong>{detail.status}</strong></div><div><span>Amount owed after entry</span><strong><LedgerAmount amount={detail.runningAmountOwed} currency={detail.currency} /></strong></div><div><span>Payment method</span><strong>{detail.paymentMethod ?? 'Not applicable'}</strong></div><div><span>Reference</span><strong>{detail.reference ?? 'None'}</strong></div></section>
				<section class="portal-detail-section" aria-label="Allocations"><h3>Allocations</h3>{#if detail.allocations.length}{#each detail.allocations as allocation (allocation.targetPublicId)}<div class="portal-allocation"><span>Applied to {allocation.targetDescription}</span><LedgerAmount amount={allocation.amount} currency={detail.currency} /></div>{/each}{:else}<p>No allocations recorded.</p>{/if}</section>
				<section class="portal-detail-section" aria-label="Documents"><h3>Documents</h3><p>{detail.sourceDocumentContext ?? 'No documents attached.'}</p></section>
			{/if}
			<Dialog.Footer><Button variant="outline" onclick={() => (selectedLedgerRow = null)}>Close</Button></Dialog.Footer>
		</Dialog.Content>
	</Dialog.Root>
{/if}

<style>
	:global(.portal-ledger-sheet) { inset: 0 0 0 auto !important; transform: none !important; width: min(38rem, 100%) !important; max-width: none !important; max-height: 100dvh !important; height: 100dvh; border-radius: 1.25rem 0 0 1.25rem; background: var(--m3c-surface-container-low); }
	.portal-detail-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 0.75rem; }
	.portal-detail-grid div { display: grid; gap: 0.2rem; border-radius: 0.75rem; background: var(--m3c-surface-container-high); padding: 0.875rem; }
	.portal-detail-grid span, .portal-detail-section p { color: var(--m3c-on-surface-variant); font-size: 0.8125rem; }
	.portal-detail-grid strong { overflow-wrap: anywhere; }
	.portal-detail-section { display: grid; gap: 0.625rem; border: 1px solid var(--m3c-outline-variant); border-radius: 1rem; padding: 1rem; }
	.portal-detail-section h3 { font-weight: 700; }
	.portal-allocation { display: flex; justify-content: space-between; gap: 1rem; border-top: 1px solid var(--m3c-outline-variant); padding-top: 0.625rem; font-size: 0.875rem; }
	@media (max-width: 40rem) { :global(.portal-ledger-sheet) { border-radius: 0; } .portal-detail-grid { grid-template-columns: 1fr; } }
</style>
