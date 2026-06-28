<script lang="ts">
	// Material 3 navigation group header for sidebars and drawers.
	import * as Tooltip from '$lib/components/ui/tooltip';
	import { ChevronDown } from '@lucide/svelte';
	import type { Snippet } from 'svelte';
	import type { HTMLButtonAttributes } from 'svelte/elements';

	let {
		label,
		active = false,
		expanded = false,
		collapsed = false,
		icon,
		class: className = '',
		'data-testid': dataTestId = 'm3-nav-group',
		type = 'button',
		...rest
	}: {
		label: string;
		active?: boolean;
		expanded?: boolean;
		collapsed?: boolean;
		icon?: Snippet;
		class?: string;
		'data-testid'?: string;
		type?: HTMLButtonAttributes['type'];
	} & HTMLButtonAttributes = $props();

	const tooltipId = $derived(`${dataTestId}-tooltip-description`);

	function invokeHandler(handler: unknown, event: MouseEvent) {
		if (typeof handler === 'function') {
			(handler as (event: MouseEvent) => void)(event);
		}
	}
</script>

{#snippet groupContent()}
	<span class="m3-nav-group__icon" data-testid="{dataTestId}-icon-wrap">
		{@render icon?.()}
	</span>
	{#if !collapsed}
		<span class="m3-nav-group__label" data-testid="{dataTestId}-label">{label}</span>
		<ChevronDown
			class="m3-nav-group__chevron {expanded ? 'expanded' : ''}"
			data-testid="{dataTestId}-chevron"
		/>
	{/if}
{/snippet}

{#if collapsed}
	<Tooltip.Root delayDuration={300}>
		<Tooltip.Trigger>
			{#snippet child({ props })}
				<button
					{type}
					class="m3-nav-group m3-state-layer {className}"
					class:active
					class:collapsed
					aria-label={label}
					{...rest}
					{...props}
					aria-describedby={tooltipId}
					onclick={(event) => { invokeHandler(props.onclick, event); invokeHandler(rest.onclick, event); }}
					data-testid={dataTestId}
				>
					{@render groupContent()}
					<span id={tooltipId} class="sr-only" data-testid="{dataTestId}-tooltip-description">{label}</span>
				</button>
			{/snippet}
		</Tooltip.Trigger>
		<Tooltip.Content side="right" data-testid="{dataTestId}-tooltip">{label}</Tooltip.Content>
	</Tooltip.Root>
{:else}
	<button
		{type}
		class="m3-nav-group m3-state-layer {className}"
		class:active
		class:collapsed
		aria-expanded={expanded}
		data-testid={dataTestId}
		{...rest}
	>
		{@render groupContent()}
	</button>
{/if}

<style>
	.m3-nav-group {
		display: flex;
		align-items: center;
		width: 100%;
		min-height: 40px;
		gap: 10px;
		border: 0;
		border-radius: var(--m3-shape-large);
		background: transparent;
		color: var(--muted-foreground, var(--m3c-on-surface-variant));
		padding: 8px 12px;
		font: var(--m3-type-label-large, 500 0.875rem / 1.25rem var(--m3-font));
		text-align: left;
		cursor: pointer;
		transition:
			background-color var(--m3-easing-fast, 150ms ease),
			color var(--m3-easing-fast, 150ms ease);
	}
	.m3-nav-group:hover,
	.active {
		background: var(--m3c-surface-container-high);
		color: var(--foreground, var(--m3c-on-surface));
	}
	.collapsed {
		justify-content: center;
		padding-inline: 8px;
	}
	.m3-nav-group__icon {
		display: inline-grid;
		place-items: center;
		width: 20px;
		height: 20px;
		flex: 0 0 auto;
		color: currentColor;
	}
	.m3-nav-group__label {
		min-width: 0;
		flex: 1 1 auto;
		overflow: hidden;
		text-overflow: ellipsis;
		white-space: nowrap;
	}
	.m3-nav-group__chevron {
		width: 14px;
		height: 14px;
		flex: 0 0 auto;
		transform: rotate(-90deg);
		transition: transform var(--m3-easing-fast-spatial, 350ms ease);
	}
	.m3-nav-group__chevron.expanded {
		transform: rotate(0deg);
	}
	.m3-nav-group :global(svg) {
		width: 18px;
		height: 18px;
	}
</style>
