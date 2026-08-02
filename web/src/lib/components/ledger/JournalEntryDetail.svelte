<script lang="ts">
	import type { JournalDetail } from '$lib/api/endpoints/accounting';
	import { formatDate } from '$lib/utils/date';
	import LedgerAmount from './LedgerAmount.svelte';

	let { entry, sourceContext, onJournalSelect }: { entry: JournalDetail; sourceContext?: string | null; onJournalSelect?: (publicId: string) => void } = $props();
	const posted = $derived(new Date(entry.postedAtUtc).toLocaleString());
	const dimensions = (line: JournalDetail['lines'][number]) => [
		line.propertyId ? `Property ${line.propertyId}` : null,
		line.unitId ? `Unit ${line.unitId}` : null,
		line.tenantAccountId ? `Tenant account ${line.tenantAccountId}` : null,
		line.ownerEntityId ? `Owner ${line.ownerEntityId}` : null
	].filter(Boolean).join(' · ');
</script>

<article class="detail">
	<header>
		<p class="eyebrow">Journal entry</p>
		<h3>{entry.description}</h3>
		<p>Effective {formatDate(entry.effectiveOn)} · Posted {posted}</p>
	</header>

	<dl class="facts">
		<div><dt>Source</dt><dd>{sourceContext ?? entry.sourceType}</dd></div>
		<div><dt>Recorded by</dt><dd>{entry.actor ?? 'System'}</dd></div>
		<div><dt>Entry ID</dt><dd>{entry.publicId}</dd></div>
		<div><dt>Operation identity</dt><dd>{entry.attemptId}</dd></div>
		<div><dt>Atomic receipt</dt><dd>{entry.atomicReceiptId}</dd></div>
		<div><dt>Idempotency identity</dt><dd>{entry.idempotencyDigest}</dd></div>
	</dl>

	<div class="lines">
		{#each entry.lines as line (line.id)}
			<div class="line">
				<div><strong>{line.accountCode} · {line.accountName}</strong>{#if line.memo}<p>{line.memo}</p>{/if}{#if dimensions(line)}<p>{dimensions(line)}</p>{/if}</div>
				<div class="line-values"><span>Debit <LedgerAmount amount={line.debitAmount} currency={entry.currency} /></span><span>Credit <LedgerAmount amount={line.creditAmount} currency={entry.currency} /></span></div>
			</div>
		{/each}
	</div>

	<section class="evidence">
		<h4>Lineage and evidence</h4>
		<p>Reverses: {#if entry.reversesJournalEntryPublicId}<button type="button" onclick={() => onJournalSelect?.(entry.reversesJournalEntryPublicId!)}>{entry.reversesJournalEntryPublicId}</button>{:else}None{/if}</p>
		<p>Reversed by: {#if entry.reversalPublicIds.length}{#each entry.reversalPublicIds as publicId, index (publicId)}{#if index > 0}, {/if}<button type="button" onclick={() => onJournalSelect?.(publicId)}>{publicId}</button>{/each}{:else}None{/if}</p>
		<p>Documents: {entry.documentIds.length ? entry.documentIds.join(', ') : 'None attached'}</p>
		{#if entry.bankReconciliationEvidence}
			<p>Bank match: {entry.bankReconciliationEvidence.status ?? 'Recorded'}{entry.bankReconciliationEvidence.bankAccountLabel ? ` · ${entry.bankReconciliationEvidence.bankAccountLabel}` : ''}{entry.bankReconciliationEvidence.matchedOn ? ` · ${formatDate(entry.bankReconciliationEvidence.matchedOn)}` : ''}</p>
		{:else}<p>Bank match: None</p>{/if}
		{#if entry.auditLink}<a href={entry.auditLink}>Open audit history</a>{:else}<p>Audit link: Not available</p>{/if}
	</section>
</article>

<style>
	.detail { display: grid; gap: 1.25rem; color: var(--m3c-on-surface); }
	.eyebrow, dt, header p, .line p, .evidence p { color: var(--m3c-on-surface-variant); font-size: 0.8125rem; }
	h3 { font-size: 1.25rem; font-weight: 750; }
	.facts { display: grid; grid-template-columns: repeat(auto-fit, minmax(12rem, 1fr)); gap: 0.875rem; }
	dd { overflow-wrap: anywhere; font-size: 0.875rem; }
	.lines { overflow: hidden; border: 1px solid var(--m3c-outline-variant); border-radius: 0.75rem; }
	.line { display: flex; justify-content: space-between; gap: 1rem; padding: 0.875rem; background: var(--m3c-surface-container-low); }
	.line + .line { border-top: 1px solid var(--m3c-outline-variant); }
	.line-values { display: grid; min-width: 10rem; text-align: right; font-size: 0.8125rem; }
	.evidence { display: grid; gap: 0.35rem; border-radius: 0.75rem; background: var(--m3c-surface-container-high); padding: 1rem; }
	h4 { font-weight: 700; }
	a { color: var(--m3c-primary); text-decoration: underline; }
	button { color: var(--m3c-primary); overflow-wrap: anywhere; text-align: left; text-decoration: underline; text-underline-offset: 0.15em; }
	@media (max-width: 40rem) { .line { flex-direction: column; } .line-values { text-align: left; } }
</style>
