<script lang="ts">
	import { navigating } from '$app/state';

	let visible = $state(false);
	let width = $state(0);
	let timeout: ReturnType<typeof setTimeout> | null = null;
	let interval: ReturnType<typeof setInterval> | null = null;

	$effect(() => {
		if (navigating.to) {
			// Start loading bar
			visible = true;
			width = 15;

			// Quickly ramp to ~80%, then slow down
			if (interval) clearInterval(interval);
			interval = setInterval(() => {
				if (width < 80) {
					width += Math.random() * 10;
				} else if (width < 95) {
					width += Math.random() * 2;
				}
			}, 200);
		} else if (visible) {
			// Navigation complete - finish the bar
			if (interval) {
				clearInterval(interval);
				interval = null;
			}
			width = 100;

			// Hide after animation completes
			if (timeout) clearTimeout(timeout);
			timeout = setTimeout(() => {
				visible = false;
				width = 0;
			}, 300);
		}

		return () => {
			if (timeout) clearTimeout(timeout);
			if (interval) clearInterval(interval);
		};
	});
</script>

{#if visible}
	<div
		class="fixed top-0 left-0 z-[9999] h-0.5 bg-primary transition-[width] duration-200 ease-out"
		style="width: {width}%"
		data-testid="navigation-loader"
	></div>
{/if}
