<script lang="ts">
	import type { Component, Snippet } from 'svelte';
	import * as Card from '$lib/components/ui/card';
	import { cn } from '$lib/utils.js';

	type Accent = 'primary' | 'success' | 'warning' | 'destructive' | 'muted';

	let {
		title,
		description = '',
		icon: IconComponent,
		accent = 'primary',
		testid,
		class: className = '',
		contentClass = '',
		actions,
		children,
	}: {
		title: string;
		description?: string;
		/** Lucide icon component shown next to the title. */
		icon?: Component<{ class?: string }>;
		/** Color key for the section icon — ties the card to its meaning. */
		accent?: Accent;
		testid?: string;
		class?: string;
		/** Extra classes for the content wrapper (e.g. a grid layout). */
		contentClass?: string;
		/** Optional header-right actions (e.g. an Add button). */
		actions?: Snippet;
		children: Snippet;
	} = $props();

	// Subtle, meaning-keyed tint for the section icon. Kept calm — this is a
	// scannable record page, not a dashboard.
	const accentClass: Record<Accent, string> = {
		primary: 'text-primary',
		success: 'text-success',
		warning: 'text-warning',
		destructive: 'text-destructive',
		muted: 'text-muted-foreground',
	};
</script>

<Card.Root class={cn('gap-4', className)} data-testid={testid}>
	<Card.Header class="flex flex-row items-start justify-between gap-3 pb-0">
		<div class="min-w-0">
			<Card.Title class="flex items-center gap-2 text-base">
				{#if IconComponent}
					<IconComponent class={cn('h-4 w-4 shrink-0', accentClass[accent])} />
				{/if}
				<span class="truncate">{title}</span>
			</Card.Title>
			{#if description}
				<Card.Description class="mt-1">{description}</Card.Description>
			{/if}
		</div>
		{#if actions}
			<div class="shrink-0">{@render actions()}</div>
		{/if}
	</Card.Header>
	<Card.Content class={contentClass}>
		{@render children()}
	</Card.Content>
</Card.Root>
