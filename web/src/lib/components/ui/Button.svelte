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
		default: 'bg-primary text-white hover:bg-primary/90',
		secondary: 'bg-secondary text-foreground hover:bg-border',
		ghost: 'text-muted-foreground hover:bg-secondary hover:text-foreground',
		danger: 'bg-destructive text-white hover:bg-red-600',
		outline: 'border border-border text-muted-foreground hover:bg-secondary hover:text-foreground',
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
		'inline-flex items-center justify-center gap-1.5 font-medium transition-colors focus:outline-none focus:ring-2 focus:ring-ring/50 disabled:opacity-50 disabled:pointer-events-none',
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
