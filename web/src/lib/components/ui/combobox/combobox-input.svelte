<script lang="ts">
	import { Combobox as ComboboxPrimitive } from "bits-ui";
	import SearchIcon from "@lucide/svelte/icons/search";
	import ChevronDownIcon from "@lucide/svelte/icons/chevron-down";
	import { cn, type WithoutChild } from "$lib/utils.js";
	import { getContext } from 'svelte';

	let {
		ref = $bindable(null),
		class: className,
		placeholder,
		...restProps
	}: WithoutChild<ComboboxPrimitive.InputProps> & { placeholder?: string } = $props();

	const ctx = getContext<{ syncInputValue?: (v: string) => void }>('combobox-ctx');
</script>

<ComboboxPrimitive.Trigger
	data-slot="combobox-trigger"
	class={cn(
		"border-input data-[placeholder]:text-muted-foreground focus-within:border-ring focus-within:ring-ring/50 flex w-full items-center gap-2 rounded-md border bg-transparent px-3 py-2 text-sm shadow-xs transition-[color,box-shadow] focus-within:ring-[3px]",
		className
	)}
>
	<SearchIcon class="size-4 shrink-0 text-muted-foreground" />
	<ComboboxPrimitive.Input
		bind:ref
		{placeholder}
		data-slot="combobox-input"
		class="flex h-5 w-full bg-transparent text-sm outline-hidden placeholder:text-muted-foreground disabled:cursor-not-allowed disabled:opacity-50"
		oninput={(e) => ctx?.syncInputValue?.((e.target as HTMLInputElement).value)}
		{...restProps}
	/>
	<ChevronDownIcon class="size-4 shrink-0 opacity-50" />
</ComboboxPrimitive.Trigger>
