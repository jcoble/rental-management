<script lang="ts">
	import type { Snippet } from 'svelte';
	import { ChevronDown } from '@lucide/svelte';

	let {
		id,
		title,
		description,
		summary,
		badge,
		open,
		ontoggle,
		children
	}: {
		id: string;
		title: string;
		description: string;
		summary: string;
		badge?: string;
		open: boolean;
		ontoggle: () => void;
		children: Snippet;
	} = $props();
</script>

<section
	class={`overflow-hidden rounded-xl border bg-card ${open ? 'border-primary/50 shadow-sm' : 'border-border'}`}
	data-testid={id}
>
	<button
		type="button"
		class="flex min-h-20 w-full items-start justify-between gap-4 px-5 py-4 text-left hover:bg-muted/35 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
		aria-expanded={open}
		aria-controls={`${id}-panel`}
		id={`${id}-trigger`}
		onclick={ontoggle}
	>
		<span class="min-w-0">
			<span class="flex flex-wrap items-center gap-2">
				<span class="font-semibold text-foreground">{title}</span>
				{#if badge}
					<span class="rounded-full border border-border px-2 py-0.5 text-xs text-muted-foreground">{badge}</span>
				{/if}
			</span>
			<span class="mt-1 block text-sm text-muted-foreground">{description}</span>
			<span class="mt-2 block text-sm font-medium text-foreground">{summary}</span>
		</span>
		<ChevronDown class={`mt-1 size-5 shrink-0 transition-transform ${open ? 'rotate-180' : ''}`} />
	</button>

	{#if open}
		<div
			id={`${id}-panel`}
			role="region"
			aria-labelledby={`${id}-trigger`}
			class="border-t border-border px-5 py-5"
		>
			{@render children()}
		</div>
	{/if}
</section>
