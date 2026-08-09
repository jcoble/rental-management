<script lang="ts">
	import { Popover as PopoverPrimitive } from 'bits-ui';
	import { cn, type WithoutChild } from '$lib/utils';
	import { getDialogOverlayBoundary } from '../dialog/dialog-overlay-boundary.svelte.js';
	import { getOverlayPositioning } from '../overlay-positioning';

	let {
		ref = $bindable(null),
		class: className,
		align = 'center',
		sideOffset = 4,
		collisionPadding,
		collisionBoundary,
		portalProps,
		children,
		...restProps
	}: WithoutChild<PopoverPrimitive.ContentProps> & {
		portalProps?: PopoverPrimitive.PortalProps;
	} = $props();
	const dialogBoundary = getDialogOverlayBoundary();
	const overlayPositioning = $derived(getOverlayPositioning(dialogBoundary, collisionBoundary, collisionPadding));
</script>

<PopoverPrimitive.Portal {...portalProps}>
	<PopoverPrimitive.Content
		bind:ref
		{align}
		{sideOffset}
		collisionBoundary={overlayPositioning.collisionBoundary}
		collisionPadding={overlayPositioning.collisionPadding}
		class={cn(
			'bg-popover text-popover-foreground m3-glass-pop data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 data-[state=closed]:zoom-out-95 data-[state=open]:zoom-in-95 data-[side=bottom]:slide-in-from-top-2 data-[side=left]:slide-in-from-right-2 data-[side=right]:slide-in-from-left-2 data-[side=top]:slide-in-from-bottom-2 z-50 w-72 rounded-md border p-4 shadow-md outline-none',
			className
		)}
		{...restProps}
	>
		{@render children?.()}
	</PopoverPrimitive.Content>
</PopoverPrimitive.Portal>
