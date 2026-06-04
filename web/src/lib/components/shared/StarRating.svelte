<script lang="ts">
	import { Star } from '@lucide/svelte';
	import { cn } from '$lib/utils.js';

	/**
	 * Star rating used both as a read-only display and as a 1–5 input.
	 * - Display (default): pass `value` (e.g. 4.3); shows filled/empty stars, rounded to nearest.
	 * - Input: set `interactive` and bind `value`; clicking a star sets 1–5.
	 */
	let {
		value = $bindable(0),
		max = 5,
		interactive = false,
		size = 'md',
		testid = 'star-rating',
		class: className
	}: {
		value?: number | null;
		max?: number;
		interactive?: boolean;
		size?: 'sm' | 'md' | 'lg';
		testid?: string;
		class?: string;
	} = $props();

	const sizeClass = $derived(
		size === 'sm' ? 'h-3.5 w-3.5' : size === 'lg' ? 'h-6 w-6' : 'h-4 w-4'
	);

	// For display we round to the nearest whole star; the numeric value is shown alongside by callers.
	const rounded = $derived(Math.round(value ?? 0));

	function setValue(n: number) {
		if (!interactive) return;
		value = n;
	}
</script>

{#if interactive}
	<div class="flex items-center gap-1" role="radiogroup" aria-label="Star rating" data-testid={testid}>
		{#each Array(max) as _, i (i)}
			{@const n = i + 1}
			<button
				type="button"
				role="radio"
				aria-checked={n === (value ?? 0)}
				aria-label="{n} {n === 1 ? 'star' : 'stars'}"
				class="rounded p-0.5 text-muted-foreground transition-colors hover:text-yellow-500 focus:outline-none focus-visible:ring-2 focus-visible:ring-ring"
				data-testid="{testid}-star-{n}"
				onclick={() => setValue(n)}
			>
				<Star class={cn(sizeClass, n <= (value ?? 0) ? 'fill-yellow-400 text-yellow-400' : '')} />
			</button>
		{/each}
	</div>
{:else}
	<span class={cn('inline-flex items-center gap-0.5', className)} data-testid={testid} aria-label="{rounded} of {max} stars">
		{#each Array(max) as _, i (i)}
			<Star class={cn(sizeClass, i < rounded ? 'fill-yellow-400 text-yellow-400' : 'text-muted-foreground/40')} />
		{/each}
	</span>
{/if}
