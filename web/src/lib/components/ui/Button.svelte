<script lang="ts">
	import { cn } from '$lib/utils/cn';
	import type { Snippet } from 'svelte';
	import type { HTMLButtonAttributes } from 'svelte/elements';

	type Variant = 'default' | 'secondary' | 'ghost' | 'danger' | 'outline';
	type Size = 'sm' | 'md' | 'lg' | 'icon';

	let {
		variant = 'default',
		size = 'md',
		class: className = '',
		children,
		...rest
	}: HTMLButtonAttributes & {
		variant?: Variant;
		size?: Size;
		children?: Snippet;
	} = $props();

	const variants: Record<Variant, string> = {
		default: 'bg-accent text-white hover:bg-accent-hover',
		secondary: 'bg-surface-hover text-text-primary hover:bg-border',
		ghost: 'text-text-secondary hover:bg-surface-hover hover:text-text-primary',
		danger: 'bg-danger text-white hover:bg-red-600',
		outline: 'border border-border text-text-secondary hover:bg-surface-hover hover:text-text-primary',
	};

	const sizes: Record<Size, string> = {
		sm: 'h-7 px-2.5 text-xs rounded-md',
		md: 'h-8 px-3 text-sm rounded-md',
		lg: 'h-10 px-4 text-sm rounded-lg',
		icon: 'h-8 w-8 rounded-md',
	};
</script>

<button
	class={cn(
		'inline-flex items-center justify-center gap-1.5 font-medium transition-colors focus:outline-none focus:ring-2 focus:ring-accent/50 disabled:opacity-50 disabled:pointer-events-none',
		variants[variant],
		sizes[size],
		className,
	)}
	{...rest}
>
	{#if children}
		{@render children()}
	{/if}
</button>
