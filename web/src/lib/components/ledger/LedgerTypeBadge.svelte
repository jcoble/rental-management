<script lang="ts">
	import { ledgerTypeLabel, ledgerTypeTone } from './labels';

	let { type }: { type: string } = $props();
	const tone = $derived(ledgerTypeTone(type));
</script>

<span class="badge" data-tone={tone}>{ledgerTypeLabel(type)}</span>

<style>
	.badge {
		display: inline-flex;
		align-items: center;
		min-height: 1.5rem;
		border: 1px solid var(--m3c-outline-variant);
		border-radius: 999px;
		padding: 0.125rem 0.625rem;
		background: var(--m3c-surface-container-high);
		color: var(--m3c-on-surface-variant);
		font-size: 0.75rem;
		font-weight: 650;
	}

	.badge[data-tone='payment'] { background: var(--m3c-success-container); color: var(--m3c-on-success-container); }
	.badge[data-tone='credit'], .badge[data-tone='bank'] { background: var(--m3c-info-container); color: var(--m3c-on-info-container); }
	.badge[data-tone='expense'], .badge[data-tone='loan'] { background: var(--m3c-warning-container); color: var(--m3c-on-warning-container); }
	.badge[data-tone='reversal'] { opacity: 0.68; }
</style>
