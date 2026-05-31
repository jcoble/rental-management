<script lang="ts">
	import { Select as SelectPrimitive } from "bits-ui";
	import { setContext } from 'svelte';

	let {
		open = $bindable(false),
		value = $bindable(),
		items = [],
		...restProps
	}: SelectPrimitive.RootProps = $props();

	setContext('select-ctx', {
		get value() { return value; },
		get items() { return items; }
	});
</script>

<SelectPrimitive.Root bind:open bind:value={value as never} {items} {...restProps} />
