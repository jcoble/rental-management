<script lang="ts">
	import { Calendar as CalendarPrimitive } from "bits-ui";
	import { cn, type WithoutChildrenOrChild } from "$lib/utils.js";
	import SimpleSelect from '$lib/components/shared/SimpleSelect.svelte';
	import {
		calendarSelectDisabled,
		dispatchCalendarSelectChange
	} from '$lib/components/ui/calendar-select-helpers';

	let {
		ref = $bindable(null),
		class: className,
		value,
		onchange,
		...restProps
	}: WithoutChildrenOrChild<CalendarPrimitive.YearSelectProps> = $props();
</script>

<span class={cn("relative flex", className)}>
	<CalendarPrimitive.YearSelect bind:ref {...restProps}>
		{#snippet child({ props, yearItems, selectedYearItem })}
			<SimpleSelect
				value={String(value ?? selectedYearItem.value)}
				options={yearItems.map((item) => ({ value: String(item.value), label: item.label }))}
				disabled={calendarSelectDisabled(props)}
				ariaLabel="Choose year"
				viewportPadding={{ bottom: 12 }}
				triggerClass="h-(--cell-size) w-auto min-w-20 border-0 bg-transparent px-2 shadow-none"
				onchange={(nextValue) => dispatchCalendarSelectChange(props, nextValue, onchange)}
			/>
		{/snippet}
	</CalendarPrimitive.YearSelect>
</span>
