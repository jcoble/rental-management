<script lang="ts">
	import type { ActivityLog } from '$lib/types';
	import { formatRelative } from '$lib/utils/date';
	import {
		Plus,
		Pencil,
		Trash2,
		ArrowRight,
		MessageSquare,
		CheckCircle,
		Zap
	} from '@lucide/svelte';

	let { activities, compact = false }: { activities: ActivityLog[]; compact?: boolean } = $props();

	function getIcon(action: string | undefined) {
		switch (action) {
			case 'created': return Plus;
			case 'updated': return Pencil;
			case 'deleted': return Trash2;
			case 'moved': return ArrowRight;
			case 'commented': return MessageSquare;
			case 'completed': return CheckCircle;
			default: return Zap;
		}
	}

	function getIconColor(action: string | undefined) {
		switch (action) {
			case 'created': return 'text-success';
			case 'deleted': return 'text-danger';
			case 'completed': return 'text-success';
			default: return 'text-text-tertiary';
		}
	}

	function getEntityHref(activity: ActivityLog): string | null {
		if (!activity.entityType) return null;
		switch (activity.entityType.toLowerCase()) {
			case 'task':
				return '/board';
			case 'phase':
				return activity.entityId ? `/phases/${activity.entityId}` : '/phases';
			case 'milestone':
				return '/milestones';
			default:
				return null;
		}
	}
</script>

<div class="space-y-1">
	{#each activities as item (item.id)}
		{@const Icon = getIcon(item.action)}
		{@const href = getEntityHref(item)}
		{#if href}
			<a
				{href}
				class="flex items-start gap-3 rounded-md px-2 py-1.5 transition-colors {compact ? '' : 'hover:bg-surface-hover'} group"
			>
				<div class="mt-0.5 {getIconColor(item.action)}">
					<Icon class="h-3.5 w-3.5" />
				</div>
				<div class="min-w-0 flex-1">
					<p class="text-sm text-text-secondary truncate group-hover:text-accent transition-colors">{item.description || `${item.action} ${item.entityType}`}</p>
					<p class="text-xs text-text-tertiary">{formatRelative(item.createdAt)}</p>
				</div>
			</a>
		{:else}
			<div class="flex items-start gap-3 rounded-md px-2 py-1.5 {compact ? '' : 'hover:bg-surface-hover'}">
				<div class="mt-0.5 {getIconColor(item.action)}">
					<Icon class="h-3.5 w-3.5" />
				</div>
				<div class="min-w-0 flex-1">
					<p class="text-sm text-text-secondary truncate">{item.description || `${item.action} ${item.entityType}`}</p>
					<p class="text-xs text-text-tertiary">{formatRelative(item.createdAt)}</p>
				</div>
			</div>
		{/if}
	{:else}
		<p class="px-2 py-4 text-center text-sm text-text-tertiary">No recent activity</p>
	{/each}
</div>
