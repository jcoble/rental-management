<!--
  DatePicker — date-only picker (shadcn Calendar inside a Popover).

  A drop-in replacement for `<input type="date">`. Binds a plain ISO `yyyy-MM-dd`
  string (or "" for empty). NO time, NO timezone: a calendar date has no tz, so we
  never do tz math here — the API layer pins date-only fields to UTC on the wire.

  Internally the value is converted at the boundary: the incoming `yyyy-MM-dd` is
  parsed to a `CalendarDate` for the bits-ui Calendar; on select we emit a plain
  `yyyy-MM-dd` string again. The trigger label is formatted human-readably (UTC-
  pinned so it never drifts a day), e.g. "Jun 4, 2026".

  Props:
    value       string  (bindable)  ISO `yyyy-MM-dd`, or "" for empty. bind:value supported.
    onchange?   (iso: string) => void   fired on select/clear (gets `yyyy-MM-dd` or "").
    placeholder string  trigger text when empty. Default "Pick a date".
    disabled?   boolean
    min?        string  ISO `yyyy-MM-dd` — earliest selectable date (inclusive).
    max?        string  ISO `yyyy-MM-dd` — latest selectable date (inclusive).
    testid?     string  applied as data-testid on the trigger button for E2E.
    id?         string  id for the trigger (label `for=` association).
-->
<script lang="ts">
	import { Calendar } from '$lib/components/ui/calendar';
	import * as Popover from '$lib/components/ui/popover';
	import { buttonVariants } from '$lib/components/ui/button';
	import { cn } from '$lib/utils';
	import CalendarIcon from '@lucide/svelte/icons/calendar';
	import { CalendarDate, parseDate, DateFormatter, type DateValue } from '@internationalized/date';

	let {
		value = $bindable(''),
		onchange,
		placeholder = 'Pick a date',
		disabled = false,
		min,
		max,
		testid,
		id
	}: {
		value?: string;
		onchange?: (iso: string) => void;
		placeholder?: string;
		disabled?: boolean;
		min?: string;
		max?: string;
		testid?: string;
		id?: string;
	} = $props();

	let open = $state(false);

	// Parse an ISO `yyyy-MM-dd` string into a CalendarDate; return undefined on
	// empty/invalid input (never throws into the template).
	function toCalendarDate(iso: string | undefined): CalendarDate | undefined {
		if (!iso) return undefined;
		try {
			return parseDate(iso);
		} catch {
			return undefined;
		}
	}

	// UTC-pinned formatter so "Jun 4" never slips to "Jun 3" in a behind-UTC tz.
	const formatter = new DateFormatter('en-US', {
		month: 'short',
		day: 'numeric',
		year: 'numeric',
		timeZone: 'UTC'
	});

	const selected = $derived(toCalendarDate(value));
	const minDate = $derived(toCalendarDate(min));
	const maxDate = $derived(toCalendarDate(max));

	const label = $derived(selected ? formatter.format(selected.toDate('UTC')) : placeholder);

	function handleValueChange(next: DateValue | undefined) {
		// CalendarDate.toString() is exactly `yyyy-MM-dd`.
		const iso = next ? next.toString() : '';
		if (iso === value) return;
		value = iso;
		onchange?.(iso);
		if (iso) open = false;
	}
</script>

<Popover.Root bind:open>
	<Popover.Trigger
		{id}
		data-testid={testid}
		{disabled}
		class={cn(
			buttonVariants({ variant: 'outline' }),
			'h-10 w-full justify-start text-left font-normal',
			!selected && 'text-muted-foreground'
		)}
	>
		<CalendarIcon class="size-4 shrink-0 opacity-70" />
		{label}
	</Popover.Trigger>
	<Popover.Content class="w-auto p-0" align="start">
		<Calendar
			type="single"
			value={selected}
			onValueChange={handleValueChange}
			minValue={minDate}
			maxValue={maxDate}
		/>
	</Popover.Content>
</Popover.Root>
