<script lang="ts">
	import { page } from '$app/stores';
	import type { AuditEntry } from '$lib/types';
	import { formatRelative } from '$lib/utils/date';
	import { Plus, Pencil, Trash2, CheckCircle, XCircle, Zap, ChevronRight, ArrowRight } from '@lucide/svelte';

	// Reusable "Recent audit" feed backed by the unified audit trail (GET /api/v1/audit).
	let { activities, compact = false }: { activities: AuditEntry[]; compact?: boolean } = $props();

	// Per-row expand state for the inline field-level diff (keyed by audit entry id).
	let expanded = $state<Record<number, boolean>>({});

	function toggle(id: number) {
		expanded[id] = !expanded[id];
	}

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

	function hasChanges(item: AuditEntry): boolean {
		return (item.changes?.length ?? 0) > 0;
	}
</script>

<div class="space-y-1">
	{#each activities as item (item.id)}
		{@const Icon = getIcon(item.operationName)}
		{@const isSelfLink = item.detailHref ? isCurrentPageHref(item.detailHref) : false}
		{@const expandable = hasChanges(item)}
		{@const isOpen = expanded[item.id] ?? false}
		{#if expandable}
			<!-- Expandable: clicking the whole row reveals the field-level diff in place. -->
			<div data-testid={item.testId}>
				<button
					type="button"
					onclick={() => toggle(item.id)}
					aria-expanded={isOpen}
					class="flex w-full items-start gap-3 rounded-md px-2 py-1.5 text-left transition-colors {compact ? '' : 'hover:bg-secondary'} group"
				>
					<div class="mt-0.5 {getIconColor(item.operationName)}">
						<Icon class="h-3.5 w-3.5" />
					</div>
					<div class="min-w-0 flex-1">
						<p class="truncate text-sm text-muted-foreground group-hover:text-foreground transition-colors">
							{item.description}
						</p>
						<p class="text-xs text-muted-foreground">
							{formatRelative(item.timestamp)}{#if item.actor} · {item.actor}{/if}
						</p>
					</div>
					<ChevronRight
						class="mt-0.5 h-3.5 w-3.5 shrink-0 text-muted-foreground transition-transform {isOpen ? 'rotate-90' : ''}"
					/>
				</button>
				{#if isOpen}
					<dl
						class="ml-7 mb-1 mt-0.5 space-y-1 rounded-md border border-border bg-muted/40 px-3 py-2"
						data-testid="audit-changes-{item.id}"
					>
						{#each item.changes ?? [] as change (change.field)}
							<div class="flex flex-wrap items-center gap-x-2 gap-y-0.5 text-xs">
								<dt class="font-medium text-foreground">{change.field}</dt>
								<dd class="flex items-center gap-1.5 text-muted-foreground">
									<span class="line-through opacity-70">{change.oldValue}</span>
									<ArrowRight class="h-3 w-3 shrink-0" />
									<span class="font-medium text-foreground">{change.newValue}</span>
								</dd>
							</div>
						{/each}
					</dl>
				{/if}
			</div>
		{:else if item.detailHref && !isSelfLink}
			<a
				href={item.detailHref}
				data-testid={item.testId}
				class="flex items-start gap-3 rounded-md px-2 py-1.5 transition-colors {compact ? '' : 'hover:bg-secondary'} group"
			>
				<div class="mt-0.5 {getIconColor(item.operationName)}">
					<Icon class="h-3.5 w-3.5" />
				</div>
				<div class="min-w-0 flex-1">
					<p class="text-sm text-muted-foreground truncate group-hover:text-primary transition-colors">{item.description}</p>
					<p class="text-xs text-muted-foreground">
						{formatRelative(item.timestamp)}{#if item.actor} · {item.actor}{/if}
					</p>
				</div>
			</a>
		{:else}
			<div class="flex items-start gap-3 rounded-md px-2 py-1.5" data-testid={item.testId}>
				<div class="mt-0.5 {getIconColor(item.operationName)}">
					<Icon class="h-3.5 w-3.5" />
				</div>
				<div class="min-w-0 flex-1">
					<p class="text-sm text-muted-foreground truncate">{item.description}</p>
					<p class="text-xs text-muted-foreground">
						{formatRelative(item.timestamp)}{#if item.actor} · {item.actor}{/if}
					</p>
				</div>
			</div>
		{/if}
	{:else}
		<p class="px-2 py-4 text-center text-sm text-muted-foreground">No recent audit</p>
	{/each}
</div>
