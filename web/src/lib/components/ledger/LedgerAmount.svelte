<script lang="ts">
	let {
		amount,
		currency,
		tone = 'charge',
		struck = false
	}: { amount: number; currency: string; tone?: string; struck?: boolean } = $props();

	const formatted = $derived.by(() => {
		const magnitude = new Intl.NumberFormat('en-US', {
			style: 'currency',
			currency,
			minimumFractionDigits: 2,
			maximumFractionDigits: 2
		}).format(amount);
		return magnitude.replace('-', '−');
	});
</script>

<span class="amount" class:struck data-tone={tone}>{formatted}</span>

<style>
	.amount { color: var(--m3c-on-surface); font-variant-numeric: tabular-nums; white-space: nowrap; }
	.amount[data-tone='payment'] { color: var(--m3c-on-success-container); }
	.amount[data-tone='credit'], .amount[data-tone='bank'] { color: var(--m3c-on-info-container); }
	.amount[data-tone='reversal'] { color: var(--m3c-on-surface-variant); }
	.struck { text-decoration: line-through; opacity: 0.72; }
</style>
