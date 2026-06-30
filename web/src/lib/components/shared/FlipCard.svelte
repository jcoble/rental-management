<!--
  FlipCard (TSK-599) — a card that flips in 3D between a read-only and an edit face.
  Built for the view → edit toggle on detail pages: clicking Edit flips the whole card
  over to reveal the form. Drive it with `flipped` (e.g. the page's `editing` flag).

  Pass ONE `face` snippet that takes `editing: boolean`; FlipCard renders it twice —
  `face(false)` on the front, `face(true)` on the back — so each card is authored once.
  Both faces are grid-stacked in a single cell, so the card is always as tall as the
  taller face (no layout jump mid-flip). `backface-visibility: hidden` shows only the
  face pointing at the viewer; the hidden face is made `inert` so its inputs can't be
  focused or clicked through. Honors prefers-reduced-motion (snaps instead of spinning).
-->
<script lang="ts">
	import type { Snippet } from 'svelte';

	let {
		flipped = false,
		face,
		durationMs = 600,
		class: className = '',
	}: {
		flipped?: boolean;
		/** Renders one face. `editing` is false on the front, true on the back. */
		face: Snippet<[boolean]>;
		durationMs?: number;
		class?: string;
	} = $props();
</script>

<div class="flip-card {className}" class:is-flipped={flipped} style="--flip-duration:{durationMs}ms">
	<div class="flip-card__inner">
		<div class="flip-card__face flip-card__front" inert={flipped}>
			{@render face(false)}
		</div>
		<div class="flip-card__face flip-card__back" inert={!flipped}>
			{@render face(true)}
		</div>
	</div>
</div>

<style>
	.flip-card {
		perspective: 2200px;
	}

	.flip-card__inner {
		display: grid;
		transform-style: preserve-3d;
		transition: transform var(--flip-duration) cubic-bezier(0.2, 0.74, 0.2, 1);
		transform: rotateY(0deg);
		will-change: transform;
	}

	.flip-card.is-flipped .flip-card__inner {
		transform: rotateY(180deg);
	}

	/* Stack both faces in one grid cell so the card sizes to the taller one. */
	.flip-card__face {
		grid-area: 1 / 1;
		min-width: 0;
		backface-visibility: hidden;
		-webkit-backface-visibility: hidden;
	}

	.flip-card__back {
		transform: rotateY(180deg);
	}

	@media (prefers-reduced-motion: reduce) {
		.flip-card__inner {
			transition: none;
		}
	}
</style>
