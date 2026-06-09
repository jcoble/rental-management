<script lang="ts">
	// Material Symbols Rounded glyph — used ONLY by the sidebar navigation icons.
	// The glyph name (e.g. "space_dashboard") is rendered as a ligature inside a
	// <span>; the parent controls fill via the `--msym-fill` custom property so an
	// active nav item can morph the FILL axis 0 -> 1 (outline -> filled).
	//
	// The self-hosted fontsource package registers the family as
	// 'Material Symbols Rounded Variable' and full.css carries every axis
	// (FILL/wght/GRAD/opsz). See src/routes/+layout.svelte for the import.

	type Props = {
		/** Material Symbols Rounded glyph (ligature) name, e.g. "storefront". */
		name: string;
		/** Force the filled variant regardless of parent active state. */
		filled?: boolean;
		/** Pixel size of the glyph (font-size). */
		size?: number;
		class?: string;
		'data-testid'?: string;
	};

	let {
		name,
		filled = false,
		size = 22,
		class: className = '',
		'data-testid': dataTestId
	}: Props = $props();
</script>

<span
	class="material-symbol {className}"
	class:material-symbol--filled={filled}
	style="font-size: {size}px;"
	aria-hidden="true"
	data-testid={dataTestId}>{name}</span>

<style>
	.material-symbol {
		/* fontsource registers the variable family with the "Variable" suffix. */
		font-family: 'Material Symbols Rounded Variable';
		font-weight: normal;
		font-style: normal;
		line-height: 1;
		display: inline-block;
		/* Prevent the ligature name leaking as text before the font paints. */
		white-space: nowrap;
		direction: ltr;
		letter-spacing: normal;
		text-transform: none;
		word-wrap: normal;
		-webkit-font-smoothing: antialiased;
		font-feature-settings: 'liga';
		/* FILL is driven by --msym-fill so a parent (active nav item) can morph it.
		   opsz min for this font is 30; values below clamp — 24 stays visually crisp. */
		font-variation-settings:
			'FILL' var(--msym-fill, 0),
			'wght' 400,
			'GRAD' 0,
			'opsz' 24;
		transition: font-variation-settings
			var(--m3-motion-duration-medium-2, 300ms)
			var(--m3-motion-easing-emphasized-decelerate, cubic-bezier(0.05, 0.7, 0.1, 1));
	}

	/* Explicit filled override (independent of the parent active rule). */
	.material-symbol--filled {
		--msym-fill: 1;
	}

	@media (prefers-reduced-motion: reduce) {
		.material-symbol {
			transition-duration: 1ms;
		}
	}
</style>
