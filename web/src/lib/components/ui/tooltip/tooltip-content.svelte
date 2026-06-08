<script lang="ts">
	import { Tooltip as TooltipPrimitive } from "bits-ui";
	import { cn } from "$lib/utils.js";
	import TooltipPortal from "./tooltip-portal.svelte";
	import type { ComponentProps } from "svelte";
	import type { WithoutChildrenOrChild } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		sideOffset = undefined,
		side = "top",
		variant = "plain",
		showCaret = false,
		children,
		arrowClasses,
		portalProps,
		...restProps
	}: TooltipPrimitive.ContentProps & {
		variant?: "plain" | "rich";
		showCaret?: boolean;
		arrowClasses?: string;
		portalProps?: WithoutChildrenOrChild<ComponentProps<typeof TooltipPortal>>;
	} = $props();

	const resolvedSideOffset = $derived(sideOffset ?? (variant === "rich" ? 8 : 6));
</script>

<TooltipPortal {...portalProps}>
	<TooltipPrimitive.Content
		bind:ref
		data-slot="tooltip-content"
		sideOffset={resolvedSideOffset}
		{side}
		data-variant={variant}
		class={cn(
			"m3-tooltip-content origin-(--bits-tooltip-content-transform-origin) text-balance border border-transparent data-[state=delayed-open]:animate-in data-[state=delayed-open]:fade-in-0 data-[state=delayed-open]:zoom-in-95 data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=closed]:zoom-out-95 data-[side=bottom]:slide-in-from-top-1 data-[side=left]:slide-in-from-end-1 data-[side=right]:slide-in-from-start-1 data-[side=top]:slide-in-from-bottom-1",
			variant === "rich"
				? "max-w-sm rounded-[var(--m3-shape-large)] px-4 py-3"
				: "",
			className
		)}
		{...restProps}
	>
		{@render children?.()}
		{#if showCaret}
			<TooltipPrimitive.Arrow>
				{#snippet child({ props })}
					<div
						class={cn(
							"z-50 size-2.5 rotate-45 rounded-[2px] bg-[var(--m3c-inverse-surface,#f4eff7)]",
							"data-[side=top]:translate-x-1/2 data-[side=top]:translate-y-[calc(-50%_+_2px)]",
							"data-[side=bottom]:-translate-x-1/2 data-[side=bottom]:-translate-y-[calc(-50%_+_1px)]",
							"data-[side=right]:translate-x-[calc(50%_+_2px)] data-[side=right]:translate-y-1/2",
							"data-[side=left]:-translate-y-[calc(50%_-_3px)]",
							arrowClasses
						)}
						{...props}
					></div>
				{/snippet}
			</TooltipPrimitive.Arrow>
		{/if}
	</TooltipPrimitive.Content>
</TooltipPortal>
