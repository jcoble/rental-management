<script lang="ts">
	import type { Snippet } from "svelte";

	let {
		children
	}: {
		children?: Snippet;
	} = $props();
</script>

<div data-slot="combobox-empty" class="py-6 text-center text-sm text-muted-foreground">
	{#if children}
		{@render children()}
	{:else}
		No results found.
	{/if}
</div>
