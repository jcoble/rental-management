<script lang="ts">
	import type { Component } from 'svelte';
	import { InboxIcon } from '@lucide/svelte';
	import { cn } from '$lib/utils.js';
	import { Button } from '$lib/components/ui/button/index.js';

	let {
		title,
		description,
		icon: IconComponent,
		actionLabel,
		onaction,
		class: className
	}: {
		title: string;
		description?: string;
		/** Lucide icon component. Defaults to InboxIcon. */
		icon?: Component<{ class?: string }>;
		/** Label for an optional action button. */
		actionLabel?: string;
		/** Callback for the action button. */
		onaction?: () => void;
		class?: string;
	} = $props();

	const Icon = $derived(IconComponent ?? InboxIcon);
</script>

<div
	class={cn(
		'flex flex-col items-center justify-center gap-3 py-12 text-center',
		className
	)}
	data-testid="empty-state"
>
	<div class="flex h-12 w-12 items-center justify-center rounded-full bg-muted" aria-hidden="true">
		<Icon class="h-6 w-6 text-muted-foreground" />
	</div>
	<div class="space-y-1">
		<p class="text-sm font-medium text-foreground" data-testid="empty-state-title">{title}</p>
		{#if description}
			<p class="text-sm text-muted-foreground" data-testid="empty-state-description">{description}</p>
		{/if}
	</div>
	{#if actionLabel && onaction}
		<Button variant="outline" size="sm" onclick={onaction} data-testid="empty-state-action">
			{actionLabel}
		</Button>
	{/if}
</div>
