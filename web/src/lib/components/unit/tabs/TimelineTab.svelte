<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import type { AuditEntry } from '$lib/types';
	import { units as unitsApi } from '$lib/api/endpoints/units';
	import ActivityFeed from '$lib/components/shared/ActivityFeed.svelte';
	import { Button } from '$lib/components/ui/button';

	let { unitId }: { unitId: number } = $props();

	const PAGE = 30;
	let take = $state(PAGE);

	// Deep, paged history: a bounded AuditLog union over the unit + its children (newest first).
	const timelineQuery = createQuery(() => ({
		queryKey: ['unit-timeline', unitId, take],
		enabled: unitId > 0,
		queryFn: () => unitsApi.timeline(unitId, { take, skip: 0 }),
		// Keep the previous page visible while the larger page loads (smooth "load more").
		placeholderData: (prev: AuditEntry[] | undefined) => prev,
	}));

	const activities = $derived(timelineQuery.data ?? []);
	// When the page came back full, there may be more to load.
	const canLoadMore = $derived(activities.length >= take);
</script>

<div class="space-y-4 rounded-xl border bg-card p-4" data-testid="unit-timeline-tab">
	{#if timelineQuery.isLoading}
		<p class="py-8 text-center text-sm text-muted-foreground">Loading history…</p>
	{:else if activities.length === 0}
		<p class="py-8 text-center text-sm text-muted-foreground">No history yet for this unit.</p>
	{:else}
		<ActivityFeed {activities} />
		{#if canLoadMore}
			<div class="flex justify-center pt-2">
				<Button variant="outline" onclick={() => (take += PAGE)} data-testid="timeline-load-more">
					Load more
				</Button>
			</div>
		{/if}
	{/if}
</div>
