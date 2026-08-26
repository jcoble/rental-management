<!--
  The assistant's one-sentence read on the day. It is a quiet line under the "Today" header, not a
  card: the ranked "Needs attention" list below it is what the landlord acts on. Keeps its own
  request so a slow assistant never delays the list, and never retries on its own.
-->
<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { ai } from '$lib/api/endpoints/ai';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { Sparkles } from '@lucide/svelte';

	const briefingQuery = createQuery(() => ({
		queryKey: ['ai-briefing', getCurrentPortfolioId()],
		queryFn: () => ai.briefing(),
		// The API already falls back to rules-only copy when optional AI wording is unavailable.
		// Do not turn one slow provider call into a minute of automatic retry skeletons.
		retry: false
	}));

	const bulletCount = $derived(briefingQuery.data?.bullets.length ?? 0);
	const line = $derived.by(() => {
		if (briefingQuery.isLoading) return 'Reading your day…';
		if (briefingQuery.isError) return null;
		if (briefingQuery.data?.summary) return briefingQuery.data.summary;
		if (bulletCount > 0) {
			return `You've got ${bulletCount} thing${bulletCount === 1 ? '' : 's'} to look at today, most urgent first.`;
		}
		return "Nothing urgent today — everything's running smoothly.";
	});
</script>

{#if line}
	<p
		class="mt-1 flex min-w-0 items-start gap-2 text-sm text-muted-foreground"
		data-testid="dashboard-briefing-line"
	>
		<Sparkles class="mt-0.5 h-4 w-4 shrink-0 text-primary" />
		<span class="min-w-0 break-words">{line}</span>
	</p>
{:else}
	<p class="mt-1 flex min-w-0 items-start gap-2 text-sm text-muted-foreground" data-testid="dashboard-briefing-line">
		<Sparkles class="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
		<span class="min-w-0 break-words">The assistant's summary didn't load.</span>
		<button type="button" class="shrink-0 underline underline-offset-2" onclick={() => briefingQuery.refetch()}>Try again</button>
	</p>
{/if}
