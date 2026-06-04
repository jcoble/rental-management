<script lang="ts">
	import type { WorkOrderTimelineEntry } from '$lib/types';
	import StatusBadge from './StatusBadge.svelte';
	import { formatRelative } from '$lib/utils/date';
	import { ArrowRight, Plus } from '@lucide/svelte';

	let {
		timeline,
		testid = 'work-order-timeline'
	}: {
		timeline: WorkOrderTimelineEntry[] | undefined | null;
		testid?: string;
	} = $props();

	// API returns oldest→newest; show newest first so the latest update is on top.
	const entries = $derived([...(timeline ?? [])].reverse());

	function whoLabel(label?: string | null): string {
		const v = (label ?? '').trim();
		return v.length > 0 ? v : 'System';
	}

	function absolute(iso: string): string {
		const d = new Date(iso);
		return isNaN(d.getTime())
			? iso
			: d.toLocaleString(undefined, {
					month: 'short',
					day: 'numeric',
					year: 'numeric',
					hour: 'numeric',
					minute: '2-digit'
				});
	}
</script>

{#if entries.length === 0}
	<p class="py-4 text-sm text-muted-foreground" data-testid="{testid}-empty">No status history yet.</p>
{:else}
	<ol class="space-y-0" data-testid={testid}>
		{#each entries as entry, i (entry.id)}
			<li class="relative flex gap-3 pb-5 last:pb-0" data-testid="{testid}-entry">
				<!-- Stepper rail -->
				<div class="flex flex-col items-center">
					<span
						class="mt-1 flex h-3 w-3 shrink-0 rounded-full {i === 0
							? 'bg-primary ring-4 ring-primary/15'
							: 'bg-border'}"
					></span>
					{#if i < entries.length - 1}
						<span class="mt-1 w-px flex-1 bg-border"></span>
					{/if}
				</div>

				<!-- Entry body -->
				<div class="min-w-0 flex-1 -mt-0.5">
					<div class="flex flex-wrap items-center gap-2">
						<StatusBadge status={entry.toStatus} />
						<span class="flex items-center gap-1 text-xs text-muted-foreground">
							{#if entry.fromStatus}
								<span data-testid="{testid}-from">{entry.fromStatus}</span>
								<ArrowRight class="h-3 w-3" />
								<span data-testid="{testid}-to">{entry.toStatus}</span>
							{:else}
								<Plus class="h-3 w-3" />
								<span data-testid="{testid}-created">Created</span>
							{/if}
						</span>
					</div>

					{#if entry.note}
						<p class="mt-1 whitespace-pre-line text-sm text-foreground" data-testid="{testid}-note">
							{entry.note}
						</p>
					{/if}

					<p class="mt-1 text-xs text-muted-foreground">
						<span data-testid="{testid}-who">{whoLabel(entry.changedByLabel)}</span>
						·
						<time datetime={entry.createdAtUtc} title={absolute(entry.createdAtUtc)} data-testid="{testid}-time">
							{formatRelative(entry.createdAtUtc)}
						</time>
					</p>
				</div>
			</li>
		{/each}
	</ol>
{/if}
