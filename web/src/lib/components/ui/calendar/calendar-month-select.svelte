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
		testid,
		...restProps
	}: WithoutChildrenOrChild<CalendarPrimitive.MonthSelectProps> & { testid?: string } = $props();
</script>

<span class={cn("relative flex", className)}>
	<CalendarPrimitive.MonthSelect bind:ref {...restProps}>
		{#snippet child({ props, monthItems, selectedMonthItem })}
			<SimpleSelect
				value={String(value ?? selectedMonthItem.value)}
				options={monthItems.map((item) => ({ value: String(item.value), label: item.label }))}
				disabled={calendarSelectDisabled(props)}
				ariaLabel="Choose month"
				testid={testid}
				viewportPadding={{ bottom: 12 }}
				triggerClass="h-(--cell-size) w-auto min-w-28 px-2 text-xs font-medium sm:text-sm"
				onchange={(nextValue) => dispatchCalendarSelectChange(props, nextValue, onchange)}
			/>
		{/snippet}
	</CalendarPrimitive.MonthSelect>
</span>
