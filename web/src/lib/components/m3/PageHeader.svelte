<script lang="ts">
	import type { Snippet } from 'svelte';
	import type { HTMLAttributes } from 'svelte/elements';

	type Density = 'compact' | 'comfortable';
	type Scale = 'page' | 'hero';
	type Tone = 'primary' | 'mint' | 'sky' | 'amber' | 'rose' | 'violet' | 'coral';

	let {
		eyebrow = '',
		title,
		description = '',
		density = 'comfortable',
		scale = 'page',
		band = false,
		art = 9,
		tone = 'primary',
		actions,
		meta,
		class: className = '',
		children,
		'data-testid': dataTestId = 'm3-page-header',
		eyebrowTestId = `${dataTestId}-eyebrow`,
		titleTestId = `${dataTestId}-title`,
		descriptionTestId = `${dataTestId}-description`,
		actionsTestId = `${dataTestId}-actions`,
		metaTestId = `${dataTestId}-meta`,
		...rest
	}: {
		eyebrow?: string;
		title: string;
		description?: string;
		density?: Density;
		scale?: Scale;
		band?: boolean;
		art?: number;
		tone?: Tone;
		actions?: Snippet;
		meta?: Snippet;
		class?: string;
		children?: Snippet;
		'data-testid'?: string;
		eyebrowTestId?: string;
		titleTestId?: string;
		descriptionTestId?: string;
		actionsTestId?: string;
		metaTestId?: string;
	} & HTMLAttributes<HTMLElement> = $props();

	const artClass = $derived(
		band
			? `is-band tone-${tone} m3-surface-art m3-surface-art--band m3-art-${String(art).padStart(2, '0')}`
			: ''
	);
</script>

<section
	class="m3-page-header {density} scale-{scale} {artClass} {className}"
	class:has-actions={Boolean(actions)}
	data-testid={dataTestId}
	{...rest}
>
	{#if meta}
		<div class="m3-page-header__meta" data-testid={metaTestId}>
			{@render meta()}
		</div>
	{/if}
	<div class="m3-page-header__copy" data-testid="{dataTestId}-copy">
		{#if eyebrow}
			<p class="m3-page-header__eyebrow" data-testid={eyebrowTestId}>{eyebrow}</p>
		{/if}
		<h1 class="m3-page-header__title" data-testid={titleTestId}>{title}</h1>
		{#if description}
			<p class="m3-page-header__description" data-testid={descriptionTestId}>{description}</p>
		{/if}
		{@render children?.()}
	</div>
	{#if actions}
		<div class="m3-page-header__actions" data-testid={actionsTestId}>
			{@render actions()}
		</div>
	{/if}
</section>

<style>
	.m3-page-header {
		min-width: 0;
		color: var(--foreground, var(--m3c-on-surface));
	}
	.is-band {
		border: 1px solid
			color-mix(
				in srgb,
				var(--m3-page-header-border, var(--m3c-outline-variant))
					var(--m3-page-header-border-strength, 42%),
				transparent
			);
		border-radius: var(--m3-shape-extra-large);
		background:
			linear-gradient(
				135deg,
				color-mix(in srgb, var(--m3c-surface-container-lowest) 20%, transparent),
				transparent 70%
			),
			var(--m3-page-header-bg, var(--m3c-surface-container-low));
		padding: clamp(1rem, 2.4vw, 1.6rem) clamp(1.25rem, 3vw, 2rem);
		box-shadow: inset 0 1px 0 rgb(255 255 255 / 0.04);
		--m3-art-scrim-from: color-mix(
			in srgb,
			var(--m3-page-header-bg, var(--m3c-surface-container-low)) 84%,
			var(--m3-page-header-tint, var(--m3c-primary-container))
		);
	}
	.tone-primary,
	.tone-violet {
		--m3-page-header-bg: var(--m3c-surface-violet, var(--m3c-surface-container-low));
		--m3-page-header-tint: var(--m3c-primary-container);
		--m3-page-header-border: var(--m3c-primary);
	}
	.tone-mint {
		--m3-page-header-bg: var(--m3c-surface-mint, var(--m3c-surface-container-low));
		--m3-page-header-tint: var(--m3c-success-container);
		--m3-page-header-border: var(--success);
	}
	.tone-sky {
		--m3-page-header-bg: var(--m3c-surface-sky, var(--m3c-surface-container-low));
		--m3-page-header-tint: var(--m3c-info-container);
		--m3-page-header-border: var(--info);
	}
	.tone-amber {
		--m3-page-header-bg: var(--m3c-surface-amber, var(--m3c-surface-container-low));
		--m3-page-header-tint: var(--m3c-warning-container);
		--m3-page-header-border: var(--warning);
	}
	.tone-rose {
		--m3-page-header-bg: var(--m3c-surface-rose, var(--m3c-surface-container-low));
		--m3-page-header-tint: var(--m3c-error-container);
		--m3-page-header-border: var(--destructive);
	}
	.tone-coral {
		--m3-page-header-bg: var(--m3c-surface-coral, var(--m3c-surface-container-low));
		--m3-page-header-tint: var(--m3c-coral-container);
		--m3-page-header-border: var(--accent-coral);
	}
	.compact {
		display: grid;
		gap: 10px;
	}
	.comfortable {
		display: grid;
		gap: 16px;
	}
	.m3-page-header__meta,
	.m3-page-header__actions {
		display: flex;
		min-width: 0;
		flex-wrap: wrap;
		align-items: center;
		gap: 8px;
	}
	.m3-page-header__actions {
		justify-content: flex-start;
	}
	.m3-page-header__copy {
		min-width: 0;
	}
	.m3-page-header__eyebrow {
		margin: 0;
		color: var(--muted-foreground, var(--m3c-on-surface-variant));
		font: var(--m3-type-label-large, 500 0.875rem / 1.25rem var(--m3-font));
		letter-spacing: 0;
	}
	.m3-page-header__title {
		margin: 8px 0 0;
		max-width: min(48rem, 100%);
		color: var(--foreground, var(--m3c-on-surface));
		font: var(--m3-type-headline-large, 600 2rem / 2.5rem var(--m3-font));
		letter-spacing: 0;
	}
	.m3-page-header__description {
		margin: 8px 0 0;
		max-width: min(42rem, 100%);
		color: var(--muted-foreground, var(--m3c-on-surface-variant));
		font: var(--m3-type-body-large, 400 1rem / 1.5rem var(--m3-font));
		letter-spacing: 0;
	}
	.compact .m3-page-header__title {
		font: var(--m3-type-title-large, 600 1.375rem / 1.75rem var(--m3-font));
	}
	.scale-hero .m3-page-header__title {
		max-width: 52rem;
		font: 600 var(--m3-type-display-medium-size, 45px) /
			var(--m3-type-display-medium-line-height, 52px) var(--m3-font);
	}
	@media (max-width: 768px) {
		.scale-hero .m3-page-header__title {
			font: 600 var(--m3-type-display-small-size, 36px) /
				var(--m3-type-display-small-line-height, 44px) var(--m3-font);
		}
	}
	@media (min-width: 768px) {
		.has-actions {
			grid-template-columns: minmax(0, 1fr) auto;
			align-items: end;
			column-gap: 24px;
		}
		.has-actions .m3-page-header__meta {
			grid-column: 1 / -1;
		}
		.has-actions .m3-page-header__copy {
			grid-column: 1;
		}
		.has-actions .m3-page-header__actions {
			grid-column: 2;
			grid-row: 2;
			justify-content: flex-end;
		}
	}
</style>
