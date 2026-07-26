<script lang="ts">
	import { RangeCalendar as RangeCalendarPrimitive } from "bits-ui";
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
	}: WithoutChildrenOrChild<RangeCalendarPrimitive.YearSelectProps> = $props();
</script>

<span class={cn("relative flex", className)}>
	<RangeCalendarPrimitive.YearSelect bind:ref {...restProps}>
		{#snippet child({ props, yearItems, selectedYearItem })}
			<SimpleSelect
				value={String(value ?? selectedYearItem.value)}
				options={yearItems.map((item) => ({ value: String(item.value), label: item.label }))}
				disabled={calendarSelectDisabled(props)}
				ariaLabel="Choose year"
				triggerClass="h-(--cell-size) w-auto min-w-20 border-0 bg-transparent px-2 shadow-none"
				onchange={(nextValue) => dispatchCalendarSelectChange(props, nextValue, onchange)}
			/>
		{/snippet}
	</RangeCalendarPrimitive.YearSelect>
</span>
