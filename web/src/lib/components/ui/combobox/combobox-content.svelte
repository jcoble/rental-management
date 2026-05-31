<script lang="ts">
	import { Combobox as ComboboxPrimitive } from "bits-ui";
	import { cn, type WithoutChild } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		sideOffset = 4,
		children,
		...restProps
	}: WithoutChild<ComboboxPrimitive.ContentProps> = $props();
</script>

<ComboboxPrimitive.Portal>
	<ComboboxPrimitive.Content
		bind:ref
		{sideOffset}
		data-slot="combobox-content"
		class={cn(
			"bg-popover text-popover-foreground data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 data-[state=closed]:zoom-out-95 data-[state=open]:zoom-in-95 data-[side=bottom]:slide-in-from-top-2 data-[side=left]:slide-in-from-end-2 data-[side=right]:slide-in-from-start-2 data-[side=top]:slide-in-from-bottom-2 relative z-50 min-w-[8rem] overflow-hidden rounded-md border shadow-md data-[side=bottom]:translate-y-1 data-[side=left]:-translate-x-1 data-[side=right]:translate-x-1 data-[side=top]:-translate-y-1",
			className
		)}
		{...restProps}
	>
		<ComboboxPrimitive.Viewport
			class="max-h-72 overflow-y-auto scroll-my-1 p-1"
		>
			{@render children?.()}
		</ComboboxPrimitive.Viewport>
	</ComboboxPrimitive.Content>
</ComboboxPrimitive.Portal>
