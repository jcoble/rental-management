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

	// Split the incoming local ISO into its date (`yyyy-MM-dd`) and time (`HH:mm`)
	// parts. We keep these as local component state and recombine on change so a
	// partially-entered value (date but no time yet) doesn't get lost.
	function splitDate(iso: string): string {
		const [datePart] = iso.split('T');
		return datePart ?? '';
	}
	function splitTime(iso: string): string {
		const timePart = iso.split('T')[1] ?? '';
		// Normalize to HH:mm for the native time input (drop seconds if present).
		return timePart ? timePart.slice(0, 5) : '';
	}

	let datePart = $state(splitDate(value));
	let timePart = $state(splitTime(value));

	// Re-sync internal parts if the bound value is replaced from outside (e.g. a
	// form loads existing data) — guarded so we don't clobber in-progress edits.
	$effect(() => {
		const incoming = value;
		const combined = combine(datePart, timePart);
		if (incoming !== combined) {
			datePart = splitDate(incoming);
			timePart = splitTime(incoming);
		}
	});

	function combine(d: string, t: string): string {
		if (!d || !t) return '';
		// Always emit seconds for a stable `yyyy-MM-ddTHH:mm:ss` shape.
		const time = t.length === 5 ? `${t}:00` : t;
		return `${d}T${time}`;
	}

	function emit() {
		const next = combine(datePart, timePart);
		if (next === value) return;
		value = next;
		onchange?.(next);
	}

	function handleDateChange(iso: string) {
		datePart = iso;
		emit();
	}

	function handleTimeInput(e: Event) {
		timePart = (e.target as HTMLInputElement).value;
		emit();
	}

	const timeInputClass =
		'h-10 rounded-md border border-input bg-transparent px-3 py-2 text-sm text-foreground outline-none transition-[color,box-shadow] focus-visible:border-ring focus-visible:ring-ring/50 focus-visible:ring-[3px] disabled:cursor-not-allowed disabled:opacity-50 [color-scheme:dark]';
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
			placeholder="Pick a date"
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
