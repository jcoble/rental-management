<script lang="ts">
	import type { GeneralLedgerRow } from '$lib/api/endpoints/accounting';
	import { CAPABILITY } from '$lib/auth/experience-policy';
	import { hasCapability } from '$lib/stores/auth.svelte';
	import { formatDate } from '$lib/utils/date';
	import { Button } from '$lib/components/ui/button';
	import * as Dialog from '$lib/components/ui/dialog';
	import AccountingImpactBlock from './AccountingImpactBlock.svelte';
	import LedgerAmount from './LedgerAmount.svelte';
	import LedgerTypeBadge from './LedgerTypeBadge.svelte';

	let { row, onClose }: { row: GeneralLedgerRow; onClose: () => void } = $props();
	const canViewAccounting = $derived(hasCapability(CAPABILITY.moneyBalancesRead));
</script>

<Dialog.Root open onOpenChange={(open) => { if (!open) onClose(); }}>
	<Dialog.Content class="ledger-sheet" data-testid="ledger-row-sheet">
		<Dialog.Header>
			<LedgerTypeBadge type={row.sourceType} />
			<Dialog.Title>{row.description}</Dialog.Title>
			<Dialog.Description>Effective {formatDate(row.effectiveOn)} · Entered {new Date(row.postedAtUtc).toLocaleString()}</Dialog.Description>
		</Dialog.Header>
		<section class="summary">
			<div><span>Account</span><strong>{row.accountCode} · {row.accountName}</strong></div>
			<div><span>Source</span><strong>{row.sourceType} · {row.sourceBusinessKey}</strong></div>
			<div><span>Amount recorded</span><strong><LedgerAmount amount={row.debitAmount || row.creditAmount} currency={row.currency} /></strong></div>
			{#if row.runningBalance !== null}<div><span>Balance</span><strong><LedgerAmount amount={row.runningBalance} currency={row.currency} /></strong></div>{/if}
		</section>
		{#if canViewAccounting}
			<AccountingImpactBlock journalEntryPublicId={row.journalEntryPublicId} compact />
		{/if}
		<Dialog.Footer><Button variant="outline" onclick={onClose}>Close</Button></Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<style>
	:global(.ledger-sheet) { inset: 0 0 0 auto !important; transform: none !important; width: min(42rem, 100%) !important; max-width: none !important; max-height: 100dvh !important; height: 100dvh; border-radius: 1.25rem 0 0 1.25rem; background: var(--m3c-surface-container-low); }
	.summary { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 0.75rem; }
	.summary div { display: grid; gap: 0.2rem; border-radius: 0.75rem; background: var(--m3c-surface-container-high); padding: 0.875rem; }
	.summary span { color: var(--m3c-on-surface-variant); font-size: 0.75rem; }
	.summary strong { color: var(--m3c-on-surface); overflow-wrap: anywhere; }
	@media (max-width: 40rem) { :global(.ledger-sheet) { border-radius: 0; } .summary { grid-template-columns: 1fr; } }
</style>
