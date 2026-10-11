<script lang="ts">
	import type { AuditEntry } from '$lib/types';
	import ActivityFeed from '$lib/components/shared/ActivityFeed.svelte';
	import * as Drawer from '$lib/components/ui/drawer';
	import { Button } from '$lib/components/ui/button';
	import { History, ChevronRight, X } from '@lucide/svelte';

	let {
		activities,
		onViewAll,
	}: {
		activities: AuditEntry[];
		/** Switch the page to the full Timeline tab. */
		onViewAll: () => void;
	} = $props();

	let open = $state(false);

	function viewFullTimeline() {
		open = false;
		onViewAll();
	}
</script>

<Button
	variant="outline"
	size="sm"
	class="ml-auto"
	onclick={() => (open = true)}
	aria-haspopup="dialog"
	aria-expanded={open}
	data-testid="unit-activity-open"
>
	<History class="h-4 w-4" />
	Activity
	{#if activities.length > 0}
		<span class="rounded-full bg-primary/15 px-1.5 py-0.5 text-xs font-semibold text-primary">
			{activities.length}
		</span>
	{/if}
</Button>

<Drawer.Root bind:open direction="right" shouldScaleBackground={false}>
	<Drawer.Content
		class="w-[min(30rem,calc(100vw-1rem))] sm:max-w-md"
		data-testid="unit-activity-flyout"
		data-dismiss-policy="dismissible"
		aria-label="Recent unit activity"
	>
		<Drawer.Header class="border-b px-5 py-4 text-left">
			<div class="flex items-start justify-between gap-4">
				<div class="min-w-0">
					<Drawer.Title class="flex items-center gap-2 text-base font-semibold">
						<History class="h-4 w-4 text-muted-foreground" />
						Recent activity
					</Drawer.Title>
					<Drawer.Description>Latest updates for this unit.</Drawer.Description>
				</div>
				<button
					type="button"
					class="m3-state-layer inline-flex size-9 shrink-0 items-center justify-center rounded-full text-muted-foreground hover:bg-secondary hover:text-foreground"
					onclick={() => (open = false)}
					aria-label="Close recent activity"
					data-testid="unit-activity-close"
				>
					<X class="h-4 w-4" />
				</button>
			</div>
		</Drawer.Header>

		<div class="min-h-0 flex-1 overflow-y-auto px-4 py-4" data-testid="unit-timeline-rail">
			{#if activities.length === 0}
				<p class="py-8 text-center text-sm text-muted-foreground">No activity yet.</p>
			{:else}
				<ActivityFeed {activities} />
			{/if}
		</div>

		<div class="border-t p-4">
			<Button
				variant="outline"
				class="w-full justify-between"
				onclick={viewFullTimeline}
				data-testid="timeline-view-all"
			>
				View full timeline <ChevronRight class="h-4 w-4" />
			</Button>
		</div>
	</Drawer.Content>
</Drawer.Root>
