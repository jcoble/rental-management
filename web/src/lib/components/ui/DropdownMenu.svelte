<script lang="ts">
	import { cn } from '$lib/utils/cn';
	import type { Snippet } from 'svelte';

	let {
		open = $bindable(false),
		align = 'left',
		trigger,
		children,
	}: {
		open?: boolean;
		align?: 'left' | 'right';
		trigger?: Snippet;
		children?: Snippet;
	} = $props();

	function handleClickOutside() {
		if (open) open = false;
	}
</script>

<svelte:window onclick={handleClickOutside} />

<div class="relative inline-block">
	<!-- Trigger -->
	<button
		onclick={(e) => { e.stopPropagation(); open = !open; }}
		class="inline-flex items-center"
	>
		{#if trigger}
			{@render trigger()}
		{/if}
	</button>

	<!-- Menu -->
	{#if open}
		<div
			class={cn(
				'absolute z-50 mt-1 min-w-[160px] rounded-lg border border-border bg-surface-elevated py-1 shadow-xl',
				align === 'right' ? 'right-0' : 'left-0',
			)}
			role="menu"
			tabindex="-1"
			onclick={(e) => e.stopPropagation()}
			onkeydown={(e) => e.stopPropagation()}
		>
			{#if children}
				{@render children()}
			{/if}
		</div>
	{/if}
</div>
