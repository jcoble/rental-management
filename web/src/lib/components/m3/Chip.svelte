<script lang="ts">
	// Material 3 status/assist chip — M3 small shape (8px), tone-coded soft tints.
	import type { Snippet } from 'svelte';
	import type { HTMLAttributes } from 'svelte/elements';

	type Tone = 'primary' | 'success' | 'warning' | 'info' | 'danger' | 'cyan' | 'neutral';
	type Variant = 'assist' | 'filter' | 'input' | 'suggestion';

	let {
		variant = 'assist',
		tone = 'neutral',
		dot = false,
		selected = false,
		class: className = '',
		children,
		...rest
	}: {
		variant?: Variant;
		tone?: Tone;
		dot?: boolean;
		selected?: boolean;
		class?: string;
		children?: Snippet;
	} & HTMLAttributes<HTMLSpanElement> = $props();

	const toneColor: Record<Tone, string> = {
		primary: 'var(--m3c-primary)',
		success: '#34d399',
		warning: '#f59e0b',
		info: '#60a5fa',
		danger: 'var(--m3c-error)',
		cyan: '#22d3ee',
		neutral: 'var(--muted-foreground, var(--m3c-on-surface-variant))'
	};
</script>

<span class="m3-chip {variant} {className}" class:selected style="--tone: {toneColor[tone]}" {...rest}>
	{#if dot}<span class="dot" aria-hidden="true"></span>{/if}
	{@render children?.()}
</span>

<style>
	.m3-chip {
		display: inline-flex;
		align-items: center;
		gap: 6px;
		height: 32px;
		padding: 0 12px;
		border-radius: var(--m3-shape-small, 8px);
		font: var(--m3-type-label-medium, 500 0.75rem / 1rem var(--m3-font));
		letter-spacing: 0;
		white-space: nowrap;
		color: var(--tone);
		border: 1px solid color-mix(in srgb, var(--tone) 38%, var(--m3c-outline-variant));
		background: color-mix(in srgb, var(--tone) 9%, var(--m3c-surface-container-low));
		transition:
			background-color var(--m3-motion-duration-short-3, 150ms) var(--m3-motion-easing-standard, cubic-bezier(0.2, 0, 0, 1)),
			border-color var(--m3-motion-duration-short-3, 150ms) var(--m3-motion-easing-standard, cubic-bezier(0.2, 0, 0, 1)),
			color var(--m3-motion-duration-short-3, 150ms) var(--m3-motion-easing-standard, cubic-bezier(0.2, 0, 0, 1)),
			transform var(--m3-motion-duration-short-4, 200ms) var(--m3-motion-easing-emphasized-decelerate, cubic-bezier(0.05, 0.7, 0.1, 1));
	}
	.filter.selected,
	.assist.selected {
		border-color: transparent;
		background: var(--m3c-secondary-container);
		color: var(--m3c-on-secondary-container);
	}
	.input {
		border-radius: var(--m3-shape-full);
	}
	.suggestion {
		background: var(--m3c-surface-container);
	}
	.dot {
		width: 6px;
		height: 6px;
		border-radius: 9999px;
		background: var(--tone);
		flex-shrink: 0;
		transition: transform var(--m3-motion-duration-short-4, 200ms) var(--m3-motion-easing-emphasized-decelerate, cubic-bezier(0.05, 0.7, 0.1, 1));
	}
	.selected .dot {
		transform: scale(1.18);
	}
	@media (prefers-reduced-motion: reduce) {
		.m3-chip,
		.dot {
			transition-duration: 1ms;
		}
	}
</style>
