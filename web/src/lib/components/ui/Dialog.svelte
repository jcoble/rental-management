<script lang="ts">
	import { cn } from '$lib/utils/cn';
	import { X } from '@lucide/svelte';
	import type { Snippet } from 'svelte';

	let {
		open = false,
		title = '',
		class: className = '',
		onclose,
		children,
	}: {
		open?: boolean;
		title?: string;
		class?: string;
		onclose?: () => void;
		children?: Snippet;
	} = $props();

	function handleKeydown(e: KeyboardEvent) {
		if (open && e.key === 'Escape') onclose?.();
	}
</script>

<svelte:window onkeydown={handleKeydown} />

{#if open}
	<!-- Backdrop -->
	<button
		class="fixed inset-0 z-50 bg-black/60 backdrop-blur-sm"
		onclick={onclose}
		tabindex="-1"
		aria-label="Close dialog"
	></button>

	<!-- Dialog -->
	<div class={cn('fixed left-1/2 top-1/2 z-50 w-full max-w-md -translate-x-1/2 -translate-y-1/2 rounded-lg border border-border bg-bg shadow-xl', className)}>
		{#if title}
			<div class="flex items-center justify-between border-b border-border px-4 py-3">
				<h2 class="text-sm font-semibold text-text-primary">{title}</h2>
				<button
					onclick={onclose}
					class="rounded p-1 text-text-tertiary transition-colors hover:bg-surface-hover hover:text-text-primary"
				>
					<X class="h-4 w-4" />
				</button>
			</div>
		{/if}
		<div class="p-4">
			{#if children}
				{@render children()}
			{/if}
		</div>
	</div>
{/if}
