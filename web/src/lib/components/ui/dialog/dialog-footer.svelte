<script lang="ts">
	import { cn, type WithElementRef } from "$lib/utils.js";
	import type { HTMLAttributes } from "svelte/elements";
	import { getDialogOverlayBoundary } from './dialog-overlay-boundary.svelte.js';

	let {
		ref = $bindable(null),
		class: className,
		children,
		...restProps
	}: WithElementRef<HTMLAttributes<HTMLDivElement>> = $props();

	const dialogOverlayBoundary = getDialogOverlayBoundary();
	let footerElement = $state<HTMLElement | null>(null);
	$effect(() => {
		ref = footerElement;
	});
	$effect(() => {
		if (!dialogOverlayBoundary) return;
		dialogOverlayBoundary.footer = footerElement;
		if (!footerElement) {
			dialogOverlayBoundary.footerHeight = 0;
			return;
		}
		const element = footerElement;
		const updateHeight = () => {
			dialogOverlayBoundary.footerHeight = element.getBoundingClientRect().height;
		};
		updateHeight();
		const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(updateHeight);
		observer?.observe(element);
		return () => {
			observer?.disconnect();
			if (dialogOverlayBoundary.footer === footerElement) {
				dialogOverlayBoundary.footer = null;
				dialogOverlayBoundary.footerHeight = 0;
			}
		};
	});
</script>

<div
	bind:this={footerElement}
	data-slot="dialog-footer"
	class={cn("flex flex-col-reverse gap-2 sm:flex-row sm:justify-end", className)}
	{...restProps}
>
	{@render children?.()}
</div>
