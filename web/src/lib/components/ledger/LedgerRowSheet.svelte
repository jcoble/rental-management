<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import type { GeneralLedgerRow, ReconciledAccountingTransaction } from '$lib/api/endpoints/accounting';
	import { tenantAccounts } from '$lib/api/endpoints/tenant-accounts';
	import { CAPABILITY } from '$lib/auth/experience-policy';
	import { hasCapability } from '$lib/stores/auth.svelte';
	import { formatDate } from '$lib/utils/date';
	import { Button } from '$lib/components/ui/button';
	import * as Dialog from '$lib/components/ui/dialog';
	import AccountingImpactBlock from './AccountingImpactBlock.svelte';
	import LedgerAmount from './LedgerAmount.svelte';
	import LedgerTypeBadge from './LedgerTypeBadge.svelte';

	let {
		row,
		transactionRow,
		fullRecordHref,
		onClose
	}: {
		row?: GeneralLedgerRow;
		transactionRow?: ReconciledAccountingTransaction;
		fullRecordHref?: string;
		onClose: () => void;
	} = $props();
	const canViewAccounting = $derived(hasCapability(CAPABILITY.moneyBalancesRead));
	const tenantLedgerQuery = createQuery(() => ({
		queryKey: ['tenant-ledger-entry', transactionRow?.tenantAccountId, transactionRow?.id],
		queryFn: () => tenantAccounts.ledgerEntry(transactionRow!.tenantAccountId!, transactionRow!.id),
		enabled: transactionRow?.kind === 'TenantLedger' && !!transactionRow.tenantAccountId
	}));
	const title = $derived(transactionRow?.title ?? row?.description ?? 'Money activity');
	const type = $derived(transactionRow?.displayType ?? row?.sourceType ?? 'OwnerActivity');
	const effectiveOn = $derived(transactionRow?.effectiveOn ?? row?.effectiveOn ?? '');
	const enteredAt = $derived(transactionRow?.enteredAtUtc ?? row?.postedAtUtc ?? '');
	const journalEntryPublicId = $derived(transactionRow?.journalEntryPublicId ?? row?.journalEntryPublicId ?? null);
	const currency = $derived(tenantLedgerQuery.data?.currency ?? row?.currency ?? 'USD');
	const amount = $derived(transactionRow
		? transactionRow.chargeAmount || transactionRow.paymentAmount || transactionRow.creditAmount
		: row?.debitAmount || row?.creditAmount || 0);
</script>

<Dialog.Root open onOpenChange={(open) => { if (!open) onClose(); }}>
	<Dialog.Content class="ledger-sheet" data-testid="ledger-row-sheet">
		<Dialog.Header>
			<LedgerTypeBadge {type} />
			<Dialog.Title>{title}</Dialog.Title>
			<Dialog.Description>Effective {formatDate(effectiveOn)} · Entered {new Date(enteredAt).toLocaleString()}</Dialog.Description>
		</Dialog.Header>
		<section class="summary">
			<div><span>Account</span><strong>{transactionRow ? (transactionRow.accountName ? `${transactionRow.accountCode} · ${transactionRow.accountName}` : 'Not assigned') : `${row?.accountCode} · ${row?.accountName}`}</strong></div>
			<div><span>Source</span><strong>{transactionRow?.sourceContext ?? row?.sourceType ?? 'Recorded activity'}</strong></div>
			<div><span>Paid by / paid to</span><strong>{transactionRow?.paidByOrTo ?? 'Not specified'}</strong></div>
			<div><span>Amount recorded</span><strong><LedgerAmount {amount} {currency} /></strong></div>
			{#if tenantLedgerQuery.data}<div><span>Amount owed after entry</span><strong><LedgerAmount amount={tenantLedgerQuery.data.runningAmountOwed} currency={tenantLedgerQuery.data.currency} /></strong></div>{/if}
			{#if !transactionRow && row?.runningBalance !== null && row?.runningBalance !== undefined}<div><span>Balance</span><strong><LedgerAmount amount={row.runningBalance} {currency} /></strong></div>{/if}
		</section>
		{#if transactionRow?.kind === 'TenantLedger'}
			<section class="allocations" aria-label="Allocations">
				<h3>Allocations</h3>
				{#if tenantLedgerQuery.isLoading}<p>Loading allocations…</p>
				{:else if tenantLedgerQuery.isError}<p role="alert">Could not load tenant allocations.</p>
				{:else if tenantLedgerQuery.data?.allocations.length}
					{#each tenantLedgerQuery.data.allocations as allocation (allocation.targetPublicId)}
						<div><span>{allocation.targetDescription}</span><LedgerAmount amount={allocation.amount} currency={tenantLedgerQuery.data.currency} /></div>
					{/each}
				{:else}<p>No allocations recorded.</p>{/if}
			</section>
		{/if}
		{#if canViewAccounting && journalEntryPublicId}
			<AccountingImpactBlock {journalEntryPublicId} sourceContext={transactionRow?.sourceContext} compact />
		{/if}
		<Dialog.Footer>
			{#if fullRecordHref ?? transactionRow?.detailHref}<Button variant="outline" href={fullRecordHref ?? transactionRow?.detailHref}>Open full record</Button>{/if}
			<Button variant="outline" onclick={onClose}>Close</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<style>
	:global(.ledger-sheet) { inset: 0 0 0 auto !important; transform: none !important; width: min(42rem, 100%) !important; max-width: none !important; max-height: 100dvh !important; height: 100dvh; border-radius: 1.25rem 0 0 1.25rem; background: var(--m3c-surface-container-low); }
	.summary { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 0.75rem; }
	.summary div { display: grid; gap: 0.2rem; border-radius: 0.75rem; background: var(--m3c-surface-container-high); padding: 0.875rem; }
	.summary span { color: var(--m3c-on-surface-variant); font-size: 0.75rem; }
	.summary strong { color: var(--m3c-on-surface); overflow-wrap: anywhere; }
	.allocations { display: grid; gap: 0.625rem; border: 1px solid var(--m3c-outline-variant); border-radius: 1rem; padding: 1rem; color: var(--m3c-on-surface); }
	.allocations h3 { font-weight: 700; }
	.allocations p { color: var(--m3c-on-surface-variant); font-size: 0.875rem; }
	.allocations div { display: flex; justify-content: space-between; gap: 1rem; border-top: 1px solid var(--m3c-outline-variant); padding-top: 0.625rem; font-size: 0.875rem; }
	@media (max-width: 40rem) { :global(.ledger-sheet) { border-radius: 0; } .summary { grid-template-columns: 1fr; } }
</style>
