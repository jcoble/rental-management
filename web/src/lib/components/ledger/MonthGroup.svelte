<script lang="ts">
	import type { Snippet } from 'svelte';
	import type { TenantMonthSummary } from '$lib/api/endpoints/tenant-accounts';
	import LedgerAmount from './LedgerAmount.svelte';

	let { month, children }: { month: TenantMonthSummary; children: Snippet } = $props();
	const label = $derived(new Intl.DateTimeFormat('en-US', { month: 'long', year: 'numeric', timeZone: 'UTC' }).format(new Date(Date.UTC(month.year, month.month - 1, 1))));
</script>

<section class="group">
	<header class="month-header">
		<h3>{label}</h3>
		<span>Opening <LedgerAmount amount={month.openingBalance} currency={month.currency} /></span>
	</header>
	<div class="rows">{@render children()}</div>
	<footer>
		<div><span>Charges</span><LedgerAmount amount={month.chargeAmount} currency={month.currency} tone="charge" /></div>
		<div><span>Payments</span><LedgerAmount amount={month.paymentAmount} currency={month.currency} tone="payment" /></div>
		<div><span>Credits</span><LedgerAmount amount={month.creditAmount} currency={month.currency} tone="credit" /></div>
		<div class="closing"><span>Closing balance</span><LedgerAmount amount={month.closingBalance} currency={month.currency} /></div>
	</footer>
</section>

<style>
	.group { overflow: clip; border: 1px solid var(--m3c-outline-variant); border-radius: 1rem; background: var(--m3c-surface-container-low); }
	.month-header { position: sticky; top: 0; z-index: 1; display: flex; justify-content: space-between; gap: 1rem; padding: 0.875rem 1rem; background: var(--m3c-surface-container-high); color: var(--m3c-on-surface); }
	h3 { font-size: 1rem; font-weight: 700; }
	.month-header span { color: var(--m3c-on-surface-variant); font-size: 0.8125rem; }
	.rows { background: var(--m3c-surface-container-lowest); }
	footer { display: grid; gap: 0.5rem; padding: 0.875rem 1rem; color: var(--m3c-on-surface-variant); font-size: 0.8125rem; }
	footer div { display: flex; justify-content: space-between; gap: 1rem; }
	footer .closing { border-top: 1px solid var(--m3c-outline-variant); padding-top: 0.625rem; color: var(--m3c-on-surface); font-weight: 700; }
</style>
