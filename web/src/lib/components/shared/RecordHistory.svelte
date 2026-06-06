<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { audit } from '$lib/api/endpoints/audit';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import ActivityFeed from '$lib/components/shared/ActivityFeed.svelte';

	// Per-record audit history: the slice of the unified trail for one (entityType, entityId), rendered
	// as the humanized activity feed. Pure read — reuses GET /api/v1/audit?entityType&entityId.
	let {
		entityType,
		entityId,
		take = 50,
	}: { entityType: string; entityId: number; take?: number } = $props();

	const portfolioId = $derived(getCurrentPortfolioId());

	const historyQuery = createQuery(() => ({
		queryKey: ['audit', 'record', portfolioId, entityType, entityId],
		queryFn: () => audit.list(portfolioId, { entityType, entityId, take, sort: '-timestamp' }),
		enabled: entityId > 0,
	}));

	const rows = $derived(historyQuery.data ?? []);
</script>

<div data-testid="record-history-{entityType}-{entityId}">
	{#if historyQuery.isLoading}
		<p class="px-2 py-4 text-center text-sm text-muted-foreground">Loading history…</p>
	{:else if historyQuery.isError}
		<p class="px-2 py-4 text-center text-sm text-destructive">Couldn't load history.</p>
	{:else if rows.length === 0}
		<p class="px-2 py-4 text-center text-sm text-muted-foreground">No history recorded yet.</p>
	{:else}
		<ActivityFeed activities={rows} />
	{/if}
</div>
