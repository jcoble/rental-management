<script lang="ts">
	// Material 3 card — M3 shape scale (medium, 12px), tonal surfaces + elevation.
	import type { Snippet } from 'svelte';
	import type { HTMLAttributes } from 'svelte/elements';

	type Variant = 'elevated' | 'filled' | 'outlined';
	type Shape = 'medium' | 'large' | 'extra-large';

	let {
		variant = 'outlined',
		shape = 'large',
		href = undefined,
		interactive = false,
		class: className = '',
		children,
		...rest
	}: {
		variant?: Variant;
		shape?: Shape;
		href?: string;
		interactive?: boolean;
		class?: string;
		children?: Snippet;
	} & HTMLAttributes<HTMLElement> = $props();

	const tag = $derived(href ? 'a' : 'div');
</script>

<svelte:element
	this={tag}
	{href}
	class="m3-card {variant} shape-{shape} {className}"
	class:interactive={interactive || href}
	class:m3-state-layer={interactive || href}
	{...rest}
>
	{@render children?.()}
</svelte:element>

<style>
	.m3-card {
		display: block;
		color: var(--foreground, var(--m3c-on-surface));
		text-decoration: none;
		position: relative;
		transition:
			box-shadow var(--m3-motion-duration-short-3, 150ms) var(--m3-motion-easing-standard, cubic-bezier(0.2, 0, 0, 1)),
			background-color var(--m3-motion-duration-short-3, 150ms) var(--m3-motion-easing-standard, cubic-bezier(0.2, 0, 0, 1)),
			border-color var(--m3-motion-duration-short-3, 150ms) var(--m3-motion-easing-standard, cubic-bezier(0.2, 0, 0, 1)),
			transform var(--m3-motion-duration-medium-1, 250ms) var(--m3-motion-easing-emphasized-decelerate, cubic-bezier(0.05, 0.7, 0.1, 1));
	}
	.shape-medium {
		border-radius: var(--m3-shape-medium, 12px);
	}
	.shape-large {
		border-radius: var(--m3-shape-large, 16px);
	}
	.shape-extra-large {
		border-radius: var(--m3-shape-extra-large, 28px);
	}
	.elevated {
		background: var(--m3c-surface-container-low);
		box-shadow: none;
	}
	.filled {
		background: var(--m3c-surface-container-high);
	}
	.outlined {
		background: var(--m3c-surface-container-low);
		border: 1px solid color-mix(in srgb, var(--m3c-outline-variant) var(--m3-card-border-strength, 0%), transparent);
	}
	.interactive {
		cursor: pointer;
	}
	.elevated.interactive:hover {
		box-shadow: var(--m3-elevation-1);
	}
	.interactive:hover {
		transform: translateY(-1px);
	}
	.interactive:active {
		transform: scale(0.99);
	}
	@media (prefers-reduced-motion: reduce) {
		.m3-card {
			transition-duration: 1ms;
		}
	}
</style>
