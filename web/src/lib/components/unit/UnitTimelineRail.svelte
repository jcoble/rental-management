<script lang="ts">
	import type { AuditEntry } from '$lib/types';
	import ActivityFeed from '$lib/components/shared/ActivityFeed.svelte';
	import { History, ChevronRight } from '@lucide/svelte';

	let {
		activities,
		onViewAll,
	}: {
		activities: AuditEntry[];
		/** Switch the page to the full Timeline tab. */
		onViewAll: () => void;
	} = $props();
</script>

<aside class="rounded-xl border bg-card p-4" data-testid="unit-timeline-rail" aria-label="Recent unit activity">
	<div class="mb-3 flex items-center justify-between">
		<h2 class="flex items-center gap-1.5 text-sm font-semibold">
			<History class="h-4 w-4 text-muted-foreground" />
			Recent activity
		</h2>
		<button
			type="button"
			onclick={onViewAll}
			class="inline-flex items-center gap-0.5 text-xs font-medium text-primary hover:underline"
			data-testid="timeline-view-all"
		>
			View all <ChevronRight class="h-3 w-3" />
		</button>
	</div>

	{#if activities.length === 0}
		<p class="py-6 text-center text-sm text-muted-foreground">No activity yet.</p>
	{:else}
		<ActivityFeed {activities} compact />
	{/if}
</aside>
