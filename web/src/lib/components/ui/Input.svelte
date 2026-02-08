<script lang="ts">
	import { cn } from '$lib/utils/cn';
	import type { HTMLInputAttributes } from 'svelte/elements';

	let {
		class: className = '',
		...rest
	}: HTMLInputAttributes & { class?: string } = $props();
</script>

<input
	class={cn(
		'h-8 w-full rounded-md border border-border bg-surface px-3 text-sm text-text-primary placeholder:text-text-tertiary focus:border-accent focus:outline-none focus:ring-1 focus:ring-accent/50 disabled:opacity-50',
		className,
	)}
	{...rest}
/>
