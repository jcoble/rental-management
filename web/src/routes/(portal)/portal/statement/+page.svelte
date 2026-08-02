<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { page } from '$app/state';
	import { portal, type PortalTenantLedgerRow, type PortalTenantMonthSummary } from '$lib/api/endpoints/portal';
	import { Button } from '$lib/components/ui/button';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import MonthGroup from '$lib/components/ledger/MonthGroup.svelte';
	import LedgerAmount from '$lib/components/ledger/LedgerAmount.svelte';
	import LedgerTypeBadge from '$lib/components/ledger/LedgerTypeBadge.svelte';
	import { formatDateOnly } from '$lib/utils/date';
	import { apiErrorMessage, showError } from '$lib/utils/toast';
	import { ArrowLeft, Download, Printer } from '@lucide/svelte';

	const accountId = Number(page.url.searchParams.get('account'));
	const from = page.url.searchParams.get('from') ?? undefined;
	const to = page.url.searchParams.get('to') ?? undefined;
	const validAccountId = Number.isInteger(accountId) && accountId > 0;
	let downloading = $state(false);

	const accountQuery = createQuery(() => ({
		queryKey: ['portal-tenant-account', accountId],
		queryFn: () => portal.tenantAccount(accountId),
		enabled: validAccountId
	}));
	const statementQuery = createQuery(() => ({
		queryKey: ['portal-tenant-statement', accountId, from, to],
		queryFn: () => portal.tenantAccountStatement(accountId, { from, to }),
		enabled: validAccountId
	}));

	function belongsToMonth(row: PortalTenantLedgerRow, month: PortalTenantMonthSummary) {
		return Number(row.effectiveOn.slice(0, 4)) === month.year && Number(row.effectiveOn.slice(5, 7)) === month.month;
	}

	async function downloadCsv() {
		downloading = true;
		try {
			const blob = await portal.downloadTenantAccountStatementCsv(accountId, { from, to });
			const url = URL.createObjectURL(blob);
			const anchor = document.createElement('a');
			anchor.href = url;
			anchor.download = `tenant-statement-${from ?? 'start'}-${to ?? 'current'}.csv`;
			anchor.click();
			URL.revokeObjectURL(url);
		} catch (error) {
			showError(apiErrorMessage(error, "We couldn't download the statement."));
		} finally {
			downloading = false;
		}
	}
</script>

<svelte:head><title>Tenant statement - Rental Command</title></svelte:head>

<div class="statement-page" data-testid="portal-statement-page">
	<header class="statement-toolbar print:hidden">
		<Button variant="ghost" href={`/portal/payments${validAccountId ? `?account=${accountId}` : ''}`}><ArrowLeft class="h-4 w-4" /> Money</Button>
		<div class="actions"><Button variant="outline" onclick={downloadCsv} disabled={!validAccountId || downloading} data-testid="portal-statement-download"><Download class="h-4 w-4" />{downloading ? 'Downloading…' : 'Download CSV'}</Button><Button onclick={() => window.print()} data-testid="portal-statement-print"><Printer class="h-4 w-4" /> Print</Button></div>
	</header>

	{#if !validAccountId}
		<div class="state" role="alert"><h1>Statement unavailable</h1><p>Open a statement from the Money page so a rental is selected.</p></div>
	{:else if accountQuery.isLoading || statementQuery.isLoading}
		<LoadingState label="Loading statement" variant="page" testid="portal-statement-loading" />
	{:else if accountQuery.isError || statementQuery.isError}
		<div class="state" role="alert"><h1>Statement unavailable</h1><p>We couldn't load this statement.</p><Button class="print:hidden" variant="outline" onclick={() => { accountQuery.refetch(); statementQuery.refetch(); }}>Try again</Button></div>
	{:else if accountQuery.data && statementQuery.data}
		{@const account = accountQuery.data}
		{@const statement = statementQuery.data}
		<article class="statement" aria-label="Tenant account statement">
			<header class="statement-heading"><div><p class="eyebrow">Rental Command</p><h1>Account statement</h1><p>{account.propertyName} · Unit {account.unitNumber}</p><p>Account {account.accountNumber}</p></div><div class="period"><span>Statement period</span><strong>{formatDateOnly(statement.periodFrom ?? from ?? '') || 'Beginning'} – {formatDateOnly(statement.periodTo ?? to ?? '') || 'Current'}</strong></div></header>
			<section class="balance-summary"><div><span>Opening balance</span><LedgerAmount amount={statement.openingBalance} currency={account.currency} /></div><div><span>Closing balance</span><LedgerAmount amount={statement.closingBalance} currency={account.currency} /></div></section>

			{#if statement.monthSummaries.length === 0}
				<p class="empty">No account activity in this period.</p>
			{:else}
				<div class="month-list">
					{#each statement.monthSummaries as month (`${month.year}-${month.month}`)}
						<MonthGroup {month}>
							<div class="table-head"><span>Date</span><span>What happened</span><span>Charges</span><span>Payments / credits</span><span>Amount owed</span></div>
							{#each statement.rows as row (row.tenantLedgerEntryId)}
								{#if belongsToMonth(row, month)}
									<div class="statement-row" data-testid={`portal-statement-row-${row.tenantLedgerEntryId}`}><div><time datetime={row.effectiveOn}>{formatDateOnly(row.effectiveOn)}</time><small>Entered {formatDateOnly(row.postedAtUtc)}</small></div><div><LedgerTypeBadge type={row.type} /><strong>{row.description}</strong></div><div>{#if row.chargeAmount}<LedgerAmount amount={row.chargeAmount} currency={row.currency} />{:else}—{/if}</div><div>{#if row.paymentAmount}<LedgerAmount amount={row.paymentAmount} currency={row.currency} tone="payment" />{:else if row.creditAmount}<LedgerAmount amount={row.creditAmount} currency={row.currency} tone="credit" />{:else}—{/if}</div><div><LedgerAmount amount={row.runningAmountOwed} currency={row.currency} /></div></div>
								{/if}
							{/each}
						</MonthGroup>
					{/each}
				</div>
			{/if}
			<footer class="statement-footer">Generated {new Date().toLocaleDateString()} · Amounts shown in {account.currency}</footer>
		</article>
	{/if}
</div>

<style>
	.statement-page { min-height: 100%; overflow-y: auto; padding: 1rem; background: var(--m3c-surface); color: var(--m3c-on-surface); }
	.statement-toolbar { display: flex; max-width: 70rem; margin: 0 auto 1rem; align-items: center; justify-content: space-between; gap: 1rem; }
	.actions { display: flex; gap: 0.75rem; }
	.statement, .state { max-width: 70rem; margin: 0 auto; border: 1px solid var(--m3c-outline-variant); border-radius: 1rem; background: var(--m3c-surface-container-lowest); padding: clamp(1rem, 4vw, 2.5rem); }
	.statement-heading { display: flex; justify-content: space-between; gap: 2rem; border-bottom: 1px solid var(--m3c-outline-variant); padding-bottom: 1.5rem; }
	.eyebrow { color: var(--m3c-primary); font-size: 0.75rem; font-weight: 750; letter-spacing: 0.08em; text-transform: uppercase; }
	h1 { font-size: 1.75rem; font-weight: 750; }
	.statement-heading p:not(.eyebrow), .period span, .statement-footer, small { color: var(--m3c-on-surface-variant); font-size: 0.8125rem; }
	.period { display: grid; align-content: start; text-align: right; }
	.balance-summary { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 1rem; padding: 1.5rem 0; }
	.balance-summary div { display: flex; justify-content: space-between; gap: 1rem; border-radius: 0.75rem; background: var(--m3c-surface-container-high); padding: 1rem; font-weight: 700; }
	.month-list { display: grid; gap: 1rem; }
	.table-head, .statement-row { display: grid; grid-template-columns: 7rem minmax(0, 1fr) 8rem 9rem 9rem; gap: 0.75rem; align-items: center; padding: 0.625rem 1rem; }
	.table-head { border-bottom: 1px solid var(--m3c-outline-variant); color: var(--m3c-on-surface-variant); font-size: 0.6875rem; font-weight: 700; letter-spacing: 0.04em; text-transform: uppercase; }
	.table-head span:nth-child(n+3), .statement-row > div:nth-child(n+3) { text-align: right; }
	.statement-row { border-bottom: 1px solid var(--m3c-outline-variant); font-size: 0.8125rem; }
	.statement-row > div:first-child, .statement-row > div:nth-child(2) { display: grid; gap: 0.2rem; }
	.statement-row > div:nth-child(2) { justify-items: start; }
	.statement-footer { margin-top: 1.5rem; border-top: 1px solid var(--m3c-outline-variant); padding-top: 1rem; text-align: center; }
	.empty, .state { text-align: center; }
	.state { display: grid; gap: 1rem; justify-items: center; }
	@media (max-width: 48rem) { .statement-heading { flex-direction: column; } .period { text-align: left; } .table-head { display: none; } .statement-row { grid-template-columns: 1fr 1fr; } .statement-row > div:nth-child(n+3) { text-align: left; } .balance-summary { grid-template-columns: 1fr; } }
	@media print {
		@page { size: letter portrait; margin: 0.45in; }
		:global(body) { background: white !important; color: black !important; }
		:global(.rc-vt-sidebar), :global(.rc-vt-topbar) { display: none !important; }
		.statement-toolbar { display: none !important; }
		:global(.customer-shell-main) { position: fixed; inset: 0; z-index: 100; display: block; overflow: visible; background: white; }
		.statement-page {
			min-height: 100%; overflow: visible; padding: 0; background: white; color: black;
			--m3c-surface: white; --m3c-surface-container-low: white; --m3c-surface-container-lowest: white;
			--m3c-surface-container-high: #f3f3f3; --m3c-on-surface: black; --m3c-on-surface-variant: #333;
			--m3c-outline-variant: #aaa; --m3c-primary: black; --m3c-success-container: #e8f5e9;
			--m3c-on-success-container: #153d20; --m3c-info-container: #e8f0fe; --m3c-on-info-container: #15345b;
		}
		.statement { max-width: none; border: 0; border-radius: 0; padding: 0; background: white; }
		.month-list { gap: 0.5rem; }
		.statement-row, .table-head { break-inside: avoid; }
		:global(.group) { break-inside: avoid; box-shadow: none !important; }
		:global(.amount), .statement-heading p, .period span, .statement-footer, small { color: black !important; }
	}
</style>
