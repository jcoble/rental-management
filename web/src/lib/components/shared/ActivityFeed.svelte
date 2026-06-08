<script lang="ts">
	import { page } from '$app/stores';
	import type { AuditEntry } from '$lib/types';
	import { formatRelative } from '$lib/utils/date';
	import { Plus, Pencil, Trash2, CheckCircle, XCircle, Zap } from '@lucide/svelte';

	// Reusable "Recent audit" feed backed by the unified audit trail (GET /api/v1/audit).
	let { activities, compact = false }: { activities: AuditEntry[]; compact?: boolean } = $props();

	function getIcon(operation: string | undefined) {
		switch (operation) {
			case 'Created':
				return Plus;
			case 'Updated':
				return Pencil;
			case 'Deleted':
				return Trash2;
			case 'Approved':
				return CheckCircle;
			case 'Rejected':
				return XCircle;
			default:
				return Zap;
		}
	}

	function getIconColor(operation: string | undefined) {
		switch (operation) {
			case 'Created':
			case 'Approved':
				return 'text-success';
			case 'Deleted':
			case 'Rejected':
				return 'text-destructive';
			default:
				return 'text-muted-foreground';
		}
	}

	function isCurrentPageHref(href: string): boolean {
		try {
			const target = new URL(href, $page.url);
			return target.pathname === $page.url.pathname && target.search === $page.url.search;
		} catch {
			return false;
		}
	}
</script>

<div class="space-y-1">
	{#each activities as item (item.id)}
		{@const Icon = getIcon(item.operationName)}
		{@const isSelfLink = item.detailHref ? isCurrentPageHref(item.detailHref) : false}
		{#if item.detailHref && !isSelfLink}
			<a
				href={item.detailHref}
				class="flex items-start gap-3 rounded-md px-2 py-1.5 transition-colors {compact ? '' : 'hover:bg-secondary'} group"
			>
				<div class="mt-0.5 {getIconColor(item.operationName)}">
					<Icon class="h-3.5 w-3.5" />
				</div>
				<div class="min-w-0 flex-1">
					<p class="text-sm text-muted-foreground truncate group-hover:text-primary transition-colors">{item.description}</p>
					<p class="text-xs text-muted-foreground">{formatRelative(item.timestamp)}</p>
				</div>
			</a>
		{:else}
			<div class="flex items-start gap-3 rounded-md px-2 py-1.5">
				<div class="mt-0.5 {getIconColor(item.operationName)}">
					<Icon class="h-3.5 w-3.5" />
				</div>
				<div class="min-w-0 flex-1">
					<p class="text-sm text-muted-foreground truncate">{item.description}</p>
					<p class="text-xs text-muted-foreground">{formatRelative(item.timestamp)}</p>
				</div>
			</div>
		{/if}
	{:else}
		<p class="px-2 py-4 text-center text-sm text-muted-foreground">No recent audit</p>
	{/each}
</div>
