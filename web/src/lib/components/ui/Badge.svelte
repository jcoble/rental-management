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
		default: 'bg-surface-hover text-text-secondary',
		success: 'bg-success/20 text-success',
		warning: 'bg-warning/20 text-warning',
		danger: 'bg-danger/20 text-danger',
		accent: 'bg-accent/20 text-accent',
		muted: 'bg-text-tertiary/20 text-text-tertiary',
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
