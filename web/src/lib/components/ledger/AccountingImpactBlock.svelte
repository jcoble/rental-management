<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { accounting } from '$lib/api/endpoints/accounting';
	import { getAuthState } from '$lib/stores/auth.svelte';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import { Button } from '$lib/components/ui/button';
	import JournalEntryDetail from './JournalEntryDetail.svelte';
	import LedgerAmount from './LedgerAmount.svelte';

	let { journalEntryPublicId, compact = false }: { journalEntryPublicId: string; compact?: boolean } = $props();
	const portfolioId = getAuthState().accessEnvelope?.selectedContext.portfolioId ?? 0;
	let expanded = $state(false);
	const journalQuery = createQuery(() => ({
		queryKey: ['journal-entry', portfolioId, journalEntryPublicId],
		queryFn: () => accounting.journalEntry(journalEntryPublicId),
		enabled: portfolioId > 0 && journalEntryPublicId.length > 0
	}));
</script>

<section class:compact class="impact" aria-label="Accounting impact">
	<div class="heading"><div><p class="eyebrow">Accounting impact</p><h3>Where this money was recorded</h3></div>{#if compact && journalQuery.data}<Button variant="ghost" size="sm" onclick={() => expanded = !expanded}>{expanded ? 'Hide details' : 'View details'}</Button>{/if}</div>
	{#if journalQuery.isLoading}
		<LoadingState label="Loading accounting impact" variant="section" />
	{:else if journalQuery.isError}
		<div class="state" role="alert"><p>Could not load the accounting impact.</p><Button variant="outline" size="sm" onclick={() => journalQuery.refetch()}>Try again</Button></div>
	{:else if !journalQuery.data || journalQuery.data.lines.length === 0}
		<p class="state">No accounting lines are available.</p>
	{:else}
		<div class="lines">
			{#each journalQuery.data.lines as line (line.id)}
				<div><span>{line.accountCode} · {line.accountName}</span><span class="values">Debit <LedgerAmount amount={line.debitAmount} currency={journalQuery.data.currency} /> · Credit <LedgerAmount amount={line.creditAmount} currency={journalQuery.data.currency} /></span></div>
			{/each}
			<div class="totals"><strong>Totals</strong><span class="values">Debit <LedgerAmount amount={journalQuery.data.totalDebits} currency={journalQuery.data.currency} /> · Credit <LedgerAmount amount={journalQuery.data.totalCredits} currency={journalQuery.data.currency} /></span></div>
		</div>
		<p class:balanced={journalQuery.data.isBalanced} class="balance-state">{journalQuery.data.isBalanced ? 'Balanced ✓' : 'Not balanced'}</p>
		{#if !compact || expanded}<div class="expanded"><JournalEntryDetail entry={journalQuery.data} /></div>{/if}
	{/if}
</section>

<style>
	.impact { border: 1px solid var(--m3c-outline-variant); border-radius: 1rem; background: var(--m3c-surface-container-low); padding: 1rem; color: var(--m3c-on-surface); }
	.heading { display: flex; align-items: start; justify-content: space-between; gap: 1rem; }
	.eyebrow { color: var(--m3c-primary); font-size: 0.75rem; font-weight: 750; letter-spacing: 0.04em; text-transform: uppercase; }
	h3 { font-weight: 700; }
	.lines { margin-top: 1rem; overflow: hidden; border: 1px solid var(--m3c-outline-variant); border-radius: 0.75rem; }
	.lines > div { display: flex; justify-content: space-between; gap: 1rem; padding: 0.625rem 0.75rem; background: var(--m3c-surface-container-lowest); font-size: 0.8125rem; }
	.lines > div + div { border-top: 1px solid var(--m3c-outline-variant); }
	.values { white-space: nowrap; color: var(--m3c-on-surface-variant); }
	.totals { background: var(--m3c-surface-container-high) !important; }
	.balance-state { margin-top: 0.75rem; color: var(--m3c-error); font-size: 0.8125rem; font-weight: 700; }
	.balance-state.balanced { color: var(--m3c-on-success-container); }
	.state { display: flex; align-items: center; justify-content: space-between; gap: 1rem; margin-top: 1rem; border-radius: 0.75rem; background: var(--m3c-surface-container-high); padding: 1rem; color: var(--m3c-on-surface-variant); }
	.expanded { margin-top: 1.25rem; border-top: 1px solid var(--m3c-outline-variant); padding-top: 1.25rem; }
	.compact .expanded { max-height: 35rem; overflow-y: auto; }
	@media (max-width: 40rem) { .lines > div { flex-direction: column; } .values { white-space: normal; } }
</style>
