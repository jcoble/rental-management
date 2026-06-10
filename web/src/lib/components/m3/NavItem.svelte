<script lang="ts">
	// Material 3 navigation item for sidebars, drawers, and navigation rails.
	import * as Tooltip from '$lib/components/ui/tooltip';
	import type { Snippet } from 'svelte';
	import type { HTMLAnchorAttributes } from 'svelte/elements';

	type Tone = 'neutral' | 'primary' | 'success' | 'info' | 'warning';
	type Variant = 'plain' | 'surface';

	let {
		href,
		label,
		description = '',
		badge = '',
		active = false,
		collapsed = false,
		tone = 'neutral',
		variant = 'plain',
		icon,
		trailing,
		class: className = '',
		'data-testid': dataTestId = 'm3-nav-item',
		...rest
	}: {
		href: string;
		label: string;
		description?: string;
		badge?: string | number;
		active?: boolean;
		collapsed?: boolean;
		tone?: Tone;
		variant?: Variant;
		icon?: Snippet;
		trailing?: Snippet;
		class?: string;
		'data-testid'?: string;
	} & HTMLAnchorAttributes = $props();

	const toneColor: Record<Tone, string> = {
		neutral: 'var(--m3c-on-surface-variant)',
		primary: 'var(--m3c-primary)',
		success: '#34d399',
		info: '#60a5fa',
		warning: '#f59e0b'
	};

	const hasBadge = $derived(badge !== '' && badge !== null && badge !== undefined);
	const tooltipId = $derived(`${dataTestId}-tooltip-description`);

	function invokeHandler(handler: unknown, event: MouseEvent) {
		if (typeof handler === 'function') {
			(handler as (event: MouseEvent) => void)(event);
		}
	}
</script>

{#snippet navContent()}
	<span class="m3-nav-item__icon" data-testid="{dataTestId}-icon-wrap">
		{@render icon?.()}
	</span>
	{#if !collapsed}
		<span class="m3-nav-item__copy" data-testid="{dataTestId}-copy">
			<span class="m3-nav-item__label" data-testid="{dataTestId}-label">{label}</span>
			{#if description}
				<span class="m3-nav-item__description" data-testid="{dataTestId}-description">{description}</span>
			{/if}
		</span>
		{#if hasBadge}
			<span class="m3-nav-item__badge" data-testid="{dataTestId}-badge">{badge}</span>
		{/if}
		{@render trailing?.()}
	{:else if hasBadge}
		<span class="m3-nav-item__badge collapsed-badge" data-testid="{dataTestId}-badge">{badge}</span>
	{/if}
{/snippet}

{#if collapsed}
	<Tooltip.Root delayDuration={300}>
		<Tooltip.Trigger>
			{#snippet child({ props })}
				<a
					{href}
					class="m3-nav-item m3-state-layer {variant} {className}"
					class:active
					class:collapsed
					style="--m3-nav-tone: {toneColor[tone]};"
					aria-label={label}
					aria-current={active ? 'page' : undefined}
					{...rest}
					{...props}
					aria-describedby={tooltipId}
					onclick={(event) => { invokeHandler(props.onclick, event); invokeHandler(rest.onclick, event); }}
					data-testid={dataTestId}
				>
					{@render navContent()}
					<span id={tooltipId} class="sr-only" data-testid="{dataTestId}-tooltip-description">{label}</span>
				</a>
			{/snippet}
		</Tooltip.Trigger>
		<Tooltip.Content side="right" data-testid="{dataTestId}-tooltip">{label}</Tooltip.Content>
	</Tooltip.Root>
{:else}
	<a
		{href}
		class="m3-nav-item m3-state-layer {variant} {className}"
		class:active
		class:collapsed
		style="--m3-nav-tone: {toneColor[tone]};"
		aria-current={active ? 'page' : undefined}
		data-testid={dataTestId}
		{...rest}
	>
		{@render navContent()}
	</a>
{/if}

<style>
	.m3-nav-item {
		position: relative;
		display: flex;
		align-items: center;
		gap: 12px;
		min-height: 48px;
		border-radius: var(--m3-shape-large);
		color: var(--muted-foreground, var(--m3c-on-surface-variant));
		padding: 10px 12px;
		text-decoration: none;
		transition:
			background-color var(--m3-easing-fast, 150ms ease),
			color var(--m3-easing-fast, 150ms ease),
			border-radius var(--m3-motion-duration-medium-2, 300ms)
				var(--m3-motion-easing-emphasized-decelerate, cubic-bezier(0.05, 0.7, 0.1, 1)),
			transform var(--m3-easing-fast-spatial, 350ms ease);
	}
	.plain {
		background: transparent;
	}
	.surface {
		background: var(--m3c-surface-container-high);
		color: var(--foreground, var(--m3c-on-surface));
	}
	.m3-nav-item:hover,
	.active {
		background: var(--m3c-surface-container-high);
		color: var(--foreground, var(--m3c-on-surface));
	}
	/* M3 Expressive selected-container shape morph: the active item's corners
	   morph from rounded-rect to a pill (animated via the border-radius transition). */
	.active {
		border-radius: var(--m3-shape-full);
	}
	@media (prefers-reduced-motion: reduce) {
		.m3-nav-item {
			transition-duration: 1ms;
		}
	}
	.surface:hover,
	.surface.active {
		background: var(--m3c-surface-container-highest);
	}
	.active .m3-nav-item__icon,
	.active .m3-nav-item__label {
		color: var(--m3-nav-tone);
	}
	.collapsed {
		justify-content: center;
		padding-inline: 8px;
	}
	.m3-nav-item__icon {
		display: inline-grid;
		place-items: center;
		width: 22px;
		height: 22px;
		flex: 0 0 auto;
		color: var(--m3-nav-tone);
	}
	.m3-nav-item__copy {
		min-width: 0;
		flex: 1 1 auto;
		display: flex;
		flex-direction: column;
	}
	.m3-nav-item__label {
		overflow: hidden;
		text-overflow: ellipsis;
		white-space: nowrap;
		font: var(--m3-type-label-large, 500 0.875rem / 1.25rem var(--m3-font));
	}
	.m3-nav-item__description {
		overflow: hidden;
		text-overflow: ellipsis;
		white-space: nowrap;
		color: color-mix(in srgb, var(--m3-nav-tone) 72%, var(--muted-foreground));
		font: var(--m3-type-body-small, 400 0.75rem / 1rem var(--m3-font));
	}
	.m3-nav-item__badge {
		display: inline-flex;
		align-items: center;
		justify-content: center;
		min-width: 20px;
		height: 20px;
		border-radius: var(--m3-shape-full);
		background: var(--m3-nav-tone);
		color: var(--m3c-on-primary);
		padding: 0 6px;
		font: var(--m3-type-label-small, 500 0.6875rem / 1rem var(--m3-font));
	}
	.collapsed-badge {
		position: absolute;
		right: 0;
		top: 0;
		transform: translate(35%, -35%);
	}
	.m3-nav-item:active {
		transform: scale(0.99);
	}
	.m3-nav-item :global(svg) {
		width: 18px;
		height: 18px;
	}
	/* Material Symbols (sidebar-nav icons only): morph FILL 0 -> 1 when the item is
	   active. The MaterialSymbol component animates font-variation-settings, so this
	   single custom-property flip drives the outline -> filled transition. */
	.active :global(.material-symbol) {
		--msym-fill: 1;
	}
</style>
