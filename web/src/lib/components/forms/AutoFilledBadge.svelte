<!--
  AutoFilledBadge — a subtle "from your lease" chip shown next to a field that the scan pre-filled,
  so the user can SEE exactly what the AI provided vs. what they typed (AC-3 transparency). Render
  only when `show` is true. Confidence (0..1) optionally tunes the tone but never blocks anything.
-->
<script lang="ts">
	let { show = false, confidence }: { show?: boolean; confidence?: number } = $props();
	const pct = $derived(confidence == null ? null : Math.round(confidence <= 1 ? confidence * 100 : confidence));
</script>

{#if show}
	<span
		class="inline-flex items-center gap-1 rounded-full bg-accent/15 px-1.5 py-0.5 text-[10px] font-medium text-accent-foreground"
		data-testid="auto-filled-badge"
		title={pct != null ? `From your lease — ${pct}% sure` : 'From your lease'}
	>
		from your lease{#if pct != null && pct < 80}&nbsp;· {pct}%{/if}
	</span>
{/if}
