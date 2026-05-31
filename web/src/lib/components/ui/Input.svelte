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
		'h-8 w-full rounded-md border border-border bg-card px-3 text-sm text-foreground placeholder:text-muted-foreground focus:border-ring focus:outline-none focus:ring-1 focus:ring-ring/50 disabled:opacity-50',
		className,
	)}
	{...rest}
/>
