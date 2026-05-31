<script lang="ts">
	import { cn } from '$lib/utils/cn';
	import type { Snippet } from 'svelte';

	type Variant = 'default' | 'success' | 'warning' | 'danger' | 'accent' | 'muted';

	let {
		variant = 'default',
		class: className = '',
		children,
	}: {
		variant?: Variant;
		class?: string;
		children?: Snippet;
	} = $props();

	const variants: Record<Variant, string> = {
		default: 'bg-secondary text-muted-foreground',
		success: 'bg-success/20 text-success',
		warning: 'bg-warning/20 text-warning',
		danger: 'bg-destructive/20 text-destructive',
		accent: 'bg-primary/20 text-primary',
		muted: 'bg-muted-foreground/20 text-muted-foreground',
	};
</script>

<span
	class={cn(
		'inline-flex items-center rounded-full px-2 py-0.5 text-[11px] font-medium',
		variants[variant],
		className,
	)}
>
	{#if children}
		{@render children()}
	{/if}
</span>
