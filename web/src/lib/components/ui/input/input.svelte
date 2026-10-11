<script lang="ts">
	import type { HTMLInputAttributes, HTMLInputTypeAttribute } from "svelte/elements";
	import { limitInputLength, maskInputValue, type InputMask } from "$lib/forms/input-masks";
	import { cn, type WithElementRef } from "$lib/utils.js";

	type InputType = Exclude<HTMLInputTypeAttribute, "file">;

	type Props = WithElementRef<
		Omit<HTMLInputAttributes, "type" | "oninput"> &
			{ mask?: InputMask; oninput?: (event: Event) => void } &
			({ type: "file"; files?: FileList } | { type?: InputType; files?: undefined })
	>;

	let {
		ref = $bindable(null),
		value = $bindable(),
		type,
		files = $bindable(),
		class: className,
		"data-slot": dataSlot = "input",
		mask,
		oninput,
		...restProps
	}: Props = $props();

	function handleInput(event: Event) {
		const input = event.currentTarget as HTMLInputElement;
		const rawMaxLength =
			typeof restProps.maxlength === "number"
				? restProps.maxlength
				: typeof restProps.maxlength === "string"
					? Number.parseInt(restProps.maxlength, 10)
					: undefined;
		const maxLength = Number.isFinite(rawMaxLength) ? rawMaxLength : undefined;
		const masked = mask ? maskInputValue(input.value, mask, { maxLength }) : input.value;
		const next = limitInputLength(masked, maxLength);
		if (next !== input.value) input.value = next;
		value = next;
		oninput?.(event);
	}
</script>

{#if type === "file"}
	<input
		bind:this={ref}
		data-slot={dataSlot}
		class={cn(
			"m3-field-surface selection:bg-primary selection:text-primary-foreground placeholder:text-muted-foreground flex h-10 w-full min-w-0 px-3 pt-1.5 text-sm font-medium shadow-xs outline-none disabled:cursor-not-allowed disabled:opacity-50",
			"focus-visible:border-ring focus-visible:ring-ring/50 focus-visible:ring-[3px]",
			"aria-invalid:ring-destructive/20 dark:aria-invalid:ring-destructive/40 aria-invalid:border-destructive",
			className
		)}
		type="file"
		bind:files
		bind:value
		oninput={oninput}
		{...restProps}
	/>
{:else}
	<input
		bind:this={ref}
		data-slot={dataSlot}
		class={cn(
			"m3-field-surface selection:bg-primary selection:text-primary-foreground placeholder:text-muted-foreground flex h-10 w-full min-w-0 px-3 py-1 text-base shadow-xs outline-none disabled:cursor-not-allowed disabled:opacity-50 md:text-sm",
			"focus-visible:border-ring focus-visible:ring-ring/50 focus-visible:ring-[3px]",
			"aria-invalid:ring-destructive/20 dark:aria-invalid:ring-destructive/40 aria-invalid:border-destructive",
			className
		)}
		{type}
		bind:value
		oninput={handleInput}
		{...restProps}
	/>
{/if}
