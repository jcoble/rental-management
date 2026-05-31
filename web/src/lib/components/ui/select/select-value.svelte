<script lang="ts">
	import { getContext } from 'svelte';
	import { cn } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		placeholder,
		children,
		...restProps
	}: {
		ref?: HTMLSpanElement | null;
		class?: string;
		placeholder?: string;
		children?: import('svelte').Snippet;
		[key: string]: unknown;
	} = $props();

	const ctx = getContext<{ value: string | string[]; items: Array<{value: string; label: string}> } | undefined>('select-ctx');

	const displayLabel = $derived.by(() => {
		if (!ctx?.items?.length) return null;
		const val = ctx.value;
		if (val === undefined || val === '' || Array.isArray(val)) return null;
		return ctx.items.find((i) => i.value === val)?.label ?? null;
	});
</script>

<span
	bind:this={ref}
	data-slot="select-value"
	class={cn("text-sm", className)}
	{...restProps}
>
	{#if children}
		{@render children()}
	{:else if displayLabel}
		{displayLabel}
	{:else if placeholder}
		<span class="text-muted-foreground">{placeholder}</span>
	{/if}
</span>
