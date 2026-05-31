<script lang="ts">
	import { Search, X } from '@lucide/svelte';

	let {
		value = $bindable(''),
		placeholder = 'Search…',
		testid = 'search',
	}: {
		value?: string;
		placeholder?: string;
		testid?: string;
	} = $props();
</script>

<div class="relative">
	<Search class="pointer-events-none absolute left-2.5 top-1/2 h-4 w-4 -translate-y-1/2 text-text-tertiary" />
	<input
		bind:value
		data-testid="{testid}-input"
		type="search"
		{placeholder}
		class="h-9 w-full rounded-md border border-border bg-surface pl-8 pr-8 text-sm text-text-primary placeholder:text-text-tertiary focus:border-accent focus:outline-none focus:ring-1 focus:ring-accent/50"
	/>
	{#if value}
		<button
			type="button"
			data-testid="{testid}-clear"
			aria-label="Clear search"
			class="absolute right-2 top-1/2 -translate-y-1/2 rounded p-0.5 text-text-tertiary hover:text-text-primary"
			onclick={() => (value = '')}
		>
			<X class="h-4 w-4" />
		</button>
	{/if}
</div>
