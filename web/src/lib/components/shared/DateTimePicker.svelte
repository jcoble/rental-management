<!--
  DateTimePicker — date + time-of-day picker (for appointments).

  Composes the shared DatePicker (date-only) with a native `<input type="time">`
  styled to match. Binds a single full ISO datetime string in LOCAL WALL-CLOCK
  time (NO timezone suffix), e.g. "2026-06-04T14:30:00", or "" for empty.

  TIMEZONE CONTRACT: `value` is local wall-clock — the date+time the landlord sees
  and types, with no offset. Callers convert to UTC on save (the API uses
  timestamptz / DateTimeKind.Utc discipline). This component does NO tz math.

  Both a date AND a time are required to form a value; until both are set, `value`
  stays "" (and onchange fires "").

  Props:
    value      string  (bindable)  local ISO datetime `yyyy-MM-ddTHH:mm:ss`, or "". bind:value supported.
    onchange?  (iso: string) => void   fired when the combined value changes (gets the ISO string or "").
    disabled?  boolean
    minDate?   string  ISO `yyyy-MM-dd` — earliest selectable date (passed to the date picker).
    maxDate?   string  ISO `yyyy-MM-dd` — latest selectable date.
    testid?    string  base data-testid; the date picker gets `{testid}-date`, time input `{testid}-time`.
    id?        string  id for the date trigger (label `for=` association).
-->
<script lang="ts">
	import DatePicker from './DatePicker.svelte';
	import { applyDateTimePartChange, combineDateTimeParts, splitDateTimeValue } from './date-time-picker-state';

	let {
		value = $bindable(''),
		onchange,
		disabled = false,
		minDate,
		maxDate,
		testid,
		id
	}: {
		value?: string;
		onchange?: (iso: string) => void;
		disabled?: boolean;
		minDate?: string;
		maxDate?: string;
		testid?: string;
		id?: string;
	} = $props();

	const initialParts = splitDateTimeValue(value);
	let datePart = $state(initialParts.datePart);
	let timePart = $state(initialParts.timePart);

	// Re-sync internal parts if the bound value is replaced from outside (e.g. a
	// form loads existing data) — guarded so we don't clobber in-progress edits.
	$effect(() => {
		const incoming = value;
		const combined = combineDateTimeParts(datePart, timePart);
		if (incoming !== combined) {
			const next = splitDateTimeValue(incoming);
			datePart = next.datePart;
			timePart = next.timePart;
		}
	});

	function commit(next: ReturnType<typeof applyDateTimePartChange>) {
		datePart = next.datePart;
		timePart = next.timePart;
		if (next.value === value) return;
		value = next.value;
		onchange?.(next.value);
	}

	function handleDateChange(iso: string) {
		const next = applyDateTimePartChange({ datePart, timePart }, { datePart: iso });
		commit(next);
	}

	function handleTimeInput(e: Event) {
		const next = applyDateTimePartChange(
			{ datePart, timePart },
			{ timePart: (e.target as HTMLInputElement).value }
		);
		commit(next);
	}

	const timeInputClass =
		'm3-field-surface h-11 px-3 py-2 text-sm text-foreground outline-none disabled:cursor-not-allowed disabled:opacity-50 [color-scheme:dark]';
</script>

<div class="flex items-center gap-2">
	<div class="flex-1">
		<DatePicker
			{id}
			value={datePart}
			onchange={handleDateChange}
			{disabled}
			min={minDate}
			max={maxDate}
			testid={testid ? `${testid}-date` : undefined}
		/>
	</div>
	<input
		type="time"
		class={timeInputClass}
		value={timePart}
		oninput={handleTimeInput}
		{disabled}
		data-testid={testid ? `${testid}-time` : undefined}
		aria-label="Time"
	/>
</div>
