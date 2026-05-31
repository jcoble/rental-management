<script lang="ts">
	import { Combobox as ComboboxPrimitive } from "bits-ui";
	import { setContext } from 'svelte';
	import { tick } from 'svelte';

	let {
		open = $bindable(false),
		value = $bindable(),
		inputValue = $bindable(""),
		items = [],
		onOpenChange,
		...restProps
	}: ComboboxPrimitive.RootProps & { inputValue?: string } = $props();

	// Resolve the display label for the current value
	function getSelectedLabel(): string {
		if (!value || !items.length) return '';
		const found = items.find((i) => i.value === value);
		return found?.label ?? '';
	}

	// Initialize inputValue to the selected item's label on mount
	if (!inputValue && value) {
		inputValue = getSelectedLabel();
	}

	// When value changes externally (e.g. form loads with existing data),
	// update inputValue to show the label — but only when dropdown is closed
	$effect(() => {
		// Access value to track it
		const _v = value;
		if (!open) {
			inputValue = getSelectedLabel();
		}
	});

	setContext('combobox-ctx', {
		get value() { return value; },
		get items() { return items; },
		syncInputValue(v: string) { inputValue = v; }
	});

	function handleOpenChange(isOpen: boolean) {
		if (isOpen) {
			// When opening, clear input so user can type to search
			inputValue = '';
		} else {
			// When closing, restore to selected item's label
			inputValue = getSelectedLabel();
		}
		onOpenChange?.(isOpen);
	}
</script>

<ComboboxPrimitive.Root
	bind:open
	bind:value={value as never}
	{inputValue}
	{items}
	onOpenChange={handleOpenChange}
	{...restProps}
/>
